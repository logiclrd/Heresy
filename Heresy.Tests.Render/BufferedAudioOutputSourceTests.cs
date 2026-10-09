using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class BufferedAudioOutputSourceTests
{
	[Test]
	public void CallbackNeverExecutesSourceRenderingAndEmptyRingDoesNotSkipMusic()
	{
		using ManualResetEventSlim entered = new(false);
		using ManualResetEventSlim release = new(false);
		TestSource source = new(2, entered, release);
		using BufferedAudioOutputSource ring = new(source, 12, 4);
		Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
		int callbackThread = Environment.CurrentManagedThreadId;
		float[] missing = new float[6];
		ring.Render(3, missing);
		Assert.That(missing, Is.All.Zero);
		Assert.That(ring.UnderrunCount, Is.EqualTo(1));
		Assert.That(source.RenderCalls, Is.Zero,
			"An empty callback must never try to generate its own samples.");
		try
		{
			release.Set();
			Assert.That(SpinWait.SpinUntil(
				() => ring.BufferedFrames >= 4, TimeSpan.FromSeconds(5)), Is.True);
			float[] pcm = new float[8];
			ring.Render(4, pcm);
			Assert.That(pcm, Is.EqualTo(new float[]
			{
				1, 2, 3, 4, 5, 6, 7, 8,
			}).AsCollection);
			Assert.That(source.RenderThreadId, Is.Not.EqualTo(callbackThread));
			Assert.That(ring.UnderrunCount, Is.EqualTo(1));
		}
		finally { release.Set(); }
	}

	[Test]
	public void RingWrapsInterleavedChannelsWithoutDroppingOrReorderingFrames()
	{
		TestSource source = new(2);
		using BufferedAudioOutputSource ring = new(source, 7, 3);
		float[] pcm = new float[70];
		for (int offset = 0; offset < 35;)
		{
			int count = Math.Min(5, 35 - offset);
			Assert.That(SpinWait.SpinUntil(
				() => ring.BufferedFrames >= count, TimeSpan.FromSeconds(5)),
				Is.True);
			ring.Render(count, pcm.AsSpan(offset * 2, count * 2));
			offset += count;
		}
		for (int frame = 0; frame < 35; frame++)
		{
			Assert.That(pcm[frame * 2], Is.EqualTo(frame * 2 + 1));
			Assert.That(pcm[frame * 2 + 1], Is.EqualTo(frame * 2 + 2));
		}
		Assert.That(ring.BufferedFrames, Is.InRange(0, 7));
		Assert.That(ring.UnderrunCount, Is.Zero);
	}

	[Test]
	public void WorkerFailureIsPublishedAndCallbackOnlyOutputsSilence()
	{
		TestSource source = new(1, fail: true);
		using BufferedAudioOutputSource ring = new(source, 8, 2);
		Assert.That(SpinWait.SpinUntil(
				() => ring.RenderingFault is not null, TimeSpan.FromSeconds(5)), Is.True);
		float[] output = new float[3];
		Assert.DoesNotThrow(() => ring.Render(3, output));
		Assert.That(output, Is.All.Zero);
		Assert.That(ring.RenderingFault, Is.TypeOf<InvalidOperationException>());
	}

	[Test]
	public void LiveCommandsAreRelayedToThreadSafeSourceQueue()
	{
		TestSource source = new(1);
		using BufferedAudioOutputSource ring = new(source, 8, 2);
		ring.EnqueueLiveEvent(ChannelTarget.Virtual(7), [new NoteOffCommand()]);
		Assert.That(source.ReceivedCommands, Is.EqualTo(1));
	}

	private sealed class TestSource : ILiveAudioOutputSource
	{
		private readonly ManualResetEventSlim? _entered;
		private readonly ManualResetEventSlim? _release;
		private readonly bool _fail;
		private long _position;
		private int _renderCalls;
		private int _renderThreadId;
		private int _receivedCommands;
		public TestSource(int channels, ManualResetEventSlim? entered = null,
			ManualResetEventSlim? release = null, bool fail = false)
		{
			Format = new AudioOutputFormat(48000, channels);
			_entered = entered;
			_release = release;
			_fail = fail;
		}
		public AudioOutputFormat Format { get; }
		public int RenderCalls => Volatile.Read(ref _renderCalls);
		public int RenderThreadId => Volatile.Read(ref _renderThreadId);
		public int ReceivedCommands => Volatile.Read(ref _receivedCommands);
		public void EnqueueLiveEvent(ChannelTarget target,
			IReadOnlyList<NoteCommand> commands)
		{
			Interlocked.Add(ref _receivedCommands, commands.Count);
		}
		public void Render(int frameCount, Span<float> destination)
		{
			_entered?.Set();
			_release?.Wait();
			if (_fail)
				throw new InvalidOperationException("Producer failed");
			Volatile.Write(ref _renderThreadId, Environment.CurrentManagedThreadId);
			Interlocked.Increment(ref _renderCalls);
			for (int i = 0; i < destination.Length; i++)
				destination[i] = Interlocked.Increment(ref _position);
		}
	}
}

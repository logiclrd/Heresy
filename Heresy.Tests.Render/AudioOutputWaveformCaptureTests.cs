using System;
using System.Threading;
using System.Threading.Tasks;

using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class AudioOutputWaveformCaptureTests
{
	[Test]
	public void HiddenScopeDoesNotCaptureAndVisibleScopeCopiesFinalInterleavedPcm()
	{
		AudioOutputWaveformCapture capture = new();
		AudioOutputWaveformCapture.Session session =
			capture.BeginSession(new AudioOutputFormat(48000, 2));
		capture.Publish(session, [1, 10, 2, 20]);
		float[] left = new float[6];
		float[] right = new float[6];
		Assert.That(capture.ReadLatest(left, right).VisibleFrames, Is.Zero);
		Assert.That(left, Is.All.NaN);

		capture.SetEnabled(true);
		capture.Publish(session, [0.25f, -0.25f, 0, 0, -0.75f, 0.75f]);
		AudioOutputWaveformCapture.Snapshot snapshot =
			capture.ReadLatest(left, right, 0, 1);
		Assert.That(snapshot.SessionId, Is.EqualTo(session.Id));
		Assert.That(snapshot.SampleRate, Is.EqualTo(48000));
		Assert.That(snapshot.ChannelCount, Is.EqualTo(2));
		Assert.That(snapshot.VisibleFrames, Is.EqualTo(3));
		Assert.That(left.AsSpan(3).ToArray(),
			Is.EqualTo(new[] { 0.25f, 0f, -0.75f }));
		Assert.That(right.AsSpan(3).ToArray(),
			Is.EqualTo(new[] { -0.25f, 0f, 0.75f }));
		Assert.That(float.IsNaN(left[2]), Is.True);
		Assert.That(float.IsNaN(right[2]), Is.True);
		// Literal zeroes in the queued output remain zeroes, not gaps.
		Assert.That(float.IsNaN(left[4]), Is.False);
	}

	[Test]
	public void CaptureKeepsExactLastFramesWithoutPeakOrEnvelopeDownsampling()
	{
		AudioOutputWaveformCapture capture = new();
		var session = capture.BeginSession(new AudioOutputFormat(48000, 1));
		capture.SetEnabled(true);
		float[] samples = new float[AudioOutputWaveformCapture.CapacityFrames + 512];
		for (int i = 0; i < samples.Length; i++)
			samples[i] = i;
		capture.Publish(session, samples);
		float[] one = new float[16], unused = new float[16];
		var snapshot = capture.ReadLatest(one, unused);
		Assert.That(snapshot.VisibleFrames, Is.EqualTo(16));
		for (int i = 0; i < one.Length; i++)
			Assert.That(one[i], Is.EqualTo(samples[samples.Length - 16 + i]));
		Assert.That(unused, Is.All.NaN);
	}

	[Test]
	public void SessionReplacementDropsOldFramesAndLateCallbackCannotReviveThem()
	{
		AudioOutputWaveformCapture capture = new();
		capture.SetEnabled(true);
		var oldSession = capture.BeginSession(new AudioOutputFormat(44100, 2));
		capture.Publish(oldSession, [0.3f, 0.4f]);
		var newSession = capture.BeginSession(new AudioOutputFormat(96000, 6));
		capture.Publish(oldSession, [0.9f, 0.9f]);
		capture.EndSession(oldSession);

		float[] first = new float[4], second = new float[4];
		var empty = capture.ReadLatest(first, second, 4, 5);
		Assert.That(empty.SessionId, Is.EqualTo(newSession.Id));
		Assert.That(empty.ChannelCount, Is.EqualTo(6));
		Assert.That(empty.VisibleFrames, Is.Zero);
		Assert.That(first, Is.All.NaN);

		capture.Publish(newSession,
			[10, 11, 12, 13, 14, 15, 20, 21, 22, 23, 24, 25]);
		var current = capture.ReadLatest(first, second, 4, 5);
		Assert.That(current.VisibleFrames, Is.EqualTo(2));
		Assert.That(first[2..], Is.EqualTo(new[] { 14f, 24f }));
		Assert.That(second[2..], Is.EqualTo(new[] { 15f, 25f }));
		capture.EndSession(newSession);
		Assert.That(capture.CurrentSessionId, Is.Zero);
		Assert.That(capture.ReadLatest(first, second).VisibleFrames, Is.Zero);
	}

	[Test]
	public void HiddenThenReopenedScopeDoesNotReplayPreviousObservations()
	{
		AudioOutputWaveformCapture capture = new();
		var session = capture.BeginSession(new AudioOutputFormat(48000, 1));
		capture.SetEnabled(true);
		capture.Publish(session, [1, 2, 3]);
		capture.SetEnabled(false);
		capture.Publish(session, [4, 5]);
		capture.SetEnabled(true);
		float[] samples = new float[5], unused = new float[5];
		Assert.That(capture.ReadLatest(samples, unused).VisibleFrames, Is.Zero);
		capture.Publish(session, [6, 7]);
		Assert.That(capture.ReadLatest(samples, unused).VisibleFrames,
			Is.EqualTo(2));
		Assert.That(samples[3..], Is.EqualTo(new[] { 6f, 7f }));
	}

	[TestCase(1)]
	[TestCase(2)]
	[TestCase(6)]
	[TestCase(8)]
	public void SpeakerFormatsRetainTheirOriginalInterleavedChannels(int channels)
	{
		AudioOutputWaveformCapture capture = new();
		var session = capture.BeginSession(new AudioOutputFormat(48000, channels));
		capture.SetEnabled(true);
		float[] samples = new float[channels * 2];
		for (int frame = 0; frame < 2; frame++)
			for (int channel = 0; channel < channels; channel++)
				samples[frame * channels + channel] = frame * 10 + channel;
		capture.Publish(session, samples);
		float[] first = new float[2], second = new float[2];
		var snapshot = capture.ReadLatest(first, second,
			channels - 1, channels > 1 ? 0 : -1);
		Assert.That(snapshot.ChannelCount, Is.EqualTo(channels));
		Assert.That(first, Is.EqualTo(
			new float[] { channels - 1, 10 + channels - 1 }));
		if (channels == 1)
			Assert.That(second, Is.All.NaN);
		else
			Assert.That(second, Is.EqualTo(new float[] { 0, 10 }));
	}

	[Test]
	public void ConcurrentReadNeverReportsPartiallyPublishedChannelFrames()
	{
		AudioOutputWaveformCapture capture = new();
		var session = capture.BeginSession(new AudioOutputFormat(48000, 2));
		capture.SetEnabled(true);
		Task writer = Task.Run(() =>
		{
			float[] samples = new float[128 * 2];
			for (int block = 0; block < 1000; block++)
			{
				for (int i = 0; i < samples.Length; i += 2)
					samples[i] = samples[i + 1] = block;
				capture.Publish(session, samples);
			}
		});
		float[] a = new float[128], b = new float[128];
		while (!writer.IsCompleted)
		{
			capture.ReadLatest(a, b, 0, 1);
			for (int i = 0; i < a.Length; i++)
				if (float.IsFinite(a[i]) && float.IsFinite(b[i]))
					Assert.That(a[i], Is.EqualTo(b[i]));
		}
		writer.GetAwaiter().GetResult();
	}

	[Test]
	public void EnabledCallbackCaptureHasNoPerBlockManagedAllocation()
	{
		AudioOutputWaveformCapture capture = new();
		var session = capture.BeginSession(new AudioOutputFormat(48000, 8));
		capture.SetEnabled(true);
		float[] samples = new float[256 * 8];
		for (int i = 0; i < 100; i++)
			capture.Publish(session, samples);
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 1000; i++)
			capture.Publish(session, samples);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.LessThan(256L),
			"PCM callbacks cannot allocate in proportion to captured blocks.");
	}
}

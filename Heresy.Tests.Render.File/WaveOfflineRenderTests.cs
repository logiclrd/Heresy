using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Threading;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.File;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render.File;

[TestFixture]
public sealed class WaveOfflineRenderTests
{
	[Test]
	public void WaveFileSinkWritesCanonicalPcm16HeaderAndSamples()
	{
		using MemoryStream stream = new();
		AudioOutputFormat format = new(
			sampleRate: 8000,
			channelCount: 2);
		using WaveFileSink sink =
			new(
				stream,
				format,
				leaveOpen: true);

		sink.Write(
			[
				-1.0f, -0.5f,
				0.0f, 0.5f,
				1.0f, 2.0f,
			]);
		sink.Complete();

		byte[] bytes = stream.ToArray();
		bytes.Length.Should().Be(44 + 12);
		System.Text.Encoding.ASCII.GetString(bytes, 0, 4)
			.Should().Be("RIFF");
		BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4))
			.Should().Be((uint)(bytes.Length - 8));
		System.Text.Encoding.ASCII.GetString(bytes, 8, 4)
			.Should().Be("WAVE");
		System.Text.Encoding.ASCII.GetString(bytes, 12, 4)
			.Should().Be("fmt ");
		BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2))
			.Should().Be(1);
		BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2))
			.Should().Be(2);
		BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4))
			.Should().Be(8000);
		BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34, 2))
			.Should().Be(16);
		System.Text.Encoding.ASCII.GetString(bytes, 36, 4)
			.Should().Be("data");
		BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4))
			.Should().Be(12);

		short[] pcm =
		[
			BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44, 2)),
			BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46, 2)),
			BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(48, 2)),
			BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(50, 2)),
			BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(52, 2)),
			BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(54, 2)),
		];
		pcm.Should().Equal(
			short.MinValue,
			(short)-16384,
			(short)0,
			(short)16384,
			short.MaxValue,
			short.MaxValue);
	}

	[Test]
	public void OfflineRendererKeepsLogicalSilenceAndRendersReleaseTail()
	{
		const int sampleRate = 10;
		ObjectId sourceId = (ObjectId)1U;
		AdsrEnvelopeDefinition envelope =
			new(
				(ObjectId)2U,
				"Volume")
			{
				Release = TimeSpan.FromMilliseconds(300),
				SustainLevel = 1.0,
			};
		InfiniteSound sound =
			new(
				new NoteConfigurationSnapshot(
					NewNotePolicy.Cut,
					envelopes:
						new EnvelopeConfigurationSnapshot(
							Volume:
								new AdsrEnvelopeCurve(
									envelope))));
		PlaybackSession session =
			Session(
				sampleRate,
				Schedule(
					Event(
						TimeSpan.Zero,
						new StartNoteCommand(sourceId))),
				new Resolver(sourceId, sound));

		using MemoryStream stream = new();
		using WaveFileSink sink =
			new(
				stream,
				new AudioOutputFormat(
					sampleRate,
					1),
				leaveOpen: true);

		OfflineRenderResult result =
			OfflinePlaybackRenderer.Render(
				session,
				logicalDuration:
					TimeSpan.FromMilliseconds(200),
				sink,
				blockFrameCount: 4);
		sink.Complete();

		result.LogicalFrameCount.Should().Be(2);
		result.TailFrameCount.Should().Be(3);
		result.TotalFrameCount.Should().Be(5);
		BinaryPrimitives.ReadUInt32LittleEndian(
				stream.ToArray().AsSpan(40, 4))
			.Should().Be(10);
	}

	[Test]
	public void OfflineRendererCutsVoiceThatStillHasNoDeterministicEndAfterSongEnd()
	{
		ObjectId sourceId = (ObjectId)1U;
		PlaybackSession session =
			Session(
				10,
				Schedule(
					Event(
						TimeSpan.Zero,
						new StartNoteCommand(sourceId))),
				new Resolver(
					sourceId,
					new InfiniteSound(
						NoteConfigurationSnapshot.Default)));
		using MemoryStream stream = new();
		using WaveFileSink sink =
			new(
				stream,
				new AudioOutputFormat(10, 1),
				leaveOpen: true);

		OfflineRenderResult result =
			OfflinePlaybackRenderer.Render(
				session,
				TimeSpan.FromMilliseconds(200),
				sink,
				blockFrameCount: 4);
		sink.Complete();

		result.LogicalFrameCount.Should().Be(2);
		result.TailFrameCount.Should().Be(1);
		result.TotalFrameCount.Should().Be(3);
		BinaryPrimitives.ReadUInt32LittleEndian(
				stream.ToArray().AsSpan(40, 4))
			.Should().Be(6);
	}

	[Test]
	public void EndOfInputNoteOffMakesLoopStyleSoundFinite()
	{
		ObjectId sourceId = (ObjectId)1U;
		NoteOffBoundSound sound = new();
		PlaybackSession session =
			Session(
				10,
				Schedule(
					Event(
						TimeSpan.Zero,
						new StartNoteCommand(sourceId))),
				new Resolver(sourceId, sound));
		float[] logical = new float[2];

		session.Render(
			0,
			2,
			logical);
		session.EndInput();

		session.HasIndefiniteActiveVoices.Should().BeFalse();
		session.IsQuiescent.Should().BeFalse();

		float[] tail = new float[2];
		session.Render(
			2,
			2,
			tail);

		session.IsQuiescent.Should().BeTrue();
	}

	private static PlaybackSession Session(
		int sampleRate,
		NoteSchedule schedule,
		ISoundResolver resolver)
		=> new(
			new RenderContext(
				new RenderConfiguration(
					sampleRate,
					[
						new OutputChannelConfiguration(
							Vector3.Zero,
							positionalImportance: 0.0),
					])),
			schedule,
			resolver);

	private static NoteSchedule Schedule(
		params NoteEvent[] events)
	{
		NoteScheduleBuilder builder = new();
		foreach (NoteEvent noteEvent in events)
			builder.Append(noteEvent);
		return builder.Freeze();
	}

	private static NoteEvent Event(
		TimeSpan time,
		params NoteCommand[] commands)
		=> new(
			new MusicalTime(time, 0.0),
			ChannelTarget.Physical(0),
			commands);

	[Test]
	public void CoroutineProgressUsesRenderedMusicalFramesWithNoInventedTotal()
	{
		using MemoryStream stream = new();
		using WaveFileSink sink = new(stream,
			new AudioOutputFormat(10, 1), leaveOpen: true);
		NeverEndingArrangement arrangement = new(
			Session(10, Schedule(), new Resolver(
				(ObjectId)99U,
				new InfiniteSound(NoteConfigurationSnapshot.Default))));
		List<OfflineRenderProgress> observed = [];
		using CancellationTokenSource cancel = new();
		CallbackProgress progress = new(update =>
		{
			observed.Add(update);
			if (update.LogicalFramesRendered == 8)
				cancel.Cancel();
		});
		Assert.Throws<OperationCanceledException>(
			() => OfflinePlaybackRenderer.Render(arrangement, sink,
				blockFrameCount: 4,
				maximumLogicalFrames: 100,
				progress: progress,
				cancellationToken: cancel.Token));
		Assert.That(observed, Has.Count.EqualTo(2));
		Assert.That(observed[0].Phase,
			Is.EqualTo(OfflineRenderPhase.LogicalBody));
		Assert.That(observed[0].LogicalFramesRendered, Is.EqualTo(4));
		Assert.That(observed[0].RenderedMusicalTime,
			Is.EqualTo(TimeSpan.FromMilliseconds(400)));
		Assert.That(observed[1].LogicalFramesRendered, Is.EqualTo(8));
		Assert.That(observed, Has.All.Matches<OfflineRenderProgress>(
			item => item.KnownLogicalFrameCount is null));
		Assert.That(sink.FramesWritten, Is.EqualTo(8),
			"Canceled exports stop at a complete PCM block boundary.");
	}

	[Test]
	public void FiniteRendererReportsKnownLengthAndFiniteReleaseTail()
	{
		PlaybackSession session = Session(10,
			Schedule(Event(TimeSpan.Zero,
				new StartNoteCommand((ObjectId)1U))),
			new Resolver((ObjectId)1U,
				new InfiniteSound(new NoteConfigurationSnapshot(
					NewNotePolicy.Cut,
					envelopes: new EnvelopeConfigurationSnapshot(
						Volume: new AdsrEnvelopeCurve(
							new AdsrEnvelopeDefinition((ObjectId)2U, "ADSR")
							{
								Release = TimeSpan.FromMilliseconds(300),
								SustainLevel = 1.0,
							}))))));
		using MemoryStream stream = new();
		using WaveFileSink sink = new(stream,
			new AudioOutputFormat(10, 1), leaveOpen: true);
		List<OfflineRenderProgress> observed = [];
		OfflineRenderResult result = OfflinePlaybackRenderer.Render(session,
			TimeSpan.FromMilliseconds(200), sink, blockFrameCount: 1,
			progress: new CallbackProgress(observed.Add));
		Assert.That(observed.Select(x => x.Phase).ToArray(),
			Does.Contain(OfflineRenderPhase.ReleaseTail));
		Assert.That(observed[^1].Phase, Is.EqualTo(OfflineRenderPhase.Completed));
		Assert.That(observed[^1].KnownLogicalFrameCount, Is.EqualTo(2));
		Assert.That(observed[^1].LogicalFramesRendered, Is.EqualTo(2));
		Assert.That(observed[^1].TailFramesRendered,
			Is.EqualTo(result.TailFrameCount));
		Assert.That(result.TailFrameCount, Is.EqualTo(3));
	}

	private sealed class CallbackProgress(Action<OfflineRenderProgress> callback)
		: IProgress<OfflineRenderProgress>
	{
		public void Report(OfflineRenderProgress update) => callback(update);
	}

	[Test]
	public void InfiniteIncrementalArrangementFailsAtExplicitExportFrameCap()
	{
		using MemoryStream stream = new();
		using WaveFileSink sink = new(
			stream, new AudioOutputFormat(10, 1), leaveOpen: true);
		NeverEndingArrangement source = new(
			Session(10, Schedule(), new Resolver(
				(ObjectId)99U,
				new InfiniteSound(NoteConfigurationSnapshot.Default))));
		Action render = () => OfflinePlaybackRenderer.Render(
			source, sink, blockFrameCount: 4, maximumLogicalFrames: 9);
		render.Should().Throw<InvalidOperationException>()
			.WithMessage("*finite export frame limit*");
	}

	private sealed class NeverEndingArrangement(PlaybackSession session)
		: IIncrementalArrangementSource
	{
		public PlaybackSession Session => session;
		public AudioOutputFormat Format => new(
			session.SampleRate, session.OutputChannelCount);
		public bool IsComplete => false;
		public TimeSpan LogicalDuration => TimeSpan.Zero;
		public int RenderLogical(int frameCount, Span<float> destination)
		{
			destination.Clear();
			return frameCount;
		}
		public void Render(int frameCount, Span<float> destination)
			=> RenderLogical(frameCount, destination);
	}

	private sealed class Resolver : ISoundResolver
	{
		private readonly ObjectId _id;
		private readonly ISound _sound;

		public Resolver(
			ObjectId id,
			ISound sound)
		{
			_id = id;
			_sound = sound;
		}

		public bool TryResolve(
			ObjectId sourceId,
			bool mixdown,
			out ISound? sound)
		{
			_ = mixdown;
			sound =
				sourceId == _id
					? _sound
					: null;
			return sound is not null;
		}
	}

	private sealed class InfiniteSound : ISound
	{
		private readonly NoteConfigurationSnapshot _configuration;

		public InfiniteSound(
			NoteConfigurationSnapshot configuration)
			=> _configuration = configuration;

		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> _configuration;

		public SoundState CreateState()
			=> new TestState();

		public long? GetEndFrameExclusive(
			RenderContext context,
			SoundState state)
		{
			_ = context;
			_ = state;
			return null;
		}

		public void Render(
			RenderContext context,
			SoundState state,
			long startFrame,
			int frameCount,
			Span<float> destination)
		{
			_ = context;
			_ = state;
			_ = startFrame;
			destination.Fill(1.0f);
		}
	}

	private sealed class NoteOffBoundSound : ISound
	{
		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> NoteConfigurationSnapshot.Default;

		public SoundState CreateState()
			=> new TestState();

		public long? GetEndFrameExclusive(
			RenderContext context,
			SoundState state)
		{
			if (!state.NoteOffTime.HasValue)
				return null;

			return checked(
				FrameTime.Ceiling(
					state.NoteOffTime.Value,
					context.Configuration.SampleRate)
				+ 2);
		}

		public void Render(
			RenderContext context,
			SoundState state,
			long startFrame,
			int frameCount,
			Span<float> destination)
		{
			_ = context;
			_ = state;
			_ = startFrame;
			destination.Fill(1.0f);
		}
	}

	private sealed class TestState : SoundState
	{
	}
}

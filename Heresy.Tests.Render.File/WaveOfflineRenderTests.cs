using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Threading;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
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
    public void ReleasedCompletionCutMatchesExplicitCutAtSameFrameIncludingRealWavTail()
    {
        const int rate = 1000;
        ObjectId source = (ObjectId)18001U;
        ObjectId envelopeId = (ObjectId)18002U;
        NodeEnvelopeDefinition authored = new(envelopeId, "ReleaseGate");
        authored.ReplaceCurve([
            new EnvelopeNode(TimeSpan.Zero, 0.4),
            new EnvelopeNode(TimeSpan.FromMilliseconds(10), 0.4),
        ], 0, 0, EnvelopeSustainLoopMode.Forward);
        SongDocument song = new();
        song.Add(authored);
        FilterAssignmentEnvelopeNames names = FilterAssignmentEnvelopeNames.Capture(song);
        IEnvelopeCurve curve = new FiniteReleasedCurve();
        EnvelopeFileResolver resolver =
            new(source, new QuietSound(), envelopeId, curve);
        FilterInstance filter = new(new FilterDefinition((ObjectId)18003U,
            "Echo", [new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward)]));
        filter.SetStageParameters(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(0.002)),
            new("Mix", FilterParameterValue.FromNumber(0)),
            new("Feedforward", FilterParameterValue.FromNumber(0.4)),
        ]);
        PlaybackSession Create(bool automated, bool noCut = false)
        {
            NoteScheduleBuilder builder = new();
            builder.Append(Event(TimeSpan.Zero, new StartNoteCommand(source)
            {
                EventFilterConfiguration = filter,
            }, new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = @\"ReleaseGate\""
                + (automated ? " THEN CUT" : ""))));
            builder.Append(Event(TimeSpan.FromMilliseconds(5),
                new NoteOffCommand()));
            if (!automated && !noCut)
                builder.Append(Event(TimeSpan.FromMilliseconds(7),
                    new NoteCutCommand()));
            PlaybackSession session = Session(rate, builder.Freeze(), resolver);
            session.NoteFilterEnvelopeResolver = resolver;
            session.FilterAssignmentNames = names;
            return session;
        }

        float[] actual = new float[20], explicitCut = new float[20],
            held = new float[20];
        Create(true).Render(0, actual.Length, actual);
        PlaybackSession manual = Create(false);
        manual.Render(0, 6, explicitCut.AsSpan(0, 6));
        manual.Render(6, 14, explicitCut.AsSpan(6, 14));
        Create(false, noCut: true).Render(0, held.Length, held);
        Assert.That(actual, Is.EqualTo(explicitCut).Within(1e-6f),
            "release-end THEN CUT must be sample-identical to explicit Note Cut at frame 7");
        Assert.That(actual.Take(5).Any(x => Math.Abs(x) > 0.05f), Is.True);
        Assert.That(Enumerable.Range(7, 8).Any(i =>
            Math.Abs(held[i] - actual[i]) > 0.001f), Is.True,
            "a no-action control must remain audible beyond the release-end Cut");

        (byte[] Data, OfflineRenderResult Result) Export(bool automated)
        {
            using MemoryStream stream = new();
            using (WaveFileSink sink = new(stream,
                new AudioOutputFormat(rate, 1), leaveOpen: true))
            {
                OfflineRenderResult result = OfflinePlaybackRenderer.Render(
                    Create(automated), TimeSpan.FromMilliseconds(20), sink,
                    blockFrameCount: automated ? 3 : 11);
                sink.Complete();
                return (stream.ToArray(), result);
            }
        }
        var completed = Export(true);
        var reference = Export(false);
        Assert.Multiple(() =>
        {
            Assert.That(completed.Result.TotalFrameCount,
                Is.EqualTo(reference.Result.TotalFrameCount),
                "both cut paths must drain exactly the same release / anti-click tail");
            Assert.That(completed.Data, Is.EqualTo(reference.Data),
                "real encoded WAV, including its header and tail, must be byte-identical");
        });
        for (int frame = 0; frame < actual.Length; frame++)
        {
            short pcm = BinaryPrimitives.ReadInt16LittleEndian(
                completed.Data.AsSpan(44 + 2 * frame, 2));
            Assert.That(pcm / 32768f, Is.EqualTo(actual[frame])
                .Within(2f / 32768f),
                $"release-boundary WAV body frame {frame}");
        }
    }

    private sealed class FiniteReleasedCurve : IEnvelopeCurve
    {
        public double GetValue(long frame, int rate, long? off) => 0.4;
        public long? GetEndActiveFrameExclusiveAfterNoteOff(long off, int rate)
            => off + 2;
    }

    [Test]
    public void ExportedWaveSamplesMatchLiveTextSelectedEnvelopeAtLateSongFrame()
    {
        const int rate = 1000;
        ObjectId sourceId = (ObjectId)17001U;
        ObjectId curveId = (ObjectId)17002U;
        NodeEnvelopeDefinition definition = new(curveId, "Sweep");
        definition.ReplaceCurve([
            new EnvelopeNode(TimeSpan.Zero, 0.2),
            new EnvelopeNode(TimeSpan.FromMilliseconds(10), 0.7),
        ], 0, 0, EnvelopeSustainLoopMode.Forward);
        SongDocument song = new();
        song.Add(definition);
        FilterAssignmentEnvelopeNames names = FilterAssignmentEnvelopeNames.Capture(song);
        FilterInstance selected = new(new FilterDefinition((ObjectId)17003U,
            "Comb", [new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward)]));
        selected.SetStageParameters(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(0.002)),
            new("Feedforward", FilterParameterValue.FromNumber(0.2)),
            new("Mix", FilterParameterValue.FromNumber(1)),
        ]);
        EnvelopeFileResolver resolver =
            new(sourceId, new QuietSound(), curveId, new NodeEnvelopeCurve(definition));
        NoteScheduleBuilder builder = new();
        builder.Append(Event(TimeSpan.Zero, new StartNoteCommand(sourceId) {
            EventFilterConfiguration = selected,
        }));
        builder.Append(Event(TimeSpan.FromMilliseconds(5),
            new SetNoteFilterAssignmentsCommand("echo.Feedforward = @\"Sweep\"")));
        NoteSchedule schedule = builder.Freeze();

        PlaybackSession Create()
            => new(new RenderContext(new RenderConfiguration(rate, [
                    new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
                ])), schedule, resolver)
            {
                NoteFilterEnvelopeResolver = resolver,
                FilterAssignmentNames = names,
            };

        float[] live = new float[15];
        Create().Render(0, live.Length, live);
        using MemoryStream stream = new();
        using (WaveFileSink sink = new(stream,
            new AudioOutputFormat(rate, 1), leaveOpen: true))
        {
            OfflinePlaybackRenderer.Render(Create(),
                TimeSpan.FromMilliseconds(15), sink,
                blockFrameCount: 3);
            sink.Complete();
        }
        byte[] wave = stream.ToArray();
        Assert.That(wave.Length, Is.GreaterThanOrEqualTo(44 + live.Length * 2));
        for (int i = 0; i < live.Length; i++)
        {
            short pcm = BinaryPrimitives.ReadInt16LittleEndian(
                wave.AsSpan(44 + 2 * i, 2));
            float decoded = pcm / 32768f;
            Assert.That(decoded, Is.EqualTo(live[i]).Within(2f / 32768f),
                $"WAV PCM at frame {i} must use the same Envelope clock as live playback");
        }
    }

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
		NodeEnvelopeDefinition envelope = ReleaseEnvelope();
		InfiniteSound sound =
			new(
				new NoteConfigurationSnapshot(
					NewNotePolicy.Cut,
					envelopes:
						new EnvelopeConfigurationSnapshot(
							Volume:
								new NodeEnvelopeCurve(
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
						Volume: new NodeEnvelopeCurve(
							ReleaseEnvelope()))))));
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


    private sealed class QuietSound : ISound
    {
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => NoteConfigurationSnapshot.Default;
        public SoundState CreateState() => new TestState();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state)
            => 15;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
            => destination.Fill(0.25f);
    }

    private sealed class EnvelopeFileResolver(
        ObjectId sourceId, ISound sound, ObjectId envelopeId, IEnvelopeCurve curve)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? resolved)
        {
            resolved = id == sourceId ? sound : null;
            return resolved is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? resolved)
        {
            resolved = id == envelopeId ? curve : null;
            return resolved is not null;
        }
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
	private static NodeEnvelopeDefinition ReleaseEnvelope()
	{
		NodeEnvelopeDefinition node = new((ObjectId)2U, "Release");
		node.ReplaceCurve(
			[
				new EnvelopeNode(TimeSpan.Zero, 1.0),
				new EnvelopeNode(TimeSpan.FromMilliseconds(300), 0.0),
			],
			0, 0, EnvelopeSustainLoopMode.Forward);
		return node;
	}

}

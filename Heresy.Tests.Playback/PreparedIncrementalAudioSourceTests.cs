using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class PreparedIncrementalAudioSourceTests
{
	private static readonly ObjectId SoundId = (ObjectId)90U;

	[Test]
	public void PreparedEventsRenderAtCeilingFramesAcrossArbitraryBlocks()
	{
		TestPattern pattern = new((ObjectId)1U,
			At(0.25, ChannelTarget.Virtual(7),
				new StartNoteCommand(SoundId)) with
			{
				Offset = new MusicalTime(TimeSpan.FromTicks(1000), 0.25),
			},
			At(0.5, ChannelTarget.Physical(0),
				new StartNoteCommand(SoundId)));
		using IncrementalRecursiveTimeline timeline = Timeline(pattern);
		using PreparedIncrementalAudioSource source =
			new(timeline, Session());
		source.PrepareThrough(TimeSpan.FromMilliseconds(110));

		float[] output = new float[105];
		source.Render(17, output.AsSpan(0, 17));
		source.Render(37, output.AsSpan(17, 37));
		source.Render(51, output.AsSpan(54, 51));

		Assert.That(output.Take(31), Is.All.Zero);
		Assert.That(output.Skip(31).Take(29), Is.All.EqualTo(1f));
		Assert.That(output.Skip(60), Is.All.EqualTo(2f));
		Assert.That(source.NextFrame, Is.EqualTo(105));
	}

	[Test]
	public void RenderNeverAdvancesUnpreparedPatternAndRejectsMissingCoverage()
	{
		TestPattern pattern = new((ObjectId)1U,
			At(0.5, ChannelTarget.Virtual(2),
				new StartNoteCommand(SoundId)));
		using IncrementalRecursiveTimeline timeline = Timeline(pattern);
		using PreparedIncrementalAudioSource source =
			new(timeline, Session());

		Assert.Throws<InvalidOperationException>(() =>
			source.Render(10, new float[10]));
		source.PrepareThrough(TimeSpan.FromMilliseconds(20));
		source.Render(20, new float[20]);
		Assert.Throws<InvalidOperationException>(() =>
			source.Render(1, new float[1]));
		source.PrepareThrough(TimeSpan.FromMilliseconds(110));
		float[] result = new float[80];
		source.Render(80, result);
		Assert.That(result.Take(40), Is.All.Zero);
		Assert.That(result.Skip(40), Is.All.EqualTo(1f));
	}

	[Test]
	public void GlobalTempoChangeRetimesLaterNoteWithoutEagerSongSchedule()
	{
		TestPattern pattern = new((ObjectId)1U,
			At(0, ChannelTarget.Global, new SetTempoCommand(250)),
			At(0.5, ChannelTarget.Virtual(2),
				new StartNoteCommand(SoundId)));
		using IncrementalRecursiveTimeline timeline = Timeline(pattern);
		using PreparedIncrementalAudioSource source =
			new(timeline, Session());
		source.PrepareThrough(TimeSpan.FromMilliseconds(100));
		float[] output = new float[80];
		source.Render(80, output);
		Assert.That(output.Take(30), Is.All.Zero);
		Assert.That(output.Skip(30), Is.All.EqualTo(1f));
	}

	[Test]
	public void SameVirtualIdInTwoRootsRemainsScopedDuringSubframeBroadcast()
	{
		TestPattern first = new((ObjectId)1U,
			At(0, ChannelTarget.Virtual(7), new StartNoteCommand(SoundId)),
			At(0, ChannelTarget.AllVirtualInScope, new NoteCutCommand()),
			At(0.5, ChannelTarget.AllVirtualInScope,
				new NoteCutCommand()));
		TestPattern second = new((ObjectId)2U,
			At(0, ChannelTarget.Virtual(7), new StartNoteCommand(SoundId)));
		using IncrementalRecursiveTimeline timeline =
			new(new SequencingContext(), new Resolver(first, second));
		timeline.AddRoot(first.Id);
		timeline.AddRoot(second.Id);
		using PreparedIncrementalAudioSource source =
			new(timeline, Session());
		source.PrepareThrough(TimeSpan.FromMilliseconds(110));
		float[] output = new float[90];
		source.Render(90, output);
		Assert.That(output[0], Is.EqualTo(2f));
		Assert.That(output[59], Is.EqualTo(2f));
		// A scoped NoteCut preserves one anti-click sample at frame 60.
		Assert.That(output[60], Is.EqualTo(2f));
		Assert.That(output[61],
			Is.EqualTo(1f + (float)AntiClickTail.CalculateDecay(1000))
				.Within(1e-6f));
		Assert.That(output[70], Is.EqualTo(1f).Within(1e-5f));
	}

	private static NoteEvent At(double row, ChannelTarget target,
		params NoteCommand[] commands)
		=> new(new MusicalTime(TimeSpan.Zero, row), target, commands);

	private static IncrementalRecursiveTimeline Timeline(TestPattern source)
	{
		IncrementalRecursiveTimeline timeline =
			new(new SequencingContext(), new Resolver(source));
		timeline.AddRoot(source.Id);
		return timeline;
	}

	private static PlaybackSession Session()
		=> new(new RenderContext(new RenderConfiguration(1000,
				[new OutputChannelConfiguration(
					Vector3.Zero, positionalImportance: 0.0)])),
			new NoteScheduleBuilder().Freeze(),
			new SoundResolver());

	private sealed class TestPattern : PatternDefinition,
		IIncrementalRawPatternNoteGenerator
	{
		private readonly NoteEvent[] _events;

		public TestPattern(ObjectId id, params NoteEvent[] events)
			: base(id, "Test")
		{
			RowCount = 1;
			ChannelCount = 1;
			_events = events;
		}

		public IEnumerable<RawPatternStep> EnumerateRawSteps(
			SequencingContext context)
		{
			foreach (NoteEvent e in _events)
				yield return new RawPatternStep.Emit(e);
		}
	}

	private sealed class Resolver(params SongObject[] objects)
		: IIncrementalInvocationResolver
	{
		private readonly Dictionary<ObjectId, SongObject> _objects =
			objects.ToDictionary(x => x.Id);

		public bool TryResolve(ObjectId id, out SongObject? value)
			=> _objects.TryGetValue(id, out value);
	}

	private sealed class SoundResolver : ISoundResolver
	{
		public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
		{
			sound = id == SoundId && !mixdown ? new ConstantSound() : null;
			return sound is not null;
		}
	}

	private sealed class ConstantSound : ISound
	{
		public NoteConfigurationSnapshot SnapshotNoteConfiguration()
			=> NoteConfigurationSnapshot.Default;

		public SoundState CreateState() => new ConstantState();

		public long? GetEndFrameExclusive(
			RenderContext context, SoundState state) => null;

		public void Render(RenderContext context, SoundState state,
			long startFrame, int frameCount, Span<float> destination)
		{
			for (int i = 0; i < frameCount; i++)
				destination[i] += 1f;
		}
	}

	private sealed class ConstantState : SoundState { }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

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
public sealed class AsyncPreparedIncrementalAudioSourceTests
{
	private static readonly ObjectId SoundId = (ObjectId)91U;

	[Test]
	public void EmptyLookaheadReturnsSilenceWithoutSkippingMusicAndRecovers()
	{
		using ManualResetEventSlim release = new(false);
		using IncrementalRecursiveTimeline timeline = Timeline(
			new BlockingPattern((ObjectId)1U, release));
		using PreparedIncrementalAudioSource prepared = new(timeline, Session());
		using AsyncPreparedIncrementalAudioSource asyncSource = new(prepared, 40);
		float[] missing = new float[10];
		asyncSource.Render(10, missing);
		Assert.That(missing, Is.All.Zero);
		Assert.That(prepared.NextFrame, Is.Zero);
		Assert.That(asyncSource.UnderrunCount, Is.EqualTo(1));
		try
		{
			release.Set();
			Assert.That(SpinWait.SpinUntil(
				() => prepared.IsPreparedToEnd
					|| prepared.PreparedThrough >= TimeSpan.FromMilliseconds(40),
				TimeSpan.FromSeconds(5)), Is.True);
			float[] result = new float[10];
			asyncSource.Render(10, result);
			Assert.That(result, Is.All.EqualTo(1f));
			Assert.That(prepared.NextFrame, Is.EqualTo(10));
			Assert.That(asyncSource.UnderrunCount, Is.EqualTo(1));
		}
		finally
		{
			release.Set();
		}
	}

	[Test]
	public void WorkerPreparesBoundedHorizonAndUpdatesAfterConsumption()
	{
		using IncrementalRecursiveTimeline timeline = Timeline(
			new BlockingPattern((ObjectId)2U, null));
		using PreparedIncrementalAudioSource prepared = new(timeline, Session());
		using AsyncPreparedIncrementalAudioSource asyncSource = new(prepared, 32);
		Assert.That(SpinWait.SpinUntil(
			() => prepared.PreparedThrough >= TimeSpan.FromMilliseconds(32),
			TimeSpan.FromSeconds(5)), Is.True);
		Assert.That(prepared.PreparedThrough,
			Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(32)));
		asyncSource.Render(16, new float[16]);
		Assert.That(SpinWait.SpinUntil(
			() => prepared.PreparedThrough >= TimeSpan.FromMilliseconds(48),
			TimeSpan.FromSeconds(5)), Is.True);
		Assert.That(prepared.PreparedThrough,
			Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(48)));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			asyncSource.Render(33, new float[33]));
	}

	[Test]
	public void WorkerFailureIsObservableWithoutThrowingFromRenderCallback()
	{
		using IncrementalRecursiveTimeline timeline = Timeline(
			new BlockingPattern((ObjectId)3U, null, fail: true));
		using PreparedIncrementalAudioSource prepared = new(timeline, Session());
		using AsyncPreparedIncrementalAudioSource asyncSource = new(prepared, 32);
		Assert.That(SpinWait.SpinUntil(
			() => asyncSource.PreparationError is not null,
			TimeSpan.FromSeconds(5)), Is.True);
		Assert.That(asyncSource.PreparationError,
			Is.TypeOf<InvalidOperationException>());
		Assert.DoesNotThrow(() => asyncSource.Render(10, new float[10]));
		Assert.That(asyncSource.UnderrunCount, Is.EqualTo(1));
		Assert.That(prepared.NextFrame, Is.Zero);
	}

	[Test]
	public void PlanOwnsLookaheadLifetimeAndPreventsDuplicateWorkers()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(patternId, "Silent")
		{
			RowCount = 1, ChannelCount = 1,
			Source = "Cut(0, 0);",
		});
		using PreparedIncrementalPlaybackPlan plan =
			new PreparedIncrementalPlaybackFactory(new RenderConfiguration(
				1000, [new OutputChannelConfiguration(
					Vector3.Zero, positionalImportance: 0.0)]))
			.Create(document, patternId);
		using AsyncPreparedIncrementalAudioSource asyncSource =
			plan.StartLookahead(32);
		Assert.Throws<InvalidOperationException>(() => plan.StartLookahead(32));
		Assert.That(SpinWait.SpinUntil(
			() => plan.Source.IsPreparedToEnd,
			TimeSpan.FromSeconds(5)), Is.True);
		float[] output = new float[8];
		asyncSource.Render(8, output);
		Assert.That(output, Is.All.Zero);
		Assert.That(asyncSource.UnderrunCount, Is.Zero);
	}

	private static IncrementalRecursiveTimeline Timeline(BlockingPattern pattern)
	{
		IncrementalRecursiveTimeline timeline =
			new(new SequencingContext(), new Resolver(pattern));
		timeline.AddRoot(pattern.Id);
		return timeline;
	}

	private static PlaybackSession Session()
		=> new(new RenderContext(new RenderConfiguration(1000,
				[new OutputChannelConfiguration(
					Vector3.Zero, positionalImportance: 0.0)])),
			new NoteScheduleBuilder().Freeze(), new Sounds());

	private sealed class BlockingPattern : PatternDefinition,
		IIncrementalRawPatternNoteGenerator
	{
		private readonly ManualResetEventSlim? _release;
		private readonly bool _fail;

		public BlockingPattern(ObjectId id, ManualResetEventSlim? release,
			bool fail = false) : base(id, "Controlled")
		{
			RowCount = 1;
			ChannelCount = 1;
			_release = release;
			_fail = fail;
		}

		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			_release?.Wait();
			if (_fail)
				throw new InvalidOperationException("Invalid producer");
			yield return new RawPatternStep.Emit(
				new NoteEvent(new MusicalTime(TimeSpan.Zero, 0),
					ChannelTarget.Virtual(7),
					[new StartNoteCommand(SoundId)]));
		}
	}

	private sealed class Resolver(BlockingPattern pattern)
		: IIncrementalInvocationResolver
	{
		public bool TryResolve(ObjectId id, out SongObject? source)
		{
			source = id == pattern.Id ? pattern : null;
			return source is not null;
		}
	}

	private sealed class Sounds : ISoundResolver
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

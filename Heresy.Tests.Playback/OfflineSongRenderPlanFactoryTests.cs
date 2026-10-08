using System;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class OfflineSongRenderPlanFactoryTests
{
	[Test]
	public void RequiresRootSequence()
	{
		SongDocument document = new();
		OfflineSongRenderPlanFactory factory =
			new(
				RenderConfiguration.Stereo(
					sampleRate: 48000));

		Action action = () =>
			factory.Create(document);

		action.Should()
			.Throw<InvalidOperationException>()
			.WithMessage("*root sequence*");
	}

	[Test]
	public void CreatesImmutableRootSequencePlanWithCompiledDuration()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(
				patternId,
				"Pattern")
			{
				RowCount = 2,
				ChannelCount = 1,
			};
		document.Add(pattern);

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(
				sequenceId,
				"Song");
		sequence.Entries.Add(
			new SequenceEntry(patternId));
		document.Add(sequence);
		document.RootSequenceId = sequenceId;
		document.MarkChanged(
			affectsAudio: true);

		OfflineSongRenderPlanFactory factory =
			new(
				RenderConfiguration.Stereo(
					sampleRate: 48000));

		OfflineSongRenderPlan plan =
			factory.Create(document);

		plan.Snapshot.Document.Should()
			.NotBeSameAs(document);
		plan.Snapshot.Document.RootSequenceId
			.Should().Be(sequenceId);
		plan.LogicalDuration.Should()
			.Be(TimeSpan.FromMilliseconds(240));
		plan.Session.SampleRate.Should().Be(48000);
		plan.Session.OutputChannelCount.Should().Be(2);

		pattern.RowCount = 8;
		document.MarkChanged(
			affectsAudio: true);

		plan.Snapshot.Document.TryGet(
				patternId,
				out SongObject? snapshotObject)
			.Should().BeTrue();
		snapshotObject.Should()
			.BeOfType<DataPatternDefinition>()
			.Which.RowCount.Should().Be(2);
		plan.LogicalDuration.Should()
			.Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void FlattenedChildExtendsOfflineLogicalDurationBeyondContainingPattern()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Long child")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Short parent")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = parent.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = childId;
		cell.Note = new StartPatternNote();
		document.Add(parent);

		ObjectId rootId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(rootId, "Song");
		sequence.Entries.Add(new SequenceEntry(parentId));
		document.Add(sequence);
		document.RootSequenceId = rootId;

		OfflineSongRenderPlanFactory factory = new(
			RenderConfiguration.Stereo(sampleRate: 48000));
		OfflineSongRenderPlan plan = factory.Create(document);

		plan.LogicalDuration.Should().Be(TimeSpan.FromMilliseconds(360));
	}

	[Test]
	public void ThirdEncounterWithSameBxxEndsOfflineArrangement()
	{
		SongDocument document = new();

		ObjectId patternId =
			document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(
				patternId,
				"Loop")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		document.Add(pattern);

		ObjectId sequenceId =
			document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(
				sequenceId,
				"Song");
		sequence.Entries.Add(
			new SequenceEntry(patternId));
		document.Add(sequence);
		document.RootSequenceId = sequenceId;
		document.MarkChanged(
			affectsAudio: true);

		OfflineSongRenderPlanFactory factory =
			new(
				RenderConfiguration.Stereo(
					sampleRate: 48000));

		OfflineSongRenderPlan plan =
			factory.Create(document);

		plan.LogicalDuration.Should()
			.Be(TimeSpan.FromMilliseconds(360));
	}

	[Test]
	public void CompilationFailureUsesPlaybackCompilationException()
	{
		SongDocument document = new();
		document.RootSequenceId =
			(ObjectId)999U;
		document.MarkChanged(
			affectsAudio: true);

		OfflineSongRenderPlanFactory factory =
			new(
				RenderConfiguration.Stereo(
					sampleRate: 48000));

		Action action = () =>
			factory.Create(document);

		action.Should()
			.Throw<PlaybackSourceCompilationException>();
	}
}

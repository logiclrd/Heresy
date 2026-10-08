using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class IncrementalPatternSlideFamiliesTests
{
	[Test]
	public void ChannelGlobalAndPanningSlidesFreezeAtTheirOwnRowBoundary()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Slide families")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerChannelVolumeSlidePatternEffect(0x04));
		cell.Effects.Add(new TrackerGlobalVolumeSlidePatternEffect(0x40));
		cell.Effects.Add(new TrackerPanningSlidePatternEffect(0x04));

		NoteScheduleBuilder eager = new();
		PatternNoteProcessor.GenerateNotes(pattern,
			new SequencingContext { ResolvePatternSourcesAtRowTime = true }, eager, out _);
		NoteEvent[] baseline = eager.Freeze().ToArray();

		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(pattern, pattern.RowCount, context);
		NoteEvent[] actual = Drain(timeline);

		Assert.That(actual.Length, Is.EqualTo(2));
		for (int i = 0; i < actual.Length; i++)
		{
			Assert.That(actual[i].Offset.TimeOffset,
				Is.EqualTo(baseline[i].Offset.TimeOffset));
			Assert.That(actual[i].Commands, Is.EqualTo(baseline[i].Commands));
		}
		Assert.That(actual[0].Commands.OfType<SetOverallChannelVolumeSlideCommand>(),
			Has.Exactly(1).Items);
		Assert.That(actual[0].Commands.OfType<SetGlobalVolumeSlideCommand>(),
			Has.Exactly(1).Items);
		Assert.That(actual[0].Commands.OfType<SetSpatialXSlideCommand>(),
			Has.Exactly(1).Items);
		Assert.That(actual[1].Commands, Does.Contain(new ClearOverallChannelVolumeSlideCommand()));
		Assert.That(actual[1].Commands, Does.Contain(new ClearGlobalVolumeSlideCommand()));
		Assert.That(actual[1].Commands, Does.Contain(new ClearSpatialXSlideCommand()));
		Assert.That(actual[1].Offset.TimeOffset, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void FineChannelAndGlobalSlidesDoNotScheduleRowEndClear()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Fine slides")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TrackerChannelVolumeSlidePatternEffect(0x2F));
		cell.Effects.Add(new TrackerGlobalVolumeSlidePatternEffect(0xF2));
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(pattern, pattern.RowCount, context);

		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual, Has.Length.EqualTo(1));
		Assert.That(actual[0].Commands.OfType<AdjustOverallChannelVolumeCommand>(),
			Has.Exactly(1).Items);
		Assert.That(actual[0].Commands.OfType<AdjustGlobalVolumeCommand>(),
			Has.Exactly(1).Items);
		Assert.That(actual.SelectMany(e => e.Commands).Any(c =>
			c is ClearOverallChannelVolumeSlideCommand or ClearGlobalVolumeSlideCommand),
			Is.False);
	}

	[Test]
	public void GlobalVolumeSlideMemoryIsSharedByMappedChildAndParent()
	{
		SequencingContext root = new();
		DataPatternDefinition parent = new((ObjectId)1U, "Wxy")
		{
			RowCount = 1, ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x40));

		DataPatternDefinition child = new((ObjectId)2U, "W00")
		{
			RowCount = 1, ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerGlobalVolumeSlidePatternEffect(0x00));

		using IncrementalPatternTimeline timeline = new(root);
		timeline.Add(parent, parent.RowCount, root);
		List<NoteEvent> emitted = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
		{
			if (step is not IncrementalPatternTimelineStep.Emit e)
				continue;
			emitted.Add(e.Note);
			if (e.Note.Commands.OfType<SetGlobalVolumeSlideCommand>().Any()
				&& emitted.Count == 1)
				timeline.Add(child, child.RowCount, root.FlattenedChild());
		}

		SetGlobalVolumeSlideCommand[] slides = emitted
			.SelectMany(e => e.Commands).OfType<SetGlobalVolumeSlideCommand>().ToArray();
		Assert.That(slides, Has.Length.EqualTo(2));
		Assert.That(slides[0].TrackerUnitsPerTick, Is.EqualTo(4));
		Assert.That(slides[1].TrackerUnitsPerTick, Is.EqualTo(4));
		Assert.That(emitted.SelectMany(e => e.Commands).OfType<ClearGlobalVolumeSlideCommand>().Count(),
			Is.EqualTo(2));
	}

	private static NoteEvent[] Drain(IncrementalPatternTimeline timeline)
	{
		List<NoteEvent> events = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				events.Add(emit.Note);
		return events.ToArray();
	}
}

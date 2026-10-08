using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class IncrementalPatternNoteProcessorTests
{
	[Test]
	public void AdvancingOneRowDoesNotApplyFutureTempoSpeedOrSourceSelection()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Deferred")
		{
			RowCount = 4,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		PatternCell future = pattern.Grid.GetOrCreateCell(2, 0);
		future.SourceId = (ObjectId)42U;
		future.Effects.Add(new SetTempoPatternEffect(250));
		future.Effects.Add(new SetSpeedPatternEffect(3));
		SequencingContext context = new();

		using IncrementalPatternNoteProcessor cursor =
			new(pattern, context, pattern.RowCount);
		Assert.That(context.State.Tempo, Is.EqualTo(125));
		Assert.That(context.State.Speed, Is.EqualTo(6));
		Assert.That(cursor.TryAdvance(out IncrementalPatternRow? first), Is.True);
		Assert.That(first!.Row, Is.EqualTo(0));
		Assert.That(first.Events.Count, Is.EqualTo(1));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(context.GetPhysicalChannelState(0).CurrentSourceId,
			Is.EqualTo(ObjectId.None));
		Assert.That(context.State.Tempo, Is.EqualTo(125));
		Assert.That(context.State.Speed, Is.EqualTo(6));

		Assert.That(cursor.TryAdvance(out _), Is.True);
		Assert.That(context.State.Tempo, Is.EqualTo(125));
		Assert.That(context.State.Speed, Is.EqualTo(6));

		Assert.That(cursor.TryAdvance(out IncrementalPatternRow? third), Is.True);
		Assert.That(third!.Row, Is.EqualTo(2));
		Assert.That(context.State.Tempo, Is.EqualTo(250));
		Assert.That(context.State.Speed, Is.EqualTo(3));
		Assert.That(context.GetPhysicalChannelState(0).CurrentSourceId,
			Is.EqualTo((ObjectId)42U));
		Assert.That(third.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(30)));
	}

	[Test]
	public void SimpleRowsMatchEagerPatternProcessorTimingAndCommandOrder()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Parity")
		{
			RowCount = 3,
			ChannelCount = 2,
		};
		PatternCell first = pattern.Grid.GetOrCreateCell(0, 1);
		first.Note = new PatternNoteCut();
		first.Effects.Add(new SetTempoPatternEffect(250));
		PatternCell next = pattern.Grid.GetOrCreateCell(1, 0);
		next.Note = new PatternNoteOff();
		next.Effects.Add(new SetSpeedPatternEffect(3));
		pattern.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteCut();

		SequencingContext eagerContext = new() { ResolvePatternSourcesAtRowTime = true };
		NoteScheduleBuilder eagerOutput = new();
		PatternNoteProcessor.GenerateNotes(pattern, eagerContext, eagerOutput,
			out TimeSpan eagerDuration);
		NoteEvent[] eagerEvents = eagerOutput.Freeze().ToArray();

		SequencingContext incrementalContext = new();
		List<NoteEvent> actual = [];
		using IncrementalPatternNoteProcessor cursor =
			new(pattern, incrementalContext, pattern.RowCount);
		while (cursor.TryAdvance(out IncrementalPatternRow? row))
			actual.AddRange(row!.Events);

		Assert.That(actual.Count, Is.EqualTo(eagerEvents.Length));
		for (int i = 0; i < actual.Count; i++)
		{
			Assert.That(actual[i].Offset.TimeOffset,
				Is.EqualTo(eagerEvents[i].Offset.TimeOffset));
			Assert.That(actual[i].Target, Is.EqualTo(eagerEvents[i].Target));
			Assert.That(actual[i].Commands, Is.EqualTo(eagerEvents[i].Commands));
		}
		Assert.That(cursor.Elapsed, Is.EqualTo(eagerDuration));
		Assert.That(incrementalContext.State.Tempo, Is.EqualTo(eagerContext.State.Tempo));
		Assert.That(incrementalContext.State.Speed, Is.EqualTo(eagerContext.State.Speed));
		Assert.That(cursor.TryAdvance(out _), Is.False);
	}

	[Test]
	public void TempoChangedExternallyBetweenRowsChangesOnlyFutureTiming()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Shared tempo")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		SequencingContext context = new();
		using IncrementalPatternNoteProcessor cursor =
			new(pattern, context, pattern.RowCount);

		Assert.That(cursor.TryAdvance(out IncrementalPatternRow? first), Is.True);
		Assert.That(first!.Events, Is.Empty);
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));

		context.State.Tempo = 250;
		Assert.That(cursor.TryAdvance(out IncrementalPatternRow? second), Is.True);
		Assert.That(second!.Events.Single().Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(second.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(60)));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(180)));
		Assert.That(cursor.TryAdvance(out _), Is.True);
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
	}

	[Test]
	public void StartRowSkipsPriorSourceSelectionAndTimingCommands()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Skip")
		{
			RowCount = 4,
			ChannelCount = 1,
		};
		PatternCell skipped = pattern.Grid.GetOrCreateCell(0, 0);
		skipped.SourceId = (ObjectId)12U;
		skipped.Effects.Add(new SetTempoPatternEffect(250));
		pattern.Grid.GetOrCreateCell(2, 0).Note = new StartPatternNote();
		SequencingContext context = new();

		using IncrementalPatternNoteProcessor cursor =
			new(pattern, context, pattern.RowCount, startRow: 2);
		Assert.That(cursor.TryAdvance(out IncrementalPatternRow? row), Is.True);
		Assert.That(row!.Row, Is.EqualTo(2));
		Assert.That(row.Events, Is.Empty);
		Assert.That(context.State.Tempo, Is.EqualTo(125));
		Assert.That(context.GetPhysicalChannelState(0).CurrentSourceId,
			Is.EqualTo(ObjectId.None));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(cursor.TryAdvance(out _), Is.True);
		Assert.That(cursor.TryAdvance(out _), Is.False);
	}

	[Test]
	public void EmptyLongPatternCanBeAdvancedIncrementallyAndDisposedEarly()
	{
		DataPatternDefinition pattern = new((ObjectId)1U, "Silent")
		{
			RowCount = 10000,
			ChannelCount = 1,
		};
		using IncrementalPatternNoteProcessor cursor =
			new(pattern, new SequencingContext(), pattern.RowCount);
		for (int i = 0; i < 4; i++)
		{
			Assert.That(cursor.TryAdvance(out IncrementalPatternRow? row), Is.True);
			Assert.That(row!.Row, Is.EqualTo(i));
			Assert.That(row.Events, Is.Empty);
		}
		Assert.That(cursor.NextRow, Is.EqualTo(4));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(480)));
	}
}

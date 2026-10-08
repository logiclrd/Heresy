using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class IncrementalScriptSequenceCursorTests
{
	[Test]
	public void EntriesAreRequestedOnlyWhenTheirPredecessorCompletes()
	{
		DataPatternDefinition first = Pattern(11, 2);
		first.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		DataPatternDefinition second = Pattern(12, 1);
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		TrackingGenerator source = new(
			new RawSequenceStep.Play(new SequenceEntry(first.Id)),
			new RawSequenceStep.Play(new SequenceEntry(second.Id)));
		using IncrementalSequenceCursor cursor = new(
			source, new Resolver(first, second), new SequencingContext());

		Assert.That(cursor.TryStep(out IncrementalPatternTimelineStep? step), Is.True);
		Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Emit>());
		Assert.That(source.Yields, Is.EqualTo(1),
			"The next Play must not execute while the current Pattern is active.");
		List<NoteEvent> remaining = Drain(cursor);
		Assert.That(remaining, Has.Count.EqualTo(1));
		Assert.That(remaining[0].Offset.TimeOffset,
			Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(source.Yields, Is.EqualTo(2));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(360)));
	}

	[Test]
	public void CpuCooperationDoesNotChangeMusicalTimeOrStartNextOrder()
	{
		DataPatternDefinition pattern = Pattern(21, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		TrackingGenerator source = new(
			new RawSequenceStep.Cooperate(),
			new RawSequenceStep.Cooperate(),
			new RawSequenceStep.Play(new SequenceEntry(pattern.Id)));
		using IncrementalSequenceCursor cursor = new(
			source, new Resolver(pattern), new SequencingContext());

		for (int i = 0; i < 2; i++)
		{
			Assert.That(cursor.TryStep(out IncrementalPatternTimelineStep? step), Is.True);
			Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Cooperate>());
			Assert.That(cursor.Tick, Is.Zero);
			Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.Zero));
		}
		Assert.That(Drain(cursor).Single().Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
	}

	[Test]
	public void BxxReusesGeneratedOrderWithoutExecutingScriptTail()
	{
		DataPatternDefinition loop = Pattern(31, 1);
		loop.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		loop.Grid.GetOrCreateCell(0, 0).Effects.Add(new TrackerOrderJumpPatternEffect(0));
		TrackingGenerator source = new(
			new RawSequenceStep.Play(new SequenceEntry(loop.Id)),
			new RawSequenceStep.Play(new SequenceEntry((ObjectId)999U)));
		int jumps = 0;
		using IncrementalSequenceCursor cursor = new(
			source, new Resolver(loop), new SequencingContext(),
			shouldFollowOrderJump: _ => ++jumps < 3);

		Assert.That(Drain(cursor), Has.Count.EqualTo(3));
		Assert.That(source.Yields, Is.EqualTo(1),
			"B00 revisits the cached Play without advancing the script.");
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(360)));
	}

	[Test]
	public void CancellingSequenceDisposesItsSuspendedSource()
	{
		TrackingGenerator source = new(
			new RawSequenceStep.Cooperate(), new RawSequenceStep.Cooperate());
		IncrementalSequenceCursor cursor = new(
			source, new Resolver(), new SequencingContext());
		Assert.That(cursor.TryStep(out _), Is.True);
		cursor.Dispose();
		Assert.That(source.Disposed, Is.True);
	}

	private static DataPatternDefinition Pattern(uint id, int rows)
		=> new((ObjectId)id, "Pattern") { RowCount = rows, ChannelCount = 1 };

	private static List<NoteEvent> Drain(IncrementalSequenceCursor cursor)
	{
		List<NoteEvent> notes = [];
		while (cursor.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				notes.Add(emit.Note);
		return notes;
	}

	private sealed class Resolver(params DataPatternDefinition[] patterns)
		: ISequencePatternResolver
	{
		private readonly Dictionary<ObjectId, IRawPatternNoteGenerator> _patterns =
			patterns.ToDictionary(p => p.Id, p => (IRawPatternNoteGenerator)p);
		public bool TryResolve(ObjectId id, out IRawPatternNoteGenerator? pattern)
			=> _patterns.TryGetValue(id, out pattern);
	}

	private sealed class TrackingGenerator(params RawSequenceStep[] steps)
		: IIncrementalRawSequenceEntryGenerator
	{
		public int Yields { get; private set; }
		public bool Disposed { get; private set; }
		public IEnumerable<RawSequenceStep> EnumerateRawSteps(SequencingContext context)
		{
			try
			{
				foreach (RawSequenceStep step in steps)
				{
					Yields++;
					yield return step;
				}
			}
			finally
			{
				Disposed = true;
			}
		}
	}
}

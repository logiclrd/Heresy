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
	public void LookupIsDeferredUntilItsPatternCompletes()
	{
		DataPatternDefinition first = Pattern(11, 2);
		first.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		DataPatternDefinition second = Pattern(12, 1);
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		RecordingLookup source = new((absolute, order, previous) =>
			order switch
			{
				0 => new SequenceEntry(first.Id),
				1 => new SequenceEntry(second.Id),
				_ => null,
			});
		using IncrementalSequenceCursor cursor = new(
			source, new Resolver(first, second), new SequencingContext());

		Assert.That(cursor.TryStep(out IncrementalPatternTimelineStep? step), Is.True);
		Assert.That(step, Is.TypeOf<IncrementalPatternTimelineStep.Emit>());
		Assert.That(source.Calls, Has.Count.EqualTo(1));
		Assert.That(Drain(cursor), Has.Count.EqualTo(1));
		Assert.That(source.Calls,
			Is.EqualTo(new[] { (0, 0, -1), (1, 1, 0), (2, 2, 1) }));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(360)));
	}

	[Test]
	public void BxxCanChooseDifferentPatternForSameIndexOnEachVisit()
	{
		DataPatternDefinition first = Pattern(31, 1);
		first.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		DataPatternDefinition second = Pattern(32, 1);
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		second.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		RecordingLookup source = new((absolute, order, previous) =>
			new SequenceEntry(absolute % 2 == 0 ? first.Id : second.Id));
		int jumps = 0;
		using IncrementalSequenceCursor cursor = new(
			source, new Resolver(first, second), new SequencingContext(),
			shouldFollowOrderJump: _ => ++jumps < 4);

		List<NoteEvent> notes = Drain(cursor);
		Assert.That(notes.Select(x => x.Commands.Single().GetType()), Is.EqualTo(
			new[] { typeof(NoteCutCommand), typeof(NoteOffCommand),
				typeof(NoteCutCommand), typeof(NoteOffCommand) }));
		Assert.That(source.Calls, Is.EqualTo(
			new[] { (0, 0, -1), (1, 0, 0), (2, 0, 0), (3, 0, 0) }));
		Assert.That(cursor.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(480)));
	}

	[Test]
	public void NullEntryEndsSequenceWithoutAnotherOrder()
	{
		RecordingLookup source = new((absolute, order, previous) => null);
		using IncrementalSequenceCursor cursor = new(
			source, new Resolver(), new SequencingContext(), startOrder: 7);
		Assert.That(cursor.TryStep(out _), Is.False);
		Assert.That(cursor.IsComplete, Is.True);
		Assert.That(source.Calls, Is.EqualTo(new[] { (0, 7, -1) }));
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

	private sealed class RecordingLookup(
		Func<int, int, int, SequenceEntry?> resolve) : ISequenceEntryProvider
	{
		public List<(int, int, int)> Calls { get; } = [];
		public SequenceEntry? GetSequenceEntry(
			int absoluteIndex, int sequenceIndex, int previousSequenceIndex)
		{
			Calls.Add((absoluteIndex, sequenceIndex, previousSequenceIndex));
			return resolve(absoluteIndex, sequenceIndex, previousSequenceIndex);
		}
	}
}

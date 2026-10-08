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
public sealed class SequenceEntryLookupTests
{
	[Test]
	public void DataLookupIgnoresVisitHistoryAndEndsOutOfRange()
	{
		DataSequenceDefinition sequence = new((ObjectId)1U, "Data");
		sequence.Entries.Add(new SequenceEntry((ObjectId)12U, 2));
		Assert.That(sequence.GetSequenceEntry(0, 0, -1),
			Is.EqualTo(new SequenceEntry((ObjectId)12U, 2)));
		Assert.That(sequence.GetSequenceEntry(100, 0, 6),
			Is.EqualTo(new SequenceEntry((ObjectId)12U, 2)));
		Assert.That(sequence.GetSequenceEntry(101, 1, 0), Is.Null);
	}

	[Test]
	public void EagerSequenceProcessorCallsScriptLookupForEveryBxxVisit()
	{
		DataPatternDefinition pattern = new((ObjectId)17U, "Loop")
			{ RowCount = 1, ChannelCount = 1 };
		pattern.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		RecordingLookup lookup = new(pattern.Id);
		NoteScheduleBuilder output = new();
		int jumps = 0;
		SequenceNoteProcessor.GenerateNotes(lookup, new Resolver(pattern),
			new SequencingContext(), output, 0, null, out TimeSpan duration,
			shouldFollowOrderJump: _ => ++jumps < 3);

		Assert.That(lookup.Calls, Is.EqualTo(new[] {
			(0, 0, -1), (1, 0, 0), (2, 0, 0),
		}));
		Assert.That(output.Freeze().Count, Is.EqualTo(3));
		Assert.That(duration, Is.EqualTo(TimeSpan.FromMilliseconds(360)));
	}

	[Test]
	public void NullFromLookupTerminatesEvenIfOrderIndexIsNonzero()
	{
		RecordingLookup lookup = new((ObjectId)12U, returnNull: true);
		NoteScheduleBuilder output = new();
		SequenceNoteProcessor.GenerateNotes(lookup, new Resolver(),
			new SequencingContext(), output, 8, null, out TimeSpan duration);
		Assert.That(duration, Is.EqualTo(TimeSpan.Zero));
		Assert.That(lookup.Calls, Is.EqualTo(new[] { (0, 8, -1) }));
	}

	private sealed class RecordingLookup(ObjectId id, bool returnNull = false)
		: ISequenceEntryProvider
	{
		public List<(int, int, int)> Calls { get; } = [];
		public SequenceEntry? GetSequenceEntry(
			int absoluteIndex, int sequenceIndex, int previousSequenceIndex)
		{
			Calls.Add((absoluteIndex, sequenceIndex, previousSequenceIndex));
			return returnNull ? null : new SequenceEntry(id);
		}
	}

	private sealed class Resolver(params DataPatternDefinition[] patterns)
		: ISequencePatternResolver
	{
		private readonly Dictionary<ObjectId, IRawPatternNoteGenerator> _patterns =
			patterns.ToDictionary(p => p.Id, p => (IRawPatternNoteGenerator)p);
		public bool TryResolve(ObjectId id, out IRawPatternNoteGenerator? pattern)
			=> _patterns.TryGetValue(id, out pattern);
	}
}

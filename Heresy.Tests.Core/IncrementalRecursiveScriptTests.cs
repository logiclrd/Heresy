using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class IncrementalRecursiveScriptTests
{
	[Test]
	public void ScriptedPatternChildChangesSharedTempoBeforeParentNextRow()
	{
		DataPatternDefinition parent = Pattern(1, 2);
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote((ObjectId)2U);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		ScriptPatternDefinition child = new((ObjectId)2U, "Script") { RowCount = 1 };
		TrackingCompiler compiler = new();
		compiler.Patterns[child.Id] = new RawPattern(
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Global,
				[new SetTempoCommand(250)]),
			new NoteEvent(MusicalTime.Zero, ChannelTarget.Physical(1),
				[new NoteCutCommand()]));
		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, child), compiler);
		timeline.AddRoot(parent.Id);

		NoteEvent[] output = Drain(timeline);
		Assert.That(output.Select(x => x.Offset.TimeOffset),
			Is.EqualTo(new[] { TimeSpan.Zero, TimeSpan.Zero,
				TimeSpan.FromMilliseconds(60) }));
		Assert.That(output.Last().Commands.Single(), Is.TypeOf<NoteOffCommand>());
		Assert.That(context.State.Tempo, Is.EqualTo(250));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(compiler.PatternCalls, Is.EqualTo(1));
	}

	[Test]
	public void RecursiveScriptedSequenceLooksUpOnlyAtOrderVisits()
	{
		ScriptSequenceDefinition sequence = new((ObjectId)10U, "Script");
		DataPatternDefinition first = Pattern(11, 1);
		first.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		DataPatternDefinition second = Pattern(12, 1);
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		Lookup provider = new((abs, index, previous) => index switch
		{
			0 => new SequenceEntry(first.Id),
			1 => new SequenceEntry(second.Id),
			_ => null,
		});
		TrackingCompiler compiler = new();
		compiler.Sequences[sequence.Id] = new Factory(provider);
		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(sequence, first, second), compiler);
		timeline.AddRoot(sequence.Id);
		NoteEvent[] actual = Drain(timeline);
		Assert.That(actual.Select(e => e.Offset.TimeOffset), Is.EqualTo(
			new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(120) }));
		Assert.That(provider.Calls, Is.EqualTo(
			new[] { (0, 0, -1), (1, 1, 0), (2, 2, 1) }));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(240)));
		Assert.That(compiler.SequenceCalls, Is.EqualTo(1));
	}

	[Test]
	public void RecursiveBxxRequeriesSameOrderAndCanChangePattern()
	{
		ScriptSequenceDefinition sequence = new((ObjectId)20U, "Repeated");
		DataPatternDefinition first = Pattern(21, 1);
		first.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		first.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		DataPatternDefinition second = Pattern(22, 1);
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		second.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerOrderJumpPatternEffect(0));
		Lookup provider = new((abs, index, previous) =>
			new SequenceEntry(abs % 2 == 0 ? first.Id : second.Id));
		TrackingCompiler compiler = new();
		compiler.Sequences[sequence.Id] = new Factory(provider);
		int jumps = 0;
		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(sequence, first, second), compiler);
		timeline.AddRoot(sequence.Id,
			shouldFollowOrderJump: _ => ++jumps < 4);

		NoteEvent[] notes = Drain(timeline);
		Assert.That(notes.Select(n => n.Offset.TimeOffset), Is.EqualTo(
			new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(240), TimeSpan.FromMilliseconds(360) }));
		Assert.That(notes.Select(n => n.Commands.Single().GetType()), Is.EqualTo(
			new[] { typeof(NoteCutCommand), typeof(NoteOffCommand),
				typeof(NoteCutCommand), typeof(NoteOffCommand) }));
		Assert.That(provider.Calls, Is.EqualTo(
			new[] { (0, 0, -1), (1, 0, 0), (2, 0, 0), (3, 0, 0) }));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(480)));
		Assert.That(timeline.IsComplete, Is.True);
	}

	[Test]
	public void CancellingSequenceStopsFurtherLookups()
	{
		ScriptSequenceDefinition sequence = new((ObjectId)10U, "Cancelled");
		DataPatternDefinition first = Pattern(11, 2);
		Lookup provider = new((abs, order, previous) => new SequenceEntry(first.Id));
		TrackingCompiler compiler = new();
		compiler.Sequences[sequence.Id] = new Factory(provider);
		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(sequence, first), compiler);
		long root = timeline.AddRoot(sequence.Id);
		Assert.That(timeline.TryStep(out _), Is.True);
		Assert.That(provider.Calls.Count, Is.EqualTo(1));
		Assert.That(timeline.Cancel(root), Is.True);
		Assert.That(timeline.IsComplete, Is.True);
		Assert.That(provider.Calls.Count, Is.EqualTo(1));
	}

	private static DataPatternDefinition Pattern(uint id, int rows)
		=> new((ObjectId)id, "Pattern") { RowCount = rows, ChannelCount = 2 };

	private static NoteEvent[] Drain(IncrementalRecursiveTimeline timeline)
	{
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit e)
				notes.Add(e.Note);
		return notes.Select(note => note with
		{
			Commands = note.Commands
				.Where(c => c is not BeginFlattenedSourceVolumeCommand)
				.ToArray(),
		}).Where(note => note.Commands.Count > 0).ToArray();
	}

	private sealed class Resolver(params SongObject[] objects)
		: IIncrementalInvocationResolver
	{
		private readonly Dictionary<ObjectId, SongObject> _objects =
			objects.ToDictionary(x => x.Id);
		public bool TryResolve(ObjectId id, out SongObject? source)
			=> _objects.TryGetValue(id, out source);
	}

	private sealed class TrackingCompiler : IIncrementalScriptSourceCompiler
	{
		public Dictionary<ObjectId, IIncrementalRawPatternNoteGenerator> Patterns
			{ get; } = [];
		public Dictionary<ObjectId, ISequenceEntrySourceFactory> Sequences
			{ get; } = [];
		public int PatternCalls { get; private set; }
		public int SequenceCalls { get; private set; }

		public IIncrementalRawPatternNoteGenerator CompilePattern(
			ScriptPatternDefinition source)
		{
			PatternCalls++;
			return Patterns[source.Id];
		}

		public ISequenceEntrySourceFactory CompileSequence(
			ScriptSequenceDefinition source)
		{
			SequenceCalls++;
			return Sequences[source.Id];
		}
	}

	private sealed class RawPattern(params NoteEvent[] notes)
		: IIncrementalRawPatternNoteGenerator
	{
		public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
		{
			foreach (NoteEvent note in notes)
				yield return new RawPatternStep.Emit(note);
		}
	}

	private sealed class Factory(ISequenceEntryProvider provider)
		: ISequenceEntrySourceFactory
	{
		public ISequenceEntryProvider Create(SequencingContext context) => provider;
	}

	private sealed class Lookup(Func<int, int, int, SequenceEntry?> resolve)
		: ISequenceEntryProvider
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

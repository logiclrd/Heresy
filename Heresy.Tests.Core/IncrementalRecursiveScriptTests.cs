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
	public void ScriptedSequenceCpuPausePrecedesItsPlayWithoutMovingTime()
	{
		ScriptSequenceDefinition sequence = new((ObjectId)10U, "Scripted orders");
		DataPatternDefinition pattern = Pattern(11, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		TrackingCompiler compiler = new();
		compiler.Sequences[sequence.Id] = new RawSequence(
			new RawSequenceStep.Cooperate(),
			new RawSequenceStep.Play(new SequenceEntry(pattern.Id)));
		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(sequence, pattern), compiler);
		timeline.AddRoot(sequence.Id);
		Assert.That(timeline.TryStep(out IncrementalPatternTimelineStep? first),
			Is.True);
		Assert.That(first, Is.TypeOf<IncrementalPatternTimelineStep.Cooperate>());
		Assert.That(timeline.Tick, Is.Zero);
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.Zero));

		NoteEvent[] emitted = Drain(timeline);
		Assert.That(emitted, Has.Length.EqualTo(1));
		Assert.That(emitted[0].Commands.Single(), Is.TypeOf<NoteCutCommand>());
		Assert.That(emitted[0].Offset.TimeOffset, Is.EqualTo(TimeSpan.Zero));
		Assert.That(timeline.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(120)));
		Assert.That(compiler.SequenceCalls, Is.EqualTo(1));
	}

	[Test]
	public void CancellingScriptedSequenceDisposesItsSuspendedEnumerator()
	{
		ScriptSequenceDefinition sequence = new((ObjectId)10U, "Endless");
		RawSequence source = new(new RawSequenceStep.Cooperate(),
			new RawSequenceStep.Cooperate());
		TrackingCompiler compiler = new();
		compiler.Sequences[sequence.Id] = source;
		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(sequence), compiler);
		long id = timeline.AddRoot(sequence.Id);
		Assert.That(timeline.TryStep(out _), Is.True);
		Assert.That(timeline.Cancel(id), Is.True);
		Assert.That(source.Disposed, Is.True);
		Assert.That(timeline.IsComplete, Is.True);
	}

	private static DataPatternDefinition Pattern(uint id, int rows)
		=> new((ObjectId)id, "Pattern") { RowCount = rows, ChannelCount = 2 };

	private static NoteEvent[] Drain(IncrementalRecursiveTimeline timeline)
	{
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit e)
				notes.Add(e.Note);
		return notes.ToArray();
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
		public Dictionary<ObjectId, IIncrementalRawSequenceEntryGenerator> Sequences
			{ get; } = [];
		public int PatternCalls { get; private set; }
		public int SequenceCalls { get; private set; }

		public IIncrementalRawPatternNoteGenerator CompilePattern(
			ScriptPatternDefinition source)
		{
			PatternCalls++;
			return Patterns[source.Id];
		}

		public IIncrementalRawSequenceEntryGenerator CompileSequence(
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

	private sealed class RawSequence(params RawSequenceStep[] steps)
		: IIncrementalRawSequenceEntryGenerator
	{
		public bool Disposed { get; private set; }
		public IEnumerable<RawSequenceStep> EnumerateRawSteps(SequencingContext context)
		{
			try
			{
				foreach (RawSequenceStep step in steps)
					yield return step;
			}
			finally
			{
				Disposed = true;
			}
		}
	}
}

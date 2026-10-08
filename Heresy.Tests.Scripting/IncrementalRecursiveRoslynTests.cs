using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class IncrementalRecursiveRoslynTests
{
	[Test]
	public void ActualNestedRoslynPatternAndSequenceShareTheParentClock()
	{
		DataPatternDefinition parent = new((ObjectId)1U, "Parent")
			{ RowCount = 2, ChannelCount = 2 };
		parent.Grid.GetOrCreateCell(0, 1).Note =
			new StartPatternNote((ObjectId)10U);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();

		ScriptSequenceDefinition sequence = new((ObjectId)10U, "Sequence")
			{ Source = "return sequenceIndex == 0 ? Play(_O(2)) : null;" };
		ScriptPatternDefinition child = new((ObjectId)2U, "Pattern")
		{
			RowCount = 1, ChannelCount = 2,
			Source = "Tempo(0, 250); Cut(0, 1);",
		};

		SequencingContext context = new();
		using IncrementalRecursiveTimeline timeline = new(
			context, new Resolver(parent, sequence, child),
			new RoslynIncrementalScriptSourceCompiler());
		timeline.AddRoot(parent.Id);
		List<NoteEvent> emitted = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit e)
				emitted.Add(e.Note);
		emitted.Select(e => e.Offset.TimeOffset).Should().Equal(
			TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(60));
		emitted[1].Target.Should().Be(ChannelTarget.Physical(2));
		context.State.Tempo.Should().Be(250);
		timeline.Elapsed.Should().Be(TimeSpan.FromMilliseconds(120));
	}

	[Test]
	public void RecursiveScriptSilentlyDropsEarlierNoteWithoutExecutingIt()
	{
		ScriptPatternDefinition pattern = new((ObjectId)4U, "Out of order")
		{
			RowCount = 4,
			Source = "Note(3, 0, _O(2)); Note(1, 0, _O(3));",
		};
		using IncrementalRecursiveTimeline timeline = new(
			new SequencingContext(), new Resolver(pattern),
			new RoslynIncrementalScriptSourceCompiler());
		timeline.AddRoot(pattern.Id);
		List<NoteEvent> notes = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emission)
				notes.Add(emission.Note);
		notes.Should().ContainSingle();
		notes[0].Commands.OfType<StartNoteCommand>()
			.Single().SourceId.Should().Be((ObjectId)2U);
		notes[0].Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(360));
	}

	private sealed class Resolver(params SongObject[] objects)
		: IIncrementalInvocationResolver
	{
		private readonly Dictionary<ObjectId, SongObject> _objects =
			objects.ToDictionary(x => x.Id);
		public bool TryResolve(ObjectId id, out SongObject? source)
			=> _objects.TryGetValue(id, out source);
	}
}

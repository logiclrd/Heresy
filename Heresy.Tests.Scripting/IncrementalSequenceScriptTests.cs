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
public sealed class IncrementalSequenceScriptTests
{
	[Test]
	public void LookupExecutesOnlyTheSelectedCase()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Lookup")
		{
			Source = """
				switch (sequenceIndex)
				{
					case 0: return Play(_O(17), 2);
					case 1: return Play(_O(18), -1);
					default: return null;
				}
				""",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeTrue();
		ISequenceEntryProvider provider = compiled.Program!
			.Create(new SequencingContext());
		provider.GetSequenceEntry(0, 0, -1).Should().Be(
			new SequenceEntry((ObjectId)17U, 2));
		Action next = () => provider.GetSequenceEntry(1, 1, 0);
		next.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void LookupsResetLocalStateAndBoundInfiniteComputation()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Finite per call")
		{
			Source = """
				int number = 1;
				if (sequenceIndex == 0) return Play(_O(17), number);
				return null;
				""",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeTrue();
		ISequenceEntryProvider provider = compiled.Program!
			.Create(new SequencingContext());
		provider.GetSequenceEntry(0, 0, -1)!.StartRow.Should().Be(1);
		provider.GetSequenceEntry(1, 0, 0)!.StartRow.Should().Be(1);

		ScriptSequenceDefinition runaway = new((ObjectId)2U, "Runaway")
			{ Source = "while (true) { }" };
		var blocked = ScriptCompiler.CompileIncrementalSequence(runaway);
		blocked.Success.Should().BeTrue();
		Action run = () => blocked.Program!.Create(new SequencingContext())
			.GetSequenceEntry(0, 0, -1);
		run.Should().Throw<Exception>();
	}

	[Test]
	public void PerInvocationRandomStateIsDeterministic()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Random")
		{
			Source = """
				if (sequenceIndex != 0) return null;
				return Random() < 0.5
					? Play(_O(17), absoluteIndex)
					: Play(_O(18), absoluteIndex);
				""",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeTrue();
		static SequenceEntry[] Extract(ISequenceEntrySourceFactory generator)
		{
			ISequenceEntryProvider provider = generator.Create(
				new SequencingContext(random: new DeterministicRandom(123456)));
			return Enumerable.Range(0, 5).Select(i =>
				provider.GetSequenceEntry(i, 0, i == 0 ? -1 : 0)!).ToArray();
		}
		SequenceEntry[] first = Extract(compiled.Program!);
		first.Should().Equal(Extract(compiled.Program!));
		first.Select(e => e.StartRow).Should().Equal(0, 1, 2, 3, 4);
	}

	[Test]
	public void RoslynLookupFeedsSharedTickCursor()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Roslyn")
		{
			Source = """
				switch (sequenceIndex)
				{
					case 0: return Play(_O(17));
					case 1: return Play(_O(18));
					default: return null;
				}
				""",
		};
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeTrue();
		DataPatternDefinition first = new((ObjectId)17U, "First")
			{ RowCount = 2, ChannelCount = 1 };
		first.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		DataPatternDefinition second = new((ObjectId)18U, "Second")
			{ RowCount = 1, ChannelCount = 1 };
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteOff();
		using IncrementalSequenceCursor cursor = new(
			compiled.Program!, new PatternResolver(first, second),
			new SequencingContext());

		List<NoteEvent> notes = [];
		while (cursor.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emitted)
				notes.Add(emitted.Note);

		notes.Select(n => n.Offset.TimeOffset).Should().Equal(
			TimeSpan.Zero, TimeSpan.FromMilliseconds(240));
		cursor.Elapsed.Should().Be(TimeSpan.FromMilliseconds(360));
		cursor.IsComplete.Should().BeTrue();
	}

	[Test]
	public void RestrictedFrameworkAccessStaysRejected()
	{
		ScriptSequenceDefinition source = new((ObjectId)1U, "Sandbox")
			{ Source = "return System.IO.File.ReadAllText(\"forbidden\");" };
		var compiled = ScriptCompiler.CompileIncrementalSequence(source);
		compiled.Success.Should().BeFalse();
		compiled.Diagnostics.Should().Contain(d => d.Code == "HRS2001");
	}

	private sealed class PatternResolver(params DataPatternDefinition[] patterns)
		: ISequencePatternResolver
	{
		private readonly Dictionary<ObjectId, IRawPatternNoteGenerator> _patterns =
			patterns.ToDictionary(p => p.Id, p => (IRawPatternNoteGenerator)p);
		public bool TryResolve(ObjectId id, out IRawPatternNoteGenerator? pattern)
			=> _patterns.TryGetValue(id, out pattern);
	}
}

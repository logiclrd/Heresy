using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class IncrementalScriptCompilerTests
{
	[Test]
	public void RoslynPatternSuspendsBeforeExecutingItsNextStatement()
	{
		ScriptPatternDefinition definition = new((ObjectId)1U, "Lazy Pattern")
		{
			RowCount = 4,
			Source = """
				Note(0, 0, _O(17));
				Note(1, 99, _O(18));
				""",
		};
		var result = ScriptCompiler.CompileIncrementalPattern(definition);
		result.Success.Should().BeTrue();

		using var iterator = result.Program!
			.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
		iterator.MoveNext().Should().BeTrue();
		var first = iterator.Current.Should()
			.BeOfType<RawPatternStep.Emit>().Subject;
		first.Note.Offset.RowOffset.Should().Be(0);
		first.Note.Commands.OfType<StartNoteCommand>()
			.Single().SourceId.Should().Be((ObjectId)17U);

		// The next invalid helper must not execute during first MoveNext.
		Action resume = () => iterator.MoveNext();
		resume.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void RoslynPatternSilentInfiniteLoopCooperatesWithoutAdvancingMusicalTime()
	{
		ScriptPatternDefinition definition = new((ObjectId)1U, "Silent Loop")
		{
			RowCount = 8,
			Source = "while (true) { }",
		};
		var result = ScriptCompiler.CompileIncrementalPattern(definition);
		result.Success.Should().BeTrue();

		using var iterator = result.Program!
			.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
		for (int i = 0; i < 3; i++)
		{
			iterator.MoveNext().Should().BeTrue();
			iterator.Current.Should().BeOfType<RawPatternStep.Cooperate>()
				.Which.Row.Should().Be(0);
		}
	}

	[Test]
	public void StreamingPatternRejectsOutOfOrderNotesInsteadOfReordering()
	{
		ScriptPatternDefinition definition = new((ObjectId)1U, "Out of order")
		{
			RowCount = 8,
			Source = "Note(4, 0, _O(17)); Note(2, 0, _O(18));",
		};
		var result = ScriptCompiler.CompileIncrementalPattern(definition);
		result.Success.Should().BeTrue();

		using var iterator = result.Program!
			.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
		iterator.MoveNext().Should().BeTrue();
		Action resume = () => iterator.MoveNext();
		resume.Should().Throw<NotSupportedException>()
			.WithMessage("*nondecreasing*");
	}

	[Test]
	public void RoslynCpuCheckpointsReachSharedTimelineWithoutRunningFutureNotes()
	{
		ScriptPatternDefinition definition = new((ObjectId)1U, "Streaming loop")
		{
			RowCount = 1,
			Source = """
				Note(0, 0, _O(17));
				for (int i = 0; i < 128; i++) { }
				Note(0.5, 0, _O(18));
				""",
		};
		var result = ScriptCompiler.CompileIncrementalPattern(definition);
		result.Success.Should().BeTrue();
		SequencingContext context = new();
		using IncrementalPatternTimeline timeline = new(context);
		timeline.Add(result.Program!, definition.RowCount, context);

		timeline.TryStep(out IncrementalPatternTimelineStep? first).Should().BeTrue();
		first.Should().BeOfType<IncrementalPatternTimelineStep.Cooperate>();
		timeline.Elapsed.Should().Be(TimeSpan.Zero);
		timeline.Tick.Should().Be(0);

		List<NoteEvent> output = [];
		while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
			if (step is IncrementalPatternTimelineStep.Emit emit)
				output.Add(emit.Note);

		output.Select(e => e.Offset.TimeOffset).Should().Equal(
			TimeSpan.Zero, TimeSpan.FromMilliseconds(60));
		output.Select(e => e.Commands.OfType<StartNoteCommand>()
			.Single().SourceId).Should().Equal((ObjectId)17U, (ObjectId)18U);
	}

	[Test]
	public void StreamingPatternPreservesExistingRestrictedDiagnostics()
	{
		ScriptPatternDefinition definition = new((ObjectId)1U, "Unsafe")
		{
			Source = "System.IO.File.ReadAllText(\"forbidden\");",
		};
		var result = ScriptCompiler.CompileIncrementalPattern(definition);
		result.Success.Should().BeFalse();
		result.Program.Should().BeNull();
		result.Diagnostics.Should().Contain(d => d.Code == "HRS2001");
	}
}

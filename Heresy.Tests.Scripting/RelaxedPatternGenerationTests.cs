using System;
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
public sealed class RelaxedPatternGenerationTests
{
	private static ScriptPatternDefinition Pattern(string source) =>
		new((ObjectId)1U, "Order") { Source = source, RowCount = 8, ChannelCount = 2 };

	private static NoteEvent[] Eager(ScriptPatternDefinition pattern, SequencingContext context)
	{
		var result = ScriptCompiler.CompilePattern(pattern);
		result.Success.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => d.Message)));
		NoteScheduleBuilder output = new();
		result.Program!.GenerateRawNotes(context, output, out double rows);
		rows.Should().Be(8);
		return output.Freeze().ToArray();
	}

	private static RawPatternStep[] Stream(ScriptPatternDefinition pattern, SequencingContext context,
		bool trace = false)
	{
		var result = ScriptCompiler.CompileIncrementalPattern(pattern, trace);
		result.Success.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => d.Message)));
		return result.Program!.EnumerateRawSteps(context).ToArray();
	}

	[TestCase("")]
	[TestCase("#order strict\n")]
	public void StrictRemainsDefaultAndDropsBackwardEmissions(string header)
	{
		var pattern = Pattern(header + "Cut(4, 0); Off(1, 0); Cut(4, 0);");
		SequencingContext eagerContext = new(), streamContext = new();
		Eager(pattern, eagerContext).Select(e => e.Offset.RowOffset).Should().Equal(4, 4);
		Stream(pattern, streamContext).OfType<RawPatternStep.Emit>()
			.Select(e => e.Row).Should().Equal(4, 4);
		eagerContext.Diagnostics.DroppedOutOfOrderNotes.Should().Be(1);
		streamContext.Diagnostics.DroppedOutOfOrderNotes.Should().Be(1);
	}

	[Test]
	public void RelaxedSortsOnlyRowsWithStableTiesAndPreservesOffsets()
	{
		const string source = """
			#order relaxed
			Cut(8, 0);
			Off(1.5, 0, timeOffsetSeconds: 0.25);
			Note(0, 1, _O(17), timeOffsetSeconds: 0.5);
			Cut(1.5, 0);
			Tempo(1, 125);
			Speed(0, 6);
			""";
		var pattern = Pattern(source);
		SequencingContext eagerContext = new(), streamContext = new();
		var eager = Eager(pattern, eagerContext);
		var steps = Stream(pattern, streamContext);
		var streamed = steps.OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
		streamed.Select(e => e.Offset.RowOffset).Should().Equal(0, 0, 1, 1.5, 1.5, 8);
		streamed.Select(e => e.EmissionOrder).Should().Equal(2L, 5L, 4L, 1L, 3L, 0L);
		streamed.Select(e => e.Offset).Should().Equal(eager.Select(e => e.Offset));
		streamed.Select(e => e.Target).Should().Equal(eager.Select(e => e.Target));
		streamed.Select(e => e.Commands.Single()).Should().Equal(eager.Select(e => e.Commands.Single()));
		streamed[0].Offset.TimeOffset.Should().Be(TimeSpan.FromSeconds(.5));
		streamed[3].Offset.TimeOffset.Should().Be(TimeSpan.FromSeconds(.25));
		steps.OfType<RawPatternStep.Cooperate>().Should().BeEmpty();
		steps.Last().Should().BeOfType<RawPatternStep.Advance>().Which.Row.Should().Be(8);
		eagerContext.Diagnostics.DroppedOutOfOrderNotes.Should().Be(0);
		streamContext.Diagnostics.DroppedOutOfOrderNotes.Should().Be(0);
		pattern.Source.Should().Be(source);
	}

	[Test]
	public void CollectionFailurePublishesNothingEvenIfFirstEventWasValid()
	{
		var pattern = Pattern("#order relaxed\nNote(0, 0, _O(17)); Note(9, 0, _O(17));");
		var eager = ScriptCompiler.CompilePattern(pattern);
		var streamed = ScriptCompiler.CompileIncrementalPattern(pattern);
		eager.Success.Should().BeTrue();
		streamed.Success.Should().BeTrue();
		NoteScheduleBuilder output = new();
		Action generate = () => eager.Program!.GenerateRawNotes(new(), output, out _);
		generate.Should().Throw<ArgumentOutOfRangeException>();
		output.Count.Should().Be(0);
		using var iterator = streamed.Program!.EnumerateRawSteps(new()).GetEnumerator();
		Action first = () => iterator.MoveNext();
		first.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void RelaxedEmptyPatternAdvancesAndRepeatedInvocationsGenerateFreshEvents()
	{
		Stream(Pattern("#order relaxed"), new()).Should().ContainSingle()
			.Which.Should().BeOfType<RawPatternStep.Advance>();
		var pattern = Pattern("#order relaxed\nNote(0, 0, _O(17), pitchMultiplier: 1 + Random());");
		var compiled = ScriptCompiler.CompileIncrementalPattern(pattern);
		compiled.Success.Should().BeTrue();
		SequencingContext context = new();
		NoteEvent First(SequencingContext c) => compiled.Program!.EnumerateRawSteps(c)
			.OfType<RawPatternStep.Emit>().Single().Note;
		var first = First(context);
		var second = First(context);
		first.Commands.Single().Should().NotBe(second.Commands.Single());
		First(new()).Commands.Single().Should().Be(first.Commands.Single());
	}

	[Test]
	public void DirectF6CallsitesSurviveSortingAndDirectiveMasking()
	{
		const string source = "#order relaxed\r\nCut(4, 0);\r\nOff(0, 0);";
		var steps = Stream(Pattern(source), new(), trace: true);
		var events = steps.OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
		events.Select(e => source.Substring(e.EditorCallsite!.Value.Start,
			e.EditorCallsite.Value.Length)).Should().Equal("Off(0, 0)", "Cut(4, 0)");
		Stream(Pattern(source), new()).OfType<RawPatternStep.Emit>()
			.Should().OnlyContain(e => e.Note.EditorCallsite == null);
	}

	[TestCase("#order bogus\nNote(0, 0, _O(17));")]
	[TestCase("Note(0, 0, _O(17));\n#order relaxed")]
	[TestCase("#order relaxed\n#line 1 \"heresy-script\"\nNote(0, 0, _O(17));")]
	[TestCase("#order relaxed\nEmitPendingStep();")]
	[TestCase("#order relaxed\nSystem.IO.File.Delete(\"x\");")]
	public void InvalidDirectiveOrForbiddenCapabilityRemainsRejected(string source)
	{
		ScriptCompiler.CompilePattern(Pattern(source)).Success.Should().BeFalse();
		ScriptCompiler.CompileIncrementalPattern(Pattern(source)).Success.Should().BeFalse();
		ScriptCompiler.AnalyzePatternSource(source).Should()
			.Contain(d => d.Severity == Heresy.Scripting.Analysis.ScriptDiagnosticSeverity.Error);
	}

	[Test]
	public void SequenceCannotSelectPatternOrdering()
	{
		const string source = "#order relaxed\nreturn Play(_O(17));";
		ScriptCompiler.AnalyzeSequenceSource(source).Should().Contain(d =>
			d.Severity == Heresy.Scripting.Analysis.ScriptDiagnosticSeverity.Error);
	}
}

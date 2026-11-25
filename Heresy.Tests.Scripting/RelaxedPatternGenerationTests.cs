using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;
using Heresy.Scripting.Runtime;

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

	[TestCase(false)]
	[TestCase(true)]
	public void RunawayRelaxedLoopAbortsBeforeAnyPublication(bool trace)
	{
		var pattern = Pattern("#order relaxed\nNote(0, 0, _O(17)); while (true) { }");
		var compiled = ScriptCompiler.CompileIncrementalPattern(pattern, trace);
		compiled.Success.Should().BeTrue();
		using var iterator = compiled.Program!.EnumerateRawSteps(new()).GetEnumerator();
		Action move = () => iterator.MoveNext();
		move.Should().Throw<SequencingResourceLimitException>();
	}

	[Test]
	public void CanceledRelaxedInvocationDoesNotPublishOrConsumeRandomValues()
	{
		var pattern = Pattern("#order relaxed\nNote(0, 0, _O(17), pitchMultiplier: 1 + Random());");
		var eager = ScriptCompiler.CompilePattern(pattern);
		var normal = ScriptCompiler.CompileIncrementalPattern(pattern);
		var traced = ScriptCompiler.CompileIncrementalPattern(pattern, true);
		Assert.That(eager.Success && normal.Success && traced.Success, Is.True);
		SequencingContext context = new();
		context.Cancellation.RequestStop();
		NoteScheduleBuilder output = new();
		Action generate = () => eager.Program!.GenerateRawNotes(context, output, out _);
		generate.Should().Throw<OperationCanceledException>();
		output.Count.Should().Be(0);
		foreach (var result in new[] { normal, traced })
		{
			using var iterator = result.Program!.EnumerateRawSteps(context).GetEnumerator();
			Action first = () => iterator.MoveNext();
			first.Should().Throw<OperationCanceledException>();
		}
		context.Random.NextDouble().Should().Be(new SequencingContext().Random.NextDouble());
		context.FlattenedChild().Cancellation.Should().BeSameAs(context.Cancellation);
		context.MixdownChild().Cancellation.Should().BeSameAs(context.Cancellation);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void RelaxedRuntimeObservesStopAfterAnEmissionBeforeReturning(bool emitAgain)
	{
		SequencingContext context = new();
		var receiver = new StopOnAppend(context.Cancellation);
		var program = new StopProbe(context, receiver, emitAgain);
		Action execute = () => program.ExecuteRelaxed();
		execute.Should().Throw<OperationCanceledException>();
		receiver.Count.Should().Be(1,
			"the next checkpoint or the final collection boundary must observe the stop");
	}

	private sealed class StopOnAppend(SequencingCancellation cancellation) : INoteReceiver
	{
		public int Count { get; private set; }
		public void Append(NoteEvent noteEvent)
		{
			Count++;
			cancellation.RequestStop();
		}
	}

	private sealed class StopProbe(SequencingContext context, INoteReceiver receiver, bool emitAgain)
		: PatternScriptProgram(context, receiver, 4, 1)
	{
		protected override void ExecuteScript()
		{
			Note(0, 0, (ObjectId)17U);
			if (emitAgain)
				Note(1, 0, (ObjectId)17U);
		}
	}

	[Test]
	public void LargeFiniteCallbackCollectionIsRepeatableAndDoesNotCooperate()
	{
		var pattern = Pattern("""
			#order relaxed
			Enumerable.Range(0, 12000).ToList().ForEach(n =>
				Note(7 - n % 8, 0, _O(17), pitchMultiplier: 1 + Random()));
			""");
		var ordinary = Stream(pattern, new());
		var traced = Stream(pattern, new(), trace: true);
		var events = ordinary.OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
		var traceEvents = traced.OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
		events.Length.Should().Be(12000);
		events.Select(e => e.Offset.RowOffset).Should().BeInAscendingOrder();
		ordinary.OfType<RawPatternStep.Cooperate>().Should().BeEmpty();
		traceEvents.Select(e => e.Offset).Should().Equal(events.Select(e => e.Offset));
		traceEvents.Select(e => ((StartNoteCommand)e.Commands.Single()).PitchMultiplier)
			.Should().Equal(events.Select(e => ((StartNoteCommand)e.Commands.Single()).PitchMultiplier));
		foreach (var group in events.GroupBy(e => e.Offset.RowOffset))
			group.Select(e => e.EmissionOrder).Should().BeInAscendingOrder();
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

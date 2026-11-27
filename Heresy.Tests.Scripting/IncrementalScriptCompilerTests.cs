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

	[TestCase(false, false)]
	[TestCase(false, true)]
	[TestCase(true, false)]
	[TestCase(true, true)]
	public void PhysicalControlsEmitTypedCommandsWithExactOptionalCallsites(
		bool relaxed, bool instrumented)
	{
		string[] calls =
		[
			"Volume(0, 1, 0.25, timeOffsetSeconds: 0.01)",
			"SourceFrame(1, 1, 12345678901L, timeOffsetSeconds: 0.02)",
			"Displacement(1, 1, NoteDisplacementAction.Continue, timeOffsetSeconds: 0.03)",
			"Past(2, 1, TrackerPastNoteAction.Off, timeOffsetSeconds: 0.04)",
		];
		string source = (relaxed ? "#order relaxed\n" : "")
			+ string.Join(";\n", calls) + ";";
		var definition = new ScriptPatternDefinition((ObjectId)1U, "Physical helpers")
			{ RowCount = 4, ChannelCount = 2, Source = source };
		var result = ScriptCompiler.CompileIncrementalPattern(definition,
			instrumentActionCallsites: instrumented);
		result.Success.Should().BeTrue();
		var events = result.Program!.EnumerateRawSteps(new SequencingContext())
			.OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
		events.Select(e => e.Offset.RowOffset).Should().Equal(0, 1, 1, 2);
		events.Select(e => e.Offset.TimeOffset).Should().Equal(
			TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20),
			TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(40));
		Assert.That(events.Select(e => e.Target), Is.All.EqualTo(ChannelTarget.Physical(1)));
		events.Select(e => e.Commands.Single()).Should().Equal(
			new SetNoteVolumeCommand(0.25), new SetSourceFrameOffsetCommand(12345678901L),
			new SetCurrentVoiceDisplacementActionCommand(NoteDisplacementAction.Continue),
			new ApplyPastNoteActionCommand(TrackerPastNoteAction.Off));
		for (int i = 0; i < calls.Length; i++)
		{
			if (instrumented)
			{
				Assert.That(events[i].EditorCallsite, Is.EqualTo(
					new Heresy.Core.Sequences.SequenceCallsite(
						source.IndexOf(calls[i], StringComparison.Ordinal), calls[i].Length)));
				Assert.That(events[i].EditorPatternRow, Is.EqualTo((int)events[i].Offset.RowOffset));
			}
			else
			{
				Assert.That(events[i].EditorCallsite, Is.Null);
				Assert.That(events[i].EditorPatternRow, Is.Null);
			}
		}
	}

	[TestCase("Volume(0, 0, -0.1)")]
	[TestCase("Volume(0, 0, double.NaN)")]
	[TestCase("SourceFrame(0, 0, -1)")]
	[TestCase("Displacement(0, 0, (NoteDisplacementAction)99)")]
	[TestCase("Past(0, 0, (TrackerPastNoteAction)99)")]
	[TestCase("Volume(0, 2, 0.5)")]
	[TestCase("Past(5, 0, TrackerPastNoteAction.Cut)")]
	[TestCase("SourceFrame(0, 0, 0, timeOffsetSeconds: 1e300)")]
	public void InvalidPhysicalControlsDoNotExecuteUntilNextStrictYield(string call)
	{
		var definition = new ScriptPatternDefinition((ObjectId)1U, "Physical validation")
			{ RowCount = 4, ChannelCount = 2, Source = "Volume(0, 0, 0.5); " + call + ";" };
		var result = ScriptCompiler.CompileIncrementalPattern(definition);
		result.Success.Should().BeTrue();
		using var iterator = result.Program!.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
		iterator.MoveNext().Should().BeTrue();
		iterator.Current.Should().BeOfType<RawPatternStep.Emit>().Which.Note.Commands.Single()
			.Should().Be(new SetNoteVolumeCommand(0.5));
		Action resume = () => iterator.MoveNext();
		resume.Should().Throw<ArgumentOutOfRangeException>();
	}

	[TestCase("Volume(callsite: default, row: 0, channel: 0, volume: 0.5);")]
	[TestCase("SourceFrame(callsite: default, row: 0, channel: 0, sourceFrameOffset: 0);")]
	[TestCase("Displacement(callsite: default, row: 0, channel: 0, action: NoteDisplacementAction.Continue);")]
	[TestCase("Past(callsite: default, row: 0, channel: 0, action: TrackerPastNoteAction.Cut);")]
	public void AuthoredPhysicalCallsiteOverloadsRemainForbidden(string source)
	{
		var definition = new ScriptPatternDefinition((ObjectId)1U, "Metadata refusal")
			{ RowCount = 4, ChannelCount = 2, Source = source };
		Assert.That(ScriptCompiler.CompileIncrementalPattern(definition,
			instrumentActionCallsites: true).Success, Is.False);
		definition.Source = "#order relaxed\n" + source;
		Assert.That(ScriptCompiler.CompileIncrementalPattern(definition,
			instrumentActionCallsites: true).Success, Is.False);
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
	public void StreamingPatternSilentlyDropsEarlierEventsAndRetainsEqualPositionOrder()
	{
		ScriptPatternDefinition definition = new((ObjectId)1U, "Out of order")
		{
			RowCount = 8,
			Source = """
				Note(4, 0, _O(17));
				Note(2, 0, _O(18));
				Off(3, 0);
				Note(4, 0, _O(19));
				Cut(5, 0);
				""",
		};
		var result = ScriptCompiler.CompileIncrementalPattern(definition);
		result.Success.Should().BeTrue();

		RawPatternStep[] steps = result.Program!
			.EnumerateRawSteps(new SequencingContext()).ToArray();
		var emissions = steps.OfType<RawPatternStep.Emit>().ToArray();
		emissions.Select(e => e.Row).Should().Equal(4, 4, 5);
		emissions[0].Note.Commands.Single().Should()
			.BeOfType<StartNoteCommand>()
			.Which.SourceId.Should().Be((ObjectId)17U);
		emissions[1].Note.Commands.Single().Should()
			.BeOfType<StartNoteCommand>()
			.Which.SourceId.Should().Be((ObjectId)19U);
		emissions[2].Note.Commands.Single().Should()
			.BeOfType<NoteCutCommand>();
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

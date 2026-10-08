using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptCompilerTests
{

	[Test]
	public void PatternScriptFixedWallOffsetsAppearInRawMusicalTime()
	{
		ScriptPatternDefinition pattern = new((ObjectId)1U, "Wall offsets")
		{
			RowCount = 3,
			Source = "Note(0.5, 0, _O(17), timeOffsetSeconds: 0.09); "
				+ "Off(1.5, 0, timeOffsetSeconds: 0.025); "
				+ "Cut(2, 0, timeOffsetSeconds: 0.1);",
		};
		ScriptCompilationResult<IRawPatternNoteGenerator> compiled =
			ScriptCompiler.CompilePattern(pattern);
		compiled.Success.Should().BeTrue();
		NoteScheduleBuilder builder = new();
		compiled.Program!.GenerateRawNotes(new SequencingContext(), builder, out _);
		NoteEvent[] events = builder.Freeze().ToArray();
		events.Select(e => e.Offset.RowOffset).Should().Equal(0.5, 1.5, 2);
		events.Select(e => e.Offset.TimeOffset).Should().Equal(
			TimeSpan.FromMilliseconds(90),
			TimeSpan.FromMilliseconds(25),
			TimeSpan.FromMilliseconds(100));
	}

	[Test]
	public void PatternScriptRejectsUnrepresentableFixedWallOffset()
	{
		ScriptPatternDefinition pattern = new((ObjectId)1U, "Oversized wall offset")
		{
			RowCount = 2,
			Source = "Cut(0, 0, timeOffsetSeconds: 1e300);",
		};
		ScriptCompilationResult<IRawPatternNoteGenerator> compiled =
			ScriptCompiler.CompilePattern(pattern);
		compiled.Success.Should().BeTrue();
		Action generate = () =>
			compiled.Program!.GenerateRawNotes(
				new SequencingContext(), new NoteScheduleBuilder(), out _);
		generate.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void PatternScriptEndpointCommandsAreInclusiveButLaterRowsAreRejected()
	{
		ScriptPatternDefinition endpoint = new((ObjectId)1U, "Endpoint")
		{
			RowCount = 2,
			Source = "Note(2, 0, _O(17)); Off(2, 0); Cut(2, 0);",
		};
		ScriptCompilationResult<IRawPatternNoteGenerator> accepted =
			ScriptCompiler.CompilePattern(endpoint);
		accepted.Success.Should().BeTrue();
		NoteScheduleBuilder notes = new();
		accepted.Program!.GenerateRawNotes(new SequencingContext(), notes, out double count);
		count.Should().Be(2);
		notes.Freeze().Select(e => e.Offset.RowOffset)
			.Should().Equal(2, 2, 2);

		ScriptPatternDefinition beyond = new((ObjectId)2U, "Past endpoint")
		{
			RowCount = 2,
			Source = "Note(2.01, 0, _O(17));",
		};
		ScriptCompilationResult<IRawPatternNoteGenerator> rejected =
			ScriptCompiler.CompilePattern(beyond);
		rejected.Success.Should().BeTrue();
		Action generate = () =>
			rejected.Program!.GenerateRawNotes(
				new SequencingContext(), new NoteScheduleBuilder(), out _);
		generate.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void PatternScriptCompilesIntoRawPatternGenerator()
	{
		ScriptPatternDefinition definition =
			new((ObjectId)1U, "Generated Pattern")
			{
				RowCount = 8,
				Source = """
					Note(0, 1, _O(17), 2.0, 0.5, true, 0.75);
					Off(2.5, 1);
					Cut(3, 2);
					Tempo(4, 150.0);
					Speed(5, 3);
					""",
			};

		ScriptCompilationResult<IRawPatternNoteGenerator> compilation =
			ScriptCompiler.CompilePattern(definition);

		compilation.Success.Should().BeTrue();
		compilation.Diagnostics
			.Should().NotContain(diagnostic =>
				diagnostic.Severity == ScriptDiagnosticSeverity.Error);
		IRawPatternNoteGenerator program =
			compilation.Program
				?? throw new AssertionException("Pattern program was null.");

		NoteScheduleBuilder output = new();
		program.GenerateRawNotes(
			new SequencingContext(),
			output,
			out double rowCount);
		NoteSchedule notes = output.Freeze();

		rowCount.Should().Be(8);
		notes.Should().HaveCount(5);

		notes[0].Offset.RowOffset.Should().Be(0);
		notes[0].Target.Should().Be(ChannelTarget.Physical(1));
		StartNoteCommand start =
			notes[0].Commands.Single()
				.Should().BeOfType<StartNoteCommand>().Subject;
		start.SourceId.Should().Be((ObjectId)17U);
		start.PitchMultiplier.Should().Be(2.0);
		start.PlaybackSpeedMultiplier.Should().Be(0.5);
		start.Mixdown.Should().BeTrue();
		start.Volume.Should().Be(0.75);

		notes[1].Offset.RowOffset.Should().Be(2.5);
		notes[1].Commands.Single()
			.Should().BeOfType<NoteOffCommand>();
		notes[2].Commands.Single()
			.Should().BeOfType<NoteCutCommand>();

		notes[3].Target.Should().Be(ChannelTarget.Global);
		notes[3].Commands.Single()
			.Should().Be(new SetTempoCommand(150.0));
		notes[4].Target.Should().Be(ChannelTarget.Global);
		notes[4].Commands.Single()
			.Should().Be(new SetSpeedCommand(3));
	}

	[Test]
	public void SequenceScriptCompilesIntoExistingSequencerContract()
	{
		ScriptSequenceDefinition definition =
			new((ObjectId)1U, "Generated Sequence")
			{
				Source = """
					Play(_O(21));
					Play(_O(22));
					""",
			};
		RecordingResolver resolver = new();

		ScriptCompilationResult<INoteSequencer> compilation =
			ScriptCompiler.CompileSequence(
				definition,
				resolver);

		compilation.Success.Should().BeTrue();
		INoteSequencer program =
			compilation.Program
				?? throw new AssertionException("Sequence program was null.");

		NoteScheduleBuilder output = new();
		program.GenerateNotes(
			new SequencingContext(),
			output,
			out TimeSpan duration);

		resolver.RequestedPatternIds
			.Should().Equal((ObjectId)21U, (ObjectId)22U);
		duration.Should().BeGreaterThan(TimeSpan.Zero);
		output.Freeze().Should().BeEmpty();
	}

	[Test]
	public void PatternRandomIsDeterministicForEquivalentContexts()
	{
		ScriptPatternDefinition definition =
			new((ObjectId)1U, "Random Pattern")
			{
				RowCount = 1,
				Source = """
					if (Random() < 0.5)
						Note(0, 0, _O(10));
					else
						Note(0, 0, _O(11));
					""",
			};

		ScriptCompilationResult<IRawPatternNoteGenerator> compilation =
			ScriptCompiler.CompilePattern(definition);
		compilation.Success.Should().BeTrue();
		IRawPatternNoteGenerator program =
			compilation.Program
				?? throw new AssertionException("Pattern program was null.");

		ObjectId first = GenerateStartedSource(program, 1234);
		ObjectId second = GenerateStartedSource(program, 1234);

		first.Should().Be(second);
	}

	[Test]
	public void ArbitraryFrameworkCallsAreRejected()
	{
		ScriptPatternDefinition definition =
			new((ObjectId)1U, "Unsafe Pattern")
			{
				Source =
					"System.IO.File.ReadAllText(\"should-not-be-readable\");",
			};

		ScriptCompilationResult<IRawPatternNoteGenerator> compilation =
			ScriptCompiler.CompilePattern(definition);

		compilation.Success.Should().BeFalse();
		compilation.Program.Should().BeNull();
		compilation.Diagnostics.Should().Contain(diagnostic =>
			diagnostic.Code == "HRS2001"
				&& diagnostic.Severity == ScriptDiagnosticSeverity.Error);
	}

	[Test]
	public void InfiniteLoopIsStoppedBySequencingResourceGuard()
	{
		ScriptPatternDefinition definition =
			new((ObjectId)1U, "Runaway Pattern")
			{
				Source = "while (true) { }",
			};

		ScriptCompilationResult<IRawPatternNoteGenerator> compilation =
			ScriptCompiler.CompilePattern(definition);
		compilation.Success.Should().BeTrue();
		IRawPatternNoteGenerator program =
			compilation.Program
				?? throw new AssertionException("Pattern program was null.");

		Action execute = () =>
		{
			NoteScheduleBuilder output = new();
			program.GenerateRawNotes(
				new SequencingContext(),
				output,
				out _);
		};

		execute.Should().Throw<SequencingResourceLimitException>();
	}

	[Test]
	public void PatternAnalysisReportsRestrictedLanguageDiagnosticsWithoutEmitting()
	{
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics =
			ScriptCompiler.AnalyzePatternSource(
				"System.IO.File.ReadAllText(\"not-allowed\");");

		diagnostics.Should().Contain(diagnostic =>
			diagnostic.Code == "HRS2001"
				&& diagnostic.Severity == ScriptDiagnosticSeverity.Error);
	}

	[Test]
	public void PatternAnalysisReportsOrdinaryCSharpBindingDiagnostics()
	{
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics =
			ScriptCompiler.AnalyzePatternSource(
				"MissingHelper();");

		diagnostics.Should().Contain(diagnostic =>
			diagnostic.Code == "CS0103"
				&& diagnostic.Severity == ScriptDiagnosticSeverity.Error);
	}

	[Test]
	public void SequenceAnalysisUsesSequenceHelperSurface()
	{
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics =
			ScriptCompiler.AnalyzeSequenceSource(
				"Note(0, 0, _O(1));");

		diagnostics.Should().Contain(diagnostic =>
			diagnostic.Code == "CS0103"
				&& diagnostic.Severity == ScriptDiagnosticSeverity.Error);
	}

	private static ObjectId GenerateStartedSource(
		IRawPatternNoteGenerator program,
		ulong seed)
	{
		NoteScheduleBuilder output = new();
		program.GenerateRawNotes(
			new SequencingContext(
				random: new DeterministicRandom(seed)),
			output,
			out _);

		return output.Freeze()[0].Commands.Single()
			.Should().BeOfType<StartNoteCommand>().Subject.SourceId;
	}

	private sealed class RecordingResolver : ISequencePatternResolver
	{
		public List<ObjectId> RequestedPatternIds { get; } = [];

		public bool TryResolve(
			ObjectId patternId,
			out IRawPatternNoteGenerator? pattern)
		{
			RequestedPatternIds.Add(patternId);
			pattern = EmptyPattern.Instance;
			return true;
		}
	}

	private sealed class EmptyPattern : IRawPatternNoteGenerator
	{
		public static EmptyPattern Instance { get; } = new();

		public void GenerateRawNotes(
			SequencingContext context,
			INoteReceiver output,
			out double rowCount)
		{
			ArgumentNullException.ThrowIfNull(context);
			ArgumentNullException.ThrowIfNull(output);
			rowCount = 1;
		}
	}
}

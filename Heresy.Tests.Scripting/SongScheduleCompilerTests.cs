using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class SongScheduleCompilerTests
{
	[Test]
	public void ScriptRootSequenceCanInvokeScriptPattern()
	{
		SongDocument document = new();

		ObjectId patternId = document.AllocateObjectId();
		document.Add(
			new ScriptPatternDefinition(patternId, "Generated")
			{
				RowCount = 2,
				Source = "Note(0, 0, _O(999));",
			});

		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(
			new ScriptSequenceDefinition(sequenceId, "Arrangement")
			{
				Source = $"Play(_O({patternId.Value}));",
			});
		document.RootSequenceId = sequenceId;

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileRoot(document);

		result.Success.Should().BeTrue();
		result.Diagnostics.Should().BeEmpty();
		result.Schedule.Should().NotBeNull();
		NoteSchedule schedule = result.Schedule!;
		schedule.Should().ContainSingle();
		schedule[0].Commands.Single()
			.Should().BeOfType<StartNoteCommand>()
			.Subject.SourceId.Should().Be((ObjectId)999U);
		result.Duration.Should().BeGreaterThan(System.TimeSpan.Zero);
	}

	[Test]
	public void DataRootSequenceCanResolveScriptPattern()
	{
		SongDocument document = new();

		ObjectId patternId = document.AllocateObjectId();
		document.Add(
			new ScriptPatternDefinition(patternId, "Generated")
			{
				RowCount = 1,
				Source = "Cut(0, 0);",
			});

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(sequenceId, "Arrangement");
		sequence.Entries.Add(new SequenceEntry(patternId));
		document.Add(sequence);
		document.RootSequenceId = sequenceId;

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileRoot(document);

		result.Success.Should().BeTrue();
		result.Schedule.Should().NotBeNull();
		result.Schedule!.Should().ContainSingle();
		result.Schedule[0].Commands.Single()
			.Should().BeOfType<NoteCutCommand>();
	}

	[Test]
	public void ReferencedScriptPatternCompilationFailureFailsWholeSchedule()
	{
		SongDocument document = new();

		ObjectId patternId = document.AllocateObjectId();
		document.Add(
			new ScriptPatternDefinition(patternId, "Broken")
			{
				Source =
					"System.IO.File.ReadAllText(\"not-allowed\");",
			});

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(sequenceId, "Arrangement");
		sequence.Entries.Add(new SequenceEntry(patternId));
		document.Add(sequence);
		document.RootSequenceId = sequenceId;

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileRoot(document);

		result.Success.Should().BeFalse();
		result.Schedule.Should().BeNull();
		result.Diagnostics.Should().Contain(diagnostic =>
			diagnostic.Code == "HRS2001"
				&& diagnostic.Severity == ScriptDiagnosticSeverity.Error);
	}

	[Test]
	public void UnreferencedBrokenScriptDoesNotBlockRootSchedule()
	{
		SongDocument document = new();

		ObjectId brokenId = document.AllocateObjectId();
		document.Add(
			new ScriptPatternDefinition(brokenId, "Unused Broken")
			{
				Source =
					"System.IO.File.ReadAllText(\"not-allowed\");",
			});

		ObjectId livePatternId = document.AllocateObjectId();
		DataPatternDefinition livePattern =
			new(livePatternId, "Live")
			{
				RowCount = 1,
			};
		livePattern.Grid.GetOrCreateCell(0, 0).Note =
			new PatternNoteCut();
		document.Add(livePattern);

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(sequenceId, "Arrangement");
		sequence.Entries.Add(new SequenceEntry(livePatternId));
		document.Add(sequence);
		document.RootSequenceId = sequenceId;

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileRoot(document);

		result.Success.Should().BeTrue();
		result.Diagnostics.Should().BeEmpty();
		result.Schedule.Should().NotBeNull();
	}


	[Test]
	public void ArbitraryDataSequenceCanStartAtOrderAndRow()
	{
		SongDocument document = new();

		ObjectId firstPatternId = document.AllocateObjectId();
		DataPatternDefinition first =
			new(firstPatternId, "First")
			{
				RowCount = 4,
			};
		first.Grid.GetOrCreateCell(0, 0).Note =
			new PatternNoteCut();
		document.Add(first);

		ObjectId secondPatternId = document.AllocateObjectId();
		DataPatternDefinition second =
			new(secondPatternId, "Second")
			{
				RowCount = 4,
			};
		second.Grid.GetOrCreateCell(2, 0).Note =
			new PatternNoteCut();
		document.Add(second);

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence =
			new(sequenceId, "Sequence");
		sequence.Entries.Add(new SequenceEntry(firstPatternId));
		sequence.Entries.Add(new SequenceEntry(secondPatternId));
		document.Add(sequence);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(
				document,
				sequenceId,
				startOrder: 1,
				startRow: 2);

		result.Success.Should().BeTrue();
		result.Schedule.Should().NotBeNull();
		result.Schedule!.Should().ContainSingle();
		result.Schedule[0].Offset.TimeOffset.Should().Be(System.TimeSpan.Zero);
		result.Schedule[0].Commands.Single()
			.Should().BeOfType<NoteCutCommand>();
	}

	[Test]
	public void ArbitraryScriptSequenceCanStartAtOrder()
	{
		SongDocument document = new();

		ObjectId firstPatternId = document.AllocateObjectId();
		DataPatternDefinition first =
			new(firstPatternId, "First")
			{
				RowCount = 1,
			};
		first.Grid.GetOrCreateCell(0, 0).Note =
			new PatternNoteCut();
		document.Add(first);

		ObjectId secondPatternId = document.AllocateObjectId();
		DataPatternDefinition second =
			new(secondPatternId, "Second")
			{
				RowCount = 1,
			};
		second.Grid.GetOrCreateCell(0, 1).Note =
			new PatternNoteCut();
		document.Add(second);

		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(
			new ScriptSequenceDefinition(sequenceId, "Script")
			{
				Source =
					$"Play(_O({firstPatternId.Value})); "
						+ $"Play(_O({secondPatternId.Value}));",
			});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(
				document,
				sequenceId,
				startOrder: 1);

		result.Success.Should().BeTrue();
		result.Schedule.Should().NotBeNull();
		result.Schedule!.Should().ContainSingle();
		result.Schedule[0].Target.PhysicalChannel.Should().Be(1);
	}

	[Test]
	public void StandalonePatternCanStartAtRequestedRow()
	{
		SongDocument document = new();

		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Pattern")
			{
				RowCount = 4,
			};
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new PatternNoteCut();
		pattern.Grid.GetOrCreateCell(2, 0).Note =
			new PatternNoteCut();
		document.Add(pattern);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(
				document,
				patternId,
				startRow: 2);

		result.Success.Should().BeTrue();
		result.Schedule.Should().NotBeNull();
		result.Schedule!.Should().ContainSingle();
		result.Schedule[0].Offset.TimeOffset.Should().Be(System.TimeSpan.Zero);
	}


	[Test]
	public void MissingRootSequenceProducesDiagnostic()
	{
		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileRoot(
				new SongDocument());

		result.Success.Should().BeFalse();
		result.Schedule.Should().BeNull();
		result.Diagnostics.Should().ContainSingle(diagnostic =>
			diagnostic.Code == "HRS3001"
				&& diagnostic.Severity == ScriptDiagnosticSeverity.Error);
	}
}

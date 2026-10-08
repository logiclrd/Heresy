using System;
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
	public void NestedFlattenedEventsAreGeneratedInParentsActiveChannelState()
	{
		SongDocument document = new();
		ObjectId source = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Child")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x20));
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x00));
		document.Add(parent);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(document, parentId);

		result.Success.Should().BeTrue();
		result.Schedule.Should().NotBeNull();
		result.Schedule!.SelectMany(e => e.Commands)
			.OfType<StartNoteCommand>()
			.Select(command => command.SourceId)
			.Should().NotContain(childId);
		// The child effect must be available to the parent's next row. The
		// previous post-compiler expansion could not resolve this D00 recall.
		result.Schedule.SelectMany(e => e.Commands)
			.OfType<SetNoteVolumeSlideCommand>()
			.Count().Should().BeGreaterThanOrEqualTo(2);
	}

	[Test]
	public void FlattenedSequenceRootSharesTrackerTempoWithFollowingPattern()
	{
		SongDocument document = new();
		ObjectId nestedId = document.AllocateObjectId();
		DataPatternDefinition nested = new(nestedId, "Tempo child")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		nested.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(nested);

		ObjectId invokeId = document.AllocateObjectId();
		DataPatternDefinition invoke = new(invokeId, "Invoke")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		invoke.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: nestedId);
		document.Add(invoke);

		ObjectId followingId = document.AllocateObjectId();
		DataPatternDefinition following = new(followingId, "Following")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		following.Grid.GetOrCreateCell(1, 0).Note =
			new PatternNoteCut();
		document.Add(following);

		ObjectId rootId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(rootId, "Song");
		sequence.Entries.Add(new SequenceEntry(invokeId));
		sequence.Entries.Add(new SequenceEntry(followingId));
		document.Add(sequence);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, rootId);

		result.Success.Should().BeTrue();
		result.Schedule.Should().NotBeNull();
		TimeSpan cutTime = result.Schedule!
			.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.Single().Offset.TimeOffset;
		// First parent row: 120ms. Following row: 60ms at new tempo.
		cutTime.Should().Be(TimeSpan.FromMilliseconds(180));
	}

	[Test]
	public void FlattenedChildSourceOnlyRowReplacesRememberedSourceBeforeLaterParentNote()
	{
		SongDocument document = new();
		ObjectId nextSource = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Source selector")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(0, 0).SourceId = nextSource;
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Note =
			new StartPatternNote();
		document.Add(parent);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(document, parentId);

		result.Success.Should().BeTrue();
		StartNoteCommand[] starts = result.Schedule!
			.SelectMany(e => e.Commands)
			.OfType<StartNoteCommand>()
			.ToArray();
		starts.Should().ContainSingle()
			.Which.SourceId.Should().Be(nextSource);
		result.Schedule.Should().NotContain(e =>
			e.Commands.Any(c => c is SelectPatternSourceCommand));
	}

	[Test]
	public void SourceOnlyRowAfterSourceOmittedNoteDoesNotRetroactivelyChangeItsSource()
	{
		SongDocument document = new();
		ObjectId selected = document.AllocateObjectId();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Select later")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();
		pattern.Grid.GetOrCreateCell(1, 0).SourceId = selected;
		pattern.Grid.GetOrCreateCell(2, 0).Note = new StartPatternNote();
		document.Add(pattern);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(document, patternId);
		result.Success.Should().BeTrue();
		StartNoteCommand[] starts = result.Schedule!
			.SelectMany(e => e.Commands).OfType<StartNoteCommand>().ToArray();
		starts.Should().ContainSingle().Which.SourceId.Should().Be(selected);
		result.Schedule.Single(e =>
			e.Commands.Any(c => c is StartNoteCommand))
			.Offset.TimeOffset.Should().BeGreaterThan(TimeSpan.FromMilliseconds(120));
	}

	[Test]
	public void SourceSelectionMemoryIsSharedAtMappedChildChannelButNotAdjacentParentChannel()
	{
		SongDocument document = new();
		ObjectId src = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Child")
		{
			RowCount = 1,
			ChannelCount = 2,
		};
		child.Grid.GetOrCreateCell(0, 1).SourceId = src;
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 2,
			ChannelCount = 3,
		};
		parent.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 1).Note = new StartPatternNote();
		parent.Grid.GetOrCreateCell(1, 2).Note = new StartPatternNote();
		document.Add(parent);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(document, parentId);
		result.Success.Should().BeTrue();
		NoteEvent note = result.Schedule!.Single(e =>
			e.Commands.Any(c => c is StartNoteCommand));
		note.Target.Should().Be(ChannelTarget.Physical(2));
		((StartNoteCommand)note.Commands.Single()).SourceId.Should().Be(src);
	}

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

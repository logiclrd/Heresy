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
	public void DelayedScriptCommandAndParentRowShareTimestampInCursorOrder()
	{
		SongDocument document = new();
		ObjectId sourceId = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Delayed at boundary")
		{
			RowCount = 2,
			Source = $"Note(0.5, 0, _O({sourceId.Value}), timeOffsetSeconds: 0.06);",
		});
		ObjectId rootId = document.AllocateObjectId();
		DataPatternDefinition root = new(rootId, "Root boundary")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		root.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sourceId: childId);
		root.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(root);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, rootId);

		compiled.Success.Should().BeTrue();
		NoteEvent[] atBoundary = compiled.Schedule!
			.Where(e => e.Offset.TimeOffset == TimeSpan.FromMilliseconds(120))
			.ToArray();
		atBoundary.Should().HaveCount(2);
		atBoundary[0].Commands.Should().ContainSingle()
			.Which.Should().BeOfType<NoteCutCommand>();
		atBoundary[1].Commands.Should().ContainSingle()
			.Which.Should().BeOfType<StartNoteCommand>();
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void ScriptedWallOffsetRunsAtAbsoluteDeadlineAfterParentTempoChange()
	{
		SongDocument document = new();
		ObjectId soundId = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Delayed note")
		{
			RowCount = 2,
			Source = $"Note(0.5, 0, _O({soundId.Value}), timeOffsetSeconds: 0.09);",
		});
		ObjectId rootId = document.AllocateObjectId();
		DataPatternDefinition root = new(rootId, "Tempo after due tick")
		{
			RowCount = 3,
			ChannelCount = 2,
		};
		root.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sourceId: childId);
		root.Grid.GetOrCreateCell(1, 1).Effects.Add(new SetTempoPatternEffect(250));
		root.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteCut();
		document.Add(root);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, rootId);

		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is StartNoteCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(150));
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void DelayedFlattenedInvocationStartsAtWallDeadlineAndRetainsChildCursor()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Nested after delay")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(child);
		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Delayed child")
		{
			RowCount = 1,
			Source = $"Note(0.5, 0, _O({childId.Value}), timeOffsetSeconds: 0.12);",
		});

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);

		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(300));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(420));
	}

	[Test]
	public void DelayedScriptedNoteCrossesSequenceOrderWithoutEarlyEmission()
	{
		SongDocument document = new();
		ObjectId soundId = document.AllocateObjectId();
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "Delayed first order")
		{
			RowCount = 1,
			Source = "Cut(0.25, 1, timeOffsetSeconds: 0.02); "
				+ $"Note(0.5, 0, _O({soundId.Value}), timeOffsetSeconds: 0.12);",
		});
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Following order")
		{
			RowCount = 2,
			ChannelCount = 2,
		};
		second.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		second.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Song");
		sequence.Entries.Add(new SequenceEntry(firstId));
		sequence.Entries.Add(new SequenceEntry(secondId));
		document.Add(sequence);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompileSequence(document, sequenceId);

		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is StartNoteCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
		compiled.Schedule!.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(50),
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(240));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(360));
	}

	[Test]
	public void ScriptedTerminalSpeedChangesNextOrderButNotCompletedFinalRow()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(childId, "Simple child")
		{
			RowCount = 1,
			ChannelCount = 1,
		});
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "Terminal Speed")
		{
			RowCount = 1,
			Source = $"Note(0, 0, _O({childId.Value})); Speed(1, 3);",
		});
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Next order")
		{
			RowCount = 1,
			ChannelCount = 2,
		};
		second.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId songId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(songId, "Song");
		sequence.Entries.Add(new SequenceEntry(firstId));
		sequence.Entries.Add(new SequenceEntry(secondId));
		document.Add(sequence);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompileSequence(document, songId);

		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is SetSpeedCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(180));
	}

	[Test]
	public void ScriptedSpeedChangesItsOwnCurrentRowBeforeFractionalEvents()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Speed-sensitive child")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(child);

		ObjectId rootId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(rootId, "Own row speed")
		{
			RowCount = 2,
			// Speed's fractional part is ignored, even though its command
			// appears after the nested start in the authored script.
			Source = $"Note(0, 0, _O({childId.Value})); Speed(0.5, 3); "
				+ "Cut(0.5, 1); Cut(1, 1);",
		});

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, rootId);

		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is SetSpeedCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.Zero);
		compiled.Schedule!.Where(e => e.Target == ChannelTarget.Physical(1)
				&& e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60));
		compiled.Schedule!.Single(e => e.Target == ChannelTarget.Physical(0)
				&& e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(60));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(120));
	}

	[Test]
	public void ScriptedChildSpeedDoesNotRetroactivelyResizeStartedParentRow()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Child speed")
		{
			RowCount = 2,
			Source = "Speed(1.5, 3); Cut(1.5, 0);",
		});
		ObjectId rootId = document.AllocateObjectId();
		DataPatternDefinition root = new(rootId, "Parent data")
		{
			RowCount = 3,
			ChannelCount = 2,
		};
		root.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		root.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
		root.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteCut();
		document.Add(root);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, rootId);

		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is SetSpeedCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		compiled.Schedule!.Single(e => e.Target == ChannelTarget.Physical(0)
				&& e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(150));
		compiled.Schedule!.Where(e => e.Target == ChannelTarget.Physical(1)
				&& e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(240));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(300));
	}

	[Test]
	public void SimultaneousScriptedSpeedChangesPreserveEachCursorsRowCapture()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Child changes speed")
		{
			RowCount = 2,
			Source = "Speed(1, 3); Cut(1.5, 0);",
		});
		ObjectId rootId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(rootId, "Parent changes speed")
		{
			RowCount = 3,
			Source = $"Note(0, 0, _O({childId.Value})); "
				+ "Speed(1, 4); Cut(2, 1);",
		});

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, rootId);

		compiled.Success.Should().BeTrue();
		NoteEvent[] speeds = compiled.Schedule!
			.Where(e => e.Commands.Any(c => c is SetSpeedCommand)).ToArray();
		speeds.Select(e => e.Offset.TimeOffset).Should().Equal(
			TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(120));
		speeds.SelectMany(e => e.Commands).OfType<SetSpeedCommand>()
			.Select(c => c.TicksPerRow).Should().Equal(4, 3);
		compiled.Schedule!.Single(e => e.Target == ChannelTarget.Physical(0)
				&& e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(150));
		compiled.Schedule!.Single(e => e.Target == ChannelTarget.Physical(1)
				&& e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(200));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(260));
	}

	[Test]
	public void ScriptedSpeedCrossingSequenceOrderAffectsUnstartedNextOrder()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Continuing child")
		{
			RowCount = 3,
			Source = "Speed(1.5, 3);",
		});
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "First order")
		{
			RowCount = 1,
			Source = $"Note(0, 0, _O({childId.Value}));",
		});
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Second order")
		{
			RowCount = 2,
			ChannelCount = 2,
		};
		second.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		second.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId songId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(songId, "Two orders");
		sequence.Entries.Add(new SequenceEntry(firstId));
		sequence.Entries.Add(new SequenceEntry(secondId));
		document.Add(sequence);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompileSequence(document, songId);

		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is SetSpeedCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		compiled.Schedule!.Where(e => e.Target == ChannelTarget.Physical(1)
				&& e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(180));
		compiled.PlaybackPositions.First(p => p.PatternId == secondId)
			.Offset.Should().Be(TimeSpan.FromMilliseconds(120));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void ScriptedChildTempoUsesRowStartNotFractionalEventTime()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Delayed scripted tempo")
		{
			RowCount = 2,
			// Tracker timing ignores the fractional row offset.
			Source = "Tempo(1.5, 250);",
		});

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 3,
			ChannelCount = 2,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
		parent.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteCut();
		document.Add(parent);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);

		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is SetTempoCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		compiled.Schedule!.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(180));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void SimultaneousScriptedParentAndChildTempoUseStableCursorOrder()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Script child")
		{
			RowCount = 2,
			Source = "Tempo(1, 250);",
		});

		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Script parent")
		{
			RowCount = 3,
			Source = $"Note(0, 0, _O({childId.Value})); Tempo(1, 200); Cut(2, 1);",
		});

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);

		compiled.Success.Should().BeTrue();
		NoteEvent[] tempoEvents = compiled.Schedule!
			.Where(e => e.Commands.Any(c => c is SetTempoCommand)).ToArray();
		tempoEvents.Select(e => e.Offset.TimeOffset).Should().Equal(
			TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(120));
		tempoEvents.SelectMany(e => e.Commands).OfType<SetTempoCommand>()
			.Select(c => c.TicksPerDiachron).Should().Equal(200, 250);
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void EqualTickScriptedChildTemposFollowMappedChannelOrder()
	{
		SongDocument document = new();
		ObjectId lowerId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(lowerId, "Lower channel")
		{
			RowCount = 1,
			Source = "Tempo(0.5, 250);",
		});
		ObjectId higherId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(higherId, "Higher channel")
		{
			RowCount = 1,
			Source = "Tempo(0.5, 200);",
		});
		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Simultaneous script starts")
		{
			RowCount = 2,
			// Reverse script emission order deliberately: channel base wins.
			Source = $"Note(0, 1, _O({higherId.Value})); "
				+ $"Note(0, 0, _O({lowerId.Value})); Cut(1, 2);",
		});

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);

		compiled.Success.Should().BeTrue();
		NoteEvent[] tempoEvents = compiled.Schedule!
			.Where(e => e.Commands.Any(c => c is SetTempoCommand)).ToArray();
		tempoEvents.Select(e => e.Offset.TimeOffset).Should().Equal(
			TimeSpan.Zero, TimeSpan.Zero);
		tempoEvents.SelectMany(e => e.Commands).OfType<SetTempoCommand>()
			.Select(c => c.TicksPerDiachron).Should().Equal(250, 200);
		compiled.Schedule!.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(75));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(150));
	}

	[Test]
	public void ScriptedTerminalChildNoteFollowsSharedTempoChange()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Terminal note")
		{
			RowCount = 2,
			Source = $"Note(2, 0, _O({sampleId.Value}));",
		});

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent tempo change")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(parent);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);

		compiled.Success.Should().BeTrue();
		NoteEvent terminal = compiled.Schedule!.Single(e =>
			e.Commands.Any(c => c is StartNoteCommand));
		terminal.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void ScriptedTerminalEventStartsGrandchildBeyondParentEnd()
	{
		SongDocument document = new();
		ObjectId grandchildId = document.AllocateObjectId();
		DataPatternDefinition grandchild = new(grandchildId, "Grandchild")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		grandchild.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(grandchild);

		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Terminal invocation")
		{
			RowCount = 2,
			Source = $"Note(2, 0, _O({grandchildId.Value}));",
		});

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Short parent")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(parent);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);

		compiled.Success.Should().BeTrue();
		NoteEvent cut = compiled.Schedule!.Single(e =>
			e.Commands.Any(c => c is NoteCutCommand));
		cut.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(240));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(300));
	}

	[Test]
	public void FractionalScriptedInvocationContinuesAcrossSequenceOrderBoundary()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Long-running child")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(child);

		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "Fractional order")
		{
			RowCount = 2,
			Source = $"Note(0.5, 0, _O({childId.Value}));",
		});
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Next order")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		second.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Song");
		sequence.Entries.Add(new SequenceEntry(firstId));
		sequence.Entries.Add(new SequenceEntry(secondId));
		document.Add(sequence);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompileSequence(document, sequenceId);
		compiled.Success.Should().BeTrue();
		NoteEvent cut = compiled.Schedule!.Single(e =>
			e.Commands.Any(c => c is NoteCutCommand));
		cut.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(210));
		compiled.PlaybackPositions.Single(p => p.PatternId == secondId)
			.Offset.Should().Be(TimeSpan.FromMilliseconds(210));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(300));
	}

	[Test]
	public void ScriptedFractionalParentStartsIndependentChildAtExactTick()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Delayed child tempo")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Fractional source")
		{
			RowCount = 3,
			Source = $"Note(0.5, 0, _O({childId.Value})); "
				+ "Cut(1.5, 1); Cut(2.5, 1);",
		});

		SongScheduleCompilationResult compilation =
			SongScheduleCompiler.CompilePattern(document, parentId);
		compilation.Success.Should().BeTrue();
		NoteEvent[] cuts = compilation.Schedule!
			.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.OrderBy(e => e.Offset.TimeOffset).ToArray();
		cuts.Select(x => x.Offset.TimeOffset).Should().Equal(
			TimeSpan.FromMilliseconds(180),
			TimeSpan.FromMilliseconds(240));
		compilation.Schedule!.Where(e => e.Commands.Any(c => c is SetTempoCommand))
			.Select(e => e.Offset.TimeOffset)
			.Should().Equal(TimeSpan.FromMilliseconds(180));
	}

	[Test]
	public void ParentTempoMovesScriptedChildFractionalNoteInSharedTickDomain()
	{
		SongDocument document = new();
		ObjectId sampleId = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Fractional child")
		{
			RowCount = 3,
			Source = $"Note(1.5, 0, _O({sampleId.Value}));",
		});

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Tempo before child note")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(parent);

		SongScheduleCompilationResult compilation =
			SongScheduleCompiler.CompilePattern(document, parentId);
		compilation.Success.Should().BeTrue();
		NoteEvent note = compilation.Schedule!.Single(e =>
			e.Commands.Any(c => c is StartNoteCommand));
		note.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(150));
	}

	[Test]
	public void ScriptedChildFractionalNoteDoesNotExecuteBeforeParentRow()
	{
		SongDocument document = new();
		ObjectId soundId = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Fractional child")
		{
			RowCount = 2,
			Source = $"Note(1.5, 0, _O({soundId.Value}));",
		});
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(parent);

		SongScheduleCompilationResult compilation =
			SongScheduleCompiler.CompilePattern(document, parentId);
		compilation.Success.Should().BeTrue();
		NoteEvent note = compilation.Schedule!.Single(e =>
			e.Commands.Any(c => c is StartNoteCommand));
		note.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
		NoteEvent cut = compilation.Schedule!.Single(e =>
			e.Commands.Any(c => c is NoteCutCommand));
		cut.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
	}

	[Test]
	public void ChildTempoAtFractionalParentPositionChangesRemainingRowLength()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Immediate tempo")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Mid-row child")
		{
			RowCount = 2,
			Source = $"Note(0.5, 0, _O({childId.Value})); Cut(1.0, 1);",
		});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(document, parentId);
		result.Success.Should().BeTrue();
		result.Diagnostics.Should().BeEmpty();
		NoteEvent cut = result.Schedule!.Single(e =>
			e.Commands.Any(command => command is NoteCutCommand));
		// 3 ticks at tempo 125 (60 ms), then 3 at tempo 250 (30 ms).
		cut.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(90));
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(150));
		NoteEvent tempo = result.Schedule!.Single(e =>
			e.Commands.Any(command => command is SetTempoCommand));
		tempo.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(60));
	}

	[Test]
	public void SimultaneousParentEventAndChildTempoShareTimestamp()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Child tempo")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Same time")
		{
			RowCount = 2,
			// Deliberately emit the other-channel event before the child.
			Source = $"Cut(0.5, 1); Note(0.5, 0, _O({childId.Value})); Cut(1, 1);",
		});
		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(document, parentId);

		result.Success.Should().BeTrue();
		NoteEvent[] cuts = result.Schedule!
			.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.OrderBy(e => e.Offset.TimeOffset).ToArray();
		cuts.Should().HaveCount(2);
		cuts[0].Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(60));
		cuts[1].Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(90));
		result.Schedule!.Single(e =>
			e.Commands.Any(c => c is SetTempoCommand))
			.Offset.TimeOffset.Should().Be(cuts[0].Offset.TimeOffset);
	}

	[Test]
	public void SimultaneousFlattenedTempoChangesFollowMappedPhysicalChannelOrder()
	{
		SongDocument document = new();
		ObjectId lowerId = document.AllocateObjectId();
		DataPatternDefinition lower = new(lowerId, "Channel zero")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		lower.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetTempoPatternEffect(200));
		document.Add(lower);

		ObjectId higherId = document.AllocateObjectId();
		DataPatternDefinition higher = new(higherId, "Channel one")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		higher.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(higher);

		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Simultaneous nested tempo")
		{
			RowCount = 2,
			// Reverse source emission order: mapped physical channel
			// order determines same-time effects.
			Source = $"Note(0.5, 1, _O({higherId.Value})); "
				+ $"Note(0.5, 0, _O({lowerId.Value})); Cut(1, 2);",
		});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(document, parentId);

		result.Success.Should().BeTrue();
		NoteEvent[] tempoChanges = result.Schedule!
			.Where(e => e.Commands.Any(c => c is SetTempoCommand))
			.ToArray();
		tempoChanges.Should().HaveCount(2);
		tempoChanges.Select(e =>
			((SetTempoCommand)e.Commands.Single()).TicksPerDiachron)
			.Should().Equal(200.0, 250.0);
		tempoChanges.Select(e => e.Offset.TimeOffset)
			.Should().OnlyContain(t => t == TimeSpan.FromMilliseconds(60));
		result.Schedule!.Single(e => e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(90));
	}

	[Test]
	public void DelayedFlattenedTempoChangesParentRemainderWithoutBackdating()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Late tempo")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(child);
		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Parent")
		{
			RowCount = 2,
			Source = $"Note(0.5, 0, _O({childId.Value})); Cut(1, 1);",
		});
		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);
		compiled.Success.Should().BeTrue();
		// Parent row one finishes at 210 ms, but the child extends
		// to 240 ms, and export must retain that longer logical tail.
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
		compiled.Schedule!.Single(e =>
			e.Commands.Any(c => c is NoteCutCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		compiled.Schedule!.Single(e =>
			e.Commands.Any(c => c is SetTempoCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
	}

	[Test]
	public void IndependentlyAdvancingChildCarriesSourceMemoryAcrossSequenceOrders()
	{
		SongDocument document = new();
		ObjectId sourceAtStart = document.AllocateObjectId();
		ObjectId sourceLater = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Source selector")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		PatternCell firstChild = child.Grid.GetOrCreateCell(0, 0);
		firstChild.SourceId = sourceAtStart;
		firstChild.Effects.Add(new SetSpeedPatternEffect(9));
		child.Grid.GetOrCreateCell(1, 0).SourceId = sourceLater;
		document.Add(child);

		ObjectId firstId = document.AllocateObjectId();
		DataPatternDefinition first = new(firstId, "First order")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		first.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		document.Add(first);

		ObjectId nextId = document.AllocateObjectId();
		DataPatternDefinition next = new(nextId, "Next order")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		next.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();
		next.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote();
		document.Add(next);

		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Arrangement");
		sequence.Entries.Add(new SequenceEntry(firstId));
		sequence.Entries.Add(new SequenceEntry(nextId));
		document.Add(sequence);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, sequenceId);
		result.Success.Should().BeTrue();
		StartNoteCommand[] starts = result.Schedule!
			.SelectMany(e => e.Commands).OfType<StartNoteCommand>().ToArray();
		starts.Select(s => s.SourceId).Should()
			.Equal(sourceAtStart, sourceLater);
		result.PlaybackPositions
			.Where(p => p.PatternId == nextId)
			.Select(p => p.Offset).Should().Equal(
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(300));
	}

	[Test]
	public void FutureChildSourceSelectionDoesNotLeakIntoEarlierParentRow()
	{
		SongDocument document = new();
		ObjectId earlier = document.AllocateObjectId();
		ObjectId later = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Independent child")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(0, 0).SourceId = earlier;
		child.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new SetSpeedPatternEffect(9));
		child.Grid.GetOrCreateCell(1, 0).SourceId = later;
		document.Add(child);
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Parent")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote();
		parent.Grid.GetOrCreateCell(2, 0).Note = new StartPatternNote();
		document.Add(parent);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);
		compiled.Success.Should().BeTrue();
		StartNoteCommand[] notes = compiled.Schedule!
			.SelectMany(e => e.Commands).OfType<StartNoteCommand>().ToArray();
		notes.Select(n => n.SourceId).Should().Equal(earlier, later);
	}

	[Test]
	public void FutureChildEffectMemoryIsNotVisibleBeforeItsOwnRow()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Delayed effect memory")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		PatternCell first = child.Grid.GetOrCreateCell(0, 0);
		first.Effects.Add(new SetSpeedPatternEffect(9));
		first.Effects.Add(new TrackerVolumeSlidePatternEffect(0x20));
		child.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0x30));
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Recall around child row")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0));
		parent.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new TrackerVolumeSlidePatternEffect(0));
		document.Add(parent);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompilePattern(document, parentId);
		result.Success.Should().BeTrue();
		SetNoteVolumeSlideCommand early = result.Schedule!
			.Single(e => e.Offset.TimeOffset == TimeSpan.FromMilliseconds(120)
				&& e.Commands.Any(c => c is SetNoteVolumeSlideCommand))
			.Commands.OfType<SetNoteVolumeSlideCommand>().Single();
		SetNoteVolumeSlideCommand later = result.Schedule!
			.Single(e => e.Offset.TimeOffset == TimeSpan.FromMilliseconds(300)
				&& e.Commands.Any(c => c is SetNoteVolumeSlideCommand))
			.Commands.OfType<SetNoteVolumeSlideCommand>().Single();

		early.TrackerUnitsPerTick.Should().BeLessThan(
			later.TrackerUnitsPerTick);
	}

	[Test]
	public void InterveningParentTempoMovesFutureChildRowInSharedTickDomain()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Late child tempo")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new SetTempoPatternEffect(200));
		document.Add(child);
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Intervening tempo")
		{
			RowCount = 4,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(new SetTempoPatternEffect(250));
		parent.Grid.GetOrCreateCell(3, 0).Note = new PatternNoteCut();
		document.Add(parent);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);
		compiled.Success.Should().BeTrue();
		NoteEvent[] tempos = compiled.Schedule!
			.Where(e => e.Commands.Any(c => c is SetTempoCommand))
			.OrderBy(e => e.Offset.TimeOffset).ToArray();
		tempos.Select(e => e.Offset.TimeOffset).Should().Equal(
			TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(180));
		NoteEvent cut = compiled.Schedule!.Single(e =>
			e.Commands.Any(c => c is NoteCutCommand));
		cut.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(255));
	}

	[Test]
	public void DeferredChildTempoIsAppliedOnlyWhenParentReachesChildRow()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Tempo on next child row")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(child);

		ObjectId parentId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(parentId, "Overlapping parent")
		{
			RowCount = 3,
			Source = $"Note(0.5, 0, _O({childId.Value})); "
				+ "Cut(1.0, 1); Cut(1.5, 1); Cut(2.0, 1);",
		});

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);

		compiled.Success.Should().BeTrue();
		NoteEvent[] cuts = compiled.Schedule!
			.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.OrderBy(e => e.Offset.TimeOffset).ToArray();
		cuts.Should().HaveCount(3);
		// The child's row one begins at t=60+120=180 ms.
		// Parent row one starts at 120, and its second half runs at 250.
		cuts.Select(e => e.Offset.TimeOffset)
			.Should().Equal(
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(180),
				TimeSpan.FromMilliseconds(210));
		compiled.Schedule!.Single(e =>
			e.Commands.Any(c => c is SetTempoCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
	}

	[Test]
	public void DeferredChildTempoAppliesAcrossSubsequentSequencePattern()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Late child tempo")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(child);
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "Start child")
		{
			RowCount = 1,
			Source = $"Note(0.5, 0, _O({childId.Value}));",
		});
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Continue")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		second.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId seqId = document.AllocateObjectId();
		DataSequenceDefinition root = new(seqId, "Root");
		root.Entries.Add(new SequenceEntry(firstId));
		root.Entries.Add(new SequenceEntry(secondId));
		document.Add(root);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, seqId);

		result.Success.Should().BeTrue();
		NoteEvent tempo = result.Schedule!.Single(e =>
			e.Commands.Any(c => c is SetTempoCommand));
		tempo.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
		NoteEvent cut = result.Schedule!.Single(e =>
			e.Commands.Any(c => c is NoteCutCommand));
		cut.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(210));
	}

	[Test]
	public void ParentTempoMovesLaterChildRowBoundaryInTickDomain()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Later child tempo")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		// Parent changes tempo at row 1, strictly before this child
		// reaches its row-2 tempo change. Child timings would need
		// interleaving to know the new wall-clock location.
		child.Grid.GetOrCreateCell(2, 0).Effects.Add(
			new SetTempoPatternEffect(250));
		document.Add(child);
		ObjectId parentId = document.AllocateObjectId();
		DataPatternDefinition parent = new(parentId, "Earlier parent tempo")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		parent.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(sourceId: childId);
		parent.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new SetTempoPatternEffect(200));
		document.Add(parent);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, parentId);
		compiled.Success.Should().BeTrue();
		compiled.Schedule!.Where(e => e.Commands.Any(c =>
			c is SetTempoCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(120),
				TimeSpan.FromMilliseconds(195));
		compiled.Duration.Should().Be(TimeSpan.FromMilliseconds(255));
	}

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
		// The child changes tempo at the shared row-start boundary:
		// both the invoking row and following row use the new 60ms duration.
		cutTime.Should().Be(TimeSpan.FromMilliseconds(120));
	}

	[Test]
	public void SkippedDataPatternSourceSelectionDoesNotLeakIntoCompiledNotes()
	{
		SongDocument document = new();
		ObjectId source = document.AllocateObjectId();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Start on row 1")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).SourceId = source;
		pattern.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote();
		document.Add(pattern);

		SongScheduleCompilationResult full =
			SongScheduleCompiler.CompilePattern(document, patternId);
		full.Success.Should().BeTrue();
		full.Schedule!.SelectMany(e => e.Commands).OfType<StartNoteCommand>()
			.Should().ContainSingle().Which.SourceId.Should().Be(source);

		SongScheduleCompilationResult skipped =
			SongScheduleCompiler.CompilePattern(document, patternId, startRow: 1);
		skipped.Success.Should().BeTrue();
		skipped.Schedule!.SelectMany(e => e.Commands).OfType<StartNoteCommand>()
			.Should().BeEmpty();
	}

	[Test]
	public void CompiledDataPatternTonePortamentoResolvesDeferredRememberedSource()
	{
		SongDocument document = new();
		ObjectId source = document.AllocateObjectId();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern = new(patternId, "Portamento source")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		pattern.Grid.GetOrCreateCell(0, 0).SourceId = source;
		PatternCell target = pattern.Grid.GetOrCreateCell(1, 0);
		target.Note = new StartPatternNote(pitchMultiplier: 2.0);
		target.Effects.Add(new TonePortamentoPatternEffect(0x10));
		document.Add(pattern);

		SongScheduleCompilationResult compiled =
			SongScheduleCompiler.CompilePattern(document, patternId);

		compiled.Success.Should().BeTrue();
		SetTonePortamentoCommand slide = compiled.Schedule!
			.SelectMany(e => e.Commands)
			.OfType<SetTonePortamentoCommand>()
			.Single(command => command.TargetNote is not null);
		slide.TargetNote!.SourceId.Should().Be(source);
		slide.TargetNote.PitchMultiplier.Should().Be(2.0);
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
		result.Schedule!.Single(e =>
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
	public void DataSequenceStartRowSkipsEarlierTempoAndRetainsAbsoluteSourceRows()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Set shared tempo")
		{
			RowCount = 1,
			Source = "Tempo(0, 250);",
		});
		ObjectId firstId = document.AllocateObjectId();
		DataPatternDefinition first = new(firstId, "First")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		first.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sourceId: childId);
		document.Add(first);
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Skip the first two rows")
		{
			RowCount = 4,
			ChannelCount = 2,
		};
		second.Grid.GetOrCreateCell(0, 1).Effects.Add(new SetTempoPatternEffect(200));
		second.Grid.GetOrCreateCell(1, 1).Effects.Add(new SetSpeedPatternEffect(3));
		second.Grid.GetOrCreateCell(2, 1).Note = new PatternNoteCut();
		second.Grid.GetOrCreateCell(3, 1).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Jump in");
		sequence.Entries.Add(new SequenceEntry(firstId));
		sequence.Entries.Add(new SequenceEntry(secondId, startRow: 2));
		document.Add(sequence);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, sequenceId);

		result.Success.Should().BeTrue();
		result.Schedule!.SelectMany(e => e.Commands).OfType<SetTempoCommand>()
			.Select(c => c.TicksPerDiachron).Should().Equal(250);
		result.Schedule!.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(120));
		result.PlaybackPositions.Where(p => p.PatternId == secondId)
			.Select(p => p.PatternRow).Should().Equal(2, 3);
		result.PlaybackPositions.First(p => p.PatternId == secondId)
			.Offset.Should().Be(TimeSpan.FromMilliseconds(60));
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(180));
	}

	[Test]
	public void ScriptSequencePlayStartRowRetainsFractionalEventsAndSharedSpeed()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Change speed")
		{
			RowCount = 2,
			// Child changes the shared speed at its later row boundary.
			Source = "Speed(1, 3);",
		});
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "Invoking")
		{
			RowCount = 1,
			Source = $"Note(0, 0, _O({childId.Value}));",
		});
		ObjectId soundId = document.AllocateObjectId();
		ObjectId secondId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(secondId, "Enter at row two")
		{
			RowCount = 4,
			Source = $"Tempo(0, 500); Cut(1.5, 1); "
				+ $"Note(2.5, 1, _O({soundId.Value})); Off(3, 1);",
		});
		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(new ScriptSequenceDefinition(sequenceId, "Generated")
		{
			Source = $"if (sequenceIndex == 0) return Play(_O({firstId.Value})); if (sequenceIndex == 1) return Play(_O({secondId.Value}), 2); return null;",
		});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, sequenceId);

		result.Success.Should().BeTrue();
		result.Schedule!.Single(e => e.Commands.Any(c => c is SetSpeedCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		result.Schedule!.Single(e => e.Commands.Any(c => c is StartNoteCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(150));
		result.Schedule!.Single(e => e.Commands.Any(c => c is NoteOffCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(180));
		result.Schedule!.Should().NotContain(e => e.Commands.Any(c => c is NoteCutCommand));
		result.Schedule!.Should().NotContain(e => e.Commands.OfType<SetTempoCommand>()
			.Any(c => c.TicksPerDiachron == 500));
		result.PlaybackPositions.Where(p => p.PatternId == secondId)
			.Select(p => p.PatternRow).Should().Equal(2, 3);
		result.PlaybackPositions.First(p => p.PatternId == secondId)
			.SequenceEntryIndex.Should().Be(1);
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void SkippedDataSourceSelectionDoesNotOverrideRememberedSource()
	{
		SongDocument document = new();
		ObjectId remembered = document.AllocateObjectId();
		ObjectId skipped = document.AllocateObjectId();
		ObjectId childId = document.AllocateObjectId();
		DataPatternDefinition child = new(childId, "Remember source")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		child.Grid.GetOrCreateCell(0, 0).SourceId = remembered;
		document.Add(child);
		ObjectId firstId = document.AllocateObjectId();
		DataPatternDefinition first = new(firstId, "Invoke selector")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		first.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sourceId: childId);
		document.Add(first);
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Skipped selection")
		{
			RowCount = 3,
			ChannelCount = 1,
		};
		second.Grid.GetOrCreateCell(0, 0).SourceId = skipped;
		second.Grid.GetOrCreateCell(1, 0).Note = new StartPatternNote();
		second.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Remember");
		sequence.Entries.Add(new SequenceEntry(firstId));
		sequence.Entries.Add(new SequenceEntry(secondId, startRow: 1));
		document.Add(sequence);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, sequenceId);

		result.Success.Should().BeTrue();
		result.Schedule!.SelectMany(e => e.Commands).OfType<StartNoteCommand>()
			.Should().ContainSingle().Which.SourceId.Should().Be(remembered);
		result.Schedule!.Single(e => e.Commands.Any(c => c is StartNoteCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(360));
	}

	[Test]
	public void ScriptSequenceStartRowPreservesPositiveWallOffsetFromSkippedRow()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(childId, "Empty nested")
		{
			RowCount = 1,
		});
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "Invoke nested")
		{
			RowCount = 1,
			Source = $"Note(0, 0, _O({childId.Value}));",
		});
		ObjectId secondId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(secondId, "Skipped with delayed cut")
		{
			RowCount = 2,
			Source = "Cut(0.5, 0, timeOffsetSeconds: 0.09); Cut(1.5, 1);",
		});
		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(new ScriptSequenceDefinition(sequenceId, "Offset beyond entry")
		{
			Source = $"if (sequenceIndex == 0) return Play(_O({firstId.Value})); if (sequenceIndex == 1) return Play(_O({secondId.Value}), 1); return null;",
		});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, sequenceId);

		result.Success.Should().BeTrue();
		result.Schedule!.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(180));
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void SequenceStartRowAtOrBeyondPatternEndAdvancesToNextOrderImmediately()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(childId, "Unused child")
		{
			RowCount = 1,
		});
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "Skipped entirely")
		{
			RowCount = 1,
			Source = $"Note(0, 0, _O({childId.Value}));",
		});
		ObjectId nextId = document.AllocateObjectId();
		DataPatternDefinition next = new(nextId, "Next")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		next.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		document.Add(next);
		ObjectId sequenceId = document.AllocateObjectId();
		DataSequenceDefinition sequence = new(sequenceId, "Skip all");
		sequence.Entries.Add(new SequenceEntry(firstId, startRow: 5));
		sequence.Entries.Add(new SequenceEntry(nextId));
		document.Add(sequence);

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, sequenceId);

		result.Success.Should().BeTrue();
		result.Schedule!.Should().ContainSingle()
			.Which.Offset.TimeOffset.Should().Be(TimeSpan.Zero);
		result.PlaybackPositions.Should().NotContain(p => p.PatternId == firstId);
		result.PlaybackPositions.First(p => p.PatternId == nextId)
			.SequenceEntryIndex.Should().Be(1);
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(120));
	}

	[Test]
	public void ScriptSequenceSchedulesChildSpeedAndLaterOrderChronologically()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Continuing speed child")
		{
			RowCount = 3,
			Source = "Speed(1.5, 3);",
		});
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "First order")
		{
			RowCount = 1,
			Source = $"Note(0, 0, _O({childId.Value}));",
		});
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Following data order")
		{
			RowCount = 2,
			ChannelCount = 2,
		};
		second.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		second.Grid.GetOrCreateCell(1, 1).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(new ScriptSequenceDefinition(sequenceId, "Script arrangement")
		{
			Source = $"if (sequenceIndex == 0) return Play(_O({firstId.Value})); if (sequenceIndex == 1) return Play(_O({secondId.Value})); return null;",
		});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, sequenceId);

		result.Success.Should().BeTrue();
		result.Schedule!.Single(e => e.Commands.Any(c => c is SetSpeedCommand))
			.Offset.TimeOffset.Should().Be(TimeSpan.FromMilliseconds(120));
		result.Schedule!.Where(e => e.Target == ChannelTarget.Physical(1)
				&& e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(180));
		result.PlaybackPositions.First(p => p.PatternId == secondId)
			.SequenceEntryIndex.Should().Be(1);
		result.PlaybackPositions.First(p => p.PatternId == secondId)
			.Offset.Should().Be(TimeSpan.FromMilliseconds(120));
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(240));
	}

	[Test]
	public void ScriptSequenceKeepsDeferredChildStartAcrossGeneratedOrders()
	{
		SongDocument document = new();
		ObjectId nestedId = document.AllocateObjectId();
		DataPatternDefinition nested = new(nestedId, "Late nested Pattern")
		{
			RowCount = 2,
			ChannelCount = 1,
		};
		nested.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteCut();
		document.Add(nested);
		ObjectId firstId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(firstId, "Delayed launch")
		{
			RowCount = 1,
			Source = $"Note(0.5, 0, _O({nestedId.Value}), timeOffsetSeconds: 0.12);",
		});
		ObjectId secondId = document.AllocateObjectId();
		DataPatternDefinition second = new(secondId, "Second order")
		{
			RowCount = 1,
			ChannelCount = 2,
		};
		second.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		document.Add(second);
		ObjectId seqId = document.AllocateObjectId();
		document.Add(new ScriptSequenceDefinition(seqId, "Late invocation")
		{
			Source = $"if (sequenceIndex == 0) return Play(_O({firstId.Value})); if (sequenceIndex == 1) return Play(_O({secondId.Value})); return null;",
		});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, seqId);

		result.Success.Should().BeTrue();
		result.Schedule!.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(300));
		result.PlaybackPositions.First(p => p.PatternId == secondId)
			.Offset.Should().Be(TimeSpan.FromMilliseconds(120));
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(420));
	}

	[Test]
	public void ScriptSequenceRepeatedPlayEntriesKeepIndependentInvocations()
	{
		SongDocument document = new();
		ObjectId childId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(childId, "Speed state")
		{
			RowCount = 1,
			Source = "Speed(0, 3);",
		});
		ObjectId patternId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(patternId, "Repeatable")
		{
			RowCount = 2,
			Source = $"Note(0, 0, _O({childId.Value})); Cut(1, 1);",
		});
		ObjectId seqId = document.AllocateObjectId();
		document.Add(new ScriptSequenceDefinition(seqId, "Repeated patterns")
		{
			Source = $"if (sequenceIndex == 0) return Play(_O({patternId.Value})); if (sequenceIndex == 1) return Play(_O({patternId.Value})); return null;",
		});

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(document, seqId);

		result.Success.Should().BeTrue();
		result.Schedule!.Where(e => e.Commands.Any(c => c is NoteCutCommand))
			.Select(e => e.Offset.TimeOffset).Should().Equal(
				TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(240));
		result.PlaybackPositions.Where(p => p.PatternId == patternId
				&& p.PatternRow == 0)
			.Select(p => p.SequenceEntryIndex).Should().Equal(0, 1);
		result.Duration.Should().Be(TimeSpan.FromMilliseconds(300));
	}


	[Test]
	public void ScriptSequenceFallbackDoesNotReexecuteRandomPlayScript()
	{
		SongDocument document = new();
		ObjectId leftId = document.AllocateObjectId();
		DataPatternDefinition left = new(leftId, "Left")
		{
			RowCount = 1,
			ChannelCount = 1,
		};
		left.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
		document.Add(left);
		ObjectId rightId = document.AllocateObjectId();
		DataPatternDefinition right = new(rightId, "Right")
		{
			RowCount = 1,
			ChannelCount = 2,
		};
		right.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteCut();
		document.Add(right);
		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(new ScriptSequenceDefinition(sequenceId, "Fallback")
		{
			Source = $"if (sequenceIndex != 0) return null; if (Random() < 0.5) return Play(_O({leftId.Value})); "
				+ $"return Play(_O({rightId.Value}));",
		});
		SequencingContext context = new();
		SequencingContext expected = new();
		expected.Random.NextDouble(); // One script execution, never two.

		SongScheduleCompilationResult result =
			SongScheduleCompiler.CompileSequence(
				document, sequenceId, context: context);

		result.Success.Should().BeTrue();
		result.Schedule!.Should().ContainSingle();
		context.Random.NextDouble().Should().Be(expected.Random.NextDouble());
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
				Source = $"return sequenceIndex == 0 ? Play(_O({patternId.Value})) : null;",
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
					$"if (sequenceIndex == 0) return Play(_O({firstPatternId.Value})); "
						+ $"if (sequenceIndex == 1) return Play(_O({secondPatternId.Value})); return null;",
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

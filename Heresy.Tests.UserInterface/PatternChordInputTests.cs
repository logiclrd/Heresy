using System;
using System.Linq;

using AwesomeAssertions;

using Avalonia.Input;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternChordInputTests
{
	[TestCase(PatternChordType.Major, new[] { 0, 4, 7 })]
	[TestCase(PatternChordType.Minor, new[] { 0, 3, 7 })]
	[TestCase(PatternChordType.DominantSeventh, new[] { 0, 4, 7, 10 })]
	[TestCase(PatternChordType.MajorSeventh, new[] { 0, 4, 7, 11 })]
	[TestCase(PatternChordType.MinorSeventh, new[] { 0, 3, 7, 10 })]
	[TestCase(PatternChordType.HalfDiminishedSeventh, new[] { 0, 3, 6, 10 })]
	[TestCase(PatternChordType.DiminishedSeventh, new[] { 0, 3, 6, 9 })]
	public void ChordTypesUseExpectedSemitoneIntervals(
		PatternChordType type,
		int[] expected)
	{
		PatternChordInputState state = new();

		state.Select(type);

		state.ToneOffsets.Should().Equal(expected);
		state.EnabledTones.Should().OnlyContain(enabled => enabled);
	}

	[Test]
	public void RotationUsesAscendingInversionVoicing()
	{
		PatternChordInputState state = new();
		state.Select(PatternChordType.Major);

		state.RotateFirstToEnd();
		state.ToneOffsets.Should().Equal(4, 7, 12);

		state.RotateLastToBeginning();
		state.ToneOffsets.Should().Equal(0, 4, 7);

		state.RotateLastToBeginning();
		state.ToneOffsets.Should().Equal(-5, 0, 4);
	}

	[Test]
	public void AddToneRepeatsChordThroughHigherOctavesAndRemoveIsOneAtATime()
	{
		PatternChordInputState state = new();
		state.Select(PatternChordType.Major);

		state.AddTone();
		state.ToneOffsets.Should().Equal(0, 4, 7, 12);
		state.AddTone();
		state.ToneOffsets.Should().Equal(0, 4, 7, 12, 16);

		state.RemoveTone();
		state.ToneOffsets.Should().Equal(0, 4, 7, 12);
	}

	[Test]
	public void EnabledToneStateIsRememberedByIndexAcrossChordChanges()
	{
		PatternChordInputState state = new();
		state.Select(PatternChordType.Major);
		state.ToggleTone(1).Should().BeTrue();

		state.Select(PatternChordType.DominantSeventh);

		state.EnabledTones.Should().Equal(true, false, true, true);

		state.Select(PatternChordType.Minor);
		state.EnabledTones.Should().Equal(true, false, true);
	}

	[Test]
	public void RemovedAndReaddedToneRetainsEnabledStateByIndex()
	{
		PatternChordInputState state = new();
		state.Select(PatternChordType.Major);
		state.AddTone();
		state.ToggleTone(3);
		state.EnabledTones[3].Should().BeFalse();

		state.RemoveTone();
		state.AddTone();

		state.EnabledTones[3].Should().BeFalse();
	}

	[Test]
	public void EnableAllReenablesEveryCurrentTone()
	{
		PatternChordInputState state = new();
		state.Select(PatternChordType.DominantSeventh);
		state.ToggleTone(0);
		state.ToggleTone(2);

		state.EnableAll();

		state.EnabledTones.Should().OnlyContain(enabled => enabled);
	}

	[Test]
	public void RootedStatusUsesConcreteTrackerNoteNames()
	{
		PatternChordInputState state = new();
		state.Select(PatternChordType.MajorSeventh);
		state.SetRoot(
			relativeSemitoneFromC4: 1);

		state.GetStatusTones()
			.Select(tone => tone.NoteText)
			.Should().Equal(
				"C#4",
				"F-4",
				"G#4",
				"C-5");
	}

	[Test]
	public void DisabledStatusToneRemainsPresent()
	{
		PatternChordInputState state = new();
		state.Select(PatternChordType.Major);
		state.ToggleTone(1);
		state.SetRoot(0);

		PatternChordStatusTone[] tones =
			[.. state.GetStatusTones()];

		tones.Should().HaveCount(3);
		tones[1].NoteText.Should().Be("E-4");
		tones[1].Enabled.Should().BeFalse();
	}

	[TestCase(PhysicalKey.Z, PatternChordType.Major)]
	[TestCase(PhysicalKey.X, PatternChordType.Minor)]
	[TestCase(PhysicalKey.C, PatternChordType.DominantSeventh)]
	[TestCase(PhysicalKey.V, PatternChordType.MajorSeventh)]
	[TestCase(PhysicalKey.B, PatternChordType.MinorSeventh)]
	[TestCase(PhysicalKey.N, PatternChordType.HalfDiminishedSeventh)]
	[TestCase(PhysicalKey.M, PatternChordType.DiminishedSeventh)]
	public void CtrlAltBottomRowChoosesChordType(
		PhysicalKey key,
		PatternChordType expected)
	{
		PatternChordKeyboard.TryGetCommand(
			key,
			KeyModifiers.Control | KeyModifiers.Alt,
			out PatternChordCommand command).Should().BeTrue();

		command.Kind.Should().Be(PatternChordCommandKind.Select);
		command.ChordType.Should().Be(expected);
	}

	[Test]
	public void CtrlAltChordEditingCommandsUseReservedPhysicalKeys()
	{
		PatternChordKeyboard.TryGetCommand(
			PhysicalKey.Minus,
			KeyModifiers.Control | KeyModifiers.Alt,
			out PatternChordCommand minus).Should().BeTrue();
		minus.Kind.Should().Be(
			PatternChordCommandKind.RotateFirstToEnd);

		PatternChordKeyboard.TryGetCommand(
			PhysicalKey.Equal,
			KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift,
			out PatternChordCommand plus).Should().BeTrue();
		plus.Kind.Should().Be(
			PatternChordCommandKind.RotateLastToBeginning);

		PatternChordKeyboard.TryGetCommand(
			PhysicalKey.NumPadMultiply,
			KeyModifiers.Control | KeyModifiers.Alt,
			out PatternChordCommand add).Should().BeTrue();
		add.Kind.Should().Be(PatternChordCommandKind.AddTone);

		PatternChordKeyboard.TryGetCommand(
			PhysicalKey.NumPadDivide,
			KeyModifiers.Control | KeyModifiers.Alt,
			out PatternChordCommand remove).Should().BeTrue();
		remove.Kind.Should().Be(PatternChordCommandKind.RemoveTone);

		PatternChordKeyboard.TryGetCommand(
			PhysicalKey.Equal,
			KeyModifiers.Control | KeyModifiers.Alt,
			out PatternChordCommand all).Should().BeTrue();
		all.Kind.Should().Be(PatternChordCommandKind.EnableAll);
	}

	[TestCase(PhysicalKey.Digit1, 0)]
	[TestCase(PhysicalKey.Digit2, 1)]
	[TestCase(PhysicalKey.Digit3, 2)]
	[TestCase(PhysicalKey.Digit4, 3)]
	[TestCase(PhysicalKey.Digit5, 4)]
	[TestCase(PhysicalKey.Digit6, 5)]
	[TestCase(PhysicalKey.Digit7, 6)]
	[TestCase(PhysicalKey.Digit8, 7)]
	[TestCase(PhysicalKey.Digit9, 8)]
	public void CtrlAltDigitsToggleChordTone(
		PhysicalKey key,
		int expectedIndex)
	{
		PatternChordKeyboard.TryGetCommand(
			key,
			KeyModifiers.Control | KeyModifiers.Alt,
			out PatternChordCommand command).Should().BeTrue();

		command.Kind.Should().Be(PatternChordCommandKind.ToggleTone);
		command.ToneIndex.Should().Be(expectedIndex);
	}

	[Test]
	public void ExtraUnexpectedModifiersDoNotSelectChord()
	{
		PatternChordKeyboard.TryGetCommand(
			PhysicalKey.Z,
			KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift,
			out _).Should().BeFalse();
	}

	[Test]
	public void ChordStateSnapshotPreservesVoicingRootAndEnableMask()
	{
		PatternChordInputState original = new();
		original.Select(PatternChordType.Major);
		original.AddTone();
		original.RotateFirstToEnd();
		original.ToggleTone(1);
		original.SetRoot(2);

		PatternChordInputSnapshot snapshot =
			original.CreateSnapshot();
		PatternChordInputState restored = new();
		restored.Restore(snapshot);

		restored.Type.Should().Be(PatternChordType.Major);
		restored.ToneOffsets.Should().Equal(4, 7, 12, 16);
		restored.EnabledTones.Should().Equal(true, false, true, true);
		restored.RootRelativeSemitone.Should().Be(2);
		restored.GetStatusTones()
			.Select(tone => tone.NoteText)
			.Should().Equal("F#4", "A-4", "D-5", "F#5");
	}

	[Test]
	public void EnterChordWritesEnabledTonesAcrossSuccessiveChannelsAndAdvancesOneRow()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Piano");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 4,
				channelCount: 5);
		PatternEffectCursor cursor =
			new(1, 1, PatternCellField.Note);
		PatternNoteInputState noteState =
			new(sample.Id, 4)
			{
				CurrentVolume = 0.5,
			};
		PatternChordInputState chordState = new();
		chordState.Select(PatternChordType.Major);
		chordState.ToggleTone(1);
		uint documentRevision =
			workspace.Document.DocumentRevision;
		uint audioRevision =
			workspace.Document.AudioRevision;

		PatternChordInputResult result =
			PatternChordEditor.TypeRootPhysical(
				workspace,
				pattern,
				cursor,
				noteState,
				chordState,
				PhysicalKey.Z);

		result.Handled.Should().BeTrue();
		result.Changed.Should().BeTrue();
		result.NoteApplied.Should().BeTrue();
		result.Channels.Should().Equal(1, 2);
		((StartPatternNote)pattern.Grid[1, 1]!.Note!)
			.PitchMultiplier.Should().BeApproximately(1.0, 1e-12);
		((StartPatternNote)pattern.Grid[1, 2]!.Note!)
			.PitchMultiplier.Should().BeApproximately(
				Math.Pow(2.0, 7.0 / 12.0),
				1e-12);
		pattern.Grid[1, 1]!.SourceId.Should().Be(sample.Id);
		pattern.Grid[1, 2]!.SourceId.Should().Be(sample.Id);
		pattern.Grid[1, 1]!.Volume.Should().Be(0.5);
		pattern.Grid[1, 2]!.Volume.Should().Be(0.5);
		cursor.Row.Should().Be(2);
		cursor.Channel.Should().Be(1);
		workspace.Document.DocumentRevision.Should().Be(
			documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(
			audioRevision + 1);
	}

	[Test]
	public void ChordEntryDropsTonesPastRightPatternEdge()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 2,
				channelCount: 3);
		PatternEffectCursor cursor =
			new(0, 2, PatternCellField.Note);
		PatternNoteInputState noteState =
			new(ObjectId.None, 4);
		PatternChordInputState chordState = new();
		chordState.Select(PatternChordType.DominantSeventh);

		PatternChordInputResult result =
			PatternChordEditor.TypeRootPhysical(
				workspace,
				pattern,
				cursor,
				noteState,
				chordState,
				PhysicalKey.Z);

		result.Channels.Should().Equal(2);
		pattern.Grid[0, 2]!.Note.Should().Be(
			new StartPatternNote());
	}

	[Test]
	public void ChordEntryUsesEditMaskForEveryEnabledDestination()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition oldSource = AddSample(workspace, "Old");
		SampleDefinition toolbarSource = AddSample(workspace, "Toolbar");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 2,
				channelCount: 4);
		for (int channel = 0; channel < 3; channel++)
		{
			PatternCell cell =
				pattern.Grid.GetOrCreateCell(0, channel);
			cell.SourceId = oldSource.Id;
			cell.Volume = 0.25;
		}
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Note);
		PatternNoteInputState noteState =
			new(toolbarSource.Id, 4)
			{
				CurrentVolume = 0.75,
				EditMask =
					PatternEditMask.Note
						| PatternEditMask.Volume,
			};
		PatternChordInputState chordState = new();
		chordState.Select(PatternChordType.Major);

		PatternChordEditor.TypeRootPhysical(
			workspace,
			pattern,
			cursor,
			noteState,
			chordState,
			PhysicalKey.Z);

		for (int channel = 0; channel < 3; channel++)
		{
			pattern.Grid[0, channel]!.SourceId
				.Should().Be(oldSource.Id);
			pattern.Grid[0, channel]!.Volume
				.Should().Be(0.75);
			pattern.Grid[0, channel]!.Note
				.Should().BeOfType<StartPatternNote>();
		}
	}

	[Test]
	public void ChordRootUsesTrackerBaseOctaveAndPhysicalPianoMap()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Pattern",
				rowCount: 2,
				channelCount: 4);
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Note);
		PatternNoteInputState noteState =
			new(ObjectId.None, 3);
		PatternChordInputState chordState = new();
		chordState.Select(PatternChordType.Minor);

		PatternChordEditor.TypeRootPhysical(
			workspace,
			pattern,
			cursor,
			noteState,
			chordState,
			PhysicalKey.Q);

		((StartPatternNote)pattern.Grid[0, 0]!.Note!)
			.PitchMultiplier.Should().BeApproximately(1.0, 1e-12);
		chordState.GetStatusTones()
			.Select(tone => tone.NoteText)
			.Should().Equal("C-4", "D#4", "G-4");
	}

	private static SampleDefinition AddSample(
		DocumentWorkspace workspace,
		string name)
	{
		ObjectId id =
			workspace.Document.AllocateObjectId();
		SampleDefinition sample =
			new(
				id,
				name,
				new ExternalAssetReference(
					System.IO.Path.GetFullPath($"{name}.wav")));
		workspace.Document.Add(sample);
		return sample;
	}
}

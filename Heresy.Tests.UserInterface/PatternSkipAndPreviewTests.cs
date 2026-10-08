using System;

using AwesomeAssertions;

using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSkipAndPreviewTests
{
	[TestCase(PhysicalKey.Digit0, 0)]
	[TestCase(PhysicalKey.Digit1, 1)]
	[TestCase(PhysicalKey.Digit2, 2)]
	[TestCase(PhysicalKey.Digit3, 3)]
	[TestCase(PhysicalKey.Digit4, 4)]
	[TestCase(PhysicalKey.Digit5, 5)]
	[TestCase(PhysicalKey.Digit6, 6)]
	[TestCase(PhysicalKey.Digit7, 7)]
	[TestCase(PhysicalKey.Digit8, 8)]
	[TestCase(PhysicalKey.Digit9, 9)]
	public void AltTopRowDigitsSelectSkipValue(PhysicalKey key, int skip)
	{
		PatternSkipKeyboard.TryGetValue(key, KeyModifiers.Alt, out int value)
			.Should().BeTrue();
		value.Should().Be(skip);
	}

	[TestCase(PhysicalKey.Z, KeyModifiers.Alt)]
	[TestCase(PhysicalKey.Digit1, KeyModifiers.None)]
	[TestCase(PhysicalKey.Digit2, KeyModifiers.Control | KeyModifiers.Alt)]
	[TestCase(PhysicalKey.Digit3, KeyModifiers.Shift | KeyModifiers.Alt)]
	[TestCase(PhysicalKey.NumPad1, KeyModifiers.Alt)]
	public void NonShortcutsDoNotChangeSkip(PhysicalKey key, KeyModifiers modifiers)
	{
		PatternSkipKeyboard.TryGetValue(key, modifiers, out _)
			.Should().BeFalse();
	}

	[Test]
	public void SkipDefaultsToOneAndValidatesRange()
	{
		PatternNoteInputState state = new(ObjectId.None, 4);
		state.SkipRows.Should().Be(1);
		state.SkipRows = 0;
		state.SkipRows.Should().Be(0);
		state.SkipRows = 9;
		state.SkipRows.Should().Be(9);
		Action below = () => state.SkipRows = -1;
		Action above = () => state.SkipRows = 10;
		below.Should().Throw<ArgumentOutOfRangeException>();
		above.Should().Throw<ArgumentOutOfRangeException>();
		state.SkipRows.Should().Be(9);
	}

	[Test]
	public void HeldCapsPreviewConsumesAllRepeatsWithoutRestartOrNoteEntry()
	{
		HeldNotePreviewKeyState state = new();
		state.KeyDown(PhysicalKey.CapsLock, 4);

		PatternPreviewKeyRouting.IsPreviewOnly(state, PhysicalKey.Z)
			.Should().BeTrue();
		state.KeyDown(PhysicalKey.Z, 4)
			.Should().BeOfType<StartHeldNotePreviewAction>();
		for (int n = 0; n < 5; n++)
		{
			PatternPreviewKeyRouting.IsPreviewOnly(state, PhysicalKey.Z)
				.Should().BeTrue();
			state.KeyDown(PhysicalKey.Z, 4).Should().BeNull();
		}
		state.ActiveNoteCount.Should().Be(1);

		// Caps Lock can be released before the held tracker note, but
		// auto-repeat must still not become document input.
		state.KeyUp(PhysicalKey.CapsLock);
		PatternPreviewKeyRouting.IsPreviewOnly(state, PhysicalKey.Z)
			.Should().BeTrue();
		state.KeyUp(PhysicalKey.Z)
			.Should().BeOfType<ReleaseHeldNotePreviewAction>();
		PatternPreviewKeyRouting.IsPreviewOnly(state, PhysicalKey.Z)
			.Should().BeFalse();
	}

	[Test]
	public void WithoutCapsTrackerAutoRepeatRemainsOrdinaryEntry()
	{
		HeldNotePreviewKeyState preview = new();
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace, "Repeat", rowCount: 16, channelCount: 1);
		PatternEffectCursor cursor = new(0, 0, PatternCellField.Note);
		PatternNoteInputState input = new(ObjectId.None, 4)
		{
			SkipRows = 3,
		};

		for (int n = 0; n < 4; n++)
		{
			PatternPreviewKeyRouting.IsPreviewOnly(preview, PhysicalKey.Z)
				.Should().BeFalse();
			PatternNoteKeyboardEditor.TypePhysical(
				workspace, pattern, cursor, input, PhysicalKey.Z)
				.Handled.Should().BeTrue();
		}
		cursor.Row.Should().Be(12);
		foreach (int row in new[] { 0, 3, 6, 9 })
			pattern.Grid[row, 0]!.Note.Should().BeOfType<StartPatternNote>();
		pattern.Grid[1, 0].Should().BeNull();
	}

	[Test]
	public void ZeroSkipStaysOnRowAndNonzeroSkipClampsToFinalRow()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace, "Skip", rowCount: 8, channelCount: 1);
		PatternEffectCursor cursor = new(2, 0, PatternCellField.Note);
		PatternNoteInputState input = new(ObjectId.None, 4)
		{
			SkipRows = 0,
		};
		PatternNoteKeyboardEditor.TypePhysical(
			workspace, pattern, cursor, input, PhysicalKey.Z);
		cursor.Row.Should().Be(2);
		input.SkipRows = 9;
		PatternNoteKeyboardEditor.TypePhysical(
			workspace, pattern, cursor, input, PhysicalKey.X);
		cursor.Row.Should().Be(7);
	}

	[Test]
	public void SkipCrossesSequenceContextAsRepeatedDownArrowsWould()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition a = PatternDocumentEditor.CreateDataPattern(
			workspace, "A", rowCount: 2, channelCount: 1);
		DataPatternDefinition b = PatternDocumentEditor.CreateDataPattern(
			workspace, "B", rowCount: 3, channelCount: 1);
		DataPatternDefinition c = PatternDocumentEditor.CreateDataPattern(
			workspace, "C", rowCount: 2, channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(a.Id));
		sequence.Entries.Add(new SequenceEntry(b.Id));
		sequence.Entries.Add(new SequenceEntry(c.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(workspace.Document, sequence, 0);
		PatternEffectCursor cursor = new(1, 0, PatternCellField.Note);
		PatternNoteInputState input = new(ObjectId.None, 4) { SkipRows = 3 };

		PatternEditorContextCursor.EditCurrent(
			context,
			cursor,
			row => PatternNoteKeyboardEditor.TypePhysical(
				workspace, row.Pattern, cursor, input, PhysicalKey.Z));
		cursor.Row.Should().Be(4);
		a.Grid[1, 0]!.Note.Should().BeOfType<StartPatternNote>();

		PatternEditorContextCursor.EditCurrent(
			context,
			cursor,
			row => PatternNoteKeyboardEditor.TypePhysical(
				workspace, row.Pattern, cursor, input, PhysicalKey.X));
		cursor.Row.Should().Be(6);
		b.Grid[2, 0]!.Note.Should().BeOfType<StartPatternNote>();
	}

	[Test]
	public void SkipClampsChannelsAtEachIntermediateSequenceRow()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition wide = PatternDocumentEditor.CreateDataPattern(
			workspace, "Wide", rowCount: 1, channelCount: 4);
		DataPatternDefinition narrow = PatternDocumentEditor.CreateDataPattern(
			workspace, "Narrow", rowCount: 1, channelCount: 1);
		DataPatternDefinition againWide = PatternDocumentEditor.CreateDataPattern(
			workspace, "AgainWide", rowCount: 1, channelCount: 4);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(wide.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id));
		sequence.Entries.Add(new SequenceEntry(againWide.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(workspace.Document, sequence, 0);
		PatternEffectCursor cursor = new(0, 3, PatternCellField.Note);
		PatternNoteInputState input = new(ObjectId.None, 4) { SkipRows = 2 };

		PatternEditorContextCursor.EditCurrent(
			context,
			cursor,
			row => PatternNoteKeyboardEditor.TypePhysical(
				workspace, row.Pattern, cursor, input, PhysicalKey.Z));

		cursor.Row.Should().Be(2);
		// A physical Down press into the intermediate narrow pattern
		// clamps channel 3 to 0 before the second Down press.
		cursor.Channel.Should().Be(0);
		wide.Grid[0, 3]!.Note.Should().BeOfType<StartPatternNote>();
	}

	[Test]
	public void ChordEntryUsesSkipValueToo()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Chords", rowCount: 16, channelCount: 4);
		PatternEffectCursor cursor = new(0, 0, PatternCellField.Note);
		PatternNoteInputState input = new(ObjectId.None, 4) { SkipRows = 2 };
		PatternChordInputState chord = new();
		chord.Select(PatternChordType.Major);

		PatternChordEditor.TypeRootPhysical(
			workspace, pattern, cursor, input, chord, PhysicalKey.Z)
			.Handled.Should().BeTrue();
		cursor.Row.Should().Be(2);
	}
}

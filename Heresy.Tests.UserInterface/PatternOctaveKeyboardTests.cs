using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternOctaveKeyboardTests
{
	[Test]
	public void FocusTraversalIncludesOctaveBetweenNoteAndSource()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Stops", 3, 2);
		PatternEffectCursor cursor = new(0, 0, PatternCellField.Note);
		cursor.MoveRight(pattern);
		cursor.Field.Should().Be(PatternCellField.Octave);
		cursor.MoveRight(pattern);
		cursor.Field.Should().Be(PatternCellField.Source);
		cursor.MoveLeft(pattern);
		cursor.Field.Should().Be(PatternCellField.Octave);
		cursor.MoveLeft(pattern);
		cursor.Field.Should().Be(PatternCellField.Note);
		cursor.MoveLeft(pattern);
		cursor.Channel.Should().Be(0);
		cursor.SetPosition(0, 1, PatternCellField.Note);
		cursor.MoveLeft(pattern);
		cursor.Channel.Should().Be(0);
		cursor.Field.Should().Be(PatternCellField.EffectParameter);
	}

	[Test]
	public void HitTestSeparatesVisibleNoteNameOctaveAndOtherFields()
	{
		PatternCellField Hit(double x) =>
			PatternCellFieldGeometry.HitTest(
				x, 280, 96, 34, 54, 0, true,
				octaveLeft: 44, octaveWidth: 22);
		Hit(3).Should().Be(PatternCellField.Note);
		Hit(43.9).Should().Be(PatternCellField.Note);
		Hit(44).Should().Be(PatternCellField.Octave);
		Hit(65.9).Should().Be(PatternCellField.Octave);
		Hit(66).Should().Be(PatternCellField.Note);
		Hit(96).Should().Be(PatternCellField.Source);
	}

	[Test]
	public void PhysicalDigitsIncludeKeypadButExcludeModifiersAndPianoLetters()
	{
		PatternOctaveKeyboardEditor.TryGetDigit(
			PhysicalKey.Digit8, KeyModifiers.None, out int octave)
			.Should().BeTrue();
		octave.Should().Be(8);
		PatternOctaveKeyboardEditor.TryGetDigit(
			PhysicalKey.NumPad0, KeyModifiers.None, out octave)
			.Should().BeTrue();
		octave.Should().Be(0);
		PatternOctaveKeyboardEditor.TryGetDigit(
			PhysicalKey.Digit8, KeyModifiers.Shift, out _)
			.Should().BeFalse();
		PatternOctaveKeyboardEditor.TryGetDigit(
			PhysicalKey.Z, KeyModifiers.None, out _)
			.Should().BeFalse();
		PatternOctaveKeyboardEditor.TryGetDigit(
			PhysicalKey.Period, KeyModifiers.None, out _)
			.Should().BeFalse();
	}

	[Test]
	public void OctaveDigitChangesOnlyExistingPitchAndOneAudioRevision()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Notes", 5, 2);
		DataPatternDefinition source =
			PatternDocumentEditor.CreateDataPattern(workspace, "Source", 2, 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		double pitch = Math.Pow(2.0, 1.0 / 12.0);
		cell.Note = new StartPatternNote(source.Id, pitch, 0.75, true,
			source.Id);
		cell.SourceId = source.Id;
		cell.Volume = 0.375;
		var trackerEffect = new TrackerVolumeSlidePatternEffect(0x22);
		var nativeEffect = new SetPlaybackFrequencyPatternEffect(440.0);
		cell.Effects.Add(trackerEffect);
		cell.Effects.Add(nativeEffect);
		PatternEffectCursor cursor = new(0, 0, PatternCellField.Octave);
		uint audioRevision = workspace.Document.AudioRevision;

		PatternOctaveKeyboardEditor.Type(workspace, pattern, cursor, 7, 2)
			.Should().BeTrue();
		StartPatternNote modified = (StartPatternNote)cell.Note!;
		modified.PitchMultiplier.Should().BeApproximately(
			pitch * 8.0, 1e-11);
		modified.PlaybackSpeedMultiplier.Should().Be(0.75);
		modified.Mixdown.Should().BeTrue();
		modified.SourceId.Should().Be(source.Id);
		modified.EventFilterDefinitionId.Should().Be(source.Id);
		cell.SourceId.Should().Be(source.Id);
		cell.Volume.Should().Be(0.375);
		cell.Effects.Should().ContainInOrder(trackerEffect, nativeEffect);
		pattern.Grid[0, 0].Should().BeSameAs(cell);
		cursor.Field.Should().Be(PatternCellField.Octave);
		cursor.Row.Should().Be(2);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		cursor.SetPosition(0, 0, PatternCellField.Octave);
		PatternOctaveKeyboardEditor.Type(workspace, pattern, cursor, 7, 0)
			.Should().BeFalse();
		cursor.Row.Should().Be(0);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void EmptyOffAndCutNotesAreNeverInventedOrReplaced()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Empty", 5, 1);
		pattern.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		pattern.Grid.GetOrCreateCell(2, 0).Note = new PatternNoteCut();
		uint revision = workspace.Document.AudioRevision;
		foreach (int row in new[] { 0, 1, 2 })
		{
			PatternEffectCursor cursor =
				new(row, 0, PatternCellField.Octave);
			PatternOctaveKeyboardEditor.Type(
				workspace, pattern, cursor, 9, 2).Should().BeFalse();
			cursor.Row.Should().Be(row + 2);
			cursor.Field.Should().Be(PatternCellField.Octave);
		}
		pattern.Grid[0, 0].Should().BeNull();
		pattern.Grid[1, 0]!.Note.Should().BeOfType<PatternNoteOff>();
		pattern.Grid[2, 0]!.Note.Should().BeOfType<PatternNoteCut>();
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[Test]
	public void OctaveEntryUsesSequenceSkipAndNarrowIntermediateChannel()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition wide = PatternDocumentEditor.CreateDataPattern(
			workspace, "Wide", 2, 3);
		DataPatternDefinition narrow = PatternDocumentEditor.CreateDataPattern(
			workspace, "Narrow", 1, 1);
		DataPatternDefinition again = PatternDocumentEditor.CreateDataPattern(
			workspace, "Again", 3, 3);
		wide.Grid.GetOrCreateCell(1, 2).Note = new StartPatternNote();
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(wide.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id));
		sequence.Entries.Add(new SequenceEntry(again.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(workspace.Document, sequence, 0);
		PatternEffectCursor cursor = new(1, 2, PatternCellField.Octave);
		PatternEditorContextCursor.EditCurrent(context, cursor,
			row => PatternOctaveKeyboardEditor.Type(
				workspace, row.Pattern, cursor, 5, 3));
		cursor.Row.Should().Be(4);
		cursor.Channel.Should().Be(0);
		cursor.Field.Should().Be(PatternCellField.Octave);
		((StartPatternNote)wide.Grid[1, 2]!.Note!)
			.PitchMultiplier.Should().BeApproximately(2.0, 1e-12);
	}

	[AvaloniaTest]
	public async Task EditorOctaveIsIndependentlyHighlightedAndIgnoresPianoAndPunctuation()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "UI", 6, 1);
		pattern.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();
		PatternEditorControl editor = new(
			new Window(), workspace, pattern, () => { }, _ => { });
		PatternEffectCursor cursor = Field<PatternEffectCursor>(editor, "_cursor");
		PatternNoteInputState input = Field<PatternNoteInputState>(
			editor, "_noteInputState");
		var octaveFields =
			Field<Dictionary<(int Row, int Channel), Border>>(
				editor, "_octaveFields");
		var noteFields =
			Field<Dictionary<(int Row, int Channel), Border>>(
				editor, "_noteFields");

		cursor.SetPosition(0, 0, PatternCellField.Octave);
		Invoke(editor, "RefreshCursorVisuals");
		octaveFields[(0, 0)].BorderBrush.Should().Be(Brushes.DeepSkyBlue);
		noteFields[(0, 0)].BorderBrush.Should().Be(Brushes.Transparent);
		uint revision = workspace.Document.AudioRevision;
		foreach (var (physical, key, symbol) in new[]
		{
			(PhysicalKey.Comma, Key.W, "w"),
			(PhysicalKey.Period, Key.V, "v"),
			(PhysicalKey.Z, Key.Z, "z"),
		})
		{
			await Press(editor, 0, 0, physical, key, symbol);
			Text(editor, 0, 0, symbol);
			cursor.Row.Should().Be(0);
			cursor.Field.Should().Be(PatternCellField.Octave);
		}
		input.EditMask.Should().Be(PatternEditMask.Default);
		workspace.Document.AudioRevision.Should().Be(revision);

		input.SkipRows = 0;
		await Press(editor, 0, 0, PhysicalKey.Digit8, Key.D8, "8");
		Text(editor, 0, 0, "8");
		((StartPatternNote)pattern.Grid[0, 0]!.Note!)
			.PitchMultiplier.Should().BeApproximately(16.0, 1e-11);
		cursor.Row.Should().Be(0);
		cursor.Field.Should().Be(PatternCellField.Octave);
		workspace.Document.AudioRevision.Should().Be(revision + 1);

		input.SkipRows = 2;
		await Press(editor, 0, 0, PhysicalKey.NumPad9, Key.NumPad9, "9");
		cursor.Row.Should().Be(2);
		cursor.Field.Should().Be(PatternCellField.Octave);
		workspace.Document.AudioRevision.Should().Be(revision + 2);
	}

	private static T Field<T>(PatternEditorControl editor, string name)
		=> (T)typeof(PatternEditorControl).GetField(
			name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static async Task Press(PatternEditorControl editor, int row,
		int channel, PhysicalKey physical, Key logical, string symbol)
	{
		KeyEventArgs e = new()
		{
			PhysicalKey = physical,
			Key = logical,
			KeySymbol = symbol,
		};
		Task task = (Task)typeof(PatternEditorControl).GetMethod(
			"OnCellKeyDownAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, [row, channel, e])!;
		await task;
		e.Handled.Should().BeTrue();
	}

	private static void Text(PatternEditorControl editor, int row,
		int channel, string value) =>
		Invoke(editor, "OnCellTextInput", row, channel,
			new TextInputEventArgs { Text = value });

	private static void Invoke(PatternEditorControl editor,
		string name, params object[] args) =>
		typeof(PatternEditorControl).GetMethod(
			name, BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, args);
}

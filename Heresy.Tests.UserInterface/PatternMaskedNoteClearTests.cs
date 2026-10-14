using System;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternMaskedNoteClearTests
{
	[TestCase(0)]
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(3)]
	[TestCase(4)]
	[TestCase(5)]
	[TestCase(6)]
	[TestCase(7)]
	public void ModelClearAlwaysRemovesNoteAndBothKindsOfEffectsButGatesSourceAndVolume(
		int maskBits)
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Clear mask", rowCount: 4, channelCount: 2);
		DataPatternDefinition source = PatternDocumentEditor.CreateDataPattern(
			workspace, "Sound source", rowCount: 2, channelCount: 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		StartPatternNote note = new(source.Id,
			pitchMultiplier: 1.5, playbackSpeedMultiplier: 0.75,
			mixdown: true, eventFilterDefinitionId: source.Id);
		cell.Note = note;
		cell.SourceId = source.Id;
		cell.Volume = 0.375;
		cell.Effects.Add(new TrackerVolumeSlidePatternEffect(0x12));
		cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
		PatternCell neighbour = pattern.Grid.GetOrCreateCell(0, 1);
		neighbour.Note = new PatternNoteOff();
		neighbour.Volume = 0.625;
		uint audioBefore = workspace.Document.AudioRevision;
		uint documentBefore = workspace.Document.DocumentRevision;
		PatternEditMask mask = (PatternEditMask)maskBits;

		bool changed = PatternDocumentEditor.ClearNoteSubfield(
			workspace, pattern, 0, 0, mask);

		Assert.That(changed, Is.True);
		Assert.That(cell.Note, Is.Null,
			"Note clears regardless of the edit mask's Note bit");
		Assert.That(cell.Effects, Is.Empty,
			"all tracker and native effects clear, irrespective of the mask");
		Assert.That(cell.SourceId,
			Is.EqualTo((mask & PatternEditMask.Source) != 0
				? ObjectId.None : source.Id));
		Assert.That(cell.Volume,
			Is.EqualTo((mask & PatternEditMask.Volume) != 0
				? (double?)null : 0.375));
		Assert.That(pattern.Grid[0, 0], (mask &
			(PatternEditMask.Source | PatternEditMask.Volume))
			== (PatternEditMask.Source | PatternEditMask.Volume)
				? Is.Null
				: Is.SameAs(cell),
			"only a truly empty cell is removed from the sparse grid");
		Assert.That(pattern.Grid[0, 1], Is.SameAs(neighbour));
		Assert.That(neighbour.Note, Is.TypeOf<PatternNoteOff>());
		Assert.That(neighbour.Volume, Is.EqualTo(0.625));
		Assert.That(workspace.Document.AudioRevision,
			Is.EqualTo(audioBefore + 1));
		Assert.That(workspace.Document.DocumentRevision,
			Is.EqualTo(documentBefore + 1));

		// Repeating Clear after the Note/effects are gone must only
		// mutate when an enabled field still contains data.
		Assert.That(PatternDocumentEditor.ClearNoteSubfield(
			workspace, pattern, 0, 0, mask), Is.False);
		Assert.That(workspace.Document.AudioRevision,
			Is.EqualTo(audioBefore + 1));
	}

	[Test]
	public void EmptyAndDisabledOnlySourceVolumeClearAreTrueNoOps()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Sparse", rowCount: 4, channelCount: 1);
		DataPatternDefinition source = PatternDocumentEditor.CreateDataPattern(
			workspace, "Source", rowCount: 1, channelCount: 1);
		PatternCell cell = pattern.Grid.GetOrCreateCell(1, 0);
		cell.SourceId = source.Id;
		cell.Volume = 0.5;
		uint audioBefore = workspace.Document.AudioRevision;
		uint documentBefore = workspace.Document.DocumentRevision;

		Assert.That(PatternDocumentEditor.ClearNoteSubfield(
			workspace, pattern, 0, 0, PatternEditMask.Default), Is.False);
		Assert.That(pattern.Grid[0, 0], Is.Null);
		foreach (PatternEditMask mask in new[]
		{
			PatternEditMask.None,
			PatternEditMask.Note,
		})
		{
			Assert.That(PatternDocumentEditor.ClearNoteSubfield(
				workspace, pattern, 1, 0, mask), Is.False);
		}
		Assert.That(cell.SourceId, Is.EqualTo(source.Id));
		Assert.That(cell.Volume, Is.EqualTo(0.5));
		Assert.That(pattern.Grid[1, 0], Is.SameAs(cell));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioBefore));
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(documentBefore));

		Assert.That(PatternDocumentEditor.ClearNoteSubfield(
			workspace, pattern, 1, 0, PatternEditMask.Source), Is.True);
		Assert.That(cell.SourceId.IsNone, Is.True);
		Assert.That(cell.Volume, Is.EqualTo(0.5));
		Assert.That(PatternDocumentEditor.ClearNoteSubfield(
			workspace, pattern, 1, 0, PatternEditMask.Source), Is.False);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioBefore + 1));

		Assert.That(PatternDocumentEditor.ClearNoteSubfield(
			workspace, pattern, 1, 0, PatternEditMask.Volume), Is.True);
		Assert.That(pattern.Grid[1, 0], Is.Null);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioBefore + 2));
		Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(documentBefore + 2));
	}

	[Test]
	public void EffectsOnlyAndEmptyTrackerSlotsStillClearAtomically()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Effects", rowCount: 3, channelCount: 1);
		PatternCell effects = pattern.Grid.GetOrCreateCell(0, 0);
		effects.Effects.Add(new EmptyTrackerPatternEffect(0x12));
		effects.Effects.Add(new SetPlaybackFrequencyPatternEffect(220));
		uint audioBefore = workspace.Document.AudioRevision;
		Assert.That(PatternDocumentEditor.ClearNoteSubfield(
			workspace, pattern, 0, 0, PatternEditMask.None), Is.True);
		Assert.That(pattern.Grid[0, 0], Is.Null);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioBefore + 1));

		PatternCell placeholder = pattern.Grid.GetOrCreateCell(1, 0);
		placeholder.Effects.Add(new EmptyTrackerPatternEffect(0));
		Assert.That(PatternDocumentEditor.ClearNoteSubfield(
			workspace, pattern, 1, 0, PatternEditMask.None), Is.True);
		Assert.That(pattern.Grid[1, 0], Is.Null);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioBefore + 2));
	}

	[AvaloniaTest]
	public async Task PhysicalPeriodUsesAllMaskCombinationsOnQwertyAndDvorak()
	{
		for (int bits = 0; bits < 8; bits++)
		for (int layout = 0; layout < 2; layout++)
		{
			DocumentWorkspace workspace = new();
			DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
				workspace, "Mask", rowCount: 4, channelCount: 2);
			DataPatternDefinition source = PatternDocumentEditor.CreateDataPattern(
				workspace, "Sample", rowCount: 2, channelCount: 1);
			PatternCell target = pattern.Grid.GetOrCreateCell(0, 0);
			target.Note = new StartPatternNote(source.Id);
			target.SourceId = source.Id;
			target.Volume = 0.75;
			target.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x52));
			target.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
			pattern.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteOff();

			PatternEditorControl editor = Editor(workspace,
				PatternEditorContext.ForPattern(workspace.Document, pattern));
			PatternNoteInputState input = Input(editor);
			input.EditMask = (PatternEditMask)bits;
			input.SkipRows = 2;
			SetCursor(editor, 0, 0, PatternCellField.Note);
			uint audioBefore = workspace.Document.AudioRevision;
			uint documentBefore = workspace.Document.DocumentRevision;
			Key logical = layout == 0 ? Key.OemPeriod : Key.V;
			string glyph = layout == 0 ? "." : "v";

			KeyEventArgs key = await Press(editor, 0, 0,
				PhysicalKey.Period, logical, glyph);
			Assert.That(key.Handled, Is.True);
			Assert.That(target.Note, Is.Null, "mask bits " + bits);
			Assert.That(target.Effects, Is.Empty, "mask bits " + bits);
			Assert.That(target.SourceId,
				Is.EqualTo((input.EditMask & PatternEditMask.Source) != 0
					? ObjectId.None : source.Id));
			Assert.That(target.Volume, Is.EqualTo(
				(input.EditMask & PatternEditMask.Volume) != 0
					? (double?)null : 0.75));
			Assert.That(pattern.Grid[0, 1]!.Note, Is.TypeOf<PatternNoteOff>());
			Assert.That(Cursor(editor).Row, Is.EqualTo(2));
			Assert.That(Cursor(editor).Channel, Is.Zero);
			Assert.That(Cursor(editor).Field, Is.EqualTo(PatternCellField.Note));
			Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audioBefore + 1));
			Assert.That(workspace.Document.DocumentRevision,
				Is.EqualTo(documentBefore + 1));

			// On Dvorak the emitted letter 'v' must not enter a second
			// effect or reset the cursor to the just-cleared display row.
			Text(editor, 0, 0, glyph);
			Assert.That(Cursor(editor).Row, Is.EqualTo(2));
			Assert.That(workspace.Document.AudioRevision,
				Is.EqualTo(audioBefore + 1));
			Assert.That(input.EditMask, Is.EqualTo((PatternEditMask)bits));
		}
	}

	[AvaloniaTest]
	public async Task MaskedNoteClearAdvancesAcrossSequenceAndLeavesDisabledFields()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first = PatternDocumentEditor.CreateDataPattern(
			workspace, "First", rowCount: 2, channelCount: 2);
		DataPatternDefinition narrow = PatternDocumentEditor.CreateDataPattern(
			workspace, "Narrow", rowCount: 1, channelCount: 1);
		DataPatternDefinition last = PatternDocumentEditor.CreateDataPattern(
			workspace, "Last", rowCount: 3, channelCount: 2);
		PatternCell target = first.Grid.GetOrCreateCell(1, 1);
		target.Note = new StartPatternNote();
		target.SourceId = narrow.Id;
		target.Volume = 0.5;
		target.Effects.Add(new TrackerVolumeSlidePatternEffect(0x21));
		last.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(narrow.Id));
		sequence.Entries.Add(new SequenceEntry(last.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(workspace.Document, sequence, 0);
		PatternEditorControl editor = Editor(workspace, context);
		Input(editor).EditMask = PatternEditMask.Note;
		Input(editor).SkipRows = 3;
		SetCursor(editor, 1, 1, PatternCellField.Note);
		uint revision = workspace.Document.AudioRevision;

		await Press(editor, 1, 1, PhysicalKey.Period, Key.V, "v");
		Assert.That(target.Note, Is.Null);
		Assert.That(target.Effects, Is.Empty);
		Assert.That(target.SourceId, Is.EqualTo(narrow.Id));
		Assert.That(target.Volume, Is.EqualTo(0.5));
		Assert.That(Cursor(editor).Row, Is.EqualTo(4));
		Assert.That(Cursor(editor).Channel, Is.Zero,
			"the intermediate one-channel Pattern permanently clamps the channel");
		Assert.That(Cursor(editor).Field, Is.EqualTo(PatternCellField.Note));
		Assert.That(last.Grid[1, 0]!.Note, Is.TypeOf<PatternNoteOff>());
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));

		// The same Pattern object may occur more than once in a Sequence.
		sequence.Entries.Add(new SequenceEntry(first.Id));
		PatternEditorContext repeated =
			PatternEditorContext.ForSequence(workspace.Document, sequence, 0);
		PatternEditorControl repeatedEditor = Editor(workspace, repeated);
		Input(repeatedEditor).EditMask = PatternEditMask.None;
		Input(repeatedEditor).SkipRows = 0;
		SetCursor(repeatedEditor, 7, 1, PatternCellField.Note);
		uint beforeNoop = workspace.Document.AudioRevision;
		await Press(repeatedEditor, 7, 1, PhysicalKey.Period, Key.OemPeriod, ".");
		Assert.That(Cursor(repeatedEditor).Row, Is.EqualTo(7));
		Assert.That(target.SourceId, Is.EqualTo(narrow.Id));
		Assert.That(target.Volume, Is.EqualTo(0.5));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(beforeNoop));
	}

	[AvaloniaTest]
	public async Task OctavePeriodAndCommaRemainInertWithAllMaskBitsEnabled()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
			workspace, "Octave", rowCount: 4, channelCount: 1);
		PatternCell target = pattern.Grid.GetOrCreateCell(0, 0);
		target.Note = new StartPatternNote();
		target.Volume = 0.5;
		target.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x52));
		PatternEditorControl editor = Editor(workspace,
			PatternEditorContext.ForPattern(workspace.Document, pattern));
		Input(editor).SkipRows = 3;
		SetCursor(editor, 0, 0, PatternCellField.Octave);
		uint revision = workspace.Document.AudioRevision;

		foreach ((PhysicalKey physical, Key logical, string symbol) in new[]
		{
			(PhysicalKey.Period, Key.OemPeriod, "."),
			(PhysicalKey.Period, Key.V, "v"),
			(PhysicalKey.Comma, Key.OemComma, ","),
			(PhysicalKey.Comma, Key.W, "w"),
		})
		{
			await Press(editor, 0, 0, physical, logical, symbol);
			Text(editor, 0, 0, symbol);
			Assert.That(Cursor(editor).Row, Is.Zero);
			Assert.That(Cursor(editor).Field, Is.EqualTo(PatternCellField.Octave));
			Assert.That(Input(editor).EditMask, Is.EqualTo(PatternEditMask.Default));
			Assert.That(target.Note, Is.TypeOf<StartPatternNote>());
			Assert.That(target.Volume, Is.EqualTo(0.5));
			Assert.That(target.Effects, Has.Count.EqualTo(1));
		}
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
	}

	private static PatternEditorControl Editor(
		DocumentWorkspace workspace, PatternEditorContext context) =>
		new(new Window(), workspace, context, () => { }, _ => { });

	private static PatternNoteInputState Input(PatternEditorControl editor) =>
		(PatternNoteInputState)typeof(PatternEditorControl).GetField(
			"_noteInputState", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static PatternEffectCursor Cursor(PatternEditorControl editor) =>
		(PatternEffectCursor)typeof(PatternEditorControl).GetField(
			"_cursor", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(editor)!;

	private static void SetCursor(PatternEditorControl editor,
		int row, int channel, PatternCellField field)
	{
		Cursor(editor).SetPosition(row, channel, field);
		typeof(PatternEditorControl).GetMethod(
			"RefreshCursorVisuals", BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, null);
	}

	private static async Task<KeyEventArgs> Press(PatternEditorControl editor,
		int row, int channel, PhysicalKey physical, Key logical, string symbol)
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
		return e;
	}

	private static void Text(PatternEditorControl editor,
		int row, int channel, string glyph) =>
		typeof(PatternEditorControl).GetMethod(
			"OnCellTextInput", BindingFlags.NonPublic | BindingFlags.Instance)!
			.Invoke(editor, [row, channel, new TextInputEventArgs { Text = glyph }]);
}

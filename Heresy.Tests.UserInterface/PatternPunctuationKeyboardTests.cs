using System;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Controls;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternPunctuationKeyboardTests
{
    [TestCase(PhysicalKey.W, Key.OemComma, ",",
        PatternPunctuationCommand.ToggleEditMask)]
    [TestCase(PhysicalKey.E, Key.OemPeriod, ".",
        PatternPunctuationCommand.ClearCell)]
    [TestCase(PhysicalKey.Comma, Key.OemComma, ",",
        PatternPunctuationCommand.ToggleEditMask)]
    [TestCase(PhysicalKey.Period, Key.OemPeriod, ".",
        PatternPunctuationCommand.ClearCell)]
    public void LayoutProducedPunctuationWinsOverPhysicalPianoPosition(
        PhysicalKey physical, Key logical, string symbol,
        PatternPunctuationCommand expected)
    {
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            physical, logical, symbol, KeyModifiers.None,
            out PatternPunctuationCommand actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(PhysicalKey.W, Key.W, "w")]
    [TestCase(PhysicalKey.E, Key.E, "e")]
    [TestCase(PhysicalKey.W, Key.W, "W")]
    [TestCase(PhysicalKey.E, Key.E, "E")]
    [TestCase(PhysicalKey.Comma, Key.W, "w")]
    [TestCase(PhysicalKey.Period, Key.E, "e")]
    [TestCase(PhysicalKey.W, Key.W, "<")]
    [TestCase(PhysicalKey.E, Key.E, ">")]
    public void LetterAndShiftedSymbolInputCannotBecomePunctuationCommands(
        PhysicalKey physical, Key logical, string symbol)
        => Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            physical, logical, symbol, KeyModifiers.None, out _), Is.False);

    [TestCase(PhysicalKey.W, Key.OemComma,
        PatternPunctuationCommand.ToggleEditMask)]
    [TestCase(PhysicalKey.E, Key.OemPeriod,
        PatternPunctuationCommand.ClearCell)]
    [TestCase(PhysicalKey.Comma, Key.None,
        PatternPunctuationCommand.ToggleEditMask)]
    [TestCase(PhysicalKey.Period, Key.None,
        PatternPunctuationCommand.ClearCell)]
    public void MissingSymbolUsesLogicalThenPhysicalPunctuationFallback(
        PhysicalKey physical, Key logical, PatternPunctuationCommand expected)
    {
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            physical, logical, null, KeyModifiers.None,
            out PatternPunctuationCommand actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(KeyModifiers.Shift)]
    [TestCase(KeyModifiers.Control)]
    [TestCase(KeyModifiers.Alt)]
    [TestCase(KeyModifiers.Meta)]
    public void ModifiedPunctuationIsNotATrackerEditingCommand(
        KeyModifiers modifiers)
    {
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            PhysicalKey.W, Key.OemComma, ",", modifiers, out _), Is.False);
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            PhysicalKey.E, Key.OemPeriod, ".", modifiers, out _), Is.False);
    }

    [Test]
    public void NumpadDecimalNeverClearsRegardlessOfProducedSymbol()
    {
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            PhysicalKey.NumPadDecimal, Key.Decimal, ".",
            KeyModifiers.None, out _), Is.False);
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            PhysicalKey.NumPadDecimal, Key.OemPeriod, null,
            KeyModifiers.None, out _), Is.False);
    }

    [AvaloniaTest]
    public async Task DvorakCommaOnPhysicalWPositionTogglesNoteEditMaskBeforePiano()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Pattern", 4, 1);
        PatternEditorControl editor = Editor(workspace, pattern);
        var state = InputState(editor);
        uint revision = workspace.Document.AudioRevision;

        KeyEventArgs comma = await Press(editor, 0, 0,
            PhysicalKey.W, Key.OemComma, ",");
        Assert.That(comma.Handled, Is.True);
        Assert.That(state.EditMask,
            Is.EqualTo(PatternEditMask.Source | PatternEditMask.Volume));
        Assert.That(Cursor(editor).Row, Is.Zero);
        Assert.That(pattern.Grid[0, 0], Is.Null);
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));

        // Its following TextInput is ignored by the Note field. It cannot
        // toggle the mask a second time or synthesize a physical W note.
        Invoke(editor, "OnCellTextInput", 0, 0,
            new TextInputEventArgs { Text = "," });
        Assert.That(state.EditMask,
            Is.EqualTo(PatternEditMask.Source | PatternEditMask.Volume));

        await Press(editor, 0, 0, PhysicalKey.W, Key.OemComma, ",");
        Assert.That(state.EditMask, Is.EqualTo(PatternEditMask.Default));
    }

    [AvaloniaTest]
    public async Task DvorakPeriodOnPhysicalEPositionClearsWholeCellButNotNextChannel()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Pattern", 4, 2);
        PatternCell target = pattern.Grid.GetOrCreateCell(0, 0);
        target.Note = new StartPatternNote();
        target.SourceId = ObjectId.None;
        target.Volume = 0.5;
        target.Effects.Add(new TrackerVolumeSlidePatternEffect(0x12));
        pattern.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteOff();
        PatternEditorControl editor = Editor(workspace, pattern);
        uint previousRevision = workspace.Document.AudioRevision;

        KeyEventArgs dot = await Press(editor, 0, 0,
            PhysicalKey.E, Key.OemPeriod, ".");
        Assert.That(dot.Handled, Is.True);
        Assert.That(pattern.Grid[0, 0], Is.Null);
        Assert.That(pattern.Grid[0, 1]!.Note,
            Is.TypeOf<PatternNoteOff>());
        Assert.That(Cursor(editor).Row, Is.Zero);
        Assert.That(workspace.Document.AudioRevision,
            Is.EqualTo(previousRevision + 1));

        // Pressing it again on the empty cell is a revision-free no-op.
        await Press(editor, 0, 0, PhysicalKey.E, Key.OemPeriod, ".");
        Assert.That(workspace.Document.AudioRevision,
            Is.EqualTo(previousRevision + 1));
    }

    [AvaloniaTest]
    public async Task QwertyPhysicalWAndERemainPianoNotesDespiteDvorakCommands()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Pattern", 4, 1);
        PatternEditorControl editor = Editor(workspace, pattern);
        KeyEventArgs w = await Press(editor, 0, 0, PhysicalKey.W, Key.W, "w");
        KeyEventArgs e = await Press(editor, 1, 0, PhysicalKey.E, Key.E, "e");

        Assert.That(w.Handled && e.Handled, Is.True);
        Assert.That(pattern.Grid[0, 0]?.Note, Is.TypeOf<StartPatternNote>());
        Assert.That(pattern.Grid[1, 0]?.Note, Is.TypeOf<StartPatternNote>());
        Assert.That(((StartPatternNote)pattern.Grid[0, 0]!.Note!)
            .PitchMultiplier, Is.EqualTo(Math.Pow(2, 14.0 / 12.0))
            .Within(1e-12));
        Assert.That(((StartPatternNote)pattern.Grid[1, 0]!.Note!)
            .PitchMultiplier, Is.EqualTo(Math.Pow(2, 16.0 / 12.0))
            .Within(1e-12));
        Assert.That(Cursor(editor).Row, Is.EqualTo(2));
        Assert.That(InputState(editor).EditMask, Is.EqualTo(PatternEditMask.Default));
    }

    [AvaloniaTest]
    public async Task NumpadPeriodNeverClearsTheNoteCell()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Pattern", 4, 1);
        pattern.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();
        PatternEditorControl editor = Editor(workspace, pattern);
        uint previousRevision = workspace.Document.AudioRevision;
        await Press(editor, 0, 0,
            PhysicalKey.NumPadDecimal, Key.Decimal, ".");

        Assert.That(pattern.Grid[0, 0]!.Note, Is.TypeOf<StartPatternNote>());
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(previousRevision));
    }

    [AvaloniaTest]
    public async Task NotePunctuationIsolatedToDisplayedSequenceOccurrence()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Reuse", 3, 1);
        // The same Pattern has one underlying cell even when repeated in
        // a Sequence. A clear is allowed once, not per occurrence.
        pattern.Grid.GetOrCreateCell(0, 0).Note = new PatternNoteCut();
        DataSequenceDefinition sequence =
            new(workspace.Document.AllocateObjectId(), "Sequence");
        sequence.Entries.Add(new SequenceEntry(pattern.Id));
        sequence.Entries.Add(new SequenceEntry(pattern.Id));
        workspace.Document.Add(sequence);
        PatternEditorContext context =
            PatternEditorContext.ForSequence(workspace.Document, sequence, 0);
        PatternEditorControl editor =
            new(new Window(), workspace, context, () => { }, _ => { });
        uint originalRevision = workspace.Document.AudioRevision;
        await Press(editor, 3, 0, PhysicalKey.E, Key.OemPeriod, ".");

        Assert.That(pattern.Grid[0, 0], Is.Null);
        Assert.That(workspace.Document.AudioRevision,
            Is.EqualTo(originalRevision + 1));
    }

    private static PatternEditorControl Editor(
        DocumentWorkspace workspace, DataPatternDefinition pattern)
        => new(new Window(), workspace, pattern, () => { }, _ => { });

    private static PatternNoteInputState InputState(PatternEditorControl editor)
        => (PatternNoteInputState)typeof(PatternEditorControl)
            .GetField("_noteInputState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;

    private static PatternEffectCursor Cursor(PatternEditorControl editor)
        => (PatternEffectCursor)typeof(PatternEditorControl)
            .GetField("_cursor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;

    private static async Task<KeyEventArgs> Press(
        PatternEditorControl editor, int row, int channel,
        PhysicalKey physical, Key logical, string symbol)
    {
        KeyEventArgs e = new()
        {
            PhysicalKey = physical,
            Key = logical,
            KeySymbol = symbol,
            KeyModifiers = KeyModifiers.None,
        };
        var task = (Task)typeof(PatternEditorControl)
            .GetMethod("OnCellKeyDownAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, [row, channel, e])!;
        await task;
        return e;
    }

    private static void Invoke(
        PatternEditorControl editor, string name, params object[] args)
        => typeof(PatternEditorControl)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, args);
}

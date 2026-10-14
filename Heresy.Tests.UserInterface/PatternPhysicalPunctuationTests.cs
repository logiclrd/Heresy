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

/// <summary>
/// Tracker Note/Source/Volume punctuation uses the QWERTY key *positions*,
/// regardless of the active OS layout. Effect command and parameter editing
/// intentionally consume the layout's TextInput symbol instead.
/// These tests replace, rather than adapt, the former glyph-driven suite.
/// </summary>
[TestFixture]
public sealed class PatternPhysicalPunctuationTests
{
    [TestCase(PhysicalKey.Comma, Key.OemComma, ",",
        PatternPunctuationCommand.ToggleEditMask)]
    [TestCase(PhysicalKey.Comma, Key.W, "w",
        PatternPunctuationCommand.ToggleEditMask)]
    [TestCase(PhysicalKey.Comma, Key.Z, "z",
        PatternPunctuationCommand.ToggleEditMask)]
    [TestCase(PhysicalKey.Period, Key.OemPeriod, ".",
        PatternPunctuationCommand.ClearField)]
    [TestCase(PhysicalKey.Period, Key.V, "v",
        PatternPunctuationCommand.ClearField)]
    [TestCase(PhysicalKey.Period, Key.E, "e",
        PatternPunctuationCommand.ClearField)]
    public void TrackerCommandsArePhysicalNotLayoutProduced(
        PhysicalKey physical, Key logical, string symbol,
        PatternPunctuationCommand expected)
    {
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            physical, KeyModifiers.None, out var result), Is.True);
        Assert.That(result, Is.EqualTo(expected));
        // The given logical key and symbol do not enter the classifier.
        Assert.That(symbol, Is.Not.Empty);
        Assert.That(logical, Is.Not.EqualTo(Key.None));
    }

    [TestCase(PhysicalKey.W, ",")]
    [TestCase(PhysicalKey.E, ".")]
    [TestCase(PhysicalKey.W, "z")]
    [TestCase(PhysicalKey.E, "e")]
    [TestCase(PhysicalKey.Z, ";")]
    [TestCase(PhysicalKey.NumPadDecimal, ".")]
    public void OtherPhysicalKeysDoNotTurnIntoCommandsOnAnyLayout(
        PhysicalKey physical, string symbol)
    {
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            physical, KeyModifiers.None, out _), Is.False, symbol);
    }

    [TestCase(KeyModifiers.Shift)]
    [TestCase(KeyModifiers.Control)]
    [TestCase(KeyModifiers.Alt)]
    [TestCase(KeyModifiers.Meta)]
    public void ModifiedPhysicalKeysAreNotPunctuationCommands(
        KeyModifiers modifiers)
    {
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            PhysicalKey.Comma, modifiers, out _), Is.False);
        Assert.That(PatternPunctuationKeyboard.TryGetCommand(
            PhysicalKey.Period, modifiers, out _), Is.False);
    }

    [AvaloniaTest]
    public async Task PhysicalCommaTogglesCurrentMaskEvenWhenDvorakProducesLetter()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 4, 2);
        PatternEditorControl editor = Editor(workspace, pattern);
        uint revision = workspace.Document.AudioRevision;
        Assert.That(Input(editor).EditMask, Is.EqualTo(PatternEditMask.Default));

        var first = await Press(editor, 0, 0,
            PhysicalKey.Comma, Key.W, "w");
        Assert.That(first.Handled, Is.True);
        Assert.That(Input(editor).EditMask,
            Is.EqualTo(PatternEditMask.Source | PatternEditMask.Volume));
        // A trailing text event must not trigger an unrelated command.
        Text(editor, 0, 0, "w");
        Assert.That(Input(editor).EditMask,
            Is.EqualTo(PatternEditMask.Source | PatternEditMask.Volume));
        Assert.That(pattern.Grid[0, 0], Is.Null);
        Assert.That(Cursor(editor).Row, Is.Zero);

        await Press(editor, 0, 0, PhysicalKey.Comma, Key.OemComma, ",");
        Text(editor, 0, 0, ",");
        Assert.That(Input(editor).EditMask, Is.EqualTo(PatternEditMask.Default));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));

        // Source and Volume fields have their own mask bits, independently
        // of what the operating system maps onto this same physical key.
        SetCursor(editor, 0, 0, PatternCellField.Source);
        await Press(editor, 0, 0, PhysicalKey.Comma, Key.Z, "z");
        Assert.That(Input(editor).EditMask,
            Is.EqualTo(PatternEditMask.Note | PatternEditMask.Volume));
        Text(editor, 0, 0, "z");
        Assert.That(Input(editor).EditMask,
            Is.EqualTo(PatternEditMask.Note | PatternEditMask.Volume));
        SetCursor(editor, 0, 0, PatternCellField.Volume);
        await Press(editor, 0, 0, PhysicalKey.Comma, Key.W, "w");
        Assert.That(Input(editor).EditMask, Is.EqualTo(PatternEditMask.Note));
        Text(editor, 0, 0, "w");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
    }

    [AvaloniaTest]
    public async Task PhysicalPeriodClearsEntireNoteCellButNotAdjacentCells()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 4, 2);
        PatternCell target = pattern.Grid.GetOrCreateCell(0, 0);
        target.Note = new StartPatternNote();
        target.Volume = 0.5;
        target.Effects.Add(new TrackerVolumeSlidePatternEffect(0x21));
        pattern.Grid.GetOrCreateCell(0, 1).Note = new PatternNoteOff();
        PatternEditorControl editor = Editor(workspace, pattern);
        uint revision = workspace.Document.AudioRevision;

        var key = await Press(editor, 0, 0,
            PhysicalKey.Period, Key.V, "v"); // Dvorak
        Assert.That(key.Handled, Is.True);
        Assert.That(pattern.Grid[0, 0], Is.Null);
        Assert.That(pattern.Grid[0, 1]!.Note, Is.TypeOf<PatternNoteOff>());
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
        Assert.That(Cursor(editor).Row, Is.Zero);
        Text(editor, 0, 0, "v");
        await Press(editor, 0, 0, PhysicalKey.Period, Key.OemPeriod, ".");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1),
            "clearing an already empty Note cell is a no-op");
    }

    [AvaloniaTest]
    public async Task PhysicalPeriodClearsOnlySourceOrVolumeAndKeepsOtherCellData()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 5, 1);
        DataPatternDefinition source =
            PatternDocumentEditor.CreateDataPattern(workspace, "Source", 2, 1);
        PatternCell target = pattern.Grid.GetOrCreateCell(0, 0);
        target.Note = new StartPatternNote();
        target.SourceId = source.Id;
        target.Volume = 0.75;
        target.Effects.Add(new TrackerVolumeSlidePatternEffect(0x22));
        PatternEditorControl editor = Editor(workspace, pattern);
        uint revision = workspace.Document.AudioRevision;

        SetCursor(editor, 0, 0, PatternCellField.Source);
        await Press(editor, 0, 0, PhysicalKey.Period, Key.E, "e");
        Text(editor, 0, 0, "e");
        Assert.That(pattern.Grid[0, 0]!.SourceId.IsNone, Is.True);
        Assert.That(pattern.Grid[0, 0]!.Note, Is.TypeOf<StartPatternNote>());
        Assert.That(pattern.Grid[0, 0]!.Volume, Is.EqualTo(0.75));
        Assert.That(pattern.Grid[0, 0]!.Effects[0],
            Is.EqualTo(new TrackerVolumeSlidePatternEffect(0x22)));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));

        SetCursor(editor, 0, 0, PatternCellField.Volume);
        await Press(editor, 0, 0, PhysicalKey.Period, Key.V, "v");
        Text(editor, 0, 0, "v");
        Assert.That(pattern.Grid[0, 0]!.Volume, Is.Null);
        Assert.That(pattern.Grid[0, 0]!.Note, Is.TypeOf<StartPatternNote>());
        Assert.That(pattern.Grid[0, 0]!.Effects[0],
            Is.EqualTo(new TrackerVolumeSlidePatternEffect(0x22)));
        Assert.That(Cursor(editor).Row, Is.EqualTo(1),
            "Volume field clear retains the existing advance policy");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2));
    }

    [AvaloniaTest]
    public async Task DvorakWAndEPianoPositionsIgnoreTheirCommaPeriodGlyphs()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 4, 1);
        PatternEditorControl editor = Editor(workspace, pattern);
        await Press(editor, 0, 0, PhysicalKey.W, Key.OemComma, ",");
        Text(editor, 0, 0, ",");
        await Press(editor, 1, 0, PhysicalKey.E, Key.OemPeriod, ".");
        Text(editor, 1, 0, ".");
        Assert.That(((StartPatternNote)pattern.Grid[0, 0]!.Note!).PitchMultiplier,
            Is.EqualTo(Math.Pow(2, 14.0 / 12.0)).Within(1e-12));
        Assert.That(((StartPatternNote)pattern.Grid[1, 0]!.Note!).PitchMultiplier,
            Is.EqualTo(Math.Pow(2, 16.0 / 12.0)).Within(1e-12));
        Assert.That(Cursor(editor).Row, Is.EqualTo(2));
        Assert.That(Input(editor).EditMask, Is.EqualTo(PatternEditMask.Default));
    }

    [AvaloniaTest]
    public async Task EffectColumnAlwaysUsesTextGlyphNotPhysicalPunctuation()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 7, 1);
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x12));
        PatternEditorControl editor = Editor(workspace, pattern);

        SetCursor(editor, 0, 0, PatternCellField.EffectCommand);
        // Dvorak: the physical QWERTY period produces 'v' (effect V).
        var dvorakV = await Press(editor, 0, 0,
            PhysicalKey.Period, Key.V, "v");
        Assert.That(dvorakV.Handled, Is.False);
        Text(editor, 0, 0, "v");
        Assert.That(cell.Effects[0],
            Is.EqualTo(new TrackerGlobalVolumePatternEffect(0x12)));

        // Dvorak: the physical QWERTY E produces '.', clearing only
        // the tracker effect command, NOT its 0x12 parameter.
        SetCursor(editor, 0, 0, PatternCellField.EffectCommand);
        var dvorakDot = await Press(editor, 0, 0,
            PhysicalKey.E, Key.OemPeriod, ".");
        Assert.That(dvorakDot.Handled, Is.False);
        Text(editor, 0, 0, ".");
        Assert.That(cell.Effects[0],
            Is.EqualTo(new EmptyTrackerPatternEffect(0x12)));

        // QWERTY: physical E produces 'e' and selects E, whereas the
        // physical period now produces '.' and clears the command again.
        SetCursor(editor, 0, 0, PatternCellField.EffectCommand);
        await Press(editor, 0, 0, PhysicalKey.E, Key.E, "e");
        Text(editor, 0, 0, "e");
        Assert.That(cell.Effects[0],
            Is.EqualTo(new TrackerPitchSlideDownPatternEffect(0x12)));
        SetCursor(editor, 0, 0, PatternCellField.EffectCommand);
        await Press(editor, 0, 0,
            PhysicalKey.Period, Key.OemPeriod, ".");
        Text(editor, 0, 0, ".");
        Assert.That(cell.Effects[0],
            Is.EqualTo(new EmptyTrackerPatternEffect(0x12)));
        Assert.That(Input(editor).EditMask, Is.EqualTo(PatternEditMask.Default));
    }

    [AvaloniaTest]
    public async Task DotInEffectParameterClearsDataNotCommand()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 4, 1);
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.Effects.Add(new TrackerPitchSlideDownPatternEffect(0x52));
        PatternEditorControl editor = Editor(workspace, pattern);
        SetCursor(editor, 0, 0, PatternCellField.EffectParameter);
        await Press(editor, 0, 0, PhysicalKey.E, Key.OemPeriod, ".");
        Text(editor, 0, 0, ".");
        Assert.That(cell.Effects[0],
            Is.EqualTo(new TrackerPitchSlideDownPatternEffect(0)));
    }

    [AvaloniaTest]
    public async Task NativeEffectDoesNotGetConvertedOrClearedByTextDot()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 4, 1);
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        var native = new SetPlaybackFrequencyPatternEffect(440);
        cell.Effects.Add(native);
        PatternEditorControl editor = Editor(workspace, pattern);
        SetCursor(editor, 0, 0, PatternCellField.EffectCommand);
        uint revision = workspace.Document.AudioRevision;
        await Press(editor, 0, 0, PhysicalKey.E, Key.OemPeriod, ".");
        Text(editor, 0, 0, ".");
        Assert.That(cell.Effects[0], Is.SameAs(native));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
    }

    [AvaloniaTest]
    public async Task RepeatedSequenceOccurrenceUsesItsUnderlyingFieldOnly()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Reuse", 3, 1);
        PatternCell target = pattern.Grid.GetOrCreateCell(0, 0);
        target.Note = new StartPatternNote();
        target.Volume = 0.5;
        DataSequenceDefinition sequence =
            new(workspace.Document.AllocateObjectId(), "Sequence");
        sequence.Entries.Add(new SequenceEntry(pattern.Id));
        sequence.Entries.Add(new SequenceEntry(pattern.Id));
        workspace.Document.Add(sequence);
        PatternEditorContext context =
            PatternEditorContext.ForSequence(workspace.Document, sequence, 0);
        PatternEditorControl editor =
            new(new Window(), workspace, context, () => { }, _ => { });
        SetCursor(editor, 3, 0, PatternCellField.Source);
        await Press(editor, 3, 0, PhysicalKey.Period, Key.V, "v");
        Assert.That(pattern.Grid[0, 0]!.Note, Is.TypeOf<StartPatternNote>());
        Assert.That(pattern.Grid[0, 0]!.Volume, Is.EqualTo(0.5));
        SetCursor(editor, 3, 0, PatternCellField.Note);
        await Press(editor, 3, 0, PhysicalKey.Period, Key.V, "v");
        Assert.That(pattern.Grid[0, 0], Is.Null);
    }

    [AvaloniaTest]
    public async Task NumpadDecimalAndModifiedPunctuationDoNotClear()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 4, 1);
        pattern.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote();
        PatternEditorControl editor = Editor(workspace, pattern);
        uint revision = workspace.Document.AudioRevision;
        await Press(editor, 0, 0, PhysicalKey.NumPadDecimal, Key.Decimal, ".");
        await Press(editor, 0, 0, PhysicalKey.Period, Key.OemPeriod, ".",
            KeyModifiers.Control);
        Assert.That(pattern.Grid[0, 0]!.Note, Is.TypeOf<StartPatternNote>());
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
    }

    private static PatternEditorControl Editor(
        DocumentWorkspace workspace, DataPatternDefinition pattern)
        => new(new Window(), workspace, pattern, () => { }, _ => { });

    private static PatternNoteInputState Input(PatternEditorControl editor)
        => (PatternNoteInputState)typeof(PatternEditorControl)
            .GetField("_noteInputState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;

    private static PatternEffectCursor Cursor(PatternEditorControl editor)
        => (PatternEffectCursor)typeof(PatternEditorControl)
            .GetField("_cursor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;

    private static void SetCursor(
        PatternEditorControl editor, int row, int channel, PatternCellField field)
    {
        Cursor(editor).SetPosition(row, channel, field);
        Invoke(editor, "RefreshCursorVisuals");
    }

    private static async Task<KeyEventArgs> Press(
        PatternEditorControl editor, int row, int channel,
        PhysicalKey physical, Key logical, string symbol,
        KeyModifiers mods = KeyModifiers.None)
    {
        KeyEventArgs e = new()
        {
            PhysicalKey = physical,
            Key = logical,
            KeySymbol = symbol,
            KeyModifiers = mods,
        };
        var task = (Task)typeof(PatternEditorControl)
            .GetMethod("OnCellKeyDownAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, [row, channel, e])!;
        await task;
        return e;
    }

    private static void Text(
        PatternEditorControl editor, int row, int channel, string text)
        => Invoke(editor, "OnCellTextInput", row, channel,
            new TextInputEventArgs { Text = text });

    private static void Invoke(
        PatternEditorControl editor, string name, params object[] args)
        => typeof(PatternEditorControl)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, args);
}

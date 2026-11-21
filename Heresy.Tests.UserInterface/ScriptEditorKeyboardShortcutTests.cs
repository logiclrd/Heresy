using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using AvaloniaEdit;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptEditorKeyboardShortcutTests
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, PrivateInstance)!
            .GetValue(target)!;

    private static KeyEventArgs Press(
        Control origin, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        KeyEventArgs args = new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        };
        origin.RaiseEvent(args);
        return args;
    }

    private static void ShowEditor(MainWindow window, Control editor)
    {
        Field<ContentControl>(window, "_mainContent").Content = editor;
        window.UpdateLayout();
    }

    [AvaloniaTest]
    public void F6TestsScriptPatternAndSequenceFromTheirOwnEditors()
    {
        foreach (bool patternKind in new[] { true, false })
        {
            RecordingTransport transport = new();
            MainWindow window = new(transport);
            DocumentWorkspace workspace = Field<DocumentWorkspace>(window, "_workspace");
            window.Show();
            try
            {
                ScriptEditorControl editor;
                ObjectId expectedId;
                string updated;
                if (patternKind)
                {
                    ScriptPatternDefinition pattern = ScriptDocumentEditor
                        .CreateScriptPattern(workspace, "Pattern");
                    editor = new(workspace, pattern, () => { }, _ => { });
                    expectedId = pattern.Id;
                    updated = pattern.Source + "\n// current draft";
                }
                else
                {
                    ScriptSequenceDefinition sequence = ScriptDocumentEditor
                        .CreateScriptSequence(workspace, "Sequence");
                    editor = new(workspace, sequence, () => { }, _ => { });
                    expectedId = sequence.Id;
                    updated = sequence.Source + "\n// current draft";
                }
                ShowEditor(window, editor);
                TextEditor source = Field<TextEditor>(editor, "_source");
                source.Text = updated;
                Assert.That(editor.HasUnsavedScriptSource, Is.True);

                KeyEventArgs f6 = Press(source, Key.F6);
                Assert.That(f6.Handled, Is.True);
                Assert.That(editor.HasUnsavedScriptSource, Is.False,
                    "F6 must audition current authored source, not the old version");
                Assert.That((patternKind
                    ? (SongObject)workspace.Document.Objects[expectedId]
                    : workspace.Document.Objects[expectedId]) switch
                    {
                        ScriptPatternDefinition p => p.Source,
                        ScriptSequenceDefinition s => s.Source,
                        _ => string.Empty,
                    }, Is.EqualTo(updated));

                if (patternKind)
                {
                    Assert.That(transport.PatternId, Is.EqualTo(expectedId));
                    Assert.That(transport.PatternStartRow, Is.Zero);
                    Assert.That(transport.PatternRepeats, Is.True);
                    Assert.That(transport.SequenceId, Is.Null);
                }
                else
                {
                    Assert.That(transport.SequenceId, Is.EqualTo(expectedId));
                    Assert.That(transport.SequencePosition, Is.Null,
                        "F6 Script Sequence should begin at the first order");
                    Assert.That(transport.PatternId, Is.Null);
                }
                Assert.That(workspace.IsModified, Is.True,
                    "F6 applies source to the song, not to the on-disk file");
            }
            finally
            {
                workspace.New();
                window.Close();
            }
        }
    }

    [AvaloniaTest]
    public void ControlSSavesScriptPatternAndSequenceSourceLocallyFromTextAndButton()
    {
        foreach (bool patternKind in new[] { true, false })
        {
            MainWindow window = new();
            DocumentWorkspace workspace = Field<DocumentWorkspace>(window, "_workspace");
            window.Show();
            try
            {
                ScriptEditorControl editor = patternKind
                    ? new ScriptEditorControl(workspace,
                        ScriptDocumentEditor.CreateScriptPattern(workspace, "Pattern"),
                        () => { }, _ => { })
                    : new ScriptEditorControl(workspace,
                        ScriptDocumentEditor.CreateScriptSequence(workspace, "Sequence"),
                        () => { }, _ => { });
                ShowEditor(window, editor);
                TextEditor source = Field<TextEditor>(editor, "_source");
                Button save = Field<Button>(editor, "_saveSource");
                source.Text += "\n// committed from text focus";
                KeyEventArgs first = Press(source, Key.S, KeyModifiers.Control);
                Assert.That(first.Handled, Is.True);
                Assert.That(editor.HasUnsavedScriptSource, Is.False);
                Assert.That(save.IsEnabled, Is.False);

                source.Text += "\n// committed from button focus";
                KeyEventArgs second = Press(save, Key.S, KeyModifiers.Control);
                Assert.That(second.Handled, Is.True);
                Assert.That(editor.HasUnsavedScriptSource, Is.False);
                Assert.That(save.IsEnabled, Is.False);

                KeyEventArgs clean = Press(source, Key.S, KeyModifiers.Control);
                Assert.That(clean.Handled, Is.True,
                    "clean source must consume Ctrl+S, not fall through to Save Document");

                ComboBox elsewhere = Field<ComboBox>(editor, "_reference");
                Assert.That(editor.TryHandleSourceSaveShortcut(elsewhere), Is.False);
                Assert.That(FileMenuKeyboard.TryGetCommand(
                    Key.S, KeyModifiers.Control, false, out FileMenuCommand file),
                    Is.True);
                Assert.That(file, Is.EqualTo(FileMenuCommand.Save));
            }
            finally
            {
                workspace.New();
                window.Close();
            }
        }
    }

    [AvaloniaTest]
    public void AvaloniaEditTextAreaDirectShortcutsAreConsumedWithoutWindowRouting()
    {
        foreach (bool patternKind in new[] { true, false })
        {
            DocumentWorkspace workspace = new();
            int auditionCalls = 0;
            ScriptEditorControl editor = patternKind
                ? new ScriptEditorControl(workspace,
                    ScriptDocumentEditor.CreateScriptPattern(workspace, "Pattern"),
                    () => { }, _ => { }, testRequested: () => auditionCalls++)
                : new ScriptEditorControl(workspace,
                    ScriptDocumentEditor.CreateScriptSequence(workspace, "Sequence"),
                    () => { }, _ => { }, testRequested: () => auditionCalls++);
            TextEditor source = Field<TextEditor>(editor, "_source");
            source.Text += "\n// new note logic";
            Assert.That(Press(source.TextArea, Key.S, KeyModifiers.Control).Handled,
                Is.True);
            Assert.That(editor.HasUnsavedScriptSource, Is.False);

            Assert.That(Press(source.TextArea, Key.F6).Handled, Is.True);
            Assert.That(auditionCalls, Is.EqualTo(1));
            Assert.That(Press(source.TextArea, Key.F6, KeyModifiers.Shift).Handled,
                Is.False);
        }
    }

    [AvaloniaTest]
    public void ControlSInScriptInstrumentCommitsOnlyItsAssociatedSourceGroup()
    {
        MainWindow window = new();
        DocumentWorkspace workspace = Field<DocumentWorkspace>(window, "_workspace");
        window.Show();
        try
        {
            var definition = ScriptInstrumentDocumentEditor.Create(
                workspace, "Instrument", "-1");
            ScriptInstrumentEditorControl editor =
                new(workspace, definition, () => { }, _ => { });
            ShowEditor(window, editor);
            TextBox selector = Field<TextBox>(editor, "_expression");
            TextBox pitch = Field<TextBox>(editor, "_pitchExpression");
            TextBox nna = Field<TextBox>(editor, "_nnaExpression");
            Button saveExpressions = Field<Button>(editor, "_saveExpressions");
            Button saveNna = Field<Button>(editor, "_saveNnaExpression");

            selector.Text = "0";
            pitch.Text = "Semitone(-2)";
            nna.Text = "InstrumentNnaAction.Cut";
            Assert.That(Press(pitch, Key.S, KeyModifiers.Control).Handled, Is.True);
            Assert.That(definition.Source, Is.EqualTo("0"));
            Assert.That(definition.PitchExpression, Is.EqualTo("Semitone(-2)"));
            Assert.That(definition.NnaExpression,
                Is.EqualTo("InstrumentNnaAction.Inherit"));
            Assert.That(saveExpressions.IsEnabled, Is.False);
            Assert.That(saveNna.IsEnabled, Is.True);

            Assert.That(Press(saveNna, Key.S, KeyModifiers.Control).Handled, Is.True);
            Assert.That(definition.NnaExpression, Is.EqualTo("InstrumentNnaAction.Cut"));
            Assert.That(saveNna.IsEnabled, Is.False);

            nna.Text = "System.Environment.TickCount";
            Assert.That(Press(nna, Key.S, KeyModifiers.Control).Handled, Is.True);
            Assert.That(definition.NnaExpression, Is.EqualTo("InstrumentNnaAction.Cut"));
            Assert.That(editor.HasUnsavedScriptSource, Is.True,
                "invalid source must remain in the editable buffer");
            Assert.That(Press(saveNna, Key.S, KeyModifiers.Control).Handled, Is.True);
            Assert.That(editor.TryHandleSourceSaveShortcut(
                Field<TextBox>(editor, "_nnaFadeMilliseconds")), Is.False);
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    [AvaloniaTest]
    public void EnvelopeSourceControlSDoesNotApplyOtherFields()
    {
        MainWindow window = new();
        DocumentWorkspace workspace = Field<DocumentWorkspace>(window, "_workspace");
        window.Show();
        try
        {
            var definition = EnvelopeDocumentEditor
                .CreateScriptEnvelope(workspace, "Envelope");
            ScriptEnvelopeEditorControl editor =
                new(workspace, definition, () => { }, _ => { });
            ShowEditor(window, editor);
            TextBox source = Field<TextBox>(editor, "_source");
            TextBox release = Field<TextBox>(editor, "_release");
            Button save = Field<Button>(editor, "_saveSource");

            release.Text = "3.25";
            source.Text = "0.75";
            Assert.That(Press(source, Key.S, KeyModifiers.Control).Handled, Is.True);
            Assert.That(definition.Source, Is.EqualTo("0.75"));
            Assert.That(definition.ReleaseDuration, Is.Null);
            Assert.That(save.IsEnabled, Is.False);

            source.Text = "0.25";
            Assert.That(Press(save, Key.S, KeyModifiers.Control).Handled, Is.True);
            Assert.That(definition.Source, Is.EqualTo("0.25"));
            Assert.That(editor.TryHandleSourceSaveShortcut(release), Is.False);
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    private sealed class RecordingTransport : ISongPlaybackTransport
    {
        public ObjectId? PatternId { get; private set; }
        public int PatternStartRow { get; private set; }
        public bool PatternRepeats { get; private set; }
        public ObjectId? SequenceId { get; private set; }
        public SequencePlaybackPosition? SequencePosition { get; private set; }

        public Task PlaySongAsync(SongDocument document) => Task.CompletedTask;

        public Task PlaySequenceAsync(SongDocument document,
            ObjectId sequenceId, SequencePlaybackPosition? startPosition = null)
        {
            SequenceId = sequenceId;
            SequencePosition = startPosition;
            return Task.CompletedTask;
        }

        public Task PlayPatternAsync(SongDocument document,
            ObjectId patternId, int startRow = 0, bool repeat = false)
        {
            PatternId = patternId;
            PatternStartRow = startRow;
            PatternRepeats = repeat;
            return Task.CompletedTask;
        }

        public Task PlayAdHocAsync(SongDocument document, NoteSchedule schedule)
            => Task.CompletedTask;

        public Task SendLiveEventAsync(SongDocument document,
            ChannelTarget target, IReadOnlyList<NoteCommand> commands)
            => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}

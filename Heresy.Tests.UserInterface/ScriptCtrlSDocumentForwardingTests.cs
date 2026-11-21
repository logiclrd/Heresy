using System;
using System.IO;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using AvaloniaEdit;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptCtrlSDocumentForwardingTests
{
    private static T Field<T>(object target, string name)
        => (T)target.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(target)!;

    private static KeyEventArgs CtrlS(Control origin)
    {
        KeyEventArgs args = new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.S,
            KeyModifiers = KeyModifiers.Control,
        };
        origin.RaiseEvent(args);
        return args;
    }

    [AvaloniaTest]
    public void TwoCtrlSKeypressesCommitScriptThenWriteSongToDisk()
    {
        foreach (bool patternKind in new[] { true, false })
        {
            MainWindow window = new();
            DocumentWorkspace workspace =
                Field<DocumentWorkspace>(window, "_workspace");
            string path = Path.Combine(Path.GetTempPath(),
                $"heresy-ctrls-{Guid.NewGuid():N}.hm.json");

            window.Show();
            try
            {
                ScriptEditorControl editor;
                if (patternKind)
                {
                    ScriptPatternDefinition pattern =
                        ScriptDocumentEditor.CreateScriptPattern(workspace, "Test");
                    editor = new ScriptEditorControl(workspace, pattern,
                        () => { }, _ => { });
                }
                else
                {
                    ScriptSequenceDefinition sequence =
                        ScriptDocumentEditor.CreateScriptSequence(workspace, "Test");
                    editor = new ScriptEditorControl(workspace, sequence,
                        () => { }, _ => { });
                }
                Field<ContentControl>(window, "_mainContent").Content = editor;
                window.UpdateLayout();

                workspace.SaveAs(path);
                Assert.That(workspace.IsModified, Is.False);

                TextEditor source = Field<TextEditor>(editor, "_source");
                source.Text += "\n// double-ctrls-marker";
                Assert.That(editor.HasUnsavedScriptSource, Is.True);
                Assert.That(CtrlS(source).Handled, Is.True);

                Assert.Multiple(() =>
                {
                    Assert.That(editor.HasUnsavedScriptSource, Is.False);
                    Assert.That(workspace.IsModified, Is.True);
                    Assert.That(File.ReadAllText(path),
                        Does.Not.Contain("double-ctrls-marker"),
                        "the first press must never save the song to disk");
                });

                Assert.That(CtrlS(source).Handled, Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(editor.HasUnsavedScriptSource, Is.False);
                    Assert.That(workspace.IsModified, Is.False);
                    Assert.That(File.ReadAllText(path),
                        Does.Contain("double-ctrls-marker"),
                        "the second, clean press saves the document to disk");
                });
            }
            finally
            {
                workspace.New();
                window.Close();
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    [AvaloniaTest]
    public void DirectAvaloniaEditTextAreaForwardsOnlyCleanSource()
    {
        foreach (bool patternKind in new[] { true, false })
        {
            DocumentWorkspace workspace = new();
            int documentSaves = 0;
            ScriptEditorControl editor = patternKind
                ? new ScriptEditorControl(workspace,
                    ScriptDocumentEditor.CreateScriptPattern(workspace, "Pattern"),
                    () => { }, _ => { },
                    documentSaveRequested: () => documentSaves++)
                : new ScriptEditorControl(workspace,
                    ScriptDocumentEditor.CreateScriptSequence(workspace, "Sequence"),
                    () => { }, _ => { },
                    documentSaveRequested: () => documentSaves++);
            TextEditor source = Field<TextEditor>(editor, "_source");

            source.Text += "\n// initial dirty source";
            Assert.That(CtrlS(source.TextArea).Handled, Is.True);
            Assert.That(editor.HasUnsavedScriptSource, Is.False);
            Assert.That(documentSaves, Is.Zero);

            Assert.That(CtrlS(source.TextArea).Handled, Is.True);
            Assert.That(documentSaves, Is.EqualTo(1));

            source.Text += "\n// another change";
            Assert.That(CtrlS(source.TextArea).Handled, Is.True);
            Assert.That(documentSaves, Is.EqualTo(1));
            Assert.That(CtrlS(source.TextArea).Handled, Is.True);
            Assert.That(documentSaves, Is.EqualTo(2));

            Assert.That(editor.TryHandleSourceSaveShortcut(
                Field<Button>(editor, "_saveSource")),
                Is.EqualTo(ScriptSourceSaveShortcutRoute.SaveDocument));
        }
    }

    [AvaloniaTest]
    public void CleanEnvelopeSourceForwardsToDocumentSaveWithoutReleaseChanges()
    {
        MainWindow window = new();
        DocumentWorkspace workspace =
            Field<DocumentWorkspace>(window, "_workspace");
        string path = Path.Combine(Path.GetTempPath(),
            $"heresy-envelope-ctrls-{Guid.NewGuid():N}.hm.json");
        window.Show();
        try
        {
            var envelope = EnvelopeDocumentEditor
                .CreateScriptEnvelope(workspace, "Envelope");
            ScriptEnvelopeEditorControl editor =
                new(workspace, envelope, () => { }, _ => { });
            Field<ContentControl>(window, "_mainContent").Content = editor;
            window.UpdateLayout();
            workspace.SaveAs(path);
            TextBox source = Field<TextBox>(editor, "_source");
            source.Text = "0.75";
            Assert.That(CtrlS(source).Handled, Is.True);
            Assert.That(workspace.IsModified, Is.True);
            Assert.That(File.ReadAllText(path), Does.Not.Contain("0.75"));

            Assert.That(CtrlS(source).Handled, Is.True);
            Assert.That(workspace.IsModified, Is.False);
            Assert.That(File.ReadAllText(path), Does.Contain("0.75"));
        }
        finally
        {
            workspace.New();
            window.Close();
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

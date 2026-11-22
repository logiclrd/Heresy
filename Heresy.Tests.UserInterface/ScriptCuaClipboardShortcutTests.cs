using System;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;

using AvaloniaEdit;

using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptCuaClipboardShortcutTests
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;

    private static KeyEventArgs Press(InputElement control, Key key, KeyModifiers modifiers)
    {
        KeyEventArgs args = new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        };
        control.RaiseEvent(args);
        return args;
    }

    [AvaloniaTest]
    public async Task PatternAndSequenceEditorsUseLegacyClipboardAliases()
    {
        foreach (bool patternKind in new[] { true, false })
        {
            DocumentWorkspace workspace = new();
            ScriptEditorControl editor = patternKind
                ? new ScriptEditorControl(workspace,
                    ScriptDocumentEditor.CreateScriptPattern(workspace, "Pattern"),
                    () => { }, _ => { })
                : new ScriptEditorControl(workspace,
                    ScriptDocumentEditor.CreateScriptSequence(workspace, "Sequence"),
                    () => { }, _ => { });
            Window window = new() { Content = editor };
            window.Show();
            try
            {
                TextEditor source = Field<TextEditor>(editor, "_source");
                source.Text = "alpha beta";
                source.Select(0, 5);
                Assert.That(Press(source.TextArea, Key.Insert, KeyModifiers.Control).Handled,
                    Is.True);
                Assert.That(await window.Clipboard!.GetTextAsync(), Is.EqualTo("alpha"));
                Assert.That(source.Text, Is.EqualTo("alpha beta"));

                source.Select(6, 4);
                Assert.That(Press(source.TextArea, Key.Delete, KeyModifiers.Shift).Handled,
                    Is.True);
                Assert.That(source.Text, Is.EqualTo("alpha "));
                Assert.That(await window.Clipboard!.GetTextAsync(), Is.EqualTo("beta"));

                source.CaretOffset = source.Text.Length;
                Assert.That(Press(source.TextArea, Key.Insert, KeyModifiers.Shift).Handled,
                    Is.True);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.That(source.Text, Is.EqualTo("alpha beta"));

                Assert.That(Press(source.TextArea, Key.Insert,
                    KeyModifiers.Control | KeyModifiers.Shift).Handled, Is.False,
                    "only exact CUA gestures should be intercepted");
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaTest]
    public async Task EnvelopeAndInstrumentExpressionTextBoxesUseSameAliases()
    {
        DocumentWorkspace workspace = new();
        ScriptEnvelopeEditorControl envelope = new(workspace,
            EnvelopeDocumentEditor.CreateScriptEnvelope(workspace, "Envelope"),
            () => { }, _ => { });
        var instrumentDefinition =
            ScriptInstrumentDocumentEditor.Create(workspace, "Instrument", "-1");
        ScriptInstrumentEditorControl instrument = new(workspace, instrumentDefinition,
            () => { }, _ => { });
        foreach (TextBox box in new[]
        {
            Field<TextBox>(envelope, "_source"),
            Field<TextBox>(envelope, "_release"),
            Field<TextBox>(instrument, "_expression"),
            Field<TextBox>(instrument, "_pitchExpression"),
            Field<TextBox>(instrument, "_nnaExpression"),
            Field<TextBox>(instrument, "_ratio"),
        })
        {
            Window window = new() { Content = box == Field<TextBox>(envelope, "_source")
                || box == Field<TextBox>(envelope, "_release") ? envelope : instrument };
            // A control cannot have two parents. Attach its owning editor
            // only once when the relevant loop switches editor.
            if (box.Parent is not null)
            {
                // Check gestures on the TextBox without changing its parent.
                // The listener was installed on the field at construction.
                box.Text = "xyz";
                box.SelectAll();
                Assert.That(Press(box, Key.Insert, KeyModifiers.Control).Handled, Is.True);
                Assert.That(Press(box, Key.Delete, KeyModifiers.Shift).Handled, Is.True);
                Assert.That(Press(box, Key.Insert, KeyModifiers.Shift).Handled, Is.True);
                continue;
            }
            window.Show();
            try
            {
                box.Text = "xyz";
                box.SelectAll();
                Assert.That(Press(box, Key.Insert, KeyModifiers.Control).Handled, Is.True);
                Assert.That(Press(box, Key.Delete, KeyModifiers.Shift).Handled, Is.True);
                Assert.That(Press(box, Key.Insert, KeyModifiers.Shift).Handled, Is.True);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            finally { window.Close(); }
        }
    }
}

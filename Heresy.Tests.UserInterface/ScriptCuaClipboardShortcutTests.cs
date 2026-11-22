using System;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
                List<string> copied = [];
                source.TextArea.TextCopied += (_, e) => copied.Add(e.Text);
                source.Select(0, 5);
                Assert.That(Press(source.TextArea, Key.Insert, KeyModifiers.Control).Handled,
                    Is.True);
                Assert.That(copied, Is.EqualTo(new[] { "alpha" }),
                    "Ctrl+Insert must invoke AvaloniaEdit's real Copy command");
                Assert.That(source.Text, Is.EqualTo("alpha beta"));

                source.Select(6, 4);
                Assert.That(Press(source.TextArea, Key.Delete, KeyModifiers.Shift).Handled,
                    Is.True);
                Assert.That(source.Text, Is.EqualTo("alpha "));
                Assert.That(copied, Is.EqualTo(new[] { "alpha", "beta" }),
                    "Shift+Delete must use the existing Cut command");

                using (DataTransfer data = new())
                {
                    DataTransferItem item = new();
                    item.Set(DataFormat.Text, "beta");
                    data.Add(item);
                    await window.Clipboard!.SetDataAsync(data);
                }
                source.CaretOffset = source.Text.Length;
                Assert.That(Press(source.TextArea, Key.Insert, KeyModifiers.Shift).Handled,
                    Is.True);
                for (int attempt = 0; attempt < 20 && source.Text != "alpha beta"; attempt++)
                    await Task.Delay(10);
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
        Window envelopeWindow = new() { Content = envelope };
        Window instrumentWindow = new() { Content = instrument };
        envelopeWindow.Show();
        instrumentWindow.Show();
        try
        {
            foreach ((TextBox box, Window window) in new[]
            {
                (Field<TextBox>(envelope, "_source"), envelopeWindow),
                (Field<TextBox>(envelope, "_release"), envelopeWindow),
                (Field<TextBox>(instrument, "_expression"), instrumentWindow),
                (Field<TextBox>(instrument, "_pitchExpression"), instrumentWindow),
                (Field<TextBox>(instrument, "_nnaExpression"), instrumentWindow),
                (Field<TextBox>(instrument, "_ratio"), instrumentWindow),
            })
            {
                box.Text = "xyz";
                box.SelectAll();
                Assert.That(Press(box, Key.Insert, KeyModifiers.Control).Handled, Is.True);
                Assert.That(await window.Clipboard!.TryGetTextAsync(), Is.EqualTo("xyz"));
                Assert.That(Press(box, Key.Delete, KeyModifiers.Shift).Handled, Is.True);
                Assert.That(box.Text, Is.Empty);
                Assert.That(Press(box, Key.Insert, KeyModifiers.Shift).Handled, Is.True);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.That(box.Text, Is.EqualTo("xyz"));
            }
        }
        finally
        {
            envelopeWindow.Close();
            instrumentWindow.Close();
        }
    }
}

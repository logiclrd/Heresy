using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthGraphDeletionKeyboardTests
{
    [AvaloniaTest]
    public void DeleteKeyAndRemoveButtonShareMutationSelectionAndNotification()
    {
        (DocumentWorkspace keyWork, FmSynthDefinition keySynth,
            FmSynthEditorControl keyEditor, int keyNode, string? keyChange) =
            CreateEditor();
        (DocumentWorkspace buttonWork, FmSynthDefinition buttonSynth,
            FmSynthEditorControl buttonEditor, int buttonNode, string? buttonChange) =
            CreateEditor();

        Select(keyEditor, keyNode);
        Select(buttonEditor, buttonNode);
        uint keyAudio = keyWork.Document.AudioRevision;
        uint keyDocument = keyWork.Document.DocumentRevision;
        uint buttonAudio = buttonWork.Document.AudioRevision;
        uint buttonDocument = buttonWork.Document.DocumentRevision;

        FmSynthGraphCanvas canvas = Field<FmSynthGraphCanvas>(keyEditor, "_canvas");
        Assert.That(canvas.Focusable, Is.True);
        KeyEventArgs pressed = Press(canvas, Key.Delete);

        StackPanel inspector = Field<StackPanel>(buttonEditor, "_inspector");
        Button remove = inspector.Children.OfType<Button>()
            .Single(button => Equals(button.Content, "Remove node"));
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Multiple(() =>
        {
            Assert.That(pressed.Handled, Is.True);
            Assert.That(keySynth.Graph.Nodes.Select(node => node.Id),
                Is.EqualTo(buttonSynth.Graph.Nodes.Select(node => node.Id)));
            Assert.That(keySynth.Graph.Nodes.Any(node => node.Id == keyNode),
                Is.False);
            Assert.That(buttonSynth.Graph.Nodes.Any(node => node.Id == buttonNode),
                Is.False);
            Assert.That(keySynth.NodePositions.Any(x => x.NodeId == keyNode),
                Is.False);
            Assert.That(buttonSynth.NodePositions.Any(x => x.NodeId == buttonNode),
                Is.False);
            Assert.That(keyWork.Document.AudioRevision,
                Is.EqualTo(keyAudio + 1));
            Assert.That(buttonWork.Document.AudioRevision,
                Is.EqualTo(buttonAudio + 1));
            Assert.That(keyWork.Document.DocumentRevision,
                Is.EqualTo(keyDocument + 1));
            Assert.That(buttonWork.Document.DocumentRevision,
                Is.EqualTo(buttonDocument + 1));
            Assert.That(Field<int?>(keyEditor, "_selectedNodeId"),
                Is.EqualTo(keySynth.Graph.OutputNodeId));
            Assert.That(Field<int?>(buttonEditor, "_selectedNodeId"),
                Is.EqualTo(buttonSynth.Graph.OutputNodeId));
        });
    }

    [AvaloniaTest]
    public void DeleteOnOutputShowsSameFailureAsRemoveButtonWithoutMutatingDocument()
    {
        (DocumentWorkspace workspace, FmSynthDefinition synth,
            FmSynthEditorControl editor, int extra, string? _) = CreateEditor();
        Select(editor, synth.Graph.OutputNodeId);
        uint audio = workspace.Document.AudioRevision;
        uint document = workspace.Document.DocumentRevision;
        KeyEventArgs key = Press(Field<FmSynthGraphCanvas>(editor, "_canvas"),
            Key.Delete);
        string? error = Field<TextBlock>(editor, "_message").Text;

        Assert.That(key.Handled, Is.True);
        Assert.That(error, Does.Contain("output node cannot be removed"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(audio));
        Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(document));
        Assert.That(synth.Graph.Nodes.Any(node => node.Id == extra), Is.True);

        StackPanel inspector = Field<StackPanel>(editor, "_inspector");
        inspector.Children.OfType<Button>()
            .Single(button => Equals(button.Content, "Remove node"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(Field<TextBlock>(editor, "_message").Text,
            Is.EqualTo(error));
        Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(document));
    }

    [AvaloniaTest]
    public void DeleteIgnoresMissingSelectionModifiersAndInspectorTextFields()
    {
        (DocumentWorkspace workspace, FmSynthDefinition synth,
            FmSynthEditorControl editor, int selected, string? _) =
            CreateEditor();
        Select(editor, selected);
        FmSynthGraphCanvas canvas = Field<FmSynthGraphCanvas>(editor, "_canvas");
        StackPanel inspector = Field<StackPanel>(editor, "_inspector");
        TextBox field = inspector.Children.OfType<TextBox>().First();
        uint revision = workspace.Document.DocumentRevision;

        // Inspector key events bubble through the inspector only; they do
        // not reach the sibling canvas's Delete handler.
        KeyEventArgs fromField = Press(field, Key.Delete);
        Assert.That(fromField.Handled, Is.False);
        Assert.That(synth.Graph.Nodes.Any(x => x.Id == selected), Is.True);

        KeyEventArgs modified = Press(canvas, Key.Delete, KeyModifiers.Control);
        Assert.That(modified.Handled, Is.False);
        Assert.That(synth.Graph.Nodes.Any(x => x.Id == selected), Is.True);

        FieldSet(editor, "_selectedNodeId", (int?)null);
        KeyEventArgs noSelection = Press(canvas, Key.Delete);
        Assert.That(noSelection.Handled, Is.False);
        Assert.That(workspace.Document.DocumentRevision, Is.EqualTo(revision));
    }

    private static KeyEventArgs Press(Control target, Key key,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        KeyEventArgs e = new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        };
        target.RaiseEvent(e);
        return e;
    }

    private static (DocumentWorkspace, FmSynthDefinition,
        FmSynthEditorControl, int, string?) CreateEditor()
    {
        DocumentWorkspace work = new();
        FmSynthDefinition synth =
            FmSynthDocumentEditor.CreateFmSynth(work, "Delete");
        int node = FmSynthDocumentEditor.AddConstantNode(
            work, synth, 0.5, 240, 80);
        FmSynthEditorControl editor = new(
            work, synth, () => { }, _ => { });
        return (work, synth, editor, node, null);
    }

    private static void Select(FmSynthEditorControl editor, int id)
        => typeof(FmSynthEditorControl)
            .GetMethod("SelectNode",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [id]);

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType()
            .GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static void FieldSet(object instance, string name, object? value)
        => instance.GetType()
            .GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}

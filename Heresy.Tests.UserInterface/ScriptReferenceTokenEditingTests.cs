using System;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using AvaloniaEdit;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptReferenceTokenEditingTests
{
    [Test]
    public void BackspaceIntoRightEdgeUnwrapsTokenToSourceWithoutClosingParen()
    {
        const string source = "var value = _O(137);";
        ScriptReferenceVisualToken token =
            AnalyzeToken(source);
        Assert.That(ScriptReferenceTokenEditing.TryPlan(
            [token], token.SourceSpan.End, backspace: true, out var edit),
            Is.True);
        Assert.That(source.Remove(edit.Offset, 1), Is.EqualTo("var value = _O(137;"));
        Assert.That(edit.CaretAfter, Is.EqualTo(token.SourceSpan.End - 1));
        Assert.That(ScriptReferenceVisualTokenCatalog.Create(
            ScriptSourceDocumentAnalyzer.Analyze(new DocumentWorkspace(),
                source.Remove(edit.Offset, 1)).References), Is.Empty);
    }

    [Test]
    public void DeleteIntoLeftEdgeUnwrapsTokenToSourceWithoutUnderscore()
    {
        const string source = "var value = _O(137);";
        ScriptReferenceVisualToken token = AnalyzeToken(source);
        Assert.That(ScriptReferenceTokenEditing.TryPlan(
            [token], token.SourceSpan.Start, backspace: false, out var edit),
            Is.True);
        Assert.That(source.Remove(edit.Offset, 1), Is.EqualTo("var value = O(137);"));
        Assert.That(edit.CaretAfter, Is.EqualTo(token.SourceSpan.Start));
    }

    [Test]
    public void OrdinaryPositionsDoNotTriggerSpecialTokenEdits()
    {
        ScriptReferenceVisualToken token = AnalyzeToken("_O(137);");
        Assert.That(ScriptReferenceTokenEditing.TryPlan(
            [token], token.SourceSpan.Start, backspace: true, out _), Is.False);
        Assert.That(ScriptReferenceTokenEditing.TryPlan(
            [token], token.SourceSpan.End, backspace: false, out _), Is.False);
    }

    [AvaloniaTest]
    public void DiagnosticButtonDisplaysUnderscoresLiterallyWithoutAcceleratorParsing()
    {
        DocumentWorkspace workspace = new();
        ScriptPatternDefinition script =
            new(workspace.Document.AllocateObjectId(), "Script")
            {
                Source = "_O(0);",
            };
        workspace.Document.Add(script);
        ScriptEditorControl editor = new(workspace, script, () => { }, _ => { });
        StackPanel analysis = (StackPanel)typeof(ScriptEditorControl)
            .GetField("_analysis", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        Button diagnostic = analysis.Children.OfType<Button>().Single(button =>
            button.Content is TextBlock text && text.Text!.Contains("HRS1001"));
        TextBlock message = (TextBlock)diagnostic.Content!;
        Assert.That(message.Text, Does.Contain("_O(137)"));
    }

    [AvaloniaTest]
    public void RoutedBackspaceAndDeleteAtVisualTokenEdgesKeepSourceEditable()
    {
        DocumentWorkspace workspace = new();
        ScriptPatternDefinition script =
            new(workspace.Document.AllocateObjectId(), "Script")
            {
                Source = "_O(137);",
            };
        workspace.Document.Add(script);
        ScriptEditorControl editor = new(workspace, script, () => { }, _ => { });
        Window window = new() { Content = editor, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            TextEditor source = (TextEditor)typeof(ScriptEditorControl)
                .GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(editor)!;
            window.UpdateLayout();
            source.CaretOffset = "_O(137)".Length;
            KeyEventArgs backspace = new()
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Back,
                KeyModifiers = KeyModifiers.None,
            };
            source.TextArea.RaiseEvent(backspace);
            Assert.That(backspace.Handled, Is.True);
            Assert.That(source.Text, Is.EqualTo("_O(137;"));

            source.Text = "_O(137);";
            source.CaretOffset = 0;
            KeyEventArgs delete = new()
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Delete,
                KeyModifiers = KeyModifiers.None,
            };
            source.TextArea.RaiseEvent(delete);
            Assert.That(delete.Handled, Is.True);
            Assert.That(source.Text, Is.EqualTo("O(137);"));
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    private static ScriptReferenceVisualToken AnalyzeToken(string source)
    {
        DocumentWorkspace workspace = new();
        return ScriptReferenceVisualTokenCatalog.Create(
            ScriptSourceDocumentAnalyzer.Analyze(workspace, source).References)
            .Single();
    }
}

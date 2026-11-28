using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using AvaloniaEdit;

using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternGenerationTraceViewTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static T Property<T>(object target, string name)
        => (T)target.GetType().GetProperty(name, Flags)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);

    [AvaloniaTest]
    public async Task DirtyDraftIsCapturedWithoutSaveAndSelectionSurvivesOrderSwitch()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Trace");
        string original = pattern.Source;
        int changes = 0, auditions = 0;
        ScriptEditorControl editor = new(workspace, pattern, () => { }, _ => changes++,
            testRequested: () => auditions++);
        Window window = new() { Content = editor, Width = 1200, Height = 900 };
        window.Show();
        try
        {
            var source = Field<TextEditor>(editor, "_source");
            source.Text = "#order relaxed\r\nNote(4, 0, _O(17));\r\nOff(1, 0);";
            source.Document.Insert(source.Document.TextLength, "\r\n// draft");
            var draft = source.Text;
            var document = source.Document;
            bool modifiedBefore = workspace.IsModified;
            await (Task)Invoke(editor, "StartGenerationTraceAsync")!;
            var view = Field<object>(editor, "_generationTrace");
            var snapshot = Field<TextEditor>(view, "_snapshot");
            var model = Property<PatternGenerationTracePresentation>(view, "Model");
            Assert.That(model.Trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), model.Trace.Detail);
            Assert.That(snapshot.IsReadOnly, Is.True);
            Assert.That(snapshot.Document, Is.Not.SameAs(document));
            Assert.That(snapshot.Text, Is.EqualTo(draft));
            Assert.That(pattern.Source, Is.EqualTo(original));
            Assert.That(editor.HasUnsavedScriptSource, Is.True);
            Assert.That(workspace.IsModified, Is.EqualTo(modifiedBefore));
            Assert.That(changes, Is.Zero);
            Assert.That(auditions, Is.Zero);

            var list = Field<ListBox>(view, "_events");
            var selector = Field<ComboBox>(view, "_order");
            list.SelectedItem = model.Rows.Single(r => r.OccurrenceId == 0);
            selector.SelectedIndex = 1;
            Assert.That((list.SelectedItem as PatternTraceRow)?.OccurrenceId, Is.EqualTo(0));
            selector.SelectedIndex = 2;
            Assert.That(model.SelectedOccurrenceIds, Is.EqualTo(new long[] { 0 }));
            Assert.That((list.SelectedItem as PatternTraceRow)?.OccurrenceId, Is.EqualTo(0));

            int offset = draft.IndexOf("Note(", StringComparison.Ordinal);
            Invoke(editor, "NavigateFromGenerationTrace", offset + 1);
            Assert.That(Property<bool>(editor, "IsGenerationTraceVisible"), Is.False);
            Assert.That(source.SelectionStart, Is.EqualTo(offset));
            Assert.That(source.SelectionLength, Is.EqualTo("Note(4, 0, _O(17))".Length));
            Assert.That(source.Document, Is.SameAs(document));
            Assert.That(source.Text, Is.EqualTo(draft));
            source.Document.UndoStack.Undo();
            Assert.That(source.Text, Does.Not.Contain("// draft"));
            Assert.That(pattern.Source, Is.EqualTo(original));
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public async Task StaleNavigationReturnsWithoutApplyingOldOffsets()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Trace");
        ScriptEditorControl editor = new(workspace, pattern, () => { }, _ => { });
        var source = Field<TextEditor>(editor, "_source");
        source.Text = "Note(0, 0, _O(17));";
        await (Task)Invoke(editor, "StartGenerationTraceAsync")!;
        source.Text = "// changed\n" + source.Text;
        source.Select(0, 0);
        Invoke(editor, "NavigateFromGenerationTrace", 1);
        Assert.That(Property<bool>(editor, "IsGenerationTraceVisible"), Is.False);
        Assert.That(source.SelectionStart, Is.Zero);
        Assert.That(source.SelectionLength, Is.Zero);
        Assert.That(Field<TextBlock>(editor, "_message").Text, Does.Contain("stale"));
    }

    [AvaloniaTest]
    public async Task CompileFailureDoesNotSaveAndSequenceHasNoTraceView()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Trace");
        ScriptEditorControl editor = new(workspace, pattern, () => { }, _ => { });
        string original = pattern.Source;
        Field<TextEditor>(editor, "_source").Text = "MissingUnknownFunction();";
        await (Task)Invoke(editor, "StartGenerationTraceAsync")!;
        var view = Field<object>(editor, "_generationTrace");
        Assert.That(Property<PatternGenerationTracePresentation?>(view, "Model"), Is.Null);
        Assert.That(Field<TextBlock>(view, "_status").Text, Does.Contain("Compilation failed"));
        Assert.That(pattern.Source, Is.EqualTo(original));

        var sequence = ScriptDocumentEditor.CreateScriptSequence(workspace, "Sequence");
        ScriptEditorControl seq = new(workspace, sequence, () => { }, _ => { });
        await (Task)Invoke(seq, "StartGenerationTraceAsync")!;
        Assert.That(Field<object?>(seq, "_generationTrace"), Is.Null);
    }

    [AvaloniaTest]
    public async Task CancelAndAbandonCannotPublishIntoReplacementRun()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Trace");
        ScriptEditorControl editor = new(workspace, pattern, () => { }, _ => { });
        var source = Field<TextEditor>(editor, "_source");
        source.Text = "while (true) { }";
        Task canceled = (Task)Invoke(editor, "StartGenerationTraceAsync")!;
        var view = Field<object>(editor, "_generationTrace");
        Field<Button>(view, "_cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await canceled;
        Assert.That(Property<PatternGenerationTracePresentation>(view, "Model").Trace.Status,
            Is.EqualTo(PatternGenerationTraceStatus.Canceled));

        Task abandoned = (Task)Invoke(editor, "StartGenerationTraceAsync")!;
        Invoke(editor, "ReturnFromGenerationTrace", false);
        source.Text = "Note(0, 0, _O(17));";
        await (Task)Invoke(editor, "StartGenerationTraceAsync")!;
        await abandoned;
        var result = Property<PatternGenerationTracePresentation>(view, "Model").Trace;
        Assert.That(result.Source, Is.EqualTo(source.Text));
        Assert.That(result.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed));
        Assert.That(result.Events.Count, Is.EqualTo(1));
    }
}

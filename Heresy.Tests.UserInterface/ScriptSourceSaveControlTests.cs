using System;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using AvaloniaEdit;

using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class ScriptSourceSaveControlTests
{
    private static T Field<T>(object target, string name) where T : class =>
        (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    [AvaloniaTest]
    public void ScriptSequenceSaveIsTextOnlyAndReversible()
    {
        DocumentWorkspace workspace = new();
        var sequence = ScriptDocumentEditor.CreateScriptSequence(workspace, "Test");
        ScriptEditorControl editor = new(workspace, sequence, () => { }, _ => { });
        TextEditor source = Field<TextEditor>(editor, "_source");
        Button save = Field<Button>(editor, "_saveSource");
        string original = source.Text;
        Assert.That(save.Content, Is.EqualTo("Save"));
        Assert.That(save.IsEnabled, Is.False);

        source.Text = original + "\n// edited";
        Assert.That(editor.HasUnsavedScriptSource, Is.True);
        Assert.That(save.IsEnabled, Is.True);
        Assert.That(sequence.Source, Is.EqualTo(original));

        source.Text = original;
        Assert.That(editor.HasUnsavedScriptSource, Is.False);
        Assert.That(save.IsEnabled, Is.False);

        source.Text = original + "\n// saved";
        Assert.That(editor.TrySaveScriptSource(), Is.True);
        Assert.That(sequence.Source, Is.EqualTo(source.Text));
        Assert.That(save.IsEnabled, Is.False);

        source.Text += "\nMissingUnknownFunction();";
        Assert.That(editor.TrySaveScriptSource(), Is.False);
        Assert.That(save.IsEnabled, Is.True);
        Assert.That(sequence.Source, Does.Not.Contain("MissingUnknownFunction"));
        editor.DiscardUnsavedScriptSource();
        Assert.That(editor.HasUnsavedScriptSource, Is.False);
        Assert.That(source.Text, Is.EqualTo(sequence.Source));
    }

    [AvaloniaTest]
    public void PatternLayoutDoesNotEnableSourceSave()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Test");
        ScriptEditorControl editor = new(workspace, pattern, () => { }, _ => { });
        Button save = Field<Button>(editor, "_saveSource");
        Field<TextBox>(editor, "_rowCount").Text = "96";
        Assert.That(editor.HasUnsavedScriptSource, Is.False);
        Assert.That(save.IsEnabled, Is.False);
        TextEditor source = Field<TextEditor>(editor, "_source");
        source.Text += "\n// changed";
        Assert.That(save.IsEnabled, Is.True);
        editor.DiscardUnsavedScriptSource();
        Assert.That(save.IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public void EnvelopeSourceSaveDoesNotImplicitlyApplyReleaseLimit()
    {
        DocumentWorkspace workspace = new();
        var envelope = EnvelopeDocumentEditor.CreateScriptEnvelope(workspace, "Test");
        ScriptEnvelopeEditorControl editor =
            new(workspace, envelope, () => { }, _ => { });
        TextBox source = Field<TextBox>(editor, "_source");
        TextBox release = Field<TextBox>(editor, "_release");
        Button save = Field<Button>(editor, "_saveSource");
        release.Text = "8.5";
        Assert.That(save.IsEnabled, Is.False);
        source.Text = "0.25";
        Assert.That(save.IsEnabled, Is.True);
        Assert.That(editor.TrySaveScriptSource(), Is.True);
        Assert.That(envelope.Source, Is.EqualTo("0.25"));
        Assert.That(envelope.ReleaseDuration, Is.Null);
        Assert.That(save.IsEnabled, Is.False);
        typeof(ScriptEnvelopeEditorControl)
            .GetMethod("ApplyReleaseLimit",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, null);
        Assert.That(envelope.ReleaseDuration,
            Is.EqualTo(TimeSpan.FromSeconds(8.5)));
    }

    [AvaloniaTest]
    public void InstrumentThreeSourceFieldsTrackTheirOwnSaveButtons()
    {
        DocumentWorkspace workspace = new();
        var instrument = ScriptInstrumentDocumentEditor.Create(
            workspace, "Test", "-1");
        ScriptInstrumentEditorControl editor =
            new(workspace, instrument, () => { }, _ => { });
        TextBox selector = Field<TextBox>(editor, "_expression");
        TextBox pitch = Field<TextBox>(editor, "_pitchExpression");
        TextBox nna = Field<TextBox>(editor, "_nnaExpression");
        TextBox fade = Field<TextBox>(editor, "_nnaFadeMilliseconds");
        Button saveExpressions = Field<Button>(editor, "_saveExpressions");
        Button saveNna = Field<Button>(editor, "_saveNnaExpression");
        Assert.That(saveExpressions.IsEnabled, Is.False);
        Assert.That(saveNna.IsEnabled, Is.False);
        fade.Text = "62";
        Assert.That(saveNna.IsEnabled, Is.False,
            "Fade-duration metadata does not make source text dirty");

        selector.Text = "0";
        Assert.That(saveExpressions.IsEnabled, Is.True);
        selector.Text = "-1";
        Assert.That(saveExpressions.IsEnabled, Is.False);
        pitch.Text = "1.25";
        nna.Text = "InstrumentNnaAction.Continue";
        Assert.That(saveExpressions.IsEnabled, Is.True);
        Assert.That(saveNna.IsEnabled, Is.True);
        Assert.That(editor.TrySaveScriptSource(), Is.True);
        Assert.That(instrument.PitchExpression, Is.EqualTo("1.25"));
        Assert.That(instrument.NnaExpression,
            Is.EqualTo("InstrumentNnaAction.Continue"));
        Assert.That(saveExpressions.IsEnabled, Is.False);
        Assert.That(saveNna.IsEnabled, Is.False);
        Assert.That(instrument.NnaFadeDuration,
            Is.EqualTo(TimeSpan.FromMilliseconds(62)));

        nna.Text = "InstrumentNnaAction.Cut";
        Assert.That(saveNna.IsEnabled, Is.True);
        editor.DiscardUnsavedScriptSource();
        Assert.That(nna.Text, Is.EqualTo(instrument.NnaExpression));
        Assert.That(editor.HasUnsavedScriptSource, Is.False);
    }
    [AvaloniaTest]
    public void InstrumentDismissalSaveIsAtomicIfOneOfThreeSourcesIsInvalid()
    {
        DocumentWorkspace workspace = new();
        var instrument = ScriptInstrumentDocumentEditor.Create(
            workspace, "Test", "-1");
        ScriptInstrumentEditorControl editor =
            new(workspace, instrument, () => { }, _ => { });
        TextBox selector = Field<TextBox>(editor, "_expression");
        TextBox pitch = Field<TextBox>(editor, "_pitchExpression");
        TextBox nna = Field<TextBox>(editor, "_nnaExpression");
        uint before = workspace.Document.AudioRevision;
        selector.Text = "0";
        pitch.Text = "1.5";
        nna.Text = "System.Environment.TickCount";
        Assert.That(editor.TrySaveScriptSource(), Is.False);
        Assert.That(instrument.Source, Is.EqualTo("-1"));
        Assert.That(instrument.PitchExpression, Is.EqualTo("1.0"));
        Assert.That(instrument.NnaExpression,
            Is.EqualTo("InstrumentNnaAction.Inherit"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(before));
        Assert.That(editor.HasUnsavedScriptSource, Is.True);
        editor.DiscardUnsavedScriptSource();
        Assert.That(editor.HasUnsavedScriptSource, Is.False);
    }

}

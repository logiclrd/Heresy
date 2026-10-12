using System;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Input;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentNnaEditorControlTests
{
    [AvaloniaTest]
    public void DataInstrumentOverrideIsAtomicValidatedAndDetectsStaleEditors()
    {
        DocumentWorkspace workspace = new();
        InstrumentDefinition instrument =
            InstrumentDocumentEditor.CreateInstrument(workspace, "NNA data");
        InstrumentEditorControl editor = new(workspace, instrument, () => { }, _ => { });
        ComboBox action = Field<ComboBox>(editor, "_nnaOverride");
        TextBox fade = Field<TextBox>(editor, "_nnaFadeMilliseconds");
        Assert.That(action.SelectedItem, Is.EqualTo(InstrumentNnaOverride.UseToneNna));
        Assert.That(fade.IsVisible, Is.False);
        uint revision = workspace.Document.AudioRevision;

        action.SelectedItem = InstrumentNnaOverride.Fade;
        Assert.That(fade.IsVisible, Is.True);
        fade.Text = "invalid";
        Invoke(editor, "ApplyNnaOverride");
        Assert.That(instrument.NnaOverride, Is.EqualTo(InstrumentNnaOverride.UseToneNna));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
        Assert.That(Field<TextBlock>(editor, "_message").Text, Does.Contain("positive finite"));

        fade.Text = "27.5";
        Invoke(editor, "ApplyNnaOverride");
        Assert.That(instrument.NnaOverride, Is.EqualTo(InstrumentNnaOverride.Fade));
        Assert.That(instrument.NnaFadeDuration, Is.EqualTo(TimeSpan.FromMilliseconds(27.5)));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
        Invoke(editor, "ApplyNnaOverride");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));

        InstrumentDocumentEditor.SetNnaOverride(workspace, instrument,
            InstrumentNnaOverride.Off, TimeSpan.FromMilliseconds(90));
        Invoke(editor, "ApplyNnaOverride");
        Assert.That(instrument.NnaOverride, Is.EqualTo(InstrumentNnaOverride.Off));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2));
        Assert.That(Field<TextBlock>(editor, "_message").Text, Does.Contain("Reopen"));
    }

    [AvaloniaTest]
    public void InstrumentContinueSelectorPublishesImmediatelyButFadeWaitsForValidDuration()
    {
        DocumentWorkspace workspace = new();
        InstrumentDefinition instrument =
            InstrumentDocumentEditor.CreateInstrument(workspace, "Continue selector");
        int changed = 0;
        InstrumentEditorControl editor = new(workspace, instrument,
            () => { }, _ => changed++);
        ComboBox action = Field<ComboBox>(editor, "_nnaOverride");
        TextBox fade = Field<TextBox>(editor, "_nnaFadeMilliseconds");
        uint before = workspace.Document.AudioRevision;

        action.SelectedItem = InstrumentNnaOverride.Continue;
        Assert.That(instrument.NnaOverride,
            Is.EqualTo(InstrumentNnaOverride.Continue),
            "selecting Continue must not silently leave the active Instrument in Cut mode");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(before + 1));
        Assert.That(changed, Is.EqualTo(1));
        SongDocumentSnapshot saved = SongDocumentSnapshot.Create(workspace.Document);
        Assert.That(((InstrumentDefinition)saved.Document.Objects[instrument.Id]).NnaOverride,
            Is.EqualTo(InstrumentNnaOverride.Continue),
            "a new playback snapshot should see the selection immediately");

        action.SelectedItem = InstrumentNnaOverride.Fade;
        Assert.That(instrument.NnaOverride, Is.EqualTo(InstrumentNnaOverride.Continue),
            "Fade needs a valid duration before it can be published");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(before + 1));
        fade.Text = "not a duration";
        Invoke(editor, "ApplyNnaOverride");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(before + 1));
        Assert.That(Field<TextBlock>(editor, "_message").Text,
            Does.Contain("positive finite"));

        fade.Text = "32.5";
        Invoke(editor, "ApplyNnaOverride");
        Assert.That(instrument.NnaOverride, Is.EqualTo(InstrumentNnaOverride.Fade));
        Assert.That(instrument.NnaFadeDuration,
            Is.EqualTo(TimeSpan.FromMilliseconds(32.5)));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(before + 2));
        action.SelectedItem = InstrumentNnaOverride.Continue;
        Assert.That(instrument.NnaOverride, Is.EqualTo(InstrumentNnaOverride.Continue));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(before + 3));
        Assert.That(changed, Is.EqualTo(3));
    }

    [AvaloniaTest]
    public void ToneNnaPersistsInSourcelessDraftThenPublishesOnSourceAssignment()
    {
        DocumentWorkspace workspace = new();
        InstrumentDefinition instrument =
            InstrumentDocumentEditor.CreateInstrument(workspace, "Draft");
        ObjectId sourceId = AddPattern(workspace);
        InstrumentEditorControl editor = new(workspace, instrument, () => { }, _ => { });
        InstrumentToneGridModel model = Field<InstrumentToneGridModel>(editor, "_model");
        Grid row = BuildToneRow(editor, 0);
        StackPanel nnaCell = (StackPanel)((Border)row.Children.Single(x => Grid.GetColumn(x) == 5)).Child!;
        ComboBox action = nnaCell.Children.OfType<ComboBox>().Single();
        TextBox fade = nnaCell.Children.OfType<TextBox>().Single();
        uint baseline = workspace.Document.AudioRevision;

        action.SelectedItem = InstrumentNnaAction.Fade;
        Assert.That(fade.IsVisible, Is.True);
        Assert.That(model.Rows.Single(x => x.Index == 0).Cells.Nna,
            Is.EqualTo(InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(100))));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(baseline));
        Assert.That(instrument.ToneSpecifications, Is.Empty);

        fade.Text = "23";
        fade.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Assert.That(model.Rows.Single(x => x.Index == 0).Cells.Nna,
            Is.EqualTo(InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(23))));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(baseline));

        ComboBox source = (ComboBox)((Border)row.Children.Single(x => Grid.GetColumn(x) == 2)).Child!;
        object choice = source.ItemsSource!.Cast<object>().Single(x =>
            (ObjectId?)x.GetType().GetProperty("Id")!.GetValue(x) == sourceId);
        source.SelectedItem = choice;
        Assert.That(instrument.ToneSpecifications, Has.Count.EqualTo(1));
        Assert.That(instrument.ToneSpecifications[0].Nna,
            Is.EqualTo(InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(23))));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(baseline + 1));
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nna-ui.hm.json");
        var roundTrip = SongDocumentJson.Deserialize(
            SongDocumentJson.Serialize(workspace.Document, file), file);
        var restored = (InstrumentDefinition)roundTrip.Objects[instrument.Id];
        Assert.That(restored.ToneSpecifications[0].Nna,
            Is.EqualTo(InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(23))));
    }

    [AvaloniaTest]
    public void RowNnaEditsCopyOnWriteSharedTonesAndRejectBadDurations()
    {
        DocumentWorkspace workspace = new();
        InstrumentDefinition instrument =
            InstrumentDocumentEditor.CreateInstrument(workspace, "Shared");
        ObjectId source = AddPattern(workspace);
        InstrumentToneGridModel prior = new(workspace, instrument);
        prior.SetRow(0, new InstrumentToneGridCells(SourceId: source));
        prior.SetRow(1, new InstrumentToneGridCells(SourceId: source));
        Assert.That(instrument.ToneSpecifications, Has.Count.EqualTo(1));
        InstrumentEditorControl editor = new(workspace, instrument, () => { }, _ => { });
        InstrumentToneGridModel model = Field<InstrumentToneGridModel>(editor, "_model");
        Grid row = BuildToneRow(editor, 0);
        StackPanel cell = (StackPanel)((Border)row.Children.Single(x => Grid.GetColumn(x) == 5)).Child!;
        ComboBox action = cell.Children.OfType<ComboBox>().Single();
        TextBox fade = cell.Children.OfType<TextBox>().Single();
        uint revision = workspace.Document.AudioRevision;

        action.SelectedItem = InstrumentNnaAction.Off;
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
        Assert.That(model.Rows.Single(x => x.Index == 1).Cells.Nna.Action,
            Is.EqualTo(InstrumentNnaAction.Inherit));
        Assert.That(model.Rows.Single(x => x.Index == 0).Cells.Nna.Action,
            Is.EqualTo(InstrumentNnaAction.Off));
        action.SelectedItem = InstrumentNnaAction.Fade;
        fade.Text = "0";
        fade.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Assert.That(Field<TextBlock>(editor, "_message").Text, Does.Contain("positive finite"));
        Assert.That(model.Rows.Single(x => x.Index == 0).Cells.Nna,
            Is.EqualTo(InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(100))));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2));
        Assert.That(model.Rows.Single(x => x.Index == 1).Cells.Nna.Action,
            Is.EqualTo(InstrumentNnaAction.Inherit));
    }

    [AvaloniaTest]
    public void ScriptExpressionAndSelectedToneNnaAreIndependentlyEditableAndPersisted()
    {
        DocumentWorkspace workspace = new();
        ObjectId source = AddPattern(workspace);
        ScriptInstrumentDefinition instrument =
            ScriptInstrumentDocumentEditor.Create(workspace, "Script", "0");
        ScriptInstrumentEditorControl editor =
            new(workspace, instrument, () => { }, _ => { });
        TextBox expression = Field<TextBox>(editor, "_nnaExpression");
        TextBox fade = Field<TextBox>(editor, "_nnaFadeMilliseconds");
        Assert.That(expression.Text, Is.EqualTo("InstrumentNnaAction.Inherit"));
        uint revision = workspace.Document.AudioRevision;
        expression.Text = "System.Environment.TickCount";
        Invoke(editor, "ApplyNnaExpression");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
        Assert.That(Field<TextBlock>(editor, "_message").Text, Is.Not.Empty);

        expression.Text =
            "pitchMultiplier < 1.5 ? InstrumentNnaAction.Continue : InstrumentNnaAction.Inherit";
        fade.Text = "47";
        Invoke(editor, "ApplyNnaExpression");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
        Assert.That(instrument.NnaFadeDuration, Is.EqualTo(TimeSpan.FromMilliseconds(47)));
        Assert.That(instrument.NnaExpression, Is.EqualTo(expression.Text));
        Invoke(editor, "ApplyNnaExpression");
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));

        ComboBox sound = Field<ComboBox>(editor, "_sound");
        sound.SelectedItem = sound.ItemsSource!.Cast<object>().Single(x =>
            (ObjectId?)x.GetType().GetProperty("Id")!.GetValue(x) == source);
        ComboBox toneAction = Field<ComboBox>(editor, "_toneNna");
        TextBox toneFade = Field<TextBox>(editor, "_toneNnaFade");
        toneAction.SelectedItem = InstrumentNnaAction.Fade;
        toneFade.Text = "35";
        Invoke(editor, "AddTone");
        Assert.That(instrument.ToneSpecifications, Has.Count.EqualTo(1));
        Assert.That(instrument.ToneSpecifications[0].SourceId, Is.EqualTo(source));
        Assert.That(instrument.ToneSpecifications[0].Nna,
            Is.EqualTo(InstrumentNnaSetting.Fade(TimeSpan.FromMilliseconds(35))));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2));

        Invoke(editor, "RefreshRows");
        ListBox rows = Field<ListBox>(editor, "_tones");
        rows.SelectedItem = rows.ItemsSource!.Cast<object>().Single();
        toneAction.SelectedItem = InstrumentNnaAction.Off;
        Invoke(editor, "UpdateTone");
        Assert.That(instrument.ToneSpecifications[0].Nna.Action,
            Is.EqualTo(InstrumentNnaAction.Off));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 3));
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "script-nna-ui.hm.json");
        var roundTrip = SongDocumentJson.Deserialize(
            SongDocumentJson.Serialize(workspace.Document, file), file);
        var restored = (ScriptInstrumentDefinition)roundTrip.Objects[instrument.Id];
        Assert.That(restored.NnaExpression, Is.EqualTo(expression.Text));
        Assert.That(restored.NnaFadeDuration, Is.EqualTo(TimeSpan.FromMilliseconds(47)));
        Assert.That(restored.ToneSpecifications[0].Nna.Action, Is.EqualTo(InstrumentNnaAction.Off));
    }

    [AvaloniaTest]
    public void ScriptDirtyNnaCannotClobberAnExternalUpdate()
    {
        DocumentWorkspace workspace = new();
        ScriptInstrumentDefinition instrument =
            ScriptInstrumentDocumentEditor.Create(workspace, "Script");
        ScriptInstrumentEditorControl editor =
            new(workspace, instrument, () => { }, _ => { });
        Field<TextBox>(editor, "_nnaExpression").Text = "InstrumentNnaAction.Fade";
        ScriptInstrumentDocumentEditor.SetNnaExpression(
            workspace, instrument, "InstrumentNnaAction.Continue",
            TimeSpan.FromMilliseconds(60));
        uint revision = workspace.Document.AudioRevision;
        Invoke(editor, "ApplyNnaExpression");
        Assert.That(instrument.NnaExpression, Is.EqualTo("InstrumentNnaAction.Continue"));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
        Assert.That(Field<TextBlock>(editor, "_message").Text, Does.Contain("Reopen"));
    }

    [Test]
    public void FadeEditorRejectsNonFiniteOrZeroDurations()
    {
        foreach (string invalid in new[] { "", "0", "-3", "NaN", "Infinity", "1e999" })
            Assert.Throws<ArgumentException>(() =>
                InstrumentNnaEditorValues.ParseFadeMilliseconds(invalid));
        Assert.That(InstrumentNnaEditorValues.ToneSetting(
                InstrumentNnaAction.Inherit, "invalid").Action,
            Is.EqualTo(InstrumentNnaAction.Inherit));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InstrumentNnaEditorValues.ToneSetting((InstrumentNnaAction)900, "3"));
    }

    private static ObjectId AddPattern(DocumentWorkspace workspace)
    {
        ObjectId id = workspace.Document.AllocateObjectId();
        workspace.Document.Add(new DataPatternDefinition(id, "Source"));
        return id;
    }

    private static Grid BuildToneRow(InstrumentEditorControl editor, int index)
    {
        ListBox rows = Field<ListBox>(editor, "_rows");
        var projected = rows.ItemsSource!.Cast<InstrumentToneGridRow>()
            .Single(x => x.Index == index);
        return (Grid)((Border)rows.ItemTemplate!.Build(projected)!).Child!;
    }

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void Invoke(object instance, string method)
        => instance.GetType().GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
}

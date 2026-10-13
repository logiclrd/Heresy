using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Dialogs;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FilterAssignmentAssistantTests
{
    [AvaloniaTest]
    public void LastRecognizedFilterCommentOverridesHintButDoesNotRewriteText()
    {
        SongDocument song = new();
        FilterDefinition first = Add(song, "First", (2, "shared"));
        FilterDefinition second = Add(song, "Second", (2, "shared"));
        string source = """
            # Filter: First
            # this is unrelated
            # Filter: unknown
            # Filter: Second
            # shared: edited by hand
            shared.CutoffHz = 1900
            """;
        NativePatternEffectEditorDialog dialog = new(
            new SetChannelFilterAssignmentsPatternEffect(source, first.Id), song);
        dialog.Show();
        var assistant = Assistant(dialog);

        Assert.That(assistant.Filter.SelectedItem!.ToString(),
            Does.StartWith("Second ("));
        Assert.That(assistant.Stage.SelectedItem!.ToString(),
            Does.StartWith("shared ("));
        Assert.That(Parameter(assistant.Fields, "CutoffHz").Text, Is.EqualTo("1900"));
        Assert.That(Raw(dialog).Text, Is.EqualTo(source),
            "opening and inspecting the assistant must never rewrite the script");
        Assert.That(FilterHint(dialog).Text, Is.EqualTo(second.Id.ToString()));
        dialog.Close();
    }

    [AvaloniaTest]
    public void LastRecognizedUniqueStageHeadingSelectsFilterAndStage()
    {
        SongDocument song = new();
        FilterDefinition first = Add(song, "First", (2, "common"));
        FilterDefinition second = Add(song, "Second", (3, "unique"), (4, "other"));
        string source = "# common: first\n# unknown\n# unique: choose this\nunique.CutoffHz = 1700";
        NativePatternEffectEditorDialog dialog = new(
            new SetNoteFilterAssignmentsPatternEffect(source, first.Id), song);
        dialog.Show();
        var assistant = Assistant(dialog);

        Assert.That(assistant.Filter.SelectedItem!.ToString(),
            Does.StartWith("Second ("));
        Assert.That(assistant.Stage.SelectedItem!.ToString(),
            Does.StartWith("unique ("));
        Assert.That(Parameter(assistant.Fields, "CutoffHz").Text, Is.EqualTo("1700"));
        Assert.That(Raw(dialog).Text, Is.EqualTo(source));
        dialog.Close();
    }

    [AvaloniaTest]
    public void AmbiguousStageHeadingFallsBackToPersistedHint()
    {
        SongDocument song = new();
        FilterDefinition first = Add(song, "First", (2, "common"));
        Add(song, "Second", (2, "common"));
        NativePatternEffectEditorDialog dialog = new(
            new SetChannelFilterAssignmentsPatternEffect(
                "# common: same in both definitions\ncommon.CutoffHz = 1600",
                first.Id), song);
        dialog.Show();
        var assistant = Assistant(dialog);
        Assert.That(assistant.Filter.SelectedItem!.ToString(),
            Does.StartWith("First ("));
        Assert.That(Parameter(assistant.Fields, "CutoffHz").Text, Is.EqualTo("1600"));
        dialog.Close();
    }

    [AvaloniaTest]
    public void ReopeningStageReadsLiveRawTextAndUpdateEditsEachExistingRowInPlace()
    {
        SongDocument song = new();
        FilterDefinition filter = Add(song, "EQ", (2, "stage2"), (20, "stage20"));
        string raw = """
            # stage2: keep this annotation
            stage2.CutoffHz = 300
            # leave this description
            stage20.CutoffHz = 1200
            STAGE2.Q = 1.4
            """;
        NativePatternEffectEditorDialog dialog = new(
            new SetChannelFilterAssignmentsPatternEffect(raw, filter.Id), song);
        dialog.Show();
        var assistant = Assistant(dialog);
        Assert.That(Raw(dialog).Text, Is.EqualTo(raw));
        Assert.That(Parameter(assistant.Fields, "CutoffHz").Text, Is.EqualTo("300"));
        Assert.That(Parameter(assistant.Fields, "Q").Text, Is.EqualTo("1.3999999999999999")
            .Or.EqualTo("1.4"));

        // An external edit to the multiline text must be reflected the next
        // time a stage is chosen, without retaining a stale parameter cache.
        Raw(dialog).Text = raw.Replace("stage2.CutoffHz = 300", "stage2.CutoffHz = 700");
        assistant.Stage.SelectedIndex = 1;
        assistant.Stage.SelectedIndex = 0;
        Assert.That(Parameter(assistant.Fields, "CutoffHz").Text, Is.EqualTo("700"));
        Parameter(assistant.Fields, "CutoffHz").Text = "2500";
        Parameter(assistant.Fields, "Q").Text = "0.9";

        uint revision = song.AudioRevision;
        assistant.Update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string expected = raw.Replace("stage2.CutoffHz = 300",
                "stage2.CutoffHz = 2500")
            .Replace("STAGE2.Q = 1.4", "STAGE2.Q = 0.90000000000000002");
        Assert.That(Raw(dialog).Text, Is.EqualTo(expected));
        Assert.That(Raw(dialog).Text, Does.Not.Contain("BiquadLowPass"));
        Assert.That(song.AudioRevision, Is.EqualTo(revision),
            "the assistant only edits the dialog draft");
        assistant.Update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(Raw(dialog).Text, Is.EqualTo(expected),
            "upserting the same controls must not duplicate rows or headers");
        dialog.Close();
    }

    [AvaloniaTest]
    public void MalformedRowsDoNotEraseValidPreviewOrGetLostOnDraftUpdate()
    {
        SongDocument song = new();
        FilterDefinition filter = Add(song, "EQ", (2, "stage2"));
        string raw = "stage2.CutoffHz = 850\nbad line\nstage2.Q = nonsense";
        NativePatternEffectEditorDialog dialog = new(
            new SetNoteFilterAssignmentsPatternEffect(raw, filter.Id), song);
        dialog.Show();
        var assistant = Assistant(dialog);
        Assert.That(Parameter(assistant.Fields, "CutoffHz").Text, Is.EqualTo("850"));
        // A malformed Q falls back to a valid default while preserving raw Q.
        Assert.That(Parameter(assistant.Fields, "Q").Text, Is.Not.EqualTo("nonsense"));
        Parameter(assistant.Fields, "CutoffHz").Text = "1800";

        assistant.Update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(Raw(dialog).Text, Does.Contain("stage2.CutoffHz = 1800"));
        Assert.That(Raw(dialog).Text, Does.Contain("bad line"));
        Assert.That(Raw(dialog).Text, Does.Not.Contain("# stage2:"),
            "an existing stage assignment must not gain a new section comment");
        Assert.That(Message(dialog).Text, Does.Contain("remaining validation issue"));
        // Strict Save still prevents invalid drafts from being persisted.
        Button apply = ((Grid)dialog.Content!).Children.OfType<StackPanel>()
            .Last().Children.OfType<Button>()
            .Single(b => Equals(b.Content, "Apply"));
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.That(dialog.IsVisible, Is.True);
        Assert.That(Message(dialog).Text, Is.Not.Empty);
        dialog.Close();
    }

    private static FilterDefinition Add(SongDocument song, string name,
        params (int Id, string Name)[] stages)
    {
        ObjectId id = song.AllocateObjectId();
        FilterDefinition filter = new(id, name,
            stages.Select(s => new FilterStageDefinition(
                s.Id, s.Name, FilterKind.BiquadLowPass)).ToArray());
        song.Add(filter);
        return filter;
    }

    private static (ComboBox Filter, ComboBox Stage,
        StackPanel Fields, Button Update) Assistant(
            NativePatternEffectEditorDialog dialog)
    {
        StackPanel assistant = Field<StackPanel>(dialog, "_assistant");
        StackPanel selectors = (StackPanel)assistant.Children[0];
        return ((ComboBox)selectors.Children[1],
            (ComboBox)selectors.Children[3],
            (StackPanel)assistant.Children[2],
            (Button)assistant.Children[4]);
    }

    private static TextBox Parameter(StackPanel fields, string name)
    {
        StackPanel row = fields.Children.OfType<StackPanel>()
            .Single(row => ((TextBlock)row.Children[0]).Text!
                .StartsWith(name + " ", StringComparison.Ordinal));
        return (TextBox)row.Children[1];
    }

    private static TextBox Raw(NativePatternEffectEditorDialog dialog) =>
        Field<Dictionary<string, Control>>(dialog, "_inputs")
            .GetValueOrDefault("assignments") as TextBox
                ?? throw new InvalidOperationException("Missing assignment text");

    private static TextBox FilterHint(NativePatternEffectEditorDialog dialog) =>
        (TextBox)Field<Dictionary<string, Control>>(dialog, "_inputs")["filterId"];

    private static TextBlock Message(NativePatternEffectEditorDialog dialog) =>
        Field<TextBlock>(dialog, "_message");

    private static T Field<T>(NativePatternEffectEditorDialog dialog, string name) =>
        (T)typeof(NativePatternEffectEditorDialog)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!;
}

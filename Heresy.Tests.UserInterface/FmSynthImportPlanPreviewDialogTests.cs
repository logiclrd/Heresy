using System;
using System.IO;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportPlanPreviewDialogTests
{
    [AvaloniaTest]
    public void PreviewRendersRealTreeDataGridAndDoesNotMutateEitherDocument()
    {
        SongDocument source = new(), destination = new();
        FmSynthDefinition nested = Add(source, "Nested",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([
                new FmSoundSourceNode(1, nested.Id, 440),
                new FmMultiNode(2, 1),
            ], 2));
        NodeEnvelopeDefinition local = new(destination.AllocateObjectId(), "Local");
        destination.Add(local);
        SongFmSynthImportSource imported = Input(source, root);
        uint srcRevision = source.DocumentRevision;
        uint dstRevision = destination.DocumentRevision;
        FmSynthImportPlanPreviewDialog dialog = new(imported, [root.Id],
            destination);

        Assert.That(dialog.GridControl, Is.InstanceOf<TreeDataGrid>());
        Assert.That(dialog.GridControl.Source, Is.InstanceOf<
            HierarchicalTreeDataGridSource<FmSynthImportGridRow>>());
        Assert.That(dialog.GridAdapter.Paths.Count, Is.GreaterThan(2));
        Assert.That(dialog.Projection.Summary.NewObjectCount, Is.EqualTo(2));
        Assert.That(dialog.SummaryText, Does.Contain("2 new objects"));
        Assert.That(dialog.SummaryText, Does.Contain("Root"));
        FmImportObjectKey key = dialog.Plan.SelectedRoots.Single();
        dialog.Projection.SetName(key, "Different, intentionally");
        Assert.That(dialog.SummaryText, Does.Contain("Different, intentionally"));
        Assert.That(source.DocumentRevision, Is.EqualTo(srcRevision));
        Assert.That(destination.DocumentRevision, Is.EqualTo(dstRevision));
        Assert.That(destination.Objects.Count, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public void PreviewDefaultsMaximizedWithOneTwoOneColumnsAndStretchingCopyName()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Stretch test",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        FmSynthImportPlanPreviewDialog dialog = new(
            Input(source, root), [root.Id], new SongDocument());
        Assert.That(dialog.WindowState, Is.EqualTo(WindowState.Maximized));
        var columns = dialog.GridAdapter.Source.Columns;
        Assert.That(columns, Has.Count.EqualTo(3));
        Assert.That(columns[0].Width.Value, Is.EqualTo(1));
        Assert.That(columns[1].Width.Value, Is.EqualTo(2));
        Assert.That(columns[2].Width.Value, Is.EqualTo(1));

        // Real template renderer: the name must be the final child of a
        // stretching DockPanel (not a fixed-width StackPanel child).
        FmSynthImportGridRow model = dialog.GridAdapter.Source.Items.Single();
        var cell = typeof(FmSynthImportPlanPreviewDialog)
            .GetMethod("CreateActionCell", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(dialog, [model]);
        DockPanel panel = (DockPanel)cell!;
        Assert.That(panel.LastChildFill, Is.True);
        Assert.That(panel.HorizontalAlignment,
            Is.EqualTo(Avalonia.Layout.HorizontalAlignment.Stretch));
        TextBox name = panel.Children.OfType<TextBox>().Single();
        Assert.That(name.Width, Is.NaN);
        Assert.That(name.HorizontalAlignment,
            Is.EqualTo(Avalonia.Layout.HorizontalAlignment.Stretch));
        Assert.That(panel.Children.Last(), Is.SameAs(name));
    }

    [AvaloniaTest]
    public void GridExpansionsSurviveEditsViaStableKeysAndAliasesNavigateToCurrentOwner()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Shared",
            new FmSynthGraph([
                new FmConstantNode(1, .1),
                new FmOperatorNode(2, FmOperatorKind.Add, [1, 1]),
            ], 2));
        SongFmSynthImportSource imported = Input(source, root);
        FmSynthImportPlanPreviewDialog dialog =
            new(imported, [root.Id], new SongDocument());
        FmSynthImportGridAdapter adapter = dialog.GridAdapter;
        FmImportTreeRowKey rootKey = dialog.Projection.Roots.Single().Key;
        adapter.Source.Expand(adapter.Paths[rootKey]);
        Assert.That(adapter.Source.TryGetModelAt(
            adapter.Paths[rootKey], out FmSynthImportGridRow? expanded), Is.True);
        Assert.That(expanded!.IsExpanded, Is.True);
        FmImportTreeRow alias = dialog.Projection.Rows.Values.Single(row =>
            row.Kind == FmImportTreeRowKind.GraphInput && row.IsAlias);
        FmImportTreeRowKey? target = dialog.Projection.NavigateToCanonical(alias.Key);
        Assert.That(target, Is.Not.Null);
        Assert.That(adapter.NavigateToCanonical(alias.Key), Is.True);
        Assert.That(adapter.Selected, Is.EqualTo(target));
        Assert.That(adapter.Paths.ContainsKey(target!.Value), Is.True);

        FmImportTreeRow canonical = dialog.Projection.Rows[target.Value];
        dialog.Projection.SetConnection(canonical.Input!.Value, false);
        Assert.That(adapter.Paths.ContainsKey(alias.Key), Is.True);
        Assert.That(dialog.Projection.Rows[alias.Key].IsCanonical, Is.True);
        Assert.That(adapter.Source.TryGetModelAt(
            adapter.Paths[rootKey], out expanded), Is.True);
        Assert.That(expanded!.IsExpanded, Is.True);
    }

    [AvaloniaTest]
    public void TreeDataGridActuallyAppliesItsFluentTemplateWhenPreviewIsShown()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Visible",
            new FmSynthGraph([
                new FmConstantNode(1, 1),
                new FmMultiNode(2, 1),
            ], 2));
        SongDocument destination = new();
        FmSynthImportPlanPreviewDialog dialog = new(
            Input(source, root), [root.Id], destination)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        dialog.Show();
        try
        {
            dialog.UpdateLayout();
            Assert.That(dialog.GridControl.Source, Is.Not.Null);
            Assert.That(dialog.GridControl.RowsPresenter, Is.Not.Null,
                "TreeDataGrid must load its control template; a source without a template renders blank");
            Assert.That(dialog.GridControl.Rows, Is.Not.Null);
            Assert.That(dialog.GridAdapter.Paths.Count, Is.GreaterThan(1));
            Assert.That(destination.Objects, Is.Empty);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaTest]
    public void GridDependencyAndPlanLabelsFollowLightAndDarkThemeForegrounds()
    {
        // Regression: the fork's template-cell contents could render black on
        // black in Dark Mode even though the tree chevrons and inputs rendered.
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "ForegroundProbe",
            new FmSynthGraph([
                new FmConstantNode(1, .5),
                new FmMultiNode(2, 1),
            ], 2));
        FmSynthImportPlanPreviewDialog dialog = new(
            Input(source, root), [root.Id], new SongDocument())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        dialog.Show();
        try
        {
            dialog.UpdateLayout();
            TextBlock Dependency() => dialog.GridControl.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(label => label.Text?.Contains("ForegroundProbe",
                    StringComparison.Ordinal) == true);
            TextBlock PlanText() => dialog.GridControl.GetVisualDescendants()
                .OfType<TextBlock>()
                .First(label => label.Text?.StartsWith("Import copy",
                    StringComparison.Ordinal) == true);

            static void AssertTextBrightness(TextBlock label, bool dark)
            {
                Assert.That(label.Foreground, Is.AssignableTo<ISolidColorBrush>(),
                    "A templated grid label needs a resolved theme brush");
                Color color = ((ISolidColorBrush)label.Foreground!).Color;
                int brightness = color.R + color.G + color.B;
                if (dark)
                    Assert.That(brightness, Is.GreaterThan(480),
                        "Dark Mode text must not be black on black");
                else
                    Assert.That(brightness, Is.LessThan(300),
                        "Light Mode text must not be white on white");
            }

            AssertTextBrightness(Dependency(), dark: true);
            AssertTextBrightness(PlanText(), dark: true);

            // DynamicResource is essential: this is the same live window,
            // with no rebuild or hardcoded light/dark color substitution.
            dialog.RequestedThemeVariant = ThemeVariant.Light;
            dialog.UpdateLayout();
            AssertTextBrightness(Dependency(), dark: false);
            AssertTextBrightness(PlanText(), dark: false);

            dialog.RequestedThemeVariant = ThemeVariant.Dark;
            dialog.UpdateLayout();
            AssertTextBrightness(Dependency(), dark: true);
            AssertTextBrightness(PlanText(), dark: true);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaTest]
    public void SelectionDialogExposesPreviewWithoutReplacingLegacyImportButton()
    {
        SongDocument source = new();
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmSynthImportSelectionDialog selector = new(Input(source, root),
            new SongDocument());
        Grid rootGrid = (Grid)selector.Content!;
        StackPanel selectActions = (StackPanel)rootGrid.Children[1];
        StackPanel footer = (StackPanel)rootGrid.Children[3];
        Assert.That(selectActions.Children.OfType<Button>().Any(button =>
            (button.Content as string)?.Contains("preview") == true
            && button.IsEnabled), Is.True);
        Assert.That(footer.Children.OfType<Button>().Any(button =>
            Equals(button.Content, "Import selected")), Is.True);
        Assert.That(footer.Children.OfType<Button>().Any(button =>
            Equals(button.Content, "Cancel")), Is.True);
    }


    [AvaloniaTest]
    public void PreviewModeRefusesCommitEvenWithAValidEditablePlan()
    {
        SongDocument source = new(), destination = new();
        FmSynthDefinition root = Add(source, "Preview only",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        FmSynthImportPlanPreviewDialog dialog = new(
            Input(source, root), [root.Id], destination);
        Assert.That(dialog.Plan.StageA!.IsInternallyValid, Is.True);
        Assert.That(dialog.CanCommit, Is.False);
        Assert.That(dialog.AttemptCommit(), Is.Null);
        Assert.That(dialog.CommittedResult, Is.Null);
        Assert.That(destination.Objects, Is.Empty);
    }

    [AvaloniaTest]
    public void CommitModeDisablesInvalidStageAAndEnablesAfterRepair()
    {
        SongDocument source = new(), destination = new();
        FmSynthDefinition root = Add(source, "Needs remapping",
            new FmSynthGraph([
                new FmSoundSourceNode(0, (ObjectId)999U, 440),
            ], 0));
        FmSynthDefinition local = Add(destination, "Local",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmSynthImportPlanPreviewDialog dialog = new(
            Input(source, root), [root.Id], destination, enableCommit: true);
        Assert.That(dialog.Plan.StageA!.IsInternallyValid, Is.False);
        Assert.That(dialog.CanCommit, Is.False);
        Assert.That(dialog.AttemptCommit(), Is.Null);
        Assert.That(destination.Objects, Has.Count.EqualTo(1));

        FmImportReferenceKey use = dialog.Plan.Effective.References.Single().Use;
        dialog.Projection.ApplyUse(use, FmImportMappingKind.ReuseExisting, local.Id);
        Assert.That(dialog.Plan.StageA!.IsInternallyValid, Is.True);
        Assert.That(dialog.CanCommit, Is.True);
        FmImportCommitResult? committed = dialog.AttemptCommit();
        Assert.That(committed?.IsCommitted, Is.True, committed?.Error);
        Assert.That(committed!.SelectedRootIds, Has.Count.EqualTo(1));
        Assert.That(dialog.CanCommit, Is.False,
            "an accepted plan cannot be committed a second time");
    }

    [AvaloniaTest]
    public void CommitModeAppliesEditedSameSongSourceMultiAndOneUndoableReceipt()
    {
        SongDocument song = new();
        FmSynthDefinition child = Add(song, "Shared",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        FmSynthDefinition root = Add(song, "Original",
            new FmSynthGraph([
                new FmSoundSourceNode(1, child.Id, 440),
                new FmMultiNode(2, 1),
            ], 2));
        FmSynthImportPlanPreviewDialog dialog = new(
            Input(song, root), [root.Id], song, enableCommit: true);
        FmImportObjectKey rootKey = dialog.Plan.SelectedRoots.Single();
        dialog.Projection.SetName(rootKey, "Original"); // intentional duplicate
        int before = song.Objects.Count;
        Assert.That(dialog.CanCommit, Is.True);
        FmImportCommitResult? result = dialog.AttemptCommit();
        Assert.That(result?.IsCommitted, Is.True, result?.Error);
        Assert.That(song.Objects, Has.Count.EqualTo(before + 2));
        Assert.That(result!.SelectedRootIds, Has.Count.EqualTo(1));
        FmSynthDefinition copy = (FmSynthDefinition)song.Objects[
            result.SelectedRootIds.Single()];
        Assert.That(copy.Name, Is.EqualTo(root.Name));
        Assert.That(copy.Id, Is.Not.EqualTo(root.Id));
        Assert.That(copy.Graph.Nodes.OfType<FmMultiNode>(), Has.Exactly(1).Items);
        Assert.That(copy.Graph.Nodes.OfType<FmSoundSourceNode>().Single().SourceId,
            Is.Not.EqualTo(child.Id));
        Assert.That(dialog.CommittedResult, Is.SameAs(result));
        Assert.That(dialog.AttemptCommit(), Is.Null);
        result.Edit!.Undo();
        Assert.That(song.Objects, Has.Count.EqualTo(before));
        result.Edit.Redo();
        Assert.That(song.Objects, Has.Count.EqualTo(before + 2));
    }

    [AvaloniaTest]
    public void FailedStageBPreservesNamesExpansionAndPlanForSafeRetry()
    {
        SongDocument source = new(), destination = new();
        FmSynthDefinition root = Add(source, "Root",
            new FmSynthGraph([new FmConstantNode(0, .5)], 0));
        FmSynthGraph original = root.Graph;
        FmSynthImportPlanPreviewDialog dialog = new(
            Input(source, root), [root.Id], destination, enableCommit: true);
        FmImportObjectKey rootKey = dialog.Plan.SelectedRoots.Single();
        dialog.Projection.SetName(rootKey, "Chosen name");
        FmImportTreeRowKey rowKey = dialog.Projection.Roots.Single().Key;
        dialog.GridAdapter.Source.Expand(dialog.GridAdapter.Paths[rowKey]);

        // Unmarked edits still invalidate the frozen source snapshot at Stage B.
        root.Graph = new FmSynthGraph([new FmConstantNode(0, 17)], 0);
        FmImportCommitResult? failure = dialog.AttemptCommit();
        Assert.That(failure?.IsCommitted, Is.False);
        Assert.That(failure?.Error, Does.Contain("changed"));
        Assert.That(destination.Objects, Is.Empty);
        Assert.That(dialog.CommittedResult, Is.Null);
        Assert.That(dialog.CanCommit, Is.True,
            "a preflight error must not disable a recoverable plan");
        Assert.That(dialog.Plan.GetProposedName(rootKey).Name,
            Is.EqualTo("Chosen name"));
        Assert.That(dialog.GridAdapter.Paths.ContainsKey(rowKey), Is.True);
        Assert.That(dialog.GridAdapter.Source.TryGetModelAt(
            dialog.GridAdapter.Paths[rowKey], out FmSynthImportGridRow? expanded),
            Is.True);
        Assert.That(expanded!.IsExpanded, Is.True);

        // Restoring exactly the old authored snapshot permits retry without
        // rebuilding the dialog or losing the user's edited copy name.
        root.Graph = original;
        FmImportCommitResult? retry = dialog.AttemptCommit();
        Assert.That(retry?.IsCommitted, Is.True, retry?.Error);
        Assert.That(((FmSynthDefinition)destination.Objects[
            retry!.SelectedRootIds.Single()]).Name, Is.EqualTo("Chosen name"));
    }

    private static FmSynthDefinition Add(SongDocument song, string name,
        FmSynthGraph graph)
    {
        FmSynthDefinition synth = new(song.AllocateObjectId(), name, graph);
        song.Add(synth);
        return synth;
    }

    private static SongFmSynthImportSource Input(SongDocument song,
        FmSynthDefinition root)
        => new(Path.GetFullPath("preview-import.hm"), song, [root]);
}

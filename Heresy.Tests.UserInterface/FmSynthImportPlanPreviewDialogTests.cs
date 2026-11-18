using System;
using System.IO;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

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

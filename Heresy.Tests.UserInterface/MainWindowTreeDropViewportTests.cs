using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class MainWindowTreeDropViewportTests
{
    [AvaloniaTest]
    public void DropPreservesOffsetWhenRepositionedRowIsAlreadyFullyVisible()
    {
        WithWindow((window, song, tree, viewer) =>
        {
            SongTreeFolder root = song.GetSectionRoot(SongTreeSection.Patterns);
            SongTreeNode moving = root.Children[0];
            SongTreeNode destination = root.Children[29];
            TreeViewItem destinationControl = ItemForNode(tree, destination);
            double targetTop = destinationControl.TranslatePoint(new Point(), viewer)!.Value.Y
                + viewer.Offset.Y;
            viewer.Offset = new Vector(0, targetTop - viewer.Viewport.Height / 2);
            window.UpdateLayout();
            double before = viewer.Offset.Y;
            Assert.That(before, Is.GreaterThan(0));
            Assert.That(targetTop, Is.GreaterThan(before + 15));
            Assert.That(targetTop, Is.LessThan(before + viewer.Viewport.Height - 15));

            SongTreeEditor.MoveBefore(song, moving, destination);
            RebuildAfterDrop(window, tree, moving);
            window.UpdateLayout();

            Assert.That(viewer.Offset.Y, Is.EqualTo(before).Within(0.01),
                "Selecting a moved row which is already visible must not scroll at all");
            AssertVisible(tree, viewer, moving);
            Assert.That(tree.SelectedItem, Is.SameAs(ItemForNode(tree, moving)));
        });
    }

    [AvaloniaTest]
    public void DropBelowViewportScrollsExactlyEnoughToRevealMovedRow()
    {
        WithWindow((window, song, tree, viewer) =>
        {
            SongTreeFolder root = song.GetSectionRoot(SongTreeSection.Patterns);
            SongTreeNode moving = root.Children[0];
            SongTreeNode destination = root.Children[^1];
            viewer.Offset = new Vector(0, 0);
            window.UpdateLayout();

            SongTreeEditor.MoveBefore(song, moving, destination);
            RebuildAfterDrop(window, tree, moving);
            window.UpdateLayout();

            TreeViewItem row = ItemForNode(tree, moving);
            Border header = (Border)row.Header!;
            double top = header.TranslatePoint(new Point(), viewer)!.Value.Y
                + viewer.Offset.Y;
            double necessary = Math.Max(0, top + header.Bounds.Height - viewer.Viewport.Height);
            Assert.That(necessary, Is.GreaterThan(0));
            Assert.That(viewer.Offset.Y, Is.EqualTo(necessary).Within(0.01),
                "Should scroll to the nearest bottom-aligned fully visible position");
            AssertVisible(tree, viewer, moving);
        });
    }

    [AvaloniaTest]
    public void DropAboveViewportScrollsExactlyEnoughToRevealMovedRow()
    {
        WithWindow((window, song, tree, viewer) =>
        {
            SongTreeFolder root = song.GetSectionRoot(SongTreeSection.Patterns);
            SongTreeNode moving = root.Children[^1];
            SongTreeNode destination = root.Children[0];
            viewer.Offset = new Vector(0, 700);
            window.UpdateLayout();
            Assert.That(viewer.Offset.Y, Is.GreaterThan(0));

            SongTreeEditor.MoveBefore(song, moving, destination);
            RebuildAfterDrop(window, tree, moving);
            window.UpdateLayout();

            Border header = (Border)ItemForNode(tree, moving).Header!;
            double necessary = header.TranslatePoint(new Point(), viewer)!.Value.Y
                + viewer.Offset.Y;
            Assert.That(viewer.Offset.Y, Is.EqualTo(necessary).Within(0.01),
                "Scroll exactly to the new row's content top, accounting for tree padding");
            AssertVisible(tree, viewer, moving);
        });
    }

    private static void WithWindow(Action<MainWindow, SongDocument, TreeView, ScrollViewer> test)
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        DocumentWorkspace workspace = (DocumentWorkspace)typeof(MainWindow)
            .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        SongDocument song = workspace.Document;
        for (int n = 0; n < 60; n++)
            song.Add(new DataPatternDefinition(song.AllocateObjectId(), $"Pattern {n:00}"));

        typeof(MainWindow).GetMethod("RefreshDocumentView",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { "Ready", null });

        window.Show();
        try
        {
            var trees = (Dictionary<SongTreeSection, TreeView>)typeof(MainWindow)
                .GetField("_trees", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            TreeView tree = trees[SongTreeSection.Patterns];
            window.UpdateLayout();
            ScrollViewer viewer = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.That(viewer.Extent.Height, Is.GreaterThan(viewer.Viewport.Height + 400),
                "Need overflowing tree data for meaningful viewport acceptance");
            test(window, song, tree, viewer);
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    private static TreeViewItem ItemForNode(TreeView tree, SongTreeNode node) =>
        tree.ItemsSource!.Cast<TreeViewItem>().Single(x =>
            ReferenceEquals(((SongTreeItemViewModel)x.Tag!).Node, node));

    private static void RebuildAfterDrop(
        MainWindow window, TreeView tree, SongTreeNode moved) =>
        typeof(MainWindow).GetMethod("RefreshAfterTreeDrop",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { tree, moved });

    private static void AssertVisible(TreeView tree, ScrollViewer viewer, SongTreeNode node)
    {
        Border header = (Border)ItemForNode(tree, node).Header!;
        double top = header.TranslatePoint(new Point(), viewer)!.Value.Y;
        Assert.That(top, Is.GreaterThanOrEqualTo(-0.05));
        Assert.That(top + header.Bounds.Height,
            Is.LessThanOrEqualTo(viewer.Viewport.Height + 0.05));
    }
}

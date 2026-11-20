using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class MainWindowTreeDragInputTests
{
    [AvaloniaTest]
    public void HandledTreeViewPointerPressStillArmsDragFromObjectRow()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        DocumentWorkspace workspace = (DocumentWorkspace)typeof(MainWindow)
            .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        SongDocument song = workspace.Document;
        song.Add(new DataPatternDefinition(song.AllocateObjectId(), "Drag me"));
        typeof(MainWindow).GetMethod("RefreshDocumentView",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { "Ready", null });

        window.Show();
        try
        {
            Dictionary<SongTreeSection, TreeView> trees =
                (Dictionary<SongTreeSection, TreeView>)typeof(MainWindow)
                    .GetField("_trees", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!;
            TreeViewItem row = trees[SongTreeSection.Patterns]
                .ItemsSource!.Cast<TreeViewItem>().Single();
            window.UpdateLayout();

            Point? point = row.TranslatePoint(new Point(70, 10), window);
            Assert.That(point.HasValue, Is.True, "The pattern row must be arranged");
            window.MouseMove(point!.Value);
            window.MouseDown(point.Value, MouseButton.Left);

            object? candidate = typeof(MainWindow).GetField("_dragCandidate",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            Assert.That(candidate, Is.SameAs(row.Tag),
                "Drag arming must see pointer presses handled by TreeViewItem");
            window.MouseUp(point.Value, MouseButton.Left);
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }
    [AvaloniaTest]
    public void CapturedTreeDragKeepsPointerBeyondViewportAndReleasesOnMouseUp()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        DocumentWorkspace workspace = (DocumentWorkspace)typeof(MainWindow)
            .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        SongDocument song = workspace.Document;
        song.Add(new DataPatternDefinition(song.AllocateObjectId(), "Drag me"));
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
            TreeViewItem row = tree.ItemsSource!.Cast<TreeViewItem>().Single();
            window.UpdateLayout();
            Point point = row.TranslatePoint(new Point(70, 10), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            Point beyondThreshold = new Point(point.X + 18, point.Y);
            window.MouseMove(beyondThreshold);

            IPointer? pointer = (IPointer?)typeof(MainWindow)
                .GetField("_dragPointer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window);
            Assert.That(pointer, Is.Not.Null);
            Assert.That(pointer!.Captured, Is.SameAs(tree),
                "The tree must own the capture for the entire drag");

            Point treeBottom = tree.TranslatePoint(
                new Point(0, tree.Bounds.Height), window)!.Value;
            Point beyondEdge = new(point.X, treeBottom.Y + 24);
            window.MouseMove(beyondEdge);
            Point dragPosition = (Point)typeof(MainWindow)
                .GetField("_dragPosition", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            Assert.That(dragPosition.Y, Is.GreaterThan(tree.Bounds.Height),
                "Captured mouse movements must keep arriving outside the viewport");
            window.MouseUp(beyondEdge, MouseButton.Left);

            Assert.That(pointer.Captured, Is.Null);
            Assert.That(typeof(MainWindow).GetField("_dragInProgress",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window),
                Is.False);
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ChangingInsertionOverlayDoesNotMoveRows()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        DocumentWorkspace workspace = (DocumentWorkspace)typeof(MainWindow)
            .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        SongDocument song = workspace.Document;
        song.Add(new DataPatternDefinition(song.AllocateObjectId(), "First"));
        song.Add(new DataPatternDefinition(song.AllocateObjectId(), "Second"));
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
            TreeViewItem[] rows = tree.ItemsSource!.Cast<TreeViewItem>().ToArray();
            Border firstHeader = (Border)rows[0].Header!;
            Grid overlayLayer = (Grid)firstHeader.Child!;
            Border guide = (Border)overlayLayer.Children[1];
            window.UpdateLayout();
            double height = firstHeader.Bounds.Height;
            double secondY = rows[1].TranslatePoint(new Point(0, 0), tree)!.Value.Y;
            MethodInfo show = typeof(MainWindow).GetMethod("ShowTreeDropHighlight",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (SongTreeDropPlacement placement in new[]
            {
                SongTreeDropPlacement.Before,
                SongTreeDropPlacement.Into,
                SongTreeDropPlacement.After,
            })
            {
                show.Invoke(window, new object[] { firstHeader, placement });
                window.UpdateLayout();
                Assert.That(firstHeader.Bounds.Height, Is.EqualTo(height).Within(0.0001));
                Assert.That(rows[1].TranslatePoint(new Point(0, 0), tree)!.Value.Y,
                    Is.EqualTo(secondY).Within(0.0001));
                Assert.That(guide.BorderThickness, Is.Not.EqualTo(new Thickness(0)));
            }
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

}

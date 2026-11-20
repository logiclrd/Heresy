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
}

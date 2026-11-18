using System;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class MainWindowImportUndoTests
{
    [AvaloniaTest]
    public void DocumentTreeShortcutsUndoAndRedoImportWithoutStealingEditorsKeys()
    {
        DocumentWorkspace workspace = new();
        MainWindow window = new(workspace);
        window.Show();
        try
        {
            SongDocument song = workspace.Document;
            SongImportBatchEdit edit = Import(song);
            workspace.ImportUndo.Record(song, edit, edit.ImportedObjectIds);
            UpdateMenu(window);
            (MenuItem undo, MenuItem redo) = ImportMenu(window);
            Assert.That(undo.IsEnabled, Is.True);
            Assert.That(redo.IsEnabled, Is.False);
            Assert.That(undo.InputGesture, Is.Null,
                "a global key gesture would intercept editor Ctrl+Z");
            Assert.That(redo.InputGesture, Is.Null);

            Control documentView = DocumentView(window);
            KeyEventArgs ctrlZ = Press(documentView, Key.Z, KeyModifiers.Control);
            Assert.That(ctrlZ.Handled, Is.True);
            Assert.That(song.Objects, Is.Empty);
            Assert.That(undo.IsEnabled, Is.False);
            Assert.That(redo.IsEnabled, Is.True);

            KeyEventArgs ctrlShiftZ = Press(documentView, Key.Z,
                KeyModifiers.Control | KeyModifiers.Shift);
            Assert.That(ctrlShiftZ.Handled, Is.True);
            Assert.That(song.Objects, Has.Count.EqualTo(1));
            Assert.That(undo.IsEnabled, Is.True);
            Assert.That(redo.IsEnabled, Is.False);

            // An editor belongs to a separate control tree. No global
            // Ctrl+Z handler or menu InputGesture may steal its undo.
            ContentControl mainContent = (ContentControl)typeof(MainWindow)
                .GetField("_mainContent",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            TextBox editor = new() { Text = "editable" };
            mainContent.Content = editor;
            UpdateMenu(window);
            Assert.That(undo.IsEnabled, Is.False);
            Assert.That(redo.IsEnabled, Is.False);
            KeyEventArgs editorKey = Press(editor, Key.Z, KeyModifiers.Control);
            Assert.That(editorKey.Handled, Is.False);
            Assert.That(song.Objects, Has.Count.EqualTo(1));

            // A stale routed event on an inactive document tree is also inert.
            KeyEventArgs orphan = Press(documentView, Key.Z, KeyModifiers.Control);
            Assert.That(orphan.Handled, Is.False);
            Assert.That(song.Objects, Has.Count.EqualTo(1));
        }
        finally
        {
            workspace.New(); // no unsaved-document close prompt
            window.Close();
        }
    }

    [AvaloniaTest]
    public void StaleImportUndoRefusesWithoutChangingSongOrChangingMenuDirection()
    {
        DocumentWorkspace workspace = new();
        MainWindow window = new(workspace);
        window.Show();
        try
        {
            SongDocument song = workspace.Document;
            SongImportBatchEdit receipt = Import(song);
            workspace.ImportUndo.Record(song, receipt,
                receipt.ImportedObjectIds);
            UpdateMenu(window);
            FmSynthDefinition copied = (FmSynthDefinition)song.Objects[
                receipt.ImportedObjectIds.Single()];
            copied.Name = "Later editor change without document MarkChanged";
            KeyEventArgs undo = Press(DocumentView(window),
                Key.Z, KeyModifiers.Control);
            Assert.That(undo.Handled, Is.True);
            Assert.That(song.Objects, Has.Count.EqualTo(1));
            Assert.That(copied.Name,
                Is.EqualTo("Later editor change without document MarkChanged"));
            Assert.That(workspace.ImportUndo.CanUndo(song), Is.True);
            Assert.That(workspace.ImportUndo.CanRedo(song), Is.False);
            TextBlock status = (TextBlock)typeof(MainWindow)
                .GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            Assert.That(status.Text, Does.Contain("song changed"));
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    private static SongImportBatchEdit Import(SongDocument song)
    {
        ObjectId next = song.NextUnallocatedObjectId;
        FmSynthDefinition candidate = new(next, "Imported",
            new FmSynthGraph([new FmConstantNode(0, 1)], 0));
        return song.CommitImportedObjects([candidate], next, _ => true);
    }

    private static (MenuItem Undo, MenuItem Redo) ImportMenu(MainWindow window)
    {
        DockPanel shell = (DockPanel)window.Content!;
        Menu menu = shell.Children.OfType<Menu>().Single();
        MenuItem edit = menu.ItemsSource!.Cast<MenuItem>()
            .Single(item => Equals(item.Header, "_Edit"));
        MenuItem[] actions = edit.ItemsSource!.Cast<MenuItem>().ToArray();
        return (actions[0], actions[1]);
    }

    private static void UpdateMenu(MainWindow window) =>
        typeof(MainWindow).GetMethod("UpdateImportUndoActions",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);

    private static Control DocumentView(MainWindow window) =>
        (Control)typeof(MainWindow).GetField("_documentView",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static KeyEventArgs Press(Control target, Key key, KeyModifiers modifiers)
    {
        KeyEventArgs args = new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        };
        target.RaiseEvent(args);
        return args;
    }
}

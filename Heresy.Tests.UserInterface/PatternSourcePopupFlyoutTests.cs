using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Assets;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSourcePopupFlyoutTests
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(PatternEditorControl editor, string name)
        => (T)typeof(PatternEditorControl).GetField(name, PrivateInstance)!
            .GetValue(editor)!;

    private static Flyout OpenSource(
        PatternEditorControl editor, Border placementTarget)
    {
        typeof(PatternEditorControl)
            .GetMethod("OpenSourcePopup", PrivateInstance)!
            .Invoke(editor, [0, 0, placementTarget]);
        return Field<Flyout>(editor, "_sourceFlyout");
    }

    private static (
        Window Owner,
        DocumentWorkspace Workspace,
        DataPatternDefinition Pattern,
        PatternEditorControl Editor,
        Border Target,
        ObjectId First,
        ObjectId Second) Setup()
    {
        DocumentWorkspace workspace = new();
        ObjectId first = workspace.Document.AllocateObjectId();
        workspace.Document.Add(new SampleDefinition(first, "First",
            new ExternalAssetReference(Path.GetFullPath("first.wav"))));
        ObjectId second = workspace.Document.AllocateObjectId();
        workspace.Document.Add(new SampleDefinition(second, "Second",
            new ExternalAssetReference(Path.GetFullPath("second.wav"))));
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "Tracker", 2, 1);
        PatternDocumentEditor.SetSource(workspace, pattern, 0, 0, first);
        Window owner = new() { Width = 1100, Height = 760 };
        PatternEditorControl editor =
            new(owner, workspace, pattern, () => { }, _ => { });
        owner.Content = editor;
        owner.Show();
        owner.UpdateLayout();
        Border target =
            Field<Dictionary<(int Row, int Channel), Border>>(
                editor, "_sourceFields")[(0, 0)];
        return (owner, workspace, pattern, editor, target, first, second);
    }

    [AvaloniaTest]
    public void SourcePickerUsesAnchoredVisibleFlyoutWithExistingSourceList()
    {
        var test = Setup();
        try
        {
            Flyout flyout = OpenSource(test.Editor, test.Target);
            Assert.Multiple(() =>
            {
                Assert.That(flyout.IsOpen, Is.True);
                Assert.That(flyout.Placement, Is.EqualTo(PlacementMode.Bottom));
                Assert.That(flyout.ShowMode, Is.EqualTo(FlyoutShowMode.Standard));
            });
            Border container = (Border)flyout.Content!;
            ListBox list = (ListBox)container.Child!;
            PatternSourceOption[] options =
                (PatternSourceOption[])list.ItemsSource!;
            Assert.Multiple(() =>
            {
                Assert.That(list.Width, Is.EqualTo(300));
                Assert.That(list.MaxHeight, Is.EqualTo(320));
                Assert.That(options.Any(x => x.Id == test.First), Is.True);
                Assert.That(options.Any(x => x.Id == test.Second), Is.True);
                Assert.That(((PatternSourceOption)list.SelectedItem!).Id,
                    Is.EqualTo(test.First));
                Assert.That(TopLevel.GetTopLevel(list), Is.Not.Null,
                    "the dropdown should be hosted in a visible popup root");
            });
            flyout.Hide();
            Assert.That(flyout.IsOpen, Is.False);
            Assert.That(Field<Flyout?>(test.Editor, "_sourceFlyout"), Is.Null);
        }
        finally
        {
            test.Owner.Close();
        }
    }

    [AvaloniaTest]
    public void SourcePickerSelectionAndDismissalPreserveExistingSemantics()
    {
        var test = Setup();
        try
        {
            Flyout first = OpenSource(test.Editor, test.Target);
            Border container = (Border)first.Content!;
            ListBox list = (ListBox)container.Child!;
            PatternSourceOption[] options =
                (PatternSourceOption[])list.ItemsSource!;
            list.SelectedItem = options.Single(x => x.Id == test.Second);

            Assert.That(test.Pattern.Grid[0, 0]!.SourceId,
                Is.EqualTo(test.Second));
            Assert.That(first.IsOpen, Is.False,
                "a selection should close the Source picker");

            // A new popup preselects the committed Source, and cancelling
            // with light-dismiss/Hide must not edit the document again.
            Border target =
                Field<Dictionary<(int Row, int Channel), Border>>(
                    test.Editor, "_sourceFields")[(0, 0)];
            Flyout second = OpenSource(test.Editor, target);
            ListBox secondList = (ListBox)((Border)second.Content!).Child!;
            Assert.That(((PatternSourceOption)secondList.SelectedItem!).Id,
                Is.EqualTo(test.Second));
            uint revision = test.Workspace.Document.AudioRevision;
            second.Hide();
            Assert.Multiple(() =>
            {
                Assert.That(second.IsOpen, Is.False);
                Assert.That(Field<Flyout?>(test.Editor, "_sourceFlyout"),
                    Is.Null);
                Assert.That(test.Workspace.Document.AudioRevision,
                    Is.EqualTo(revision));
                Assert.That(test.Pattern.Grid[0, 0]!.SourceId,
                    Is.EqualTo(test.Second));
            });
        }
        finally
        {
            test.Owner.Close();
        }
    }
}

using System;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SynthUiTerminologyTests
{
    [AvaloniaTest]
    public void PatchesPaneOffersSynthActionsWhileKeepingPcmAndExistingSection()
    {
        MainWindow window = new();
        try
        {
            Grid document = (Grid)typeof(MainWindow)
                .GetField("_documentView",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            Border patches = document.Children.OfType<Border>()
                .Single(border => Grid.GetRow(border) == 1
                    && Grid.GetColumn(border) == 0);
            StackPanel header = ((Grid)patches.Child!).Children
                .OfType<StackPanel>().Single();
            TextBlock title = header.Children.OfType<TextBlock>().Single();
            WrapPanel actions = header.Children.OfType<WrapPanel>().Single();

            Assert.Multiple(() =>
            {
                Assert.That(title.Text, Is.EqualTo("Patches"));
                Assert.That(actions.Children.OfType<Button>()
                    .Select(button => button.Content?.ToString()), Is.EqualTo(new[]
                    {
                        "+ Synth", "+ Import Synths", "+ Import Audio", "+ Folder",
                    }));
                Assert.That(SongTreeSections.GetName(SongTreeSection.Samples),
                    Is.EqualTo("Patches"));
                Assert.That(SongTreeSections.ForKind(SongObjectKind.FmSynth),
                    Is.EqualTo(SongTreeSection.Samples));
            });
        }
        finally
        {
            window.Close();
        }
    }

    [Test]
    public void ExistingFmSynthObjectKindStillOpensEditorWithSynthOnlyMenuLabel()
    {
        SongDocument document = new();
        ObjectId id = document.AllocateObjectId();
        FmSynthDefinition synth = new(id, "Bell",
            new FmSynthGraph([new FmConstantNode(0, 0.25)], 0));
        document.Add(synth);
        SongTreeItemViewModel item = SongTreeItemViewModel.Create(document,
            (SongTreeObject)document.GetSectionRoot(SongTreeSection.Samples)
                .Children.Single());

        Assert.Multiple(() =>
        {
            Assert.That(item.Kind, Is.EqualTo(SongObjectKind.FmSynth));
            Assert.That(item.DisplayName, Is.EqualTo("Bell"));
            Assert.That(SongTreeDefaultActivation.Resolve(
                SongTreeSection.Samples, item),
                Is.EqualTo(SongTreeActivationKind.FmSynth));
            Assert.That(SongTreeDefaultActivation.GetMenuHeader(
                SongTreeActivationKind.FmSynth), Is.EqualTo("Edit Synth..."));
        });
    }

    [AvaloniaTest]
    public void ImportSelectionDialogUsesSynthWordingWithoutChangingSourceObjects()
    {
        SongDocument song = new();
        ObjectId id = song.AllocateObjectId();
        FmSynthDefinition synth = new(id, "Bass",
            new FmSynthGraph([new FmConstantNode(0, 0.5)], 0));
        song.Add(synth);
        SongFmSynthImportSource source =
            new("source.hm.json", song, [synth]);

        FmSynthImportSelectionDialog dialog = new(source);
        Grid content = (Grid)dialog.Content!;
        TextBlock instructions = content.Children.OfType<TextBlock>().Single();
        ScrollViewer scroll = content.Children.OfType<ScrollViewer>().Single();
        CheckBox selected = ((StackPanel)scroll.Content!).Children
            .OfType<CheckBox>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(dialog.Title,
                Is.EqualTo("Import synths from Heresy song"));
            Assert.That(instructions.Text, Does.StartWith(
                "Choose synths to import from source.hm.json."));
            Assert.That(instructions.Text, Does.Not.Contain("FM synth"));
            Assert.That(selected.Content?.ToString(),
                Is.EqualTo($"Bass  (Object {id.Value})"));
            Assert.That(selected.IsChecked, Is.True);
            Assert.That(source.Synths.Single(), Is.SameAs(synth));
            Assert.That(source.Synths.Single().Kind, Is.EqualTo(SongObjectKind.FmSynth));
        });
        dialog.Close();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Media;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.ViewModels;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class TrackerEffectPickerTests
{
    [Test]
    public void CatalogueNamesColourKeysAndFactoriesMatchKeyboardAndStickers()
    {
        TrackerEffectCommand[] choices =
            PatternEffectCodec.TrackerCommands.ToArray();
        Assert.That(choices.Select(choice => choice.Letter),
            Is.EqualTo(Enumerable.Range('A', 26).Select(i => (char)i)));
        Assert.That(choices.Select(choice => choice.Name),
            Has.All.Not.Empty);
        Assert.That(choices.Select(choice => choice.DisplayName).Distinct().Count(),
            Is.EqualTo(26));
        foreach (TrackerEffectCommand choice in choices)
        {
            foreach (byte parameter in new byte[] { 0, 0x15, 0x73, 0xFF })
            {
                Assert.That(PatternEffectCodec.TryCreateTracker(
                    choice.Letter, parameter, out PatternEffect? effect), Is.True,
                    choice.DisplayName);
                Assert.That(effect, Is.Not.Null);
                Assert.That(effect, Is.EqualTo(choice.Create(parameter)));
                Assert.That(PatternEffectCodec.TryDecodeTracker(
                    effect!, out char roundtrip, out byte actual), Is.True);
                Assert.That(roundtrip, Is.EqualTo(choice.Letter));
                Assert.That(actual, Is.EqualTo(parameter));
                PatternEffectViewModel sticker = PatternEffectViewModel.Create(effect!);
                Assert.That(sticker.ColorKey,
                    Is.EqualTo(PatternEffectCodec.TrackerColorKey(choice.Letter)));
            }
        }

        Assert.That(PatternEffectCodec.TryCreateTracker('?', 0, out _), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PatternEffectCodec.TrackerColorKey('?'));
    }

    [AvaloniaTest]
    public void EmptyEffectMenuOffersDirectInPlacePickerWithStickerColours()
    {
        List<(int? Index, bool After, char Command)> called = [];
        PatternEffectStripControl strip = NewStrip((i, after, command) =>
            called.Add((i, after, command)));
        strip.SetEffects([]);
        ContextMenu menu = strip.ContextMenu!;
        MenuItem picker = Items(menu).Single(x =>
            (string?)x.Header == "Insert Tracker Effect...");
        Assert.That(Items(picker).Length, Is.EqualTo(26));
        Assert.That(Items(menu).Any(x =>
            (string?)x.Header == "Insert Native Effect..."), Is.True);
        Assert.That(Items(menu).Any(x =>
            (string?)x.Header == "Paste Effect Stack"), Is.True);

        foreach (TrackerEffectCommand choice in PatternEffectCodec.TrackerCommands)
        {
            MenuItem item = Items(picker).Single(x =>
                (string?)x.Header == choice.DisplayName);
            Border swatch = (Border)item.Icon!;
            Assert.That(swatch.Width, Is.EqualTo(12));
            SolidColorBrush color = (SolidColorBrush)swatch.Background!;
            PatternEffectViewModel sticker = PatternEffectViewModel.Create(
                choice.Create(0));
            Assert.That(color.Color,
                Is.EqualTo(StickerColor(sticker.ColorKey)));
        }
        Click(Items(picker).Single(x => (string?)x.Header ==
            PatternEffectCodec.TrackerCommands[6].DisplayName));
        Assert.That(called, Is.EqualTo(
            new[] { ((int?)null, false, 'G') }));
    }

    [AvaloniaTest]
    public void PopulatedEffectMenuRetainsLegacyActionsAndTargetsClickedIndex()
    {
        List<(int? Index, bool After, char Command)> called = [];
        PatternEffectStripControl strip = NewStrip((i, after, command) =>
            called.Add((i, after, command)));
        PatternEffectViewModel tracker = PatternEffectViewModel.Create(
            new TonePortamentoPatternEffect(0x11));
        PatternEffectViewModel native = PatternEffectViewModel.Create(
            new SetPlaybackFrequencyPatternEffect(440));
        strip.SetEffects(new[] { tracker, native });

        ContextMenu menu = (ContextMenu)typeof(PatternEffectStripControl)
            .GetMethod("BuildContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(strip, new object[] { 1, native })!;
        MenuItem picker = Items(menu).Single(x =>
            (string?)x.Header == "Insert Tracker Effect...");
        Assert.That(Items(menu).Any(x =>
            (string?)x.Header == "Insert Tracker Slot Before"), Is.True);
        Assert.That(Items(menu).Any(x =>
            (string?)x.Header == "Insert Tracker Slot After"), Is.True);
        Assert.That(Items(menu).Any(x =>
            (string?)x.Header == "Insert Native Effect Before..."), Is.True);
        Assert.That(Items(menu).Any(x =>
            (string?)x.Header == "Insert Native Effect After..."), Is.True);
        Assert.That(Items(menu).Any(x =>
            (string?)x.Header == "Edit Parameters..."), Is.True);
        MenuItem before = Items(picker).Single(x =>
            (string?)x.Header == "Before selected");
        MenuItem after = Items(picker).Single(x =>
            (string?)x.Header == "After selected");
        Assert.That(Items(before).Length, Is.EqualTo(26));
        Assert.That(Items(after).Length, Is.EqualTo(26));

        Click(Items(before).Single(x => (string?)x.Header ==
            PatternEffectCodec.TrackerCommands[7].DisplayName));
        Click(Items(after).Single(x => (string?)x.Header ==
            PatternEffectCodec.TrackerCommands[18].DisplayName));
        Assert.That(called, Is.EqualTo(
            new[] { ((int?)1, false, 'H'), ((int?)1, true, 'S') }));
    }

    [AvaloniaTest]
    public void PatternEditorPickerInsertsBeforeAfterClickedStackElementWithOneRevision()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern = PatternDocumentEditor.CreateDataPattern(
            workspace, "Stack", rowCount: 2, channelCount: 1);
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.Effects.Add(new TonePortamentoPatternEffect(0x17));
        cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
        cell.Effects.Add(new VibratoPatternEffect(0x20));
        PatternEditorControl editor = new(new Window(), workspace, pattern,
            () => { }, _ => { });
        uint revision = workspace.Document.AudioRevision;

        InvokePicker(editor, 0, 0, 1, false, 'S');
        Assert.That(cell.Effects, Is.EqualTo(new PatternEffect[]
        {
            new TonePortamentoPatternEffect(0x17),
            new UndefinedTrackerSpecialPatternEffect(0),
            new SetPlaybackFrequencyPatternEffect(440),
            new VibratoPatternEffect(0x20),
        }));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
        InvokePicker(editor, 0, 0, 2, true, 'G');
        Assert.That(cell.Effects, Is.EqualTo(new PatternEffect[]
        {
            new TonePortamentoPatternEffect(0x17),
            new UndefinedTrackerSpecialPatternEffect(0),
            new SetPlaybackFrequencyPatternEffect(440),
            new TonePortamentoPatternEffect(0),
            new VibratoPatternEffect(0x20),
        }));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 2));
    }

    [AvaloniaTest]
    public void EmptyPickerInsertsDirectlyAndSequencePickerEditsMappedPattern()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition first = PatternDocumentEditor.CreateDataPattern(
            workspace, "First", rowCount: 2, channelCount: 1);
        DataPatternDefinition second = PatternDocumentEditor.CreateDataPattern(
            workspace, "Second", rowCount: 2, channelCount: 1);
        DataSequenceDefinition sequence =
            SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
        sequence.Entries.Add(new SequenceEntry(first.Id));
        sequence.Entries.Add(new SequenceEntry(second.Id));
        PatternEditorContext context = PatternEditorContext.ForSequence(
            workspace.Document, sequence, initialEntryIndex: 1);
        PatternEditorControl editor = new(new Window(), workspace, context,
            () => { }, _ => { });
        uint revision = workspace.Document.AudioRevision;

        InvokePicker(editor, context.InitialDisplayRow, 0, null, false, 'A');
        Assert.That(first.Grid[0, 0], Is.Null);
        Assert.That(second.Grid[0, 0]!.Effects, Is.EqualTo(
            new PatternEffect[] { new TrackerSpeedPatternEffect(0) }));
        Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
    }

    private static PatternEffectStripControl NewStrip(
        Action<int?, bool, char> insert)
        => new(
            viewportWidth: 280, collapsedWidth: 54, rowHeight: 26,
            requestExpansion: () => { },
            requestSelection: (_, _) => { },
            requestEditNative: _ => { },
            requestInsertNative: (_, _) => { },
            requestInsertNativeEmpty: () => { },
            requestCopyStack: () => { },
            requestPasteStack: () => { },
            requestDelete: _ => { },
            requestInsert: (_, _) => { },
            requestReorder: (_, _) => { },
            requestInsertTracker: insert);

    private static MenuItem[] Items(ContextMenu menu)
        => menu.ItemsSource!.Cast<object>().OfType<MenuItem>().ToArray();

    private static MenuItem[] Items(MenuItem menu)
        => menu.ItemsSource!.Cast<object>().OfType<MenuItem>().ToArray();

    private static void Click(MenuItem item)
        => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static Avalonia.Media.Color StickerColor(int key)
    {
        Color[] palette =
            (Color[])typeof(PatternEffectStripControl).GetField(
                "Palette", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
        return palette[Math.Abs(key % palette.Length)];
    }

    private static void InvokePicker(
        PatternEditorControl editor, int row, int channel,
        int? index, bool after, char command)
        => typeof(PatternEditorControl).GetMethod(
            "InsertTrackerEffectAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, new object?[] { row, channel, index, after, command });
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Realtime;
using Heresy.UserInterface;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Views;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class DataSequencePlaybackShortcutsTests
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, PrivateInstance)!
            .GetValue(target)!;

    private static KeyEventArgs Press(Control target, Key key,
        KeyModifiers modifiers = KeyModifiers.None)
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

    [AvaloniaTest]
    public async Task F6InDataSequenceEditorPlaysThisSequenceFromFirstOrder()
    {
        RecordingTransport transport = new();
        MainWindow window = new(transport);
        DocumentWorkspace workspace = Field<DocumentWorkspace>(window, "_workspace");
        window.Show();
        try
        {
            DataPatternDefinition pattern =
                PatternDocumentEditor.CreateDataPattern(workspace, "A", 8, 1);
            DataSequenceDefinition sequence = SequenceDocumentEditor
                .CreateDataSequence(workspace, "Edited sequence");
            SequenceDocumentEditor.InsertEntry(workspace, sequence, 0, pattern.Id);
            // The active song's root is intentionally some other Sequence.
            DataSequenceDefinition other = SequenceDocumentEditor
                .CreateDataSequence(workspace, "Other root");
            SequenceDocumentEditor.SetRootSequence(workspace, other);
            SequenceEditorControl editor = Open(window, sequence);

            KeyEventArgs f6 = Press(Field<TextBox>(editor, "_addStartRow"), Key.F6);
            Assert.That(f6.Handled, Is.True);
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.That(transport.PlaySequenceId, Is.EqualTo(sequence.Id));
            Assert.That(transport.PlaySequencePosition, Is.Null,
                "Data Sequence F6 starts at order zero, not the song root");
            Assert.That(transport.PlayPatternId, Is.Null);
            Assert.That(workspace.Document.RootSequenceId, Is.EqualTo(other.Id));
            Assert.That(Press(editor, Key.F6, KeyModifiers.Control).Handled, Is.False);
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    [AvaloniaTest]
    public void PlaybackHighlightTracksActualSequenceOccurrenceAndRebuilds()
    {
        DocumentWorkspace workspace = new();
        DataPatternDefinition pattern =
            PatternDocumentEditor.CreateDataPattern(workspace, "A", 8, 1);
        DataPatternDefinition alternate =
            PatternDocumentEditor.CreateDataPattern(workspace, "B", 8, 1);
        DataSequenceDefinition sequence =
            SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
        SequenceDocumentEditor.InsertEntry(workspace, sequence, 0, pattern.Id);
        SequenceDocumentEditor.InsertEntry(workspace, sequence, 1, pattern.Id);
        SequenceDocumentEditor.InsertEntry(workspace, sequence, 2, alternate.Id);

        SequenceEditorControl editor =
            new(workspace, sequence, () => { }, _ => { }, (_, _) => { });
        Dictionary<int, Border> rows = Field<Dictionary<int, Border>>(editor, "_entryRows");
        Color highlightColor = new UserInterfaceConfiguration().PatternPlaybackRowHighlight;
        void AssertPlaying(int? playing)
        {
            for (int i = 0; i < 3; i++)
            {
                if (playing == i)
                    Assert.That(((SolidColorBrush)rows[i].Background!).Color,
                        Is.EqualTo(highlightColor), "order " + i);
                else
                    Assert.That(rows[i].Background, Is.SameAs(Brushes.Transparent),
                        "order " + i);
            }
        }

        editor.SetPlaybackPosition(
            new PlaybackPatternPosition(pattern.Id, 2, sequence.Id, 1));
        AssertPlaying(1);
        editor.SetPlaybackPosition(
            new PlaybackPatternPosition(pattern.Id, 7, sequence.Id, 0));
        AssertPlaying(0);
        editor.SetPlaybackPosition(
            new PlaybackPatternPosition(alternate.Id, 0, sequence.Id, 2));
        AssertPlaying(2);

        typeof(SequenceEditorControl).GetMethod("RefreshRows", PrivateInstance)!
            .Invoke(editor, null);
        AssertPlaying(2);

        editor.SetPlaybackPosition(
            new PlaybackPatternPosition(pattern.Id, 2, sequence.Id, 2));
        AssertPlaying(null); // Order 2 is now B, not A.
        editor.SetPlaybackPosition(
            new PlaybackPatternPosition(pattern.Id, 2, (ObjectId)987U, 1));
        AssertPlaying(null);
        editor.SetPlaybackPosition(
            new PlaybackPatternPosition(pattern.Id, 2, null, null));
        AssertPlaying(null);
        editor.SetPlaybackPosition(
            new PlaybackPatternPosition(pattern.Id, 2, sequence.Id, 1));
        AssertPlaying(1);
        editor.SetPlaybackPosition(null);
        AssertPlaying(null);
    }

    [AvaloniaTest]
    public async Task LiveTransportPositionUpdatesCurrentEditorAndClearsOnStop()
    {
        RecordingTransport transport = new();
        MainWindow window = new(transport);
        DocumentWorkspace workspace = Field<DocumentWorkspace>(window, "_workspace");
        window.Show();
        try
        {
            DataPatternDefinition pattern =
                PatternDocumentEditor.CreateDataPattern(workspace, "A", 8, 1);
            DataSequenceDefinition sequence =
                SequenceDocumentEditor.CreateDataSequence(workspace, "Sequence");
            for (int i = 0; i < 25; i++)
                SequenceDocumentEditor.InsertEntry(workspace, sequence, i, pattern.Id);

            transport.PublishPosition(
                new PlaybackPatternPosition(pattern.Id, 2, sequence.Id, 1));
            SequenceEditorControl editor = Open(window, sequence);
            Dictionary<int, Border> rows = Field<Dictionary<int, Border>>(editor, "_entryRows");
            Assert.That(rows[1].Background, Is.Not.SameAs(Brushes.Transparent),
                "opening during active playback adopts the transport position");

            transport.PublishPosition(
                new PlaybackPatternPosition(pattern.Id, 3, sequence.Id, 23));
            await Dispatcher.UIThread.InvokeAsync(() => { });
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Loaded);
            Assert.That(rows[1].Background, Is.SameAs(Brushes.Transparent));
            Assert.That(rows[23].Background, Is.Not.SameAs(Brushes.Transparent));
            ScrollViewer scroll = Field<ScrollViewer>(editor, "_scroll");
            Assert.That(scroll.Offset.Y, Is.GreaterThan(0),
                "the playing order should be scrolled into view");

            transport.PublishPosition(null);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.That(rows[23].Background, Is.SameAs(Brushes.Transparent));
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    private static SequenceEditorControl Open(
        MainWindow window, DataSequenceDefinition sequence)
    {
        MethodInfo show = typeof(MainWindow).GetMethods(PrivateInstance)
            .Single(m => m.Name == "ShowSequenceEditor"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(DataSequenceDefinition));
        show.Invoke(window, [sequence, null]);
        window.UpdateLayout();
        return (SequenceEditorControl)Field<ContentControl>(
            window, "_mainContent").Content!;
    }

    private sealed class RecordingTransport :
        ISongPlaybackTransport, IPlaybackPositionTransport
    {
        public ObjectId? PlaySequenceId { get; private set; }
        public SequencePlaybackPosition? PlaySequencePosition { get; private set; }
        public ObjectId? PlayPatternId { get; private set; }
        public PlaybackPatternPosition? CurrentPlaybackPosition { get; private set; }
        public event EventHandler<PlaybackPositionChangedEventArgs>? PlaybackPositionChanged;

        public void PublishPosition(PlaybackPatternPosition? position)
        {
            CurrentPlaybackPosition = position;
            PlaybackPositionChanged?.Invoke(
                this, new PlaybackPositionChangedEventArgs(position));
        }

        public Task PlaySongAsync(SongDocument document) => Task.CompletedTask;

        public Task PlaySequenceAsync(
            SongDocument document, ObjectId sequenceId,
            SequencePlaybackPosition? startPosition = null)
        {
            PlaySequenceId = sequenceId;
            PlaySequencePosition = startPosition;
            return Task.CompletedTask;
        }

        public Task PlayPatternAsync(
            SongDocument document, ObjectId patternId,
            int startRow = 0, bool repeat = false)
        {
            PlayPatternId = patternId;
            return Task.CompletedTask;
        }

        public Task PlayAdHocAsync(SongDocument document, NoteSchedule schedule)
            => Task.CompletedTask;

        public Task SendLiveEventAsync(SongDocument document,
            ChannelTarget target, IReadOnlyList<NoteCommand> commands)
            => Task.CompletedTask;

        public Task StopAsync()
        {
            PublishPosition(null);
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}

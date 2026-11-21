using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;

using Heresy.Core.Objects;
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
public sealed class ScriptSequencePlayHighlightTests
{
    private const BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, PrivateInstance)!
            .GetValue(target)!;

    private static SequenceCallsite? ShownCallsite(ScriptEditorControl editor)
    {
        object renderer = Field<object>(editor, "_playbackRenderer");
        return Field<SequenceCallsite?>(renderer, "_span");
    }

    private static KeyEventArgs Press(Control control, Key key)
    {
        KeyEventArgs args = new()
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
        };
        control.RaiseEvent(args);
        return args;
    }

    [AvaloniaTest]
    public void MarkerTracksOnlyMatchingF6SessionAndUnchangedAuthorSource()
    {
        DocumentWorkspace workspace = new();
        var sequence = ScriptDocumentEditor.CreateScriptSequence(workspace, "Test");
        sequence.Source = "return Play(_O(17));";
        ScriptEditorControl editor = new(workspace, sequence,
            () => { }, _ => { });
        TextEditor source = Field<TextEditor>(editor, "_source");
        const int start = 7;
        SequenceCallsite callsite = new(start, "Play(_O(17))".Length);

        Guid first = Guid.NewGuid();
        editor.BeginEditorSequenceTrace(first);
        PlaybackPatternPosition active = new((ObjectId)17U, 0, sequence.Id, 0)
        {
            EditorCallsiteSession = first,
            EditorPlayCallsite = callsite,
        };
        editor.SetPlaybackPosition(active);
        Assert.That(ShownCallsite(editor), Is.EqualTo(callsite));
        Assert.That(source.Text, Is.EqualTo(sequence.Source),
            "the overlay cannot rewrite or select the user's source");

        editor.SetPlaybackPosition(new PlaybackPatternPosition(
            (ObjectId)17U, 0, (ObjectId)99U, 0)
        {
            EditorCallsiteSession = first,
            EditorPlayCallsite = callsite,
        });
        Assert.That(ShownCallsite(editor), Is.Null);
        editor.SetPlaybackPosition(active);
        Assert.That(ShownCallsite(editor), Is.EqualTo(callsite));

        Guid second = Guid.NewGuid();
        editor.BeginEditorSequenceTrace(second);
        Assert.That(ShownCallsite(editor), Is.Null);
        editor.SetPlaybackPosition(active);
        Assert.That(ShownCallsite(editor), Is.Null,
            "a prior session's delayed row events cannot be highlighted");
        editor.SetPlaybackPosition(active with { EditorCallsiteSession = second });
        Assert.That(ShownCallsite(editor), Is.EqualTo(callsite));

        source.Text += " // updated after F6";
        Assert.That(ShownCallsite(editor), Is.Null,
            "the source version changed while old audio kept running");
        editor.SetPlaybackPosition(active with { EditorCallsiteSession = second });
        Assert.That(ShownCallsite(editor), Is.Null);
        editor.SetPlaybackPosition(null);
        Assert.That(ShownCallsite(editor), Is.Null);
    }

    [AvaloniaTest]
    public async Task F6UsesOptionalEditorTraceTransportAndStopsOnPositionClear()
    {
        RecordingTransport transport = new();
        using LazySongPlaybackTransport lazy = new(() => transport);
        MainWindow window = new(lazy);
        DocumentWorkspace workspace = Field<DocumentWorkspace>(window, "_workspace");
        var sequence = ScriptDocumentEditor.CreateScriptSequence(workspace, "Test");
        sequence.Source = "return Play(_O(17));";
        ScriptEditorControl editor = new(workspace, sequence,
            () => { }, _ => { });
        window.Show();
        try
        {
            Field<ContentControl>(window, "_mainContent").Content = editor;
            window.UpdateLayout();
            TextEditor source = Field<TextEditor>(editor, "_source");

            KeyEventArgs key = Press(source, Key.F6);
            Assert.That(key.Handled, Is.True);
            Assert.That(transport.TraceSequence, Is.EqualTo(sequence.Id));
            Assert.That(transport.TraceSession, Is.Not.Null);
            Assert.That(transport.NormalSequencePlays, Is.Zero);

            var visible = new PlaybackPatternPosition(
                (ObjectId)17U, 0, sequence.Id, 0)
            {
                EditorCallsiteSession = transport.TraceSession,
                EditorPlayCallsite = new SequenceCallsite(7, "Play(_O(17))".Length),
            };
            transport.Publish(visible);
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Background);
            Assert.That(ShownCallsite(editor),
                Is.EqualTo(new SequenceCallsite(7, "Play(_O(17))".Length)));

            transport.Publish(null);
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Background);
            Assert.That(ShownCallsite(editor), Is.Null);

            // A new F6 compiles a distinct session. Old queued events
            // cannot light a different playback snapshot's source.
            Guid oldSession = transport.TraceSession!.Value;
            Assert.That(Press(source, Key.F6).Handled, Is.True);
            Assert.That(transport.TraceSession, Is.Not.EqualTo(oldSession));
            transport.Publish(visible);
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Background);
            Assert.That(ShownCallsite(editor), Is.Null);
        }
        finally
        {
            workspace.New();
            window.Close();
        }
    }

    private sealed class RecordingTransport :
        ISongPlaybackTransport, IEditorSequencePlaybackTransport,
        IPlaybackPositionTransport
    {
        public ObjectId? TraceSequence { get; private set; }
        public Guid? TraceSession { get; private set; }
        public int NormalSequencePlays { get; private set; }
        public PlaybackPatternPosition? CurrentPlaybackPosition { get; private set; }
        public event EventHandler<PlaybackPositionChangedEventArgs>? PlaybackPositionChanged;

        public void Publish(PlaybackPatternPosition? position)
        {
            CurrentPlaybackPosition = position;
            PlaybackPositionChanged?.Invoke(this,
                new PlaybackPositionChangedEventArgs(position));
        }

        public Task PlayEditorSequenceAsync(
            SongDocument document, ObjectId sequenceId, Guid editorCallsiteSession)
        {
            TraceSequence = sequenceId;
            TraceSession = editorCallsiteSession;
            Publish(null);
            return Task.CompletedTask;
        }

        public Task PlaySongAsync(SongDocument document) => Task.CompletedTask;

        public Task PlaySequenceAsync(
            SongDocument document, ObjectId sequenceId,
            SequencePlaybackPosition? startPosition = null)
        {
            NormalSequencePlays++;
            Publish(null);
            return Task.CompletedTask;
        }

        public Task PlayPatternAsync(
            SongDocument document, ObjectId patternId,
            int startRow = 0, bool repeat = false)
            => Task.CompletedTask;

        public Task PlayAdHocAsync(
            SongDocument document, NoteSchedule schedule) => Task.CompletedTask;

        public Task SendLiveEventAsync(SongDocument document,
            ChannelTarget target, IReadOnlyList<NoteCommand> commands)
            => Task.CompletedTask;

        public Task StopAsync()
        {
            Publish(null);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}

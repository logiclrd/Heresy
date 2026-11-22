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

    private static IReadOnlyList<SequenceCallsite> ShownCallsites(ScriptEditorControl editor)
    {
        object renderer = Field<object>(editor, "_playbackRenderer");
        return Field<IReadOnlyList<SequenceCallsite>>(renderer, "_spans");
    }

    private static IReadOnlyList<SequenceCallsite> ShownActiveNoteCallsites(
        ScriptEditorControl editor)
    {
        object renderer = Field<object>(editor, "_playbackRenderer");
        return Field<IReadOnlyList<SequenceCallsite>>(renderer, "_activeNoteSpans");
    }

    [AvaloniaTest]
    public void SustainedNotesUseIndependentOverlayAndHonorSourceSession()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Note trace");
        pattern.Source = "Note(0, 0, _O(2)); Cut(8, 0);";
        ScriptEditorControl editor = new(workspace, pattern, () => { }, _ => { });
        TextEditor source = Field<TextEditor>(editor, "_source");
        long session = EditorCallsiteSessionIds.Next();
        editor.BeginEditorPatternTrace(session);

        SequenceCallsite note = new(0, "Note(0, 0, _O(2))".Length);
        int cutAt = pattern.Source.IndexOf("Cut(", StringComparison.Ordinal);
        SequenceCallsite cut = new(cutAt, "Cut(8, 0)".Length);
        editor.SetPlaybackPosition(new PlaybackPatternPosition(pattern.Id, 3, null, null)
        {
            EditorCallsiteSession = session,
            EditorActiveNoteCallsites = [note],
        });
        Assert.That(ShownActiveNoteCallsites(editor),
            Is.EquivalentTo(new[] { note }));
        Assert.That(ShownCallsites(editor), Is.Empty);

        editor.SetPlaybackPosition(new PlaybackPatternPosition(pattern.Id, 8, null, null)
        {
            EditorCallsiteSession = session,
            EditorPatternCallsites = [cut],
            EditorActiveNoteCallsites = [note],
        });
        Assert.That(ShownActiveNoteCallsites(editor),
            Is.EquivalentTo(new[] { note }));
        Assert.That(ShownCallsites(editor), Is.EquivalentTo(new[] { cut }));
        editor.SetPlaybackPosition(new PlaybackPatternPosition(pattern.Id, 9, null, null)
        {
            EditorCallsiteSession = session,
            EditorActiveNoteCallsites = [],
        });
        Assert.That(ShownActiveNoteCallsites(editor), Is.Empty);

        editor.SetPlaybackPosition(new PlaybackPatternPosition(pattern.Id, 3, null, null)
        {
            EditorCallsiteSession = session,
            EditorActiveNoteCallsites = [note],
        });
        source.Text += " // changed after F6";
        Assert.That(ShownActiveNoteCallsites(editor), Is.Empty);
        Assert.That(ShownCallsites(editor), Is.Empty);
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
    public void PatternRowRendersAllSimultaneousCallsitesAndClearsOnRowOrEdit()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Test");
        pattern.Source = "Note(0, 0, _O(2)); ChannelFilter(0, 0);";
        ScriptEditorControl editor = new(workspace, pattern, () => { }, _ => { });
        TextEditor source = Field<TextEditor>(editor, "_source");
        long session = EditorCallsiteSessionIds.Next();
        editor.BeginEditorPatternTrace(session);
        SequenceCallsite note = new(0, "Note(0, 0, _O(2))".Length);
        int filterStart = pattern.Source.IndexOf("ChannelFilter(", StringComparison.Ordinal);
        SequenceCallsite filter = new(filterStart, "ChannelFilter(0, 0)".Length);
        PlaybackPatternPosition row = new(pattern.Id, 0, null, null)
        {
            EditorCallsiteSession = session,
            EditorPatternCallsites = [note, filter],
            EditorPatternCallsite = filter,
        };
        editor.SetPlaybackPosition(row);
        Assert.That(ShownCallsites(editor), Is.EquivalentTo(new[] { note, filter }));
        Assert.That(source.Text, Is.EqualTo(pattern.Source));

        editor.SetPlaybackPosition(new PlaybackPatternPosition(pattern.Id, 1, null, null)
        {
            EditorCallsiteSession = session,
        });
        Assert.That(ShownCallsites(editor), Is.Empty);
        editor.SetPlaybackPosition(row);
        Assert.That(ShownCallsites(editor), Has.Count.EqualTo(2));

        editor.SetPlaybackPosition(row with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() });
        Assert.That(ShownCallsites(editor), Is.Empty);
        editor.SetPlaybackPosition(row);
        source.Text += " // modified";
        Assert.That(ShownCallsites(editor), Is.Empty);
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

        long first = EditorCallsiteSessionIds.Next();
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

        long second = EditorCallsiteSessionIds.Next();
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
            long oldSession = transport.TraceSession!.Value;
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

    [AvaloniaTest]
    public async Task PatternF6ThroughLazyFacadeHighlightsOnlyCurrentSourceSession()
    {
        RecordingTransport transport = new();
        using LazySongPlaybackTransport lazy = new(() => transport);
        MainWindow window = new(lazy);
        DocumentWorkspace workspace = Field<DocumentWorkspace>(window, "_workspace");
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Test");
        pattern.Source = "ChannelFilter(0, 0);";
        ScriptEditorControl editor = new(workspace, pattern,
            () => { }, _ => { });
        window.Show();
        try
        {
            Field<ContentControl>(window, "_mainContent").Content = editor;
            window.UpdateLayout();
            TextEditor source = Field<TextEditor>(editor, "_source");
            Assert.That(Press(source, Key.F6).Handled, Is.True);
            Assert.That(transport.TracePattern, Is.EqualTo(pattern.Id));
            Assert.That(transport.TracePatternSession, Is.Not.Null);
            Assert.That(transport.NormalPatternPlays, Is.Zero,
                "the production lazy facade must not downgrade a traced F6 audition");

            SequenceCallsite span = new(0, "ChannelFilter(0, 0)".Length);
            PlaybackPatternPosition active = new(pattern.Id, 0, null, null)
            {
                EditorCallsiteSession = transport.TracePatternSession,
                EditorPatternCallsite = span,
            };
            transport.Publish(active);
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Background);
            Assert.That(ShownCallsite(editor), Is.EqualTo(span));

            transport.Publish(new PlaybackPatternPosition((ObjectId)999U, 0, null, null)
            {
                EditorCallsiteSession = transport.TracePatternSession,
                EditorPatternCallsite = span,
            });
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Background);
            Assert.That(ShownCallsite(editor), Is.Null);
            transport.Publish(active);
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Background);
            Assert.That(ShownCallsite(editor), Is.EqualTo(span));

            long oldSession = transport.TracePatternSession!.Value;
            Assert.That(Press(source, Key.F6).Handled, Is.True);
            Assert.That(transport.TracePatternSession, Is.Not.EqualTo(oldSession));
            transport.Publish(active);
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Background);
            Assert.That(ShownCallsite(editor), Is.Null);

            transport.Publish(active with
            {
                EditorCallsiteSession = transport.TracePatternSession,
            });
            await Dispatcher.UIThread.InvokeAsync(() => { },
                DispatcherPriority.Background);
            Assert.That(ShownCallsite(editor), Is.EqualTo(span));
            source.Text += "// modified";
            Assert.That(ShownCallsite(editor), Is.Null,
                "old spans cannot survive a changed author source buffer");
            transport.Publish(null);
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
        IEditorPatternPlaybackTransport, IPlaybackPositionTransport
    {
        public ObjectId? TraceSequence { get; private set; }
        public ObjectId? TracePattern { get; private set; }
        public long? TracePatternSession { get; private set; }
        public int NormalPatternPlays { get; private set; }
        public long? TraceSession { get; private set; }
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
            SongDocument document, ObjectId sequenceId, long editorCallsiteSession)
        {
            TraceSequence = sequenceId;
            TraceSession = editorCallsiteSession;
            Publish(null);
            return Task.CompletedTask;
        }

        public Task PlayEditorPatternAsync(
            SongDocument document, ObjectId patternId, long editorCallsiteSession)
        {
            TracePattern = patternId;
            TracePatternSession = editorCallsiteSession;
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
        {
            NormalPatternPlays++;
            return Task.CompletedTask;
        }

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

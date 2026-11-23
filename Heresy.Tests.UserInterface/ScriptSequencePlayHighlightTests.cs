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
    public void SameSourceNoteInvocationsPaintOneRegionUntilLastLiveVoiceEnds()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(
            workspace, "Repeated source invocation");
        const string note = "Note(0, channel, _O(20))";
        pattern.Source = "for (int channel = 0; channel < 2; channel++) "
            + note + "; Cut(2, 0); Cut(4, 1);";
        ScriptEditorControl editor = new(
            workspace, pattern, () => { }, _ => { });
        TextEditor source = Field<TextEditor>(editor, "_source");
        long session = EditorCallsiteSessionIds.Next();
        editor.BeginEditorPatternTrace(session);
        SequenceCallsite callsite = new(
            pattern.Source.IndexOf(note, StringComparison.Ordinal), note.Length);

        // The producer has two distinct live voices that originated at
        // this same authored statement. The UI presents the union of
        // source spans instead of inventing two overlapping rectangles.
        editor.SetPlaybackPosition(new PlaybackPatternPosition(
            pattern.Id, 0, null, null)
        {
            EditorCallsiteSession = session,
            EditorPatternCallsite = callsite,
            EditorPatternCallsites = [callsite],
            EditorActiveNoteCallsites = [callsite],
        });
        IReadOnlyList<SequenceCallsite> initialGreen = ShownCallsites(editor);
        IReadOnlyList<SequenceCallsite> initialBlue =
            ShownActiveNoteCallsites(editor);
        Assert.Multiple(() =>
        {
            Assert.That(initialGreen, Is.EqualTo(new[] { callsite }));
            Assert.That(initialBlue, Is.EqualTo(new[] { callsite }));
            Assert.That(source.Text, Is.EqualTo(pattern.Source));
        });

        // Multiple activations with equivalent authored offsets do not
        // create duplicated green/blue regions, even if a caller passes
        // noncanonical repeated source spans.
        editor.SetPlaybackPosition(new PlaybackPatternPosition(
            pattern.Id, 0, null, null)
        {
            EditorCallsiteSession = session,
            EditorPatternCallsite = callsite,
            EditorPatternCallsites = [callsite, callsite],
            EditorActiveNoteCallsites = [callsite, callsite],
        });
        Assert.Multiple(() =>
        {
            Assert.That(ShownCallsites(editor), Is.SameAs(initialGreen));
            Assert.That(ShownActiveNoteCallsites(editor),
                Is.SameAs(initialBlue));
        });

        // One voice was cut; the other still owns this exact same span.
        editor.SetPlaybackPosition(new PlaybackPatternPosition(
            pattern.Id, 3, null, null)
        {
            EditorCallsiteSession = session,
            EditorActiveNoteCallsites = [callsite],
        });
        Assert.Multiple(() =>
        {
            Assert.That(ShownCallsites(editor), Is.Empty);
            Assert.That(ShownActiveNoteCallsites(editor),
                Is.SameAs(initialBlue),
                "the shared blue region survives the first voice's Cut");
        });

        editor.SetPlaybackPosition(new PlaybackPatternPosition(
            pattern.Id, 5, null, null)
        {
            EditorCallsiteSession = session,
            EditorActiveNoteCallsites = [],
        });
        Assert.That(ShownActiveNoteCallsites(editor), Is.Empty,
            "blue disappears after the last live voice has ended");
        Assert.That(source.Text, Is.EqualTo(pattern.Source));
    }

    [AvaloniaTest]
    public void DistinctPlaySitesRemainDistinctDespiteTargetingSamePattern()
    {
        DocumentWorkspace workspace = new();
        var sequence = ScriptDocumentEditor.CreateScriptSequence(
            workspace, "Shared target");
        const string play = "Play(_O(17))";
        sequence.Source = "if (sequenceIndex == 0) return " + play
            + "; if (sequenceIndex == 1) return " + play + "; return default;";
        ScriptEditorControl editor = new(
            workspace, sequence, () => { }, _ => { });
        TextEditor source = Field<TextEditor>(editor, "_source");
        int first = sequence.Source.IndexOf(play, StringComparison.Ordinal);
        int second = sequence.Source.IndexOf(
            play, first + play.Length, StringComparison.Ordinal);
        SequenceCallsite firstSpan = new(first, play.Length);
        SequenceCallsite secondSpan = new(second, play.Length);
        long session = EditorCallsiteSessionIds.Next();
        editor.BeginEditorSequenceTrace(session);

        PlaybackPatternPosition firstOrder = new(
            (ObjectId)17U, 0, sequence.Id, 0)
        {
            EditorCallsiteSession = session,
            EditorPlayCallsite = firstSpan,
        };
        PlaybackPatternPosition secondOrder = new(
            (ObjectId)17U, 0, sequence.Id, 1)
        {
            EditorCallsiteSession = session,
            EditorPlayCallsite = secondSpan,
        };
        editor.SetPlaybackPosition(firstOrder);
        Assert.That(ShownCallsite(editor), Is.EqualTo(firstSpan));
        editor.SetPlaybackPosition(secondOrder);
        Assert.That(ShownCallsite(editor), Is.EqualTo(secondSpan),
            "Pattern identity does not determine the painted Play span");

        // A second invocation of the first authored site has no new
        // visual identity; it still paints the original first site.
        editor.SetPlaybackPosition(new PlaybackPatternPosition(
            (ObjectId)17U, 0, sequence.Id, 2)
        {
            EditorCallsiteSession = session,
            EditorPlayCallsite = firstSpan,
        });
        Assert.That(ShownCallsite(editor), Is.EqualTo(firstSpan));
        Assert.That(source.Text, Is.EqualTo(sequence.Source),
            "only the overlay changes, never the user's script text");
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
    public void UnchangedF6PositionNotificationsReuseOverlayArrays()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(
            workspace, "Repeated overlay poll");
        pattern.Source = "Note(0, 0, _O(20)); Cut(2, 0);";
        ScriptEditorControl editor = new(
            workspace, pattern, () => { }, _ => { });
        TextEditor source = Field<TextEditor>(editor, "_source");
        long session = EditorCallsiteSessionIds.Next();
        editor.BeginEditorPatternTrace(session);
        SequenceCallsite note = new(0, "Note(0, 0, _O(20))".Length);
        SequenceCallsite cut = new(
            pattern.Source.IndexOf("Cut(", StringComparison.Ordinal),
            "Cut(2, 0)".Length);
        PlaybackPatternPosition position =
            new(pattern.Id, 2, null, null)
            {
                EditorCallsiteSession = session,
                EditorPatternCallsites = [note, cut],
                EditorActiveNoteCallsites = [note],
            };

        editor.SetPlaybackPosition(position);
        IReadOnlyList<SequenceCallsite> initialGreen = ShownCallsites(editor);
        IReadOnlyList<SequenceCallsite> initialBlue =
            ShownActiveNoteCallsites(editor);
        Assert.Multiple(() =>
        {
            Assert.That(initialGreen, Is.EquivalentTo(new[] { note, cut }));
            Assert.That(initialBlue, Is.EquivalentTo(new[] { note }));
            Assert.That(ShownCallsite(editor), Is.EqualTo(cut));
        });

        // New position records/arrays with unchanged displayed contents
        // should neither replace cached snapshots nor request a redraw.
        editor.SetPlaybackPosition(position with
        {
            EditorPatternCallsites = [note, cut],
            EditorActiveNoteCallsites = [note],
        });
        Assert.That(ShownCallsites(editor), Is.SameAs(initialGreen));
        Assert.That(ShownActiveNoteCallsites(editor),
            Is.SameAs(initialBlue));

        // The renderer still sanitizes duplicated or malformed spans.
        // Sanitization with the same visible result must keep the cache.
        SequenceCallsite invalid = new(
            pattern.Source.Length + 2, 3);
        editor.SetPlaybackPosition(position with
        {
            EditorPatternCallsites = [note, cut, note, invalid],
            EditorActiveNoteCallsites = [note, invalid, note],
        });
        Assert.That(ShownCallsites(editor), Is.SameAs(initialGreen));
        Assert.That(ShownActiveNoteCallsites(editor),
            Is.SameAs(initialBlue));

        // A blue-only lifetime transition must invalidate blue without
        // touching the green action overlay.
        editor.SetPlaybackPosition(position with
        {
            EditorPatternCallsites = [note, cut],
            EditorActiveNoteCallsites = [],
        });
        Assert.Multiple(() =>
        {
            Assert.That(ShownCallsites(editor), Is.SameAs(initialGreen));
            Assert.That(ShownActiveNoteCallsites(editor), Is.Empty);
            Assert.That(ShownActiveNoteCallsites(editor),
                Is.Not.SameAs(initialBlue));
        });

        // New green membership also produces a distinct frozen snapshot.
        editor.SetPlaybackPosition(position with
        {
            EditorPatternCallsites = [cut],
            EditorActiveNoteCallsites = [],
        });
        Assert.That(ShownCallsites(editor),
            Is.EquivalentTo(new[] { cut }));
        Assert.That(ShownCallsites(editor), Is.Not.SameAs(initialGreen));

        // A text edit invalidates the session spans even if audio keeps
        // playing, and subsequent stale positions cannot reapply them.
        source.Text += " // source modified";
        Assert.That(ShownCallsites(editor), Is.Empty);
        editor.SetPlaybackPosition(position);
        Assert.That(ShownCallsites(editor), Is.Empty);
        Assert.That(ShownActiveNoteCallsites(editor), Is.Empty);
    }

    [AvaloniaTest]
    public void FollowPlaybackOptInRequestsNavigationWithoutMovingSelection()
    {
        DocumentWorkspace workspace = new();
        var sequence = ScriptDocumentEditor.CreateScriptSequence(workspace, "Follow");
        sequence.Source = new string('\n', 150) + "return Play(_O(17));";
        ScriptEditorControl editor = new(workspace, sequence, () => { }, _ => { });
        Window window = new() { Width = 1100, Height = 950, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            TextEditor source = Field<TextEditor>(editor, "_source");
            CheckBox follow = Field<CheckBox>(editor, "_followPlayback");
            Assert.That(follow.IsChecked, Is.False,
                "following playback must be opt-in");

            source.Select(3, 2);
            int caret = source.CaretOffset;
            int selectionStart = source.SelectionStart;
            int selectionLength = source.SelectionLength;

            int targetStart = sequence.Source.IndexOf("Play(", StringComparison.Ordinal);
            Assert.That(source.Document.GetLocation(targetStart).Line,
                Is.GreaterThan(100),
                "the authored Play call is deliberately far below the first screen");
            long session = EditorCallsiteSessionIds.Next();
            SequenceCallsite span = new(targetStart, "Play(_O(17))".Length);
            PlaybackPatternPosition active = new((ObjectId)17U, 0, sequence.Id, 0)
            {
                EditorCallsiteSession = session,
                EditorPlayCallsite = span,
            };
            editor.BeginEditorSequenceTrace(session);
            editor.SetPlaybackPosition(active);
            Assert.That(Field<SequenceCallsite?>(editor, "_lastFollowedCallsite"),
                Is.Null, "F6 alone must not request navigation");

            follow.IsChecked = true;
            editor.SetPlaybackPosition(null);
            editor.SetPlaybackPosition(active with
            {
                EditorCallsiteSession = EditorCallsiteSessionIds.Next(),
            });
            Assert.That(Field<SequenceCallsite?>(editor, "_lastFollowedCallsite"),
                Is.Null, "a stale session cannot request source navigation");
            editor.SetPlaybackPosition(active);
            Assert.That(Field<SequenceCallsite?>(editor, "_lastFollowedCallsite"),
                Is.EqualTo(span),
                "the current session should request AvaloniaEdit ScrollTo");
            Assert.Multiple(() =>
            {
                Assert.That(source.CaretOffset, Is.EqualTo(caret));
                Assert.That(source.SelectionStart, Is.EqualTo(selectionStart));
                Assert.That(source.SelectionLength, Is.EqualTo(selectionLength));
                Assert.That(source.Text, Is.EqualTo(sequence.Source));
            });

            // Repeated position updates retain the already-followed call;
            // a new genuine activation is required before following again.
            editor.SetPlaybackPosition(active);
            Assert.That(Field<SequenceCallsite?>(editor, "_lastFollowedCallsite"),
                Is.EqualTo(span));
            source.Text += " // edited after F6";
            editor.SetPlaybackPosition(active);
            Assert.That(Field<SequenceCallsite?>(editor, "_lastFollowedCallsite"),
                Is.Null, "edited source must suppress stale navigation");
        }
        finally { window.Close(); }
    }

    [AvaloniaTest]
    public void FollowPlaybackIgnoresBlueOnlyNoteLifetimeUpdates()
    {
        DocumentWorkspace workspace = new();
        var pattern = ScriptDocumentEditor.CreateScriptPattern(workspace, "Follow");
        const string note = "Note(0, 0, _O(17))";
        const string cut = "Cut(8, 0)";
        pattern.Source = note + ";" + new string('\n', 150) + cut + ";";
        ScriptEditorControl editor = new(workspace, pattern, () => { }, _ => { });
        Window window = new() { Width = 1100, Height = 950, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            CheckBox follow = Field<CheckBox>(editor, "_followPlayback");
            follow.IsChecked = true;
            long session = EditorCallsiteSessionIds.Next();
            editor.BeginEditorPatternTrace(session);

            SequenceCallsite noteSpan = new(0, note.Length);
            int cutStart = pattern.Source.IndexOf(cut, StringComparison.Ordinal);
            SequenceCallsite cutSpan = new(cutStart, cut.Length);
            editor.SetPlaybackPosition(new PlaybackPatternPosition(pattern.Id, 4, null, null)
            {
                EditorCallsiteSession = session,
                EditorActiveNoteCallsites = [noteSpan],
            });
            Assert.That(ShownActiveNoteCallsites(editor),
                Is.EquivalentTo(new[] { noteSpan }));
            Assert.That(Field<SequenceCallsite?>(editor, "_lastFollowedCallsite"),
                Is.Null, "blue lifetime snapshots must never request scrolling");

            editor.SetPlaybackPosition(new PlaybackPatternPosition(pattern.Id, 8, null, null)
            {
                EditorCallsiteSession = session,
                EditorPatternCallsites = [cutSpan],
                EditorActiveNoteCallsites = [noteSpan],
            });
            Assert.That(Field<SequenceCallsite?>(editor, "_lastFollowedCallsite"),
                Is.EqualTo(cutSpan),
                "a new green Cut activation should request source navigation");

            editor.SetPlaybackPosition(new PlaybackPatternPosition(pattern.Id, 9, null, null)
            {
                EditorCallsiteSession = session,
                EditorActiveNoteCallsites = [noteSpan],
            });
            Assert.That(Field<SequenceCallsite?>(editor, "_lastFollowedCallsite"),
                Is.Null, "blue lifetime alone does not keep following the Cut");
        }
        finally { window.Close(); }
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

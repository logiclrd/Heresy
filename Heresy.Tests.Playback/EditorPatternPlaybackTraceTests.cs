using System;
using System.Linq;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class EditorPatternPlaybackTraceTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void SameRowVirtualControlsRetainSpansRegardlessOfOrder(bool reverseOrder)
    {
        SongDocument song = new();
        string volume0 = "VolumeVirtual(0, 7U, .25)";
        string frame0 = "SourceFrameVirtual(0, 7U, 256)";
        string displacement8 = "DisplacementVirtual(8, 7U, NoteDisplacementAction.Continue)";
        string past8 = "PastVirtual(8, 7U, TrackerPastNoteAction.Cut)";
        string source = reverseOrder
            ? $"{frame0}; {volume0}; {past8}; {displacement8};"
            : $"{volume0}; {frame0}; {displacement8}; {past8};";
        ScriptPatternDefinition script = new((ObjectId)5U, "Virtual controls")
        {
            RowCount = 16, ChannelCount = 1, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        Guid session = Guid.NewGuid();
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = session };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            float[] samples = new float[512];
            audio.Render(samples.Length, samples);
            PlaybackPatternPosition? zero = timeline!.GetPositionAt(TimeSpan.Zero);
            Assert.That(zero, Is.Not.Null);
            Assert.That(zero!.EditorCallsiteSession, Is.EqualTo(session));
            Assert.That(zero.EditorPatternCallsites, Is.EquivalentTo(new[]
            {
                new SequenceCallsite(source.IndexOf(volume0, StringComparison.Ordinal),
                    volume0.Length),
                new SequenceCallsite(source.IndexOf(frame0, StringComparison.Ordinal),
                    frame0.Length),
            }));

            PlaybackPatternPosition? after = null;
            for (int i = 0; i < 600; i++)
            {
                audio.Render(samples.Length, samples);
                after = timeline.GetPositionAt(TimeSpan.FromSeconds(
                    (i + 2) * samples.Length / 48000.0));
                if (after?.PatternRow >= 9)
                    break;
            }
            Assert.That(after?.PatternRow, Is.GreaterThanOrEqualTo(9));
            PlaybackPatternPosition? rowEight = timeline.Entries
                .Where(x => x.Position.PatternRow == 8
                    && x.Position.EditorPatternCallsites is { Count: 2 })
                .Select(x => x.Position).LastOrDefault();
            Assert.That(rowEight, Is.Not.Null);
            Assert.That(rowEight!.EditorPatternCallsites, Is.EquivalentTo(new[]
            {
                new SequenceCallsite(source.IndexOf(displacement8, StringComparison.Ordinal),
                    displacement8.Length),
                new SequenceCallsite(source.IndexOf(past8, StringComparison.Ordinal),
                    past8.Length),
            }));
            Assert.That(after!.EditorPatternCallsites, Is.Null,
                "later row boundaries clear both virtual-control highlights");
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SameRowPatternActionsRetainBothSpansRegardlessOfSourceOrder(bool filterFirst)
    {
        SongDocument song = new();
        DataPatternDefinition silentChild = new((ObjectId)2U, "Empty nested sound")
        {
            RowCount = 1, ChannelCount = 1,
        };
        song.Add(silentChild);
        string note0 = "Note(0, 0, _O(2), Octave(0))";
        string filter0 = "ChannelFilter(0, 0)";
        string note8 = "Note(8, 0, _O(2), Octave(.6))";
        string filter8 = "ChannelFilter(8, 0)";
        string source = filterFirst
            ? $"{filter0}; {note0}; {filter8}; {note8};"
            : $"{note0}; {filter0}; {note8}; {filter8};";
        ScriptPatternDefinition script = new((ObjectId)5U, "Simultaneous")
        {
            RowCount = 16, ChannelCount = 1, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        Guid session = Guid.NewGuid();
        PatternPlaybackRequest traced = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = session };
        IAudioOutputSource audio = factory.Create(traced);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(traced,
                out PlaybackPositionTimeline? timeline), Is.True);
            float[] samples = new float[512];
            audio.Render(samples.Length, samples);
            PlaybackPatternPosition? zero = timeline!.GetPositionAt(TimeSpan.Zero);
            Assert.That(zero, Is.Not.Null);
            Assert.That(zero!.EditorPatternCallsites, Is.EquivalentTo(new[]
            {
                new SequenceCallsite(source.IndexOf(note0, StringComparison.Ordinal), note0.Length),
                new SequenceCallsite(source.IndexOf(filter0, StringComparison.Ordinal), filter0.Length),
            }));
            Assert.That(zero.EditorCallsiteSession, Is.EqualTo(session));

            PlaybackPatternPosition? later = null;
            for (int i = 0; i < 600; i++)
            {
                audio.Render(samples.Length, samples);
                later = timeline.GetPositionAt(TimeSpan.FromSeconds(
                    (i + 2) * samples.Length / 48000.0));
                if (later?.PatternRow >= 9)
                    break;
            }
            Assert.That(later?.PatternRow, Is.GreaterThanOrEqualTo(9),
                "the playback cursor should pass the row-eight activity");
            // The timeline is a log, not only the most recent position.
            PlaybackPatternPosition? rowEight = null;
            foreach (PlaybackPositionTimelineEntry entry in timeline.Entries)
                if (entry.Position.PatternRow == 8 &&
                    entry.Position.EditorPatternCallsites is { Count: 2 })
                    rowEight = entry.Position;
            Assert.That(rowEight, Is.Not.Null,
                "both row-eight actions must be simultaneously visible");
            Assert.That(rowEight!.EditorPatternCallsites, Is.EquivalentTo(new[]
            {
                new SequenceCallsite(source.IndexOf(note8, StringComparison.Ordinal), note8.Length),
                new SequenceCallsite(source.IndexOf(filter8, StringComparison.Ordinal), filter8.Length),
            }));
            Assert.That(later!.EditorPatternCallsites, Is.Null,
                "later row boundaries clear both action highlights");
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [Test]
    public void SameRowGlobalAndPhysicalActionsAppearTogetherInPlayback()
    {
        SongDocument song = new();
        // The global tempo/speed changes must be traced at their real
        // musical deadline, with no reordering or dropped same-row spans.
        const string source = """
            Tempo(0, 125);
            Speed(0, 6);
            Pan(0, 0, 0.4);
            Surround(8, 0, true);
            FilterCutoff(8, 0, .5);
            """;
        ScriptPatternDefinition script = new((ObjectId)5U, "Timed controls")
        {
            RowCount = 16, ChannelCount = 1, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        Guid session = Guid.NewGuid();
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = session };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            float[] samples = new float[512];
            audio.Render(samples.Length, samples);
            PlaybackPatternPosition? zero = timeline!.GetPositionAt(TimeSpan.Zero);
            Assert.That(zero, Is.Not.Null);
            Assert.That(zero!.EditorCallsiteSession, Is.EqualTo(session));
            string[] first = { "Tempo(0, 125)", "Speed(0, 6)", "Pan(0, 0, 0.4)" };
            Assert.That(zero.EditorPatternCallsites, Is.EquivalentTo(first.Select(
                call => new SequenceCallsite(source.IndexOf(call, StringComparison.Ordinal),
                    call.Length))));
            for (int i = 0; i < 2000; i++)
                audio.Render(samples.Length, samples);
            var later = timeline.Entries
                .Where(x => x.Position.PatternRow == 8
                    && x.Position.EditorPatternCallsites is { Count: 2 })
                .Select(x => x.Position).LastOrDefault();
            Assert.That(later, Is.Not.Null);
            string[] second = { "Surround(8, 0, true)", "FilterCutoff(8, 0, .5)" };
            Assert.That(later!.EditorPatternCallsites, Is.EquivalentTo(second.Select(
                call => new SequenceCallsite(source.IndexOf(call, StringComparison.Ordinal),
                    call.Length))));
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [Test]
    public void SameRowNoteOffAndCutActionsHaveIndependentPlaybackSpans()
    {
        SongDocument song = new();
        const string source = "Off(0, 0); Cut(0, 0); Off(8, 0); Cut(8, 0);";
        ScriptPatternDefinition script = new((ObjectId)5U, "Actions")
        {
            RowCount = 16, ChannelCount = 1, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        Guid session = Guid.NewGuid();
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = session };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            float[] samples = new float[512];
            audio.Render(samples.Length, samples);
            PlaybackPatternPosition? zero = timeline!.GetPositionAt(TimeSpan.Zero);
            Assert.That(zero!.EditorPatternCallsites, Is.EquivalentTo(new[]
            {
                new SequenceCallsite(0, "Off(0, 0)".Length),
                new SequenceCallsite(source.IndexOf("Cut(0, 0)", StringComparison.Ordinal),
                    "Cut(0, 0)".Length),
            }));
            for (int i = 0; i < 300; i++)
                audio.Render(samples.Length, samples);
            var eight = timeline.Entries
                .Where(x => x.Position.PatternRow == 8
                    && x.Position.EditorPatternCallsites is { Count: 2 })
                .Select(x => x.Position).LastOrDefault();
            Assert.That(eight, Is.Not.Null);
            Assert.That(eight!.EditorPatternCallsites, Is.EquivalentTo(new[]
            {
                new SequenceCallsite(source.IndexOf("Off(8, 0)", StringComparison.Ordinal),
                    "Off(8, 0)".Length),
                new SequenceCallsite(source.IndexOf("Cut(8, 0)", StringComparison.Ordinal),
                    "Cut(8, 0)".Length),
            }));
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [Test]
    public void PatternRequestDeliversDueActionButOrdinaryRequestHasNoTrace()
    {
        SongDocument song = new();
        ScriptPatternDefinition script = new((ObjectId)5U, "Actions")
        {
            RowCount = 3, ChannelCount = 1,
            Source = "ChannelFilter(0, 0); ChannelFilter(1, 0);",
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        Guid session = Guid.NewGuid();
        PatternPlaybackRequest trace = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = session };

        PlaybackPatternPosition? Run(PatternPlaybackRequest request)
        {
            IAudioOutputSource source = factory.Create(request);
            try
            {
                Assert.That(factory.TryTakePlaybackPositionTimeline(
                    request, out PlaybackPositionTimeline? timeline), Is.True);
                float[] samples = new float[512];
                source.Render(samples.Length, samples);
                return timeline!.GetPositionAt(TimeSpan.Zero);
            }
            finally { (source as IDisposable)?.Dispose(); }
        }

        PlaybackPatternPosition? active = Run(trace);
        Assert.That(active, Is.Not.Null);
        Assert.That(active!.PatternId, Is.EqualTo(script.Id));
        Assert.That(active.EditorCallsiteSession, Is.EqualTo(session));
        Assert.That(active.EditorPatternCallsite,
            Is.EqualTo(new SequenceCallsite(0, "ChannelFilter(0, 0)".Length)));
        Assert.That(active.EditorPlayCallsite, Is.Null);

        PlaybackPatternPosition? ordinary = Run(
            PatternPlaybackRequest.Create(song, script.Id));
        Assert.That(ordinary, Is.Not.Null);
        Assert.That(ordinary!.PatternId, Is.EqualTo(script.Id));
        Assert.That(ordinary.EditorCallsiteSession, Is.Null);
        Assert.That(ordinary.EditorPatternCallsite, Is.Null);
    }
}

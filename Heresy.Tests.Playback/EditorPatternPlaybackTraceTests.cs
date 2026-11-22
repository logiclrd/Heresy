using System;
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

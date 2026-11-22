using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using System.Numerics;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class EditorPatternPlaybackTraceTests
{
    [Test]
    public void F6SustainedOriginsFollowRealVoicesAcrossRowsAndCuts()
    {
        SongDocument song = new();
        const uint sampleId = 20;
        song.Add(SampleDefinition.CreateImported((ObjectId)sampleId,
            "Long sustained", "sustained.wav", LongMonoWave()));
        string noteA = $"Note(0, 0, _O({sampleId}))";
        string noteB = $"Note(0, 1, _O({sampleId}))";
        string source = $"{noteA}; {noteB}; Cut(4, 0); Cut(8, 1);";
        ScriptPatternDefinition script = new((ObjectId)5U, "Two sustained voices")
        {
            RowCount = 16, ChannelCount = 2, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            float[] samples = new float[512];
            double seconds = 0;

            PlaybackPatternPosition? RenderNext()
            {
                audio.Render(samples.Length, samples);
                seconds += samples.Length / 48000.0;
                return timeline!.GetPositionAt(TimeSpan.FromSeconds(seconds));
            }

            PlaybackPatternPosition? SeekRow(int row)
            {
                for (int i = 0; i < 500; i++)
                {
                    PlaybackPatternPosition? position = RenderNext();
                    if (position?.PatternRow >= row)
                        return position;
                }
                Assert.Fail($"Playback did not reach row {row}.");
                return null;
            }

            SequenceCallsite spanA = new(source.IndexOf(noteA,
                StringComparison.Ordinal), noteA.Length);
            SequenceCallsite spanB = new(source.IndexOf(noteB,
                StringComparison.Ordinal), noteB.Length);
            PlaybackPatternPosition? zero = RenderNext();
            Assert.That(zero?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { spanA, spanB }),
                "both actual newly started voices must be highlighted");

            PlaybackPatternPosition? rowTwo = SeekRow(2);
            Assert.That(rowTwo?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { spanA, spanB }),
                "the originating Note calls persist beyond their scheduled row");
            Assert.That(rowTwo?.EditorPatternCallsites, Is.Null,
                "scheduled-action highlights still clear at the next row");

            // Every row-boundary entry must inherit the last known voice
            // snapshot, not momentarily erase it until the next PCM block.
            var stableRowEntries = timeline!.Entries.Where(x =>
                x.Position.PatternRow is >= 1 and <= 3
                && x.Position.EditorPatternCallsites is null).ToArray();
            Assert.That(stableRowEntries, Is.Not.Empty);
            foreach (var entry in stableRowEntries)
                Assert.That(entry.Position.EditorActiveNoteCallsites,
                    Is.EquivalentTo(new[] { spanA, spanB }),
                    $"row {entry.Position.PatternRow} must not flicker");

            PlaybackPatternPosition? rowFive = SeekRow(5);
            Assert.That(rowFive?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { spanB }),
                "Cut removes only its actual voice, not the other source span");
            PlaybackPatternPosition? rowNine = SeekRow(9);
            Assert.That(rowNine?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "both voiced origins must disappear after their Cuts");
            Assert.That(timeline.Entries.Count, Is.LessThan(80),
                "long notes must not append a duplicate position every PCM block");
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [Test]
    public void NaturalSampleEndInsideSinglePcmBufferHasExactBlueLifetimeAndPcmParity()
    {
        SongDocument song = new();
        // Sixteen frames at 8 kHz end after approximately 2 ms at
        // 48 kHz output, entirely inside a single 512-frame buffer.
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Two millisecond Sample", "brief.wav", MonoWave(16)));
        const string note = "Note(0, 0, _O(20))";
        ScriptPatternDefinition script = new((ObjectId)5U, "Natural ending")
        {
            RowCount = 16, ChannelCount = 1, Source = note + ";",
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory tracedFactory = new(config);
        PlaybackRequestAudioSourceFactory ordinaryFactory = new(config);
        PatternPlaybackRequest tracedRequest = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        PatternPlaybackRequest ordinaryRequest = PatternPlaybackRequest.Create(
            song, script.Id);
        IAudioOutputSource traced = tracedFactory.Create(tracedRequest);
        IAudioOutputSource ordinary = ordinaryFactory.Create(ordinaryRequest);
        try
        {
            Assert.That(tracedFactory.TryTakePlaybackPositionTimeline(
                tracedRequest, out PlaybackPositionTimeline? timeline),
                Is.True);
            float[] withTrace = new float[512];
            float[] withoutTrace = new float[512];
            traced.Render(withTrace.Length, withTrace);
            ordinary.Render(withoutTrace.Length, withoutTrace);

            SequenceCallsite sourceSpan = new(0, note.Length);
            Assert.That(timeline!.GetPositionAt(TimeSpan.FromMilliseconds(1))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { sourceSpan }),
                "the note must be blue during its natural sample playback");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(3))
                    ?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "blue clears at the natural end, not buffer end");
            Assert.That(timeline.Entries.Any(e =>
                    e.Offset > TimeSpan.Zero
                    && e.Offset < TimeSpan.FromMilliseconds(3)
                    && e.Position.EditorActiveNoteCallsites is { Count: 0 }),
                Is.True, "a precise in-buffer note-end transition is published");

            Assert.That(withTrace.Any(x => Math.Abs(x) > 0.00001f),
                Is.True, "the test's Sample produces audible PCM");
            for (int i = 0; i < withTrace.Length; i++)
                Assert.That(withTrace[i], Is.EqualTo(withoutTrace[i])
                    .Within(0.000001f),
                    $"F6 instrumentation changed PCM at frame {i}");
        }
        finally
        {
            (traced as IDisposable)?.Dispose();
            (ordinary as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void ConcurrentNaturalEndsWithinOneBufferKeepIndependentSourceSpans()
    {
        SongDocument song = new();
        // Independent note origins finish at about 2 ms and 6 ms,
        // both inside one 512-frame output block at 48 kHz.
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Early ending", "early.wav", MonoWave(16)));
        song.Add(SampleDefinition.CreateImported((ObjectId)21U,
            "Later ending", "later.wav", MonoWave(48)));
        const string early = "Note(0, 0, _O(20))";
        const string late = "Note(0, 1, _O(21))";
        ScriptPatternDefinition script = new((ObjectId)5U, "Overlapping EOF")
        {
            RowCount = 16, ChannelCount = 2,
            Source = early + "; " + late + ";",
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory tracedFactory = new(config);
        PlaybackRequestAudioSourceFactory ordinaryFactory = new(config);
        PatternPlaybackRequest tracedRequest = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        PatternPlaybackRequest ordinaryRequest = PatternPlaybackRequest.Create(
            song, script.Id);
        IAudioOutputSource traced = tracedFactory.Create(tracedRequest);
        IAudioOutputSource ordinary = ordinaryFactory.Create(ordinaryRequest);
        try
        {
            Assert.That(tracedFactory.TryTakePlaybackPositionTimeline(
                tracedRequest, out PlaybackPositionTimeline? timeline),
                Is.True);
            float[] withTrace = new float[512];
            float[] withoutTrace = new float[512];
            traced.Render(withTrace.Length, withTrace);
            ordinary.Render(withoutTrace.Length, withoutTrace);

            SequenceCallsite earlySpan = new(0, early.Length);
            SequenceCallsite lateSpan = new(early.Length + 2, late.Length);
            Assert.That(timeline!.GetPositionAt(TimeSpan.FromMilliseconds(1))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { earlySpan, lateSpan }),
                "both Notes have independent origins while both sound");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(3))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { lateSpan }),
                "the earlier Sample EOF must not clear the other Note");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(7))
                    ?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "the last origin clears at its own natural EOF");

            PlaybackPositionTimelineEntry[] changes = timeline.Entries
                .Where(e => e.Offset > TimeSpan.Zero &&
                    e.Offset < TimeSpan.FromMilliseconds(7) &&
                    e.Position.EditorActiveNoteCallsites is not null)
                .ToArray();
            Assert.That(changes.Any(e =>
                e.Offset >= TimeSpan.FromMilliseconds(1.5) &&
                e.Offset <= TimeSpan.FromMilliseconds(2.5) &&
                e.Position.EditorActiveNoteCallsites!.SequenceEqual(
                    new[] { lateSpan })),
                Is.True, "the first completion has its own in-block boundary");
            Assert.That(changes.Any(e =>
                e.Offset >= TimeSpan.FromMilliseconds(5.5) &&
                e.Offset <= TimeSpan.FromMilliseconds(6.5) &&
                e.Position.EditorActiveNoteCallsites!.Count == 0),
                Is.True, "the second completion has its own in-block boundary");
            Assert.That(timeline.Entries.Count, Is.LessThan(20),
                "ordinary buffer boundaries must not multiply trace entries");

            Assert.That(withTrace.Any(x => Math.Abs(x) > 0.00001f),
                Is.True, "both Samples must actually produce PCM");
            for (int frame = 0; frame < withTrace.Length; frame++)
                Assert.That(withTrace[frame], Is.EqualTo(withoutTrace[frame])
                    .Within(0.000001f),
                    $"F6 tracing changed combined PCM at frame {frame}");
        }
        finally
        {
            (traced as IDisposable)?.Dispose();
            (ordinary as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void FadingDisplacedInstrumentVoiceKeepsOriginUntilExactFadeEnd()
    {
        SongDocument song = new();
        // A long Sample cannot naturally finish during this buffer.
        // The Instrument's four-millisecond NNA fade therefore determines
        // the first authored Note's actual lifetime after displacement.
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Long source", "long.wav", LongMonoWave()));
        InstrumentDefinition instrument = new((ObjectId)21U, "Four-ms NNA fade")
        {
            NnaOverride = InstrumentNnaOverride.Fade,
            NnaFadeDuration = TimeSpan.FromMilliseconds(4),
        };
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = (ObjectId)20U,
        });
        instrument.ToneTable.Add(0);
        song.Add(instrument);

        const string first = "Note(0, 0, _O(21))";
        const string second =
            "Note(0, 0, _O(21), timeOffsetSeconds: 0.002)";
        const string cut = "Cut(0, 0, timeOffsetSeconds: 0.003)";
        string source = first + "; " + second + "; " + cut + ";";
        ScriptPatternDefinition script = new((ObjectId)5U, "Fading NNA")
        {
            RowCount = 16, ChannelCount = 1, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory tracedFactory = new(config);
        PlaybackRequestAudioSourceFactory ordinaryFactory = new(config);
        PatternPlaybackRequest tracedRequest = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        PatternPlaybackRequest ordinaryRequest = PatternPlaybackRequest.Create(
            song, script.Id);
        IAudioOutputSource traced = tracedFactory.Create(tracedRequest);
        IAudioOutputSource ordinary = ordinaryFactory.Create(ordinaryRequest);
        try
        {
            Assert.That(tracedFactory.TryTakePlaybackPositionTimeline(
                tracedRequest, out PlaybackPositionTimeline? timeline), Is.True);
            float[] withTrace = new float[512];
            float[] withoutTrace = new float[512];
            traced.Render(withTrace.Length, withTrace);
            ordinary.Render(withoutTrace.Length, withoutTrace);

            SequenceCallsite firstSpan = new(0, first.Length);
            SequenceCallsite secondSpan = new(first.Length + 2, second.Length);
            Assert.That(timeline!.GetPositionAt(TimeSpan.FromMilliseconds(1))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan }),
                "the first Note owns the current voice before displacement");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(2.5))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan, secondSpan }),
                "the fading NNA tail and replacement have distinct origins");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(4))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan }),
                "Cut at 3 ms removes only the current replacement voice");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(7))
                    ?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "the NNA origin clears after its 6 ms fade end");

            Assert.That(timeline.Entries.Any(e =>
                e.Offset >= TimeSpan.FromMilliseconds(5.5)
                && e.Offset <= TimeSpan.FromMilliseconds(6.5)
                && e.Position.EditorActiveNoteCallsites is { Count: 0 }),
                Is.True,
                "the trace must publish NNA fade completion inside the buffer");
            Assert.That(timeline.Entries.Count, Is.LessThan(25),
                "a finite fade must not create redundant per-buffer entries");
            Assert.That(withTrace.Any(x => Math.Abs(x) > 0.00001f),
                Is.True, "the old and new Notes must actually sound");
            for (int frame = 0; frame < withTrace.Length; frame++)
                Assert.That(withTrace[frame], Is.EqualTo(withoutTrace[frame])
                    .Within(0.000001f),
                    $"F6 tracing changed NNA fade PCM at frame {frame}");
        }
        finally
        {
            (traced as IDisposable)?.Dispose();
            (ordinary as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void FiniteVolumeReleaseKeepsOnlyInstrumentOriginPastOffWithinOneBuffer()
    {
        SongDocument song = new();
        // Both starts have the same long PCM source. At 2 ms, bare
        // Sample Off is definite; the Instrument's sustain/release
        // envelope holds its note until the independent 6 ms endpoint.
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Long source", "long.wav", LongMonoWave()));
        NodeEnvelopeDefinition release = new((ObjectId)22U, "Four-ms release");
        release.ReplaceCurve(
            [
                new EnvelopeNode(TimeSpan.Zero, 1.0),
                new EnvelopeNode(TimeSpan.FromMilliseconds(4), 0.0),
            ],
            0, 0, EnvelopeSustainLoopMode.Forward);
        song.Add(release);
        InstrumentDefinition instrument = new((ObjectId)21U, "Sustaining tone");
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = (ObjectId)20U,
            VolumeEnvelopeId = release.Id,
        });
        instrument.ToneTable.Add(0);
        song.Add(instrument);

        const string sustained = "Note(0, 0, _O(21))";
        const string bare = "Note(0, 1, _O(20))";
        const string releaseInstrument = "Off(0, 0, timeOffsetSeconds: 0.002)";
        const string releaseBare = "Off(0, 1, timeOffsetSeconds: 0.002)";
        string source = sustained + "; " + bare + "; "
            + releaseInstrument + "; " + releaseBare + ";";
        ScriptPatternDefinition script = new((ObjectId)5U, "Off release precision")
        {
            RowCount = 16, ChannelCount = 2, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory tracedFactory = new(config);
        PlaybackRequestAudioSourceFactory ordinaryFactory = new(config);
        PatternPlaybackRequest tracedRequest = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        PatternPlaybackRequest ordinaryRequest = PatternPlaybackRequest.Create(
            song, script.Id);
        IAudioOutputSource traced = tracedFactory.Create(tracedRequest);
        IAudioOutputSource ordinary = ordinaryFactory.Create(ordinaryRequest);
        try
        {
            Assert.That(tracedFactory.TryTakePlaybackPositionTimeline(
                tracedRequest, out PlaybackPositionTimeline? timeline), Is.True);
            float[] withTrace = new float[512];
            float[] withoutTrace = new float[512];
            traced.Render(withTrace.Length, withTrace);
            ordinary.Render(withoutTrace.Length, withoutTrace);

            SequenceCallsite instrumentSpan = new(0, sustained.Length);
            SequenceCallsite bareSpan = new(sustained.Length + 2, bare.Length);
            Assert.That(timeline!.GetPositionAt(TimeSpan.FromMilliseconds(1))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { instrumentSpan, bareSpan }),
                "both authored Notes must be active before their Offs");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(3))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { instrumentSpan }),
                "only the Instrument with an actual sustain release survives Off");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(5))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { instrumentSpan }),
                "the finite release keeps its original callsite blue");
            Assert.That(timeline.GetPositionAt(TimeSpan.FromMilliseconds(7))
                    ?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "blue clears after the Instrument's 6 ms release end");

            Assert.That(timeline.Entries.Any(e =>
                e.Offset >= TimeSpan.FromMilliseconds(1.5)
                && e.Offset <= TimeSpan.FromMilliseconds(2.5)
                && e.Position.EditorActiveNoteCallsites is { Count: 1 }
                && e.Position.EditorActiveNoteCallsites.Contains(instrumentSpan)),
                Is.True, "the bare Sample clears on the exact Off boundary");
            Assert.That(timeline.Entries.Any(e =>
                e.Offset >= TimeSpan.FromMilliseconds(5.5)
                && e.Offset <= TimeSpan.FromMilliseconds(6.5)
                && e.Position.EditorActiveNoteCallsites is { Count: 0 }),
                Is.True, "the finite release clears inside the PCM buffer");
            Assert.That(timeline.Entries.Count, Is.LessThan(30),
                "trace state should not be republished on every segment");
            Assert.That(withTrace.Any(x => Math.Abs(x) > 0.00001f),
                Is.True, "the fixture must produce audible PCM");
            for (int frame = 0; frame < withTrace.Length; frame++)
                Assert.That(withTrace[frame], Is.EqualTo(withoutTrace[frame])
                    .Within(0.000001f),
                    $"F6 tracing changed release PCM at frame {frame}");
        }
        finally
        {
            (traced as IDisposable)?.Dispose();
            (ordinary as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void RepeatedF6PatternWithOffsetCutsNeverPublishesTimelineBackwards()
    {
        SongDocument song = new();
        song.Add(SampleDefinition.CreateImported((ObjectId)2U,
            "Test sample", "test.wav", LongMonoWave()));
        // Actual user script that previously crashed during its second
        // F6 loop: a delayed Cut can overlap cursor lookahead for later
        // rows while shorter Note events still belong to an earlier time.
        const string source = """
            Note(0, 0, _O(2));
            Note(2, 1, _O(2));

            Cut(3, 0, timeOffsetSeconds: 0.06);
            Cut(5, 1, timeOffsetSeconds: 0.06);
            """;
        ScriptPatternDefinition script = new((ObjectId)5U, "Loop repro")
        {
            RowCount = 8, ChannelCount = 2, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, script.Id, repeat: true) with
        {
            EditorCallsiteSession = EditorCallsiteSessionIds.Next(),
        };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            float[] buffer = new float[512];
            int loops = 0;
            int? previousRow = null;
            double seconds = 0;
            for (int i = 0; i < 1500 && loops < 3; i++)
            {
                // Must not throw "Streamed Pattern rows must be
                // chronological" on the second or a later loop.
                audio.Render(buffer.Length, buffer);
                seconds += buffer.Length / 48000.0;
                PlaybackPatternPosition? position = timeline!.GetPositionAt(
                    TimeSpan.FromSeconds(seconds));
                if (position?.PatternRow is int row)
                {
                    if (previousRow is int prev && row < prev)
                        loops++;
                    previousRow = row;
                }
            }
            Assert.That(loops, Is.GreaterThanOrEqualTo(2),
                "exercise multiple full root Pattern F6 repeats");
            Assert.That(timeline!.Entries.Zip(timeline.Entries.Skip(1))
                .All(pair => pair.First.Offset <= pair.Second.Offset),
                Is.True, "row, action, and active-note entries remain chronological");

            // The 0.06-second delayed cuts must remain distinct and in
            // their own musical order, even after several loops.
            SequenceCallsite cut0 = new(source.IndexOf("Cut(3,",
                StringComparison.Ordinal), "Cut(3, 0, timeOffsetSeconds: 0.06)".Length);
            SequenceCallsite cut1 = new(source.IndexOf("Cut(5,",
                StringComparison.Ordinal), "Cut(5, 1, timeOffsetSeconds: 0.06)".Length);
            Assert.That(timeline.Entries.Any(e =>
                e.Position.EditorPatternCallsites?.Contains(cut0) == true),
                Is.True);
            Assert.That(timeline.Entries.Any(e =>
                e.Position.EditorPatternCallsites?.Contains(cut1) == true),
                Is.True);
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [Test]
    public void ShortNoteInsideOnePcmBufferHasDistinctSourceLifetimeBoundaries()
    {
        SongDocument song = new();
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Sustained", "sustained.wav", LongMonoWave()));
        const string note = "Note(0, 0, _O(20))";
        const string cut = "Cut(0, 0, timeOffsetSeconds: 0.003)";
        ScriptPatternDefinition script = new((ObjectId)5U, "Brief F6")
        {
            RowCount = 16, ChannelCount = 1,
            Source = note + "; " + cut + ";",
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            // 512 frames span 10.67 ms. Both events fall inside this one
            // buffer, and a client-block-only snapshot would miss the note.
            float[] buffer = new float[512];
            audio.Render(buffer.Length, buffer);
            SequenceCallsite origin = new(0, note.Length);

            PlaybackPatternPosition? atStart =
                timeline!.GetPositionAt(TimeSpan.FromSeconds(0.001));
            PlaybackPatternPosition? afterCut =
                timeline.GetPositionAt(TimeSpan.FromSeconds(0.004));
            Assert.That(atStart?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { origin }),
                "the origin must be blue between Note and Cut deadlines");
            Assert.That(afterCut?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "the origin must clear at the actual Cut deadline");

            PlaybackPositionTimelineEntry[] noteChanges =
                timeline.Entries.Where(x =>
                    x.Position.EditorActiveNoteCallsites is { Count: > 0 })
                    .ToArray();
            Assert.That(noteChanges, Is.Not.Empty);
            Assert.That(noteChanges[0].Offset,
                Is.LessThan(TimeSpan.FromSeconds(0.003)),
                "the blue source must begin before the Cut within the buffer");
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [Test]
    public void VirtualNnaContinueRetainsOriginalNoteCallsiteUntilPastCut()
    {
        SongDocument song = new();
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Long sustained", "sustained.wav", LongMonoWave()));
        const string first = "NoteVirtual(0, 7U, _O(20))";
        const string replacement = "NoteVirtual(4, 7U, _O(20))";
        string source = first + "; " +
            "DisplacementVirtual(2, 7U, NoteDisplacementAction.Continue); " +
            replacement + "; CutVirtual(8, 7U); " +
            "PastVirtual(12, 7U, TrackerPastNoteAction.Cut);";
        ScriptPatternDefinition script = new((ObjectId)5U, "NNA origin")
        {
            RowCount = 16, ChannelCount = 1, Source = source,
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            SequenceCallsite firstSpan = new(source.IndexOf(first,
                StringComparison.Ordinal), first.Length);
            SequenceCallsite replacementSpan = new(source.IndexOf(replacement,
                StringComparison.Ordinal), replacement.Length);
            float[] samples = new float[512];
            double seconds = 0;
            PlaybackPatternPosition? AtRow(int target)
            {
                for (int i = 0; i < 700; i++)
                {
                    audio.Render(samples.Length, samples);
                    seconds += samples.Length / 48000.0;
                    var position = timeline!.GetPositionAt(TimeSpan.FromSeconds(seconds));
                    if (position?.PatternRow >= target)
                        return position;
                }
                Assert.Fail($"Did not reach row {target}");
                return null;
            }
            Assert.That(AtRow(1)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan }));
            Assert.That(AtRow(5)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan, replacementSpan }),
                "a displaced Continue NNA voice keeps its original source span");
            Assert.That(AtRow(9)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan }),
                "CutVirtual only retires the new current voice");
            Assert.That(AtRow(13)?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "PastVirtual Cut finally retires the displaced NNA");
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [Test]
    public void FlattenedNestedPatternsKeepDistinctNoteOriginsUntilDescendantsEnd()
    {
        SongDocument song = new();
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Long sustained", "sustained.wav", LongMonoWave()));

        // A flattened Script Note is only an instigator, not a renderer voice.
        // Real notes start two levels below it. Reusing the same nested
        // source must not coalesce independent invocation-owned origins.
        DataPatternDefinition leaf = new((ObjectId)2U, "Leaf")
        {
            RowCount = 16, ChannelCount = 1,
        };
        leaf.Grid.GetOrCreateCell(2, 0).Note =
            new StartPatternNote((ObjectId)20U);
        leaf.Grid.GetOrCreateCell(3, 0).Note = new PatternNoteCut();
        // The gap from row 3 through row 6 is silent; the outer
        // flattened invocation is still an active logical source note.
        leaf.Grid.GetOrCreateCell(6, 0).Note =
            new StartPatternNote((ObjectId)20U);
        leaf.Grid.GetOrCreateCell(7, 0).Note = new PatternNoteCut();
        song.Add(leaf);
        DataPatternDefinition middle = new((ObjectId)3U, "Middle")
        {
            RowCount = 16, ChannelCount = 1,
        };
        middle.Grid.GetOrCreateCell(0, 0).Note =
            new StartPatternNote(leaf.Id);
        song.Add(middle);

        const string first = "Note(0, 0, _O(3))";
        const string second = "Note(0, 1, _O(3))";
        string source = first + "; " + second +
            "; Cut(8, 0); Cut(12, 1);";
        ScriptPatternDefinition root = new((ObjectId)5U, "Flattened owners")
        {
            RowCount = 16, ChannelCount = 2, Source = source,
        };
        song.Add(root);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, root.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            SequenceCallsite firstSpan = new(source.IndexOf(first,
                StringComparison.Ordinal), first.Length);
            SequenceCallsite secondSpan = new(source.IndexOf(second,
                StringComparison.Ordinal), second.Length);
            float[] samples = new float[512];
            double seconds = 0;
            PlaybackPatternPosition? AtRow(int target)
            {
                for (int i = 0; i < 700; i++)
                {
                    audio.Render(samples.Length, samples);
                    seconds += samples.Length / 48000.0;
                    var pos = timeline!.GetPositionAt(TimeSpan.FromSeconds(seconds));
                    if (pos?.PatternRow >= target)
                        return pos;
                }
                Assert.Fail($"Did not reach root row {target}");
                return null;
            }

            Assert.That(AtRow(1)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan, secondSpan }),
                "flattened instigators are active before their first child voice");
            Assert.That(AtRow(3)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan, secondSpan }),
                "nested descendant voices carry their distinct top-level origins");

            PlaybackPatternPosition? silentGap = AtRow(5);
            Assert.That(silentGap?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan, secondSpan }),
                "both logical notes stay blue even with no sounding child voices");
            Assert.That(samples.All(value => Math.Abs(value) < 0.00001f),
                Is.True, "the gap is genuinely silent, not a sustained child voice");

            Assert.That(AtRow(7)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { firstSpan, secondSpan }),
                "repeated child notes do not affect the parent lifetime");
            Assert.That(AtRow(9)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { secondSpan }),
                "Cut only retires the first nested source's descendants");
            Assert.That(AtRow(13)?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "both instigators cease when their voices are retired");
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [Test]
    public void SilentFlattenedPatternStaysBlueUntilItsOwnNaturalEnd()
    {
        SongDocument song = new();
        DataPatternDefinition silentChild = new((ObjectId)2U, "Silent child")
        {
            RowCount = 8, ChannelCount = 1,
        };
        song.Add(silentChild);
        const string call = "Note(0, 0, _O(2))";
        ScriptPatternDefinition root = new((ObjectId)5U, "Root")
        {
            RowCount = 16, ChannelCount = 1, Source = call + ";",
        };
        song.Add(root);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(
            song, root.Id) with { EditorCallsiteSession = EditorCallsiteSessionIds.Next() };
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            SequenceCallsite span = new(0, call.Length);
            float[] samples = new float[512];
            double seconds = 0;
            PlaybackPatternPosition? AtRow(int target)
            {
                for (int i = 0; i < 700; i++)
                {
                    audio.Render(samples.Length, samples);
                    seconds += samples.Length / 48000.0;
                    PlaybackPatternPosition? position = timeline!.GetPositionAt(
                        TimeSpan.FromSeconds(seconds));
                    if (position?.PatternRow >= target)
                        return position;
                }
                Assert.Fail($"Did not reach root row {target}");
                return null;
            }

            Assert.That(AtRow(1)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { span }),
                "an empty nested pattern is still a playing logical note");
            Assert.That(AtRow(7)?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[] { span }),
                "last silent row belongs to the still-running pattern");
            Assert.That(AtRow(9)?.EditorActiveNoteCallsites ?? [],
                Is.Empty, "a naturally completed producer with no voices retires");
            Assert.That(samples.All(value => value == 0f), Is.True,
                "this case never creates a sounding renderer voice");
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    // Diagnostic profile only: timing and allocation amounts are not CI
    // correctness budgets. Ordinary transport must not install any F6
    // callsite metadata, while both producer paths render identical PCM.
    [Test]
    public void CompareOrdinaryAndEditorF6PatternProducerCostsWithoutTimingThresholds()
    {
        const int sampleRate = 48000;
        const int blockFrames = 512;
        const int warmupBlocks = 64;
        const int measuredBlocks = 96;
        const int rounds = 7;
        SongDocument song = new();
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Steady profile sample", "steady.wav", LongMonoWave()));
        const string note = "Note(0, 0, _O(20))";
        ScriptPatternDefinition script = new((ObjectId)5U, "F6 profile")
        {
            RowCount = 8, ChannelCount = 1,
            Source = note + "; NoteVirtual(2, 7U, _O(20)); "
                + "Cut(4, 0); CutVirtual(6, 7U);",
        };
        song.Add(script);
        RenderConfiguration config = new(sampleRate, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });

        // Each independent request freezes the same song. A session ID is
        // the entire switch enabling editor-only compiler and PCM callbacks.
        PlaybackRequestAudioSourceFactory ordinaryFactory = new(config);
        PlaybackRequestAudioSourceFactory tracedFactory = new(config);
        PatternPlaybackRequest ordinaryRequest = PatternPlaybackRequest.Create(
            song, script.Id, repeat: true);
        long editorSession = EditorCallsiteSessionIds.Next();
        PatternPlaybackRequest tracedRequest = PatternPlaybackRequest.Create(
            song, script.Id, repeat: true) with
        {
            EditorCallsiteSession = editorSession,
        };
        IAudioOutputSource ordinary = ordinaryFactory.Create(ordinaryRequest);
        IAudioOutputSource traced = tracedFactory.Create(tracedRequest);
        try
        {
            Assert.That(ordinaryFactory.TryTakePlaybackPositionTimeline(
                ordinaryRequest, out PlaybackPositionTimeline? ordinaryTimeline),
                Is.True);
            Assert.That(tracedFactory.TryTakePlaybackPositionTimeline(
                tracedRequest, out PlaybackPositionTimeline? tracedTimeline),
                Is.True);
            Assert.That(ordinaryTimeline, Is.Not.Null);
            Assert.That(tracedTimeline, Is.Not.Null);

            float[] ordinaryPcm = new float[blockFrames];
            float[] tracedPcm = new float[blockFrames];
            bool audible = false;
            for (int block = 0; block < warmupBlocks; block++)
            {
                Array.Clear(ordinaryPcm);
                Array.Clear(tracedPcm);
                ordinary.Render(blockFrames, ordinaryPcm);
                traced.Render(blockFrames, tracedPcm);
                for (int frame = 0; frame < blockFrames; frame++)
                {
                    if (Math.Abs(ordinaryPcm[frame] - tracedPcm[frame]) > 0.000001f)
                        Assert.Fail(
                            $"Editor F6 tracing changed PCM in warmup block {block} frame {frame}.");
                    audible |= Math.Abs(ordinaryPcm[frame]) > 0.00001f;
                }
            }
            Assert.That(audible, Is.True, "profile must exercise audible voices");
            Assert.That(tracedTimeline!.Entries.Any(e =>
                e.Position.EditorCallsiteSession == editorSession
                && e.Position.EditorPatternCallsites is { Count: > 0 }),
                Is.True, "the F6 side must actually publish authored callsite events");

            double[] ordinaryMicros = new double[rounds];
            double[] tracedMicros = new double[rounds];
            long[] ordinaryBytes = new long[rounds];
            long[] tracedBytes = new long[rounds];

            void Measure(IAudioOutputSource output, float[] pcm,
                double[] micros, long[] bytes, int round)
            {
                long beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int block = 0; block < measuredBlocks; block++)
                {
                    Array.Clear(pcm);
                    output.Render(blockFrames, pcm);
                }
                micros[round] = Stopwatch.GetElapsedTime(start).TotalMilliseconds
                    * 1000 / measuredBlocks;
                bytes[round] = GC.GetAllocatedBytesForCurrentThread()
                    - beforeAllocation;
            }

            // Keep both producers on the same absolute playback position
            // for every pair. Alternating the order reduces fixed order bias.
            for (int round = 0; round < rounds; round++)
            {
                if (round % 2 == 0)
                {
                    Measure(ordinary, ordinaryPcm, ordinaryMicros, ordinaryBytes, round);
                    Measure(traced, tracedPcm, tracedMicros, tracedBytes, round);
                }
                else
                {
                    Measure(traced, tracedPcm, tracedMicros, tracedBytes, round);
                    Measure(ordinary, ordinaryPcm, ordinaryMicros, ordinaryBytes, round);
                }
            }

            // No ordinary row (including subsequent pattern repetitions)
            // may contain a traced active voice, action or session identity.
            Assert.That(ordinaryTimeline!.Entries, Is.Not.Empty);
            Assert.That(ordinaryTimeline.Entries.All(e =>
                e.Position.EditorCallsiteSession is null
                && e.Position.EditorPatternCallsite is null
                && e.Position.EditorPatternCallsites is null
                && e.Position.EditorActiveNoteCallsites is null),
                Is.True, "ordinary playback must remain uninstrumented");
            Assert.That(tracedTimeline.Entries.Any(e =>
                e.Position.EditorCallsiteSession == editorSession
                && e.Position.EditorActiveNoteCallsites is { Count: > 0 }),
                Is.True, "the traced path must exercise actual sounding origins");

            static double Median(double[] values)
            {
                double[] ordered = values.OrderBy(value => value).ToArray();
                return ordered[ordered.Length / 2];
            }

            double ordinaryUs = Median(ordinaryMicros);
            double tracedUs = Median(tracedMicros);
            long ordinaryAllocated = ordinaryBytes.Sum();
            long tracedAllocated = tracedBytes.Sum();
            Assert.That(double.IsFinite(ordinaryUs) && double.IsFinite(tracedUs),
                Is.True);
            TestContext.Progress.WriteLine(
                $"Editor F6 producer profile: sample rate={sampleRate}, "
                + $"block frames={blockFrames}, warmup blocks={warmupBlocks}, "
                + $"paired rounds={rounds}, blocks/arm/round={measuredBlocks}, "
                + $"ordinary median us/block={ordinaryUs:F3}, "
                + $"traced median us/block={tracedUs:F3}, "
                + $"traced/ordinary median ratio={tracedUs / ordinaryUs:F3}, "
                + $"ordinary total render-thread allocation bytes={ordinaryAllocated}, "
                + $"traced total render-thread allocation bytes={tracedAllocated}. "
                + "Measurements include producer callback/timeline maintenance "
                + "but exclude compiler preparation, UI painting and device jitter.");
        }
        finally
        {
            (ordinary as IDisposable)?.Dispose();
            (traced as IDisposable)?.Dispose();
        }
    }

    [Test]
    public void OrdinaryPlaybackHasNoSustainedNoteTraceMetadata()
    {
        SongDocument song = new();
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Long sustained", "sustained.wav", LongMonoWave()));
        ScriptPatternDefinition script = new((ObjectId)5U, "Normal")
        {
            RowCount = 16, ChannelCount = 1,
            Source = "Note(0, 0, _O(20));",
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        PatternPlaybackRequest request = PatternPlaybackRequest.Create(song, script.Id);
        IAudioOutputSource audio = factory.Create(request);
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(request,
                out PlaybackPositionTimeline? timeline), Is.True);
            float[] samples = new float[512];
            audio.Render(samples.Length, samples);
            PlaybackPatternPosition? current = timeline!.GetPositionAt(
                TimeSpan.FromSeconds(samples.Length / 48000.0));
            Assert.That(current, Is.Not.Null);
            Assert.That(current!.EditorActiveNoteCallsites, Is.Null);
            Assert.That(current.EditorPatternCallsites, Is.Null);
        }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    private static byte[] LongMonoWave() => MonoWave(8000 * 2);

    private static byte[] MonoWave(int frames)
    {
        const int sampleRate = 8000;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + frames * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(frames * 2);
        for (int i = 0; i < frames; i++)
            writer.Write((short)12000);
        writer.Flush();
        return stream.ToArray();
    }

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
        long session = EditorCallsiteSessionIds.Next();
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
        long session = EditorCallsiteSessionIds.Next();
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
        long session = EditorCallsiteSessionIds.Next();
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
        long session = EditorCallsiteSessionIds.Next();
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
    public void EditedScriptRestartKeepsOldAndNewF6SessionTimelinesIsolated()
    {
        SongDocument song = new();
        song.Add(SampleDefinition.CreateImported((ObjectId)20U,
            "Long sound", "long.wav", LongMonoWave()));
        const string originalNote = "Note(0, 0, _O(20))";
        const string revisedNote = "Note(0, 0, _O(20), volume: 0.5)";
        const string revisedPrefix = "Cut(0, 0); ";
        ScriptPatternDefinition script = new((ObjectId)5U, "Recompiled F6")
        {
            RowCount = 16, ChannelCount = 1,
            Source = originalNote + ";",
        };
        song.Add(script);

        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        long firstSession = EditorCallsiteSessionIds.Next();
        long secondSession = EditorCallsiteSessionIds.Next();
        PatternPlaybackRequest firstRequest = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = firstSession };
        IAudioOutputSource first = factory.Create(firstRequest);
        IAudioOutputSource? second = null;
        try
        {
            Assert.That(factory.TryTakePlaybackPositionTimeline(firstRequest,
                out PlaybackPositionTimeline? firstTimeline), Is.True);
            Assert.That(firstTimeline, Is.Not.Null);
            float[] pcm = new float[512];
            first.Render(pcm.Length, pcm);
            Assert.That(firstTimeline!.GetPositionAt(TimeSpan.FromMilliseconds(1))
                    ?.EditorActiveNoteCallsites,
                Is.EquivalentTo(new[]
                {
                    new SequenceCallsite(0, originalNote.Length),
                }));

            // Change the authored source in the live document. The first
            // request's frozen snapshot must not acquire the new callsite.
            script.Source = revisedPrefix + revisedNote + ";";
            PatternPlaybackRequest secondRequest = PatternPlaybackRequest.Create(
                song, script.Id) with { EditorCallsiteSession = secondSession };
            second = factory.Create(secondRequest);

            // Record equality is insufficient: requests with identical
            // values but different identities may not steal a timeline.
            Assert.That(factory.TryTakePlaybackPositionTimeline(
                secondRequest with { },
                out PlaybackPositionTimeline? clonedTimeline), Is.False);
            Assert.That(clonedTimeline, Is.Null);
            Assert.That(factory.TryTakePlaybackPositionTimeline(
                firstRequest, out PlaybackPositionTimeline? stale), Is.False);
            Assert.That(stale, Is.Null);
            Assert.That(factory.TryTakePlaybackPositionTimeline(
                secondRequest, out PlaybackPositionTimeline? secondTimeline),
                Is.True);
            Assert.That(secondTimeline, Is.Not.Null);
            Assert.That(secondTimeline!.Entries, Is.Empty,
                "no stale old producer snapshot can initialize a new session");

            // Simulate a retired PCM worker delivering another block after
            // the replacement session is installed. It must change only its
            // own timeline, not the new session's row or voice snapshots.
            first.Render(pcm.Length, pcm);
            Assert.That(secondTimeline.Entries, Is.Empty,
                "late old-session callbacks cannot publish to a new timeline");
            second.Render(pcm.Length, pcm);

            PlaybackPatternPosition? oldPosition = firstTimeline.GetPositionAt(
                TimeSpan.FromMilliseconds(5));
            PlaybackPatternPosition? newPosition = secondTimeline.GetPositionAt(
                TimeSpan.FromMilliseconds(1));
            Assert.Multiple(() =>
            {
                Assert.That(oldPosition?.EditorCallsiteSession,
                    Is.EqualTo(firstSession));
                Assert.That(newPosition?.EditorCallsiteSession,
                    Is.EqualTo(secondSession));
                Assert.That(oldPosition?.EditorActiveNoteCallsites,
                    Is.EquivalentTo(new[]
                    {
                        new SequenceCallsite(0, originalNote.Length),
                    }));
                Assert.That(newPosition?.EditorActiveNoteCallsites,
                    Is.EquivalentTo(new[]
                    {
                        new SequenceCallsite(revisedPrefix.Length,
                            revisedNote.Length),
                    }));
                Assert.That(oldPosition?.EditorPatternCallsites,
                    Is.EquivalentTo(new[]
                    {
                        new SequenceCallsite(0, originalNote.Length),
                    }));
                Assert.That(newPosition?.EditorPatternCallsites,
                    Is.EquivalentTo(new[]
                    {
                        new SequenceCallsite(0, "Cut(0, 0)".Length),
                        new SequenceCallsite(revisedPrefix.Length,
                            revisedNote.Length),
                    }));
            });

            int oldEntryCount = firstTimeline.Entries.Count;
            second.Render(pcm.Length, pcm);
            Assert.That(firstTimeline.Entries.Count, Is.EqualTo(oldEntryCount),
                "the new PCM producer cannot append to the old timeline");
        }
        finally
        {
            (first as IDisposable)?.Dispose();
            (second as IDisposable)?.Dispose();
        }
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
        long session = EditorCallsiteSessionIds.Next();
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

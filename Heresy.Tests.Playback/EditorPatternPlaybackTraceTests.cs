using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Numerics;

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
            song, script.Id) with { EditorCallsiteSession = Guid.NewGuid() };
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
            song, script.Id) with { EditorCallsiteSession = Guid.NewGuid() };
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
            song, root.Id) with { EditorCallsiteSession = Guid.NewGuid() };
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
            song, root.Id) with { EditorCallsiteSession = Guid.NewGuid() };
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

    private static byte[] LongMonoWave()
    {
        const int sampleRate = 8000;
        const int frames = sampleRate * 2;
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

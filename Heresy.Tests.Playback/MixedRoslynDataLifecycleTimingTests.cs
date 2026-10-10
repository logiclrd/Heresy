using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// Actual Roslyn coroutine and data-grid patterns compete for one shared
/// physical channel while an independent script changes the tracker clock.
/// Tests use the production IncrementalRecursiveTimeline/PCM source, not an
/// eager song schedule or a synthetic translated-script event list.
/// </summary>
[TestFixture]
public sealed class MixedRoslynDataLifecycleTimingTests
{
    private static readonly ObjectId DataId = (ObjectId)1U;
    private static readonly ObjectId ScriptId = (ObjectId)2U;
    private static readonly ObjectId ClockId = (ObjectId)3U;
    private static readonly ObjectId DataSampleId = (ObjectId)10U;
    private static readonly ObjectId ScriptSampleId = (ObjectId)11U;

    [TestCase(false)]
    [TestCase(true)]
    public void DelayedScriptNoteAndOffInterleaveWithDataSd2S74S70AfterClockChange(
        bool speedInsteadOfTempo)
    {
        using Scenario scenario = Create(speedInsteadOfTempo);
        List<NoteEvent> emitted = [];
        while (scenario.Timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit e)
                emitted.Add(e.Note);

        double dataStart = speedInsteadOfTempo ? 160 : 140;
        double off = speedInsteadOfTempo ? 202 : 166;
        double secondStart = speedInsteadOfTempo ? 210 : 165;
        double end = speedInsteadOfTempo ? 240 : 180;
        double firstStart = 160;

        static double Milliseconds(NoteEvent note)
            => note.Offset.TimeOffset.TotalMilliseconds;
        Assert.Multiple(() =>
        {
            Assert.That(emitted.Where(e => e.Commands
                    .OfType<StartNoteCommand>()
                    .Any(c => c.SourceId == DataSampleId))
                .Select(Milliseconds),
                Is.EqualTo(new[] { dataStart }).Within(1e-6),
                "Data SD2 must resolve against the changed shared clock.");
            Assert.That(emitted.Where(e => e.Commands
                    .OfType<StartNoteCommand>()
                    .Any(c => c.SourceId == ScriptSampleId))
                .Select(Milliseconds),
                Is.EqualTo(new[] { firstStart, secondStart }).Within(1e-6),
                "The scripted fixed-wall Note retains its 160-ms deadline.");
            Assert.That(emitted.Where(e => e.Commands
                    .Any(c => c is NoteOffCommand))
                .Select(Milliseconds),
                Is.EqualTo(new[] { off }).Within(1e-6),
                "The script Off has its own row-relative +10-ms wall delay.");
            Assert.That(emitted.Where(e => e.Commands
                    .Any(c => c is NoteCutCommand))
                .Select(Milliseconds),
                Is.EqualTo(new[] { end }).Within(1e-6));
            Assert.That(emitted.Where(e => e.Commands
                    .Any(c => c is SetCurrentVoiceDisplacementActionCommand))
                .Select(Milliseconds),
                Is.EqualTo(new[] { dataStart }).Within(1e-6),
                "Data S74 must be delivered atomically with SD2's new note.");
            Assert.That(emitted.Where(e => e.Commands
                    .Any(c => c is ApplyPastNoteActionCommand))
                .Select(Milliseconds),
                Is.EqualTo(new[] { end }).Within(1e-6),
                "Data S70 and scripted Cut must share the row-2 boundary.");
            Assert.That(emitted.Where(e => e.Commands
                    .Any(c => speedInsteadOfTempo
                        ? c is SetSpeedCommand
                        : c is SetTempoCommand))
                .Select(Milliseconds),
                Is.EqualTo(new[] { 120.0 }).Within(1e-6));
        });

        // The Speed scenario has a true same-frame collision between
        // the SD2+S74 data Note and the earlier-authored wall-delayed
        // script Note. The Tempo scenario keeps distinct 140/160-ms
        // deadlines; both cases have a row-2 S70 / scripted Cut tie.
        if (speedInsteadOfTempo)
        {
            NoteEvent[] tied = emitted.Where(e =>
                Math.Abs(Milliseconds(e) - 160) < 1e-6
                && e.Commands.Any(c => c is StartNoteCommand)).ToArray();
            Assert.That(tied, Has.Length.EqualTo(2));
            Assert.That(tied[0].Commands.OfType<StartNoteCommand>()
                .Single().SourceId, Is.EqualTo(DataSampleId),
                "The data SD2 tick is delivered before the script wall note.");
            Assert.That(tied[1].Commands.OfType<StartNoteCommand>()
                .Single().SourceId, Is.EqualTo(ScriptSampleId));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ProductionCoroutinePcmIsChunkInvariantAcrossMixedLifecycleDeadlines(
        bool speedInsteadOfTempo)
    {
        float[] entire = Render(speedInsteadOfTempo, [420]);
        float[] chunks = Render(speedInsteadOfTempo,
            [1, 7, 13, 2, 19, 5, 61, 3, 11]);

        Assert.That(chunks, Is.EqualTo(entire).Within(1e-6f),
            "Changing PCM block sizes must not advance a deferred Roslyn " +
            "event across data SDx, S7x, Off or Cut boundaries.");

        Assert.Multiple(() =>
        {
            Assert.That(entire[100], Is.Zero.Within(1e-6f));
            if (speedInsteadOfTempo)
            {
                Assert.That(entire[150], Is.Zero.Within(1e-6f));
                Assert.That(entire[170], Is.EqualTo(2f).Within(1e-6f),
                    "Data and script voices must overlap after the tie; " +
                    "data S74 keeps its displaced note alive.");
                Assert.That(entire[207], Is.EqualTo(1f).Within(1e-6f),
                    "Script Off cuts only the current voice, not the NNA tail.");
                Assert.That(entire[220], Is.EqualTo(2f).Within(1e-5f));
            }
            else
            {
                Assert.That(entire[145], Is.EqualTo(1f).Within(1e-6f));
                Assert.That(entire[159], Is.EqualTo(1f).Within(1e-6f));
                Assert.That(entire[161], Is.EqualTo(2f).Within(1e-6f),
                    "Tempo retimes SD2, but the script note keeps wall time.");
            }
            Assert.That(entire[360], Is.Zero.Within(1e-6f),
                "S70 and scripted Cut terminate both sources and their tails.");
        });
    }

    private static float[] Render(bool speedInsteadOfTempo, int[] chunkSizes)
    {
        using Scenario scenario = Create(speedInsteadOfTempo);
        using PreparedIncrementalAudioSource pcm =
            new(scenario.Timeline, CreateSession());
        float[] result = new float[420];
        int index = 0;
        int chunk = 0;
        while (index < result.Length)
        {
            int count = Math.Min(chunkSizes[chunk++ % chunkSizes.Length],
                result.Length - index);
            pcm.Render(count, result.AsSpan(index, count));
            index += count;
        }
        Assert.That(pcm.NextFrame, Is.EqualTo(result.Length));
        return result;
    }

    private static Scenario Create(bool speedInsteadOfTempo)
    {
        DataPatternDefinition data = new(DataId, "Data note + S7x")
        {
            RowCount = 4,
            ChannelCount = 1,
        };
        PatternCell cell = data.Grid.GetOrCreateCell(1, 0);
        cell.Note = new StartPatternNote(DataSampleId);
        cell.Effects.Add(new TrackerNewNoteActionPatternEffect(
            NoteDisplacementAction.Continue));
        cell.Effects.Add(new TrackerNoteDelayPatternEffect(2));
        data.Grid.GetOrCreateCell(2, 0).Effects.Add(
            new TrackerPastNoteActionPatternEffect(TrackerPastNoteAction.Cut));

        ScriptPatternDefinition script = new(ScriptId, "Actual Roslyn lifecycle")
        {
            RowCount = 4,
            ChannelCount = 1,
            Source = """
                Note(0, 0, _O(11), timeOffsetSeconds: 0.160);
                Off(1.6, 0, 0.010);
                Note(1.75, 0, _O(11));
                Cut(2, 0);
                """,
        };
        ScriptPatternDefinition clock = new(ClockId, "Independent clock")
        {
            RowCount = 4,
            ChannelCount = 1,
            Source = speedInsteadOfTempo
                ? "Speed(1, 3);"
                : "Tempo(1, 250);",
        };
        IncrementalRecursiveTimeline timeline = new(
            new SequencingContext(),
            new Resolver(data, script, clock),
            new RoslynIncrementalScriptSourceCompiler());
        // Registration order is intentional: at a simultaneous SD2
        // tracker tick and fixed-wall script deadline, both root cursors
        // have the same physical host; the due data tick wins the tie.
        timeline.AddRoot(DataId);
        timeline.AddRoot(ScriptId);
        timeline.AddRoot(ClockId);
        return new Scenario(timeline);
    }

    private static PlaybackSession CreateSession()
    {
        ISound data = CreateSample(DataSampleId);
        ISound script = CreateSample(ScriptSampleId);
        return new PlaybackSession(
            new RenderContext(new RenderConfiguration(1000,
                [new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)])),
            new NoteScheduleBuilder().Freeze(),
            new SampleResolver(DataSampleId, data, ScriptSampleId, script));
    }

    private static ISound CreateSample(ObjectId id)
    {
        SampleDefinition definition = new(
            id, "Loop", new ExternalAssetReference("loop.raw"))
        {
            Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4),
        };
        return new SampleSound(
            definition, new MemorySampleData(1000, 1, [1f, 1f, 1f, 1f]));
    }

    private sealed class Scenario(IncrementalRecursiveTimeline timeline)
        : IDisposable
    {
        public IncrementalRecursiveTimeline Timeline { get; } = timeline;
        public void Dispose() => Timeline.Dispose();
    }

    private sealed class Resolver(params SongObject[] objects)
        : IIncrementalInvocationResolver
    {
        private readonly Dictionary<ObjectId, SongObject> _objects =
            objects.ToDictionary(o => o.Id);

        public bool TryResolve(ObjectId id, out SongObject? source)
            => _objects.TryGetValue(id, out source);
    }

    private sealed class SampleResolver(
        ObjectId dataId, ISound data,
        ObjectId scriptId, ISound script) : ISoundResolver
    {
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            sound = mixdown ? null
                : id == dataId ? data
                : id == scriptId ? script : null;
            return sound is not null;
        }
    }
}

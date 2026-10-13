using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// End-to-end reproducer for a 20-sample tick on every Pattern row,
/// persistent channel FIR selection at row 16, and phase text assignments
/// on rows 32/48. Note and channel assignments address different layers.
/// </summary>
[TestFixture]
public sealed class FirPhaseAssignmentScopeTests
{
    private const int SampleRate = 8000;

    [Test]
    public void NoteAssignmentsDoNotModifyPersistentChannelFir()
    {
        Trace trace = RenderScenario(useChannelAssignments: false);
        float[] linear = Tick(trace, 16);
        Assert.That(Tick(trace, 32), Is.EqualTo(linear).Within(1e-6f),
            "A Note Filter assignment must not modify a Channel Filter.");
        Assert.That(Tick(trace, 48), Is.EqualTo(linear).Within(1e-6f),
            "Later notes without an event Filter must not reconfigure the host.");
    }

    [Test]
    public void ChannelAssignmentsChangeFirPhaseAtRows32And48()
    {
        Trace trace = RenderScenario(useChannelAssignments: true);
        float[] dry = Tick(trace, 0);
        float[] linear = Tick(trace, 16);
        float[] minimum = Tick(trace, 32);
        float[] maximum = Tick(trace, 48);

        Assert.That(linear, Is.Not.EqualTo(dry),
            "Channel FIR selection must change the tick waveform at row 16.");
        Assert.That(Tick(trace, 31), Is.EqualTo(linear).Within(1e-6f));
        Assert.That(Tick(trace, 47), Is.EqualTo(minimum).Within(1e-6f));
        Assert.That(Tick(trace, 63), Is.EqualTo(maximum).Within(1e-6f));
        Assert.That(Difference(linear, minimum), Is.GreaterThan(0.02),
            "Minimum phase must not retain the original Linear FIR kernel.");
        Assert.That(Difference(minimum, maximum), Is.GreaterThan(0.02),
            "Maximum phase must not retain the Minimum FIR kernel.");
    }

    [Test]
    public void ChannelPhaseAssignmentsProduceStableDistinctTickIslandsAndOrderedEnergyCentroids()
    {
        // Render the actual 64-row Pattern through the frozen playback
        // factory. Find islands from the PCM itself: don't infer note
        // boundaries from tracker rows, frame counts, or MIDI timing.
        Trace trace = RenderScenario(useChannelAssignments: true);
        IReadOnlyList<float[]> islands = IsolateNonzeroIslands(trace.Pcm);

        Assert.That(islands, Has.Count.EqualTo(64),
            "Every short tick should form one island separated by real silence.");

        // Four groups: dry (rows 0-15), Linear (16-31),
        // Minimum (32-47), Maximum (48-63).
        // Test *all* 16 notes in each group, not just the first/last.
        for (int group = 0; group < 4; group++)
        {
            float[] first = islands[group * 16];
            Assert.That(first.Length, Is.GreaterThanOrEqualTo(20));
            Assert.That(first.Length, Is.LessThan(256),
                "One tick plus a 127-tap finite FIR cannot fill the row gap.");

            for (int offset = 1; offset < 16; offset++)
                Assert.That(islands[group * 16 + offset],
                    Is.EqualTo(first),
                    $"Islands in phase group {group} differ at offset {offset}.");
        }

        float[][] templates = [islands[0], islands[16], islands[32], islands[48]];
        for (int earlier = 0; earlier < templates.Length; earlier++)
        {
            for (int later = earlier + 1; later < templates.Length; later++)
                Assert.That(templates[earlier].SequenceEqual(templates[later]),
                    Is.False,
                    $"Phase/treatment groups {earlier} and {later} must be distinct.");
        }

        double dry = EnergyCentroid(templates[0]);
        double linear = EnergyCentroid(templates[1]);
        double minimum = EnergyCentroid(templates[2]);
        double maximum = EnergyCentroid(templates[3]);

        Assert.Multiple(() =>
        {
            Assert.That(dry, Is.LessThan(linear),
                "The dry short tick should be earlier than Linear FIR.");
            Assert.That(minimum, Is.LessThan(linear - 10),
                "Minimum phase must concentrate energy earlier than Linear.");
            Assert.That(maximum, Is.GreaterThan(linear + 10),
                "Maximum phase must concentrate energy later than Linear.");
        });

        TestContext.WriteLine(
            $"64 tick-islands: dry={dry:F2}, Linear={linear:F2}, " +
            $"Minimum={minimum:F2}, Maximum={maximum:F2} samples " +
            "(relative to each island's first non-silent frame).");
    }

    // We discover island limits from rendered audio, not from the Pattern
    // schedule. For FIR ringing there may be interior zero crossings and
    // near-zero taps; only a *long* run of silence divides two islands.
    // The normal row separation is much larger than 256 PCM frames.
    private static IReadOnlyList<float[]> IsolateNonzeroIslands(float[] pcm)
    {
        const float nonSilenceThreshold = 1e-8f;
        const int separationFrames = 256;
        List<float[]> islands = [];
        int start = -1, lastAudible = -1;

        for (int frame = 0; frame < pcm.Length; frame++)
        {
            if (MathF.Abs(pcm[frame]) <= nonSilenceThreshold)
                continue;

            if (start >= 0 && frame - lastAudible > separationFrames)
            {
                islands.Add(pcm[start..(lastAudible + 1)]);
                start = -1;
            }

            if (start < 0)
                start = frame;
            lastAudible = frame;
        }

        if (start >= 0)
            islands.Add(pcm[start..(lastAudible + 1)]);

        return islands;
    }

    // The energy-weighted sample-position average, measured *relative*
    // to the first significant output sample in the isolated island.
    private static double EnergyCentroid(float[] island)
    {
        double energySum = 0, weightedIndexSum = 0;
        for (int i = 0; i < island.Length; i++)
        {
            double energy = (double)island[i] * island[i];
            energySum += energy;
            weightedIndexSum += i * energy;
        }
        Assert.That(energySum, Is.GreaterThan(1e-10),
            "The island cannot be silent when computing its centroid.");
        return weightedIndexSum / energySum;
    }

    private static Trace RenderScenario(bool useChannelAssignments)
    {
        SongDocument song = new();
        ObjectId sampleId = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(sampleId,
            "Twenty-frame click", "click.wav", Wave()));

        ObjectId filterId = song.AllocateObjectId();
        FilterDefinition filter = new(filterId, "Low-pass FIR", [
            new FilterStageDefinition(1, "low", FilterKind.FirLowPass),
        ]);
        song.Add(filter);

        ObjectId patternId = song.AllocateObjectId();
        DataPatternDefinition pattern = new(patternId, "64 ticks")
        {
            RowCount = 64,
            ChannelCount = 1,
        };
        for (int row = 0; row < 64; row++)
            pattern.Grid.GetOrCreateCell(row, 0).Note =
                new StartPatternNote(sampleId);
        pattern.Grid.GetOrCreateCell(16, 0).Effects.Add(
            new SetChannelFilterPatternEffect(filterId));
        pattern.Grid.GetOrCreateCell(32, 0).Effects.Add(
            useChannelAssignments
                ? new SetChannelFilterAssignmentsPatternEffect(
                    "low.PhaseResponse = Minimum")
                : new SetNoteFilterAssignmentsPatternEffect(
                    "low.PhaseResponse = Minimum"));
        pattern.Grid.GetOrCreateCell(48, 0).Effects.Add(
            useChannelAssignments
                ? new SetChannelFilterAssignmentsPatternEffect(
                    "low.PhaseResponse = Maximum")
                : new SetNoteFilterAssignmentsPatternEffect(
                    "low.PhaseResponse = Maximum"));
        song.Add(pattern);

        RenderConfiguration mono = new(
            SampleRate,
            [new OutputChannelConfiguration(Vector3.Zero,
                positionalImportance: 0)]);
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(mono).Create(song, patternId);
        float[] result = new float[SampleRate * 12];
        plan.Source.Render(result.Length, result);
        Assert.That(result.All(float.IsFinite), Is.True);
        List<int> ticks = FindTicks(result);
        Assert.That(ticks, Has.Count.EqualTo(64),
            "One isolated click should sound on each of the 64 rows.");
        return new Trace(result, ticks.ToArray());
    }

    // A 20-sample pulse exceeds 0.2 per tick, while the FIR impulse tail
    // never spans the >=400-frame inter-tick separation.
    private static List<int> FindTicks(float[] pcm)
    {
        List<int> result = [];
        int last = -400;
        for (int frame = 0; frame < pcm.Length; frame++)
        {
            if (Math.Abs(pcm[frame]) > 0.2f && frame - last >= 400)
            {
                result.Add(frame);
                last = frame;
            }
        }
        return result;
    }

    private sealed record Trace(float[] Pcm, int[] Onsets);

    private static float[] Tick(Trace trace, int row)
    {
        int start = Math.Max(0, trace.Onsets[row] - 16);
        return trace.Pcm.Skip(start).Take(180).ToArray();
    }

    private static double Difference(float[] a, float[] b)
        => a.Zip(b, (x, y) => Math.Abs(x - y)).Sum();

    private static byte[] Wave()
    {
        const int frames = 20;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        for (int n = 0; n < frames; n++)
            writer.Write((short)24000);
        return stream.ToArray();
    }
}

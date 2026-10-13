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
        float[] pcm = RenderScenario(useChannelAssignments: false);
        float[] linear = Tick(pcm, 16);
        Assert.That(Tick(pcm, 32), Is.EqualTo(linear).Within(1e-6f),
            "A Note Filter assignment must not modify a Channel Filter.");
        Assert.That(Tick(pcm, 48), Is.EqualTo(linear).Within(1e-6f),
            "Later notes without an event Filter must not reconfigure the host.");
    }

    [Test]
    public void ChannelAssignmentsChangeFirPhaseAtRows32And48()
    {
        float[] pcm = RenderScenario(useChannelAssignments: true);
        float[] dry = Tick(pcm, 0);
        float[] linear = Tick(pcm, 16);
        float[] minimum = Tick(pcm, 32);
        float[] maximum = Tick(pcm, 48);

        Assert.That(linear, Is.Not.EqualTo(dry),
            "Channel FIR selection must change the tick waveform at row 16.");
        Assert.That(Tick(pcm, 31), Is.EqualTo(linear).Within(1e-6f));
        Assert.That(Tick(pcm, 47), Is.EqualTo(minimum).Within(1e-6f));
        Assert.That(Tick(pcm, 63), Is.EqualTo(maximum).Within(1e-6f));
        Assert.That(Difference(linear, minimum), Is.GreaterThan(0.02),
            "Minimum phase must not retain the original Linear FIR kernel.");
        Assert.That(Difference(minimum, maximum), Is.GreaterThan(0.02),
            "Maximum phase must not retain the Minimum FIR kernel.");
    }

    private static float[] RenderScenario(bool useChannelAssignments)
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
        _onsets = ticks.ToArray();
        return result;
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

    private static int[] _onsets = [];

    private static float[] Tick(float[] pcm, int row)
    {
        int start = Math.Max(0, _onsets[row] - 16);
        return pcm.Skip(start).Take(180).ToArray();
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

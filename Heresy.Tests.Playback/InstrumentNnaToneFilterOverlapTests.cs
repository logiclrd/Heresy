using System;
using System.IO;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// Two actual data-Instrument tones, with different note-local Filters, on
/// successive notes of one ordinary Pattern channel. Checks the prepared
/// snapshot plus incremental pattern producer, not just direct ISound binding.
/// </summary>
[TestFixture]
public sealed class InstrumentNnaToneFilterOverlapTests
{
    private const int Rate = 8000;

    [TestCase(InstrumentNnaOverride.Continue, true)]
    [TestCase(InstrumentNnaOverride.Cut, false)]
    public void DistinctFilteredTonesRetainOldVoiceOnlyForContinue(
        InstrumentNnaOverride policy, bool expectOverlap)
    {
        SongDocument song = CreateSong(policy, out ObjectId patternId, out _);
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(new RenderConfiguration(
                Rate, [new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)])).Create(song, patternId);

        // Eight tracker rows separate the C-5 and G-5. Use a long
        // source so this test cannot confuse natural sample exhaustion
        // with new-note displacement.
        float[] intro = new float[Rate];
        plan.Source.Render(intro.Length, intro);
        PlaybackVoice? second = plan.Session.GetChannelState(0).CurrentVoice;
        Assert.That(second, Is.Not.Null);
        Assert.That(second!.Configuration.NewNotePolicy.Action,
            Is.EqualTo(policy == InstrumentNnaOverride.Continue
                ? NewNoteAction.Continue : NewNoteAction.Cut));
        Assert.That(second.ToneFilterState, Is.Not.Null);

        if (expectOverlap)
        {
            Assert.That(plan.Session.VirtualVoices.Count, Is.EqualTo(1),
                "C-5 should migrate to a dynamic voice, not abruptly cut.");
            PlaybackVoice prior = plan.Session.VirtualVoices.Single();
            Assert.That(prior.StartFrame, Is.Zero);
            Assert.That(prior.Configuration.NewNotePolicy.Action,
                Is.EqualTo(NewNoteAction.Continue));
            Assert.That(prior.ToneFilterState, Is.Not.Null);
            Assert.That(prior.ToneFilterState,
                Is.Not.SameAs(second.ToneFilterState),
                "the low-pass and high-pass histories must remain note-local");
        }
        else
            Assert.That(plan.Session.VirtualVoices, Is.Empty);

        float[] after = new float[Rate / 8];
        plan.Source.Render(after.Length, after);
        if (expectOverlap)
        {
            Assert.That(after.TakeLast(100).Average(x => x),
                Is.GreaterThan(0.15),
                "the prior steady-state low-pass should still be audible " +
                "after the new high-pass transient dies away");
            Assert.That(plan.Session.VirtualVoices, Has.Count.EqualTo(1));
        }
        else
            Assert.That(Math.Abs(after.TakeLast(100).Average(x => x)),
                Is.LessThan(0.05));
    }

    private static SongDocument CreateSong(
        InstrumentNnaOverride policy,
        out ObjectId patternId, out ObjectId instrumentId)
    {
        SongDocument song = new();
        ObjectId sampleId = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(sampleId,
            "Steady test signal", "steady.wav", Wave()));
        ObjectId lowId = song.AllocateObjectId();
        FilterDefinition lowDef = new(lowId, "Lowpass");
        song.Add(lowDef);
        FilterInstance low = new(lowDef);
        low.SetStageParameter(1, "Mode",
            FilterParameterValue.FromChoice("LowPass"));
        low.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(240));

        ObjectId highId = song.AllocateObjectId();
        FilterDefinition highDef = new(highId, "Highpass");
        song.Add(highDef);
        FilterInstance high = new(highDef);
        high.SetStageParameter(1, "Mode",
            FilterParameterValue.FromChoice("HighPass"));
        high.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(240));

        instrumentId = song.AllocateObjectId();
        InstrumentDefinition instrument = new(instrumentId, "C-5 / G-5")
        {
            NnaOverride = policy,
        };
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sampleId,
            FilterConfiguration = low,
        });
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sampleId,
            FilterConfiguration = high,
        });
        for (int i = 0; i <= 19; i++)
            instrument.ToneTable.Add(i == 12 ? 0 : i == 19 ? 1 : -1);
        song.Add(instrument);

        patternId = song.AllocateObjectId();
        DataPatternDefinition pattern = new(patternId, "NNA filtered tones")
        {
            RowCount = 24,
            ChannelCount = 1,
        };
        pattern.Grid.GetOrCreateCell(0, 0).Note =
            new StartPatternNote(instrumentId, pitchMultiplier: 2);
        pattern.Grid.GetOrCreateCell(8, 0).Note =
            new StartPatternNote(instrumentId,
                pitchMultiplier: 2 * Math.Pow(2, 7.0 / 12));
        song.Add(pattern);
        return song;
    }

    private static byte[] Wave()
    {
        const int frames = Rate * 16;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 2);
        for (int i = 0; i < frames; i++)
            writer.Write((short)16384);
        return stream.ToArray();
    }
}

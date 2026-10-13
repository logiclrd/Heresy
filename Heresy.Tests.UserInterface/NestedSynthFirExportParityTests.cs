using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.File;
using Heresy.Render.Realtime;
using Heresy.UserInterface.Exporting;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// Acceptance across the frozen Pattern producer, actual FLAC encoder/decoder
/// and the bounded realtime PCM ring. The same sample passes through an inner
/// and an outer Synth's separately authored FIR phase/tail boundaries.
/// </summary>
[TestFixture]
public sealed class NestedSynthFirExportParityTests
{
    private const int Rate = 8000;
    private const int InnerTailFrames = 64;
    private const int OuterTailFrames = 56;
    private const int ExpectedEndExclusive = 1 + InnerTailFrames + OuterTailFrames;
    private const int LogicalFrames = 960; // one 125 BPM / speed 6 row

    [TestCase("Minimum", 1)]
    [TestCase("Maximum", 1)]
    [TestCase("Minimum", 2)]
    [TestCase("Maximum", 2)]
    public async Task NestedGraphFirTailMatchesDirectChunksBufferedProducerAndRealFlac(
        string phase, int channels)
    {
        (SongDocument song, ObjectId patternId) = CreateSong(phase);
        RenderConfiguration configuration = Configuration(channels);
        PreparedIncrementalPlaybackFactory producer =
            new(configuration);
        using PreparedIncrementalPlaybackPlan whole =
            producer.Create(song, patternId);
        using PreparedIncrementalPlaybackPlan split =
            producer.Create(song, patternId);
        using PreparedIncrementalPlaybackPlan lifecycle =
            producer.Create(song, patternId);

        float[] body = new float[LogicalFrames * channels];
        float[] chunks = new float[body.Length];
        whole.Source.Render(LogicalFrames, body);
        int[] sizes = [1, 3, 17, 2, 29, 71, 5, 11, 43];
        for (int frame = 0, block = 0; frame < LogicalFrames; block++)
        {
            int count = Math.Min(sizes[block % sizes.Length], LogicalFrames - frame);
            split.Source.Render(count, chunks.AsSpan(frame * channels,
                count * channels));
            frame += count;
        }
        Assert.That(chunks, Is.EqualTo(body).Within(1e-6f),
            "phase-specific FIR histories must be identical across block boundaries");

        float[] beforeEnd = new float[(ExpectedEndExclusive - 1) * channels];
        lifecycle.Source.Render(ExpectedEndExclusive - 1, beforeEnd);
        Assert.That(lifecycle.Session.GetChannelState(0).CurrentVoice, Is.Not.Null,
            "both nested authored graph FIR tail allowances are still active");
        lifecycle.Source.Render(1, new float[channels]);
        Assert.That(lifecycle.Session.GetChannelState(0).CurrentVoice, Is.Null,
            "nested finite source must retire at the exact exclusive end");
        Assert.That(body.Take(ExpectedEndExclusive * channels)
            .Any(x => Math.Abs(x) > 1e-5f), Is.True);
        Assert.That(body.Skip(ExpectedEndExclusive * channels),
            Is.All.Zero, "no FIR residue may escape the final authored end");

        PlaybackRequestAudioSourceFactory liveFactory = new(configuration);
        IAudioOutputSource raw = liveFactory.Create(
            PatternPlaybackRequest.Create(song, patternId, repeat: false));
        using IDisposable liveOwner = (IDisposable)raw;
        using BufferedAudioOutputSource buffered =
            new(raw, capacityFrames: 128, blockFrames: 17);
        float[] live = new float[body.Length];
        int[] callbackSizes = [1, 31, 5, 17, 2, 43, 7, 11];
        for (int frame = 0, block = 0; frame < LogicalFrames; block++)
        {
            int count = Math.Min(callbackSizes[block % callbackSizes.Length],
                LogicalFrames - frame);
            Assert.That(SpinWait.SpinUntil(
                () => buffered.BufferedFrames >= count
                    || buffered.RenderingFault is not null,
                TimeSpan.FromSeconds(10)), Is.True,
                "dedicated PCM producer must supply frames without an underrun");
            Assert.That(buffered.RenderingFault, Is.Null);
            buffered.Render(count, live.AsSpan(frame * channels,
                count * channels));
            frame += count;
        }
        Assert.That(buffered.UnderrunCount, Is.Zero);
        Assert.That(live, Is.EqualTo(body).Within(1e-6f),
            "buffered playback and unbuffered Pattern PCM must agree");

        string path = Path.Combine(Path.GetTempPath(),
            $"heresy-nested-fir-{Guid.NewGuid():N}.flac");
        try
        {
            SongExportService service = new(
                new OfflineSongRenderPlanFactory(configuration));
            OfflineRenderResult exported = await service.ExportAsync(
                song, path, OfflineAudioFileFormat.Flac);
            Assert.That(exported.LogicalFrameCount, Is.EqualTo(LogicalFrames));
            Assert.That(exported.TailFrameCount, Is.Zero,
                "both nested FIR releases conclude within the logical row");
            SamplePcmData decoded = SampleAudioCodec.Decode(
                await File.ReadAllBytesAsync(path), path);
            Assert.Multiple(() =>
            {
                Assert.That(decoded.SampleRate, Is.EqualTo(Rate));
                Assert.That(decoded.ChannelCount, Is.EqualTo(channels));
                Assert.That(decoded.FrameCount, Is.EqualTo(LogicalFrames));
            });
            for (int frame = 0; frame < LogicalFrames; frame++)
                for (int channel = 0; channel < channels; channel++)
                    Assert.That(decoded.GetSample(frame, channel),
                        Is.EqualTo(body[frame * channels + channel])
                            .Within(2.0f / 32768),
                        $"decoded FLAC frame {frame}, channel {channel}");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void MinimumAndMaximumPhaseAreAudiblyDistinctWithSameTailBudget()
    {
        float[] minimum = RenderPhase("Minimum");
        float[] maximum = RenderPhase("Maximum");
        static double Centroid(float[] pcm)
        {
            double energy = 0, moment = 0;
            for (int frame = 0; frame < pcm.Length; frame++)
            {
                double power = pcm[frame] * (double)pcm[frame];
                energy += power;
                moment += frame * power;
            }
            return moment / energy;
        }
        double early = Centroid(minimum);
        double late = Centroid(maximum);
        Assert.Multiple(() =>
        {
            Assert.That(double.IsFinite(early), Is.True);
            Assert.That(double.IsFinite(late), Is.True);
            Assert.That(early, Is.LessThan(late),
                "minimum-phase energy must precede maximum-phase energy");
            Assert.That(late - early, Is.GreaterThan(5),
                "the test must actually exercise distinct phase designs");
        });
    }

    private static float[] RenderPhase(string phase)
    {
        (SongDocument song, ObjectId patternId) = CreateSong(phase);
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Configuration(1))
                .Create(song, patternId);
        float[] pcm = new float[ExpectedEndExclusive];
        plan.Source.Render(pcm.Length, pcm);
        return pcm;
    }

    private static (SongDocument Song, ObjectId PatternId) CreateSong(string phase)
    {
        SongDocument song = new();
        ObjectId sampleId = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(sampleId,
            "One frame impulse", "impulse.wav", OneFrameImpulse()));
        ObjectId filterId = song.AllocateObjectId();
        FilterDefinition filterDefinition = new(filterId, "FIR", [
            new FilterStageDefinition(1, "low", FilterKind.FirLowPass),
        ]);
        song.Add(filterDefinition);
        FilterInstance fir = new(filterDefinition);
        fir.SetStageParameter(1, "TapCount",
            FilterParameterValue.FromNumber(31));
        fir.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1200));
        fir.SetStageParameter(1, "PhaseResponse",
            FilterParameterValue.FromChoice(phase));

        ObjectId innerId = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(innerId, "Inner " + phase,
            new FmSynthGraph([
                new FmSoundSourceNode(0, sampleId, C4()),
                new FmFilterNode(1, 0, fir,
                    tailSeconds: InnerTailFrames / (double)Rate),
            ], 1)));
        ObjectId outerId = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(outerId, "Outer " + phase,
            new FmSynthGraph([
                new FmSoundSourceNode(0, innerId, C4()),
                new FmFilterNode(1, 0, fir,
                    tailSeconds: OuterTailFrames / (double)Rate),
            ], 1)));
        ObjectId patternId = song.AllocateObjectId();
        DataPatternDefinition pattern = new(patternId, "One row")
        {
            RowCount = 1, ChannelCount = 1,
        };
        PatternCell start = pattern.Grid.GetOrCreateCell(0, 0);
        start.SourceId = outerId;
        start.Note = new StartPatternNote();
        song.Add(pattern);
        ObjectId rootId = song.AllocateObjectId();
        DataSequenceDefinition sequence = new(rootId, "Root");
        sequence.Entries.Add(new SequenceEntry(patternId));
        song.Add(sequence);
        song.RootSequenceId = rootId;
        return (song, patternId);
    }

    private static RenderConfiguration Configuration(int channels)
        => channels == 1
            ? new RenderConfiguration(Rate, [
                new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0),
            ])
            : RenderConfiguration.Stereo(Rate);

    private static double C4() => 440.0 * Math.Pow(2, -9.0 / 12.0);

    private static byte[] OneFrameImpulse()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(38); // 36 + 2 data bytes
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(Rate); writer.Write(Rate * 2);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(2); writer.Write((short)16384);
        writer.Flush();
        return stream.ToArray();
    }
}

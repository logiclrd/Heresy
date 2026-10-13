using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.File;
using Heresy.Render.Realtime;
using Heresy.Render.Samples;
using Heresy.UserInterface.Exporting;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

/// <summary>
/// Acceptance of editor-authored ISound source nodes as frozen production
/// Pattern voices: live transport, the bounded audio worker and actual FLAC
/// export must agree across all four supported directly invocable source types.
/// </summary>
[TestFixture]
public sealed class FmSynthSourcePlaybackExportAcceptanceTests
{
    private const int Rate = 8000;
    private const int LogicalFrames = 1920;
    private static readonly double C4 = 440.0 * Math.Pow(2.0, -9.0 / 12.0);

    [TestCase("Sample", 1)]
    [TestCase("Sample", 2)]
    [TestCase("DataInstrument", 1)]
    [TestCase("DataInstrument", 2)]
    [TestCase("ScriptInstrument", 1)]
    [TestCase("ScriptInstrument", 2)]
    [TestCase("RecursiveSynth", 1)]
    [TestCase("RecursiveSynth", 2)]
    public async Task FrozenSourceMatchesSplitBufferedAndActualFlac(
        string kind, int channels)
    {
        (SongDocument song, ObjectId patternId, ObjectId outerId,
            SampleDefinition pcm) = BuildSong(kind);
        RenderConfiguration configuration = Configuration(channels);
        SongDocument exportSnapshot = SongDocumentSnapshot.Create(song).Document;
        GuardSamples guard = new();
        PreparedIncrementalPlaybackFactory factory = new(configuration, guard);
        using PreparedIncrementalPlaybackPlan whole =
            factory.Create(song, patternId);
        using PreparedIncrementalPlaybackPlan split =
            factory.Create(song, patternId);
        IAudioOutputSource raw = new PlaybackRequestAudioSourceFactory(
            configuration, guard).Create(
                PatternPlaybackRequest.Create(song, patternId, repeat: false));
        using IDisposable liveOwner = (IDisposable)raw;
        using BufferedAudioOutputSource buffered =
            new(raw, capacityFrames: 128, blockFrames: 17);

        // Source binding and decoding must already be complete. Replacing the
        // mutable original graph and PCM after snapshot capture cannot affect
        // any of these three running voices or resolve a fresh source in PCM.
        Assert.That(guard.Calls, Is.GreaterThan(0));
        guard.ForbidResolution = true;
        FmSynthDefinition original =
            (FmSynthDefinition)song.Objects[outerId];
        original.Graph = new FmSynthGraph([new FmConstantNode(0, 0)], 0);
        pcm.ReplaceImportedEncoding("silence.wav", FourFrameWave(0));
        song.MarkChanged(affectsAudio: true);

        float[] expected = new float[LogicalFrames * channels];
        whole.Source.Render(LogicalFrames, expected);
        float[] pieces = new float[expected.Length];
        int[] blockSizes = [1, 17, 3, 51, 2, 11, 23, 5];
        for (int frame = 0, block = 0; frame < LogicalFrames; block++)
        {
            int count = Math.Min(blockSizes[block % blockSizes.Length],
                LogicalFrames - frame);
            split.Source.Render(count, pieces.AsSpan(
                frame * channels, count * channels));
            frame += count;
        }
        Assert.That(pieces, Is.EqualTo(expected).Within(1e-6f),
            "source graph, loop and inherited release must be block invariant");

        float[] live = new float[expected.Length];
        int[] callbacks = [1, 31, 4, 17, 9, 43, 7, 13];
        for (int frame = 0, block = 0; frame < LogicalFrames; block++)
        {
            int count = Math.Min(callbacks[block % callbacks.Length],
                LogicalFrames - frame);
            Assert.That(SpinWait.SpinUntil(
                () => buffered.BufferedFrames >= count
                    || buffered.RenderingFault is not null,
                TimeSpan.FromSeconds(10)), Is.True,
                "the dedicated producer must fill the bounded PCM buffer");
            Assert.That(buffered.RenderingFault, Is.Null);
            buffered.Render(count, live.AsSpan(
                frame * channels, count * channels));
            frame += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(guard.CallsDuringRender, Is.Zero,
                "no Sample resolution after all frozen producer plans are bound");
            Assert.That(buffered.UnderrunCount, Is.Zero);
            Assert.That(live, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(expected.Take(200 * channels)
                .Any(x => Math.Abs(x) > 1e-5f), Is.True,
                "a silent source cannot prove PCM parity");
            Assert.That(expected.Skip(1500 * channels), Is.All.Zero,
                "every source must finish its Note Off/release and graph Filter tail");
            Assert.That(whole.Session.GetChannelState(0).CurrentVoice, Is.Null);
            Assert.That(split.Session.GetChannelState(0).CurrentVoice, Is.Null);
        });
        if (kind != "Sample")
            Assert.That(expected.Skip(960 * channels).Take(80 * channels)
                .Any(x => Math.Abs(x) > 1e-5f), Is.True,
                "the inherited Instrument release must remain audible after Off");

        string path = Path.Combine(Path.GetTempPath(),
            $"heresy-source-acceptance-{kind}-{Guid.NewGuid():N}.flac");
        try
        {
            SongExportService exporter = new(
                new OfflineSongRenderPlanFactory(configuration));
            OfflineRenderResult exported = await exporter.ExportAsync(
                exportSnapshot, path, OfflineAudioFileFormat.Flac);
            SamplePcmData decoded = SampleAudioCodec.Decode(
                await File.ReadAllBytesAsync(path), path);
            Assert.Multiple(() =>
            {
                Assert.That(exported.LogicalFrameCount, Is.EqualTo(LogicalFrames));
                Assert.That(exported.TailFrameCount, Is.Zero,
                    "all releases conclude during the two-row Pattern");
                Assert.That(decoded.SampleRate, Is.EqualTo(Rate));
                Assert.That(decoded.ChannelCount, Is.EqualTo(channels));
                Assert.That(decoded.FrameCount, Is.EqualTo(LogicalFrames));
            });
            for (int frame = 0; frame < LogicalFrames; frame++)
                for (int channel = 0; channel < channels; channel++)
                    Assert.That(decoded.GetSample(frame, channel),
                        Is.EqualTo(expected[frame * channels + channel])
                            .Within(2f / 32768),
                        $"FLAC frame {frame} channel {channel} of {kind}");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void PatternOrSequenceAsSynthSourceFailsFrozenPreparation(
        bool throughInstrument, bool sequence)
    {
        SongDocument song = new();
        ObjectId forbiddenId = song.AllocateObjectId();
        if (sequence)
            song.Add(new DataSequenceDefinition(forbiddenId, "Invalid child sequence"));
        else
            song.Add(new DataPatternDefinition(forbiddenId, "Invalid child pattern")
            {
                RowCount = 1, ChannelCount = 1,
            });
        ObjectId sourceId = forbiddenId;
        if (throughInstrument)
        {
            ObjectId instrumentId = song.AllocateObjectId();
            InstrumentDefinition instrument = new(instrumentId, "Invalid tone");
            instrument.ToneSpecifications.Add(new ToneSpecification
            {
                SourceId = forbiddenId,
            });
            instrument.ToneTable.Add(0);
            song.Add(instrument);
            sourceId = instrumentId;
        }
        ObjectId synthId = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(synthId, "Should reject private mixdown",
            new FmSynthGraph([new FmSoundSourceNode(0, sourceId, C4)], 0)));
        ObjectId patternId = song.AllocateObjectId();
        DataPatternDefinition root = new(patternId, "Root")
        {
            RowCount = 1, ChannelCount = 1,
        };
        PatternCell cell = root.Grid.GetOrCreateCell(0, 0);
        cell.SourceId = synthId;
        cell.Note = new StartPatternNote();
        song.Add(root);

        PreparedIncrementalPlaybackFactory factory =
            new(Configuration(1));
        Assert.That(() => factory.Create(song, patternId),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.Contains("not a directly invocable ISound"),
            "Pattern/Sequence child sources must not silently create a private mixdown");
    }

    private static (SongDocument Song, ObjectId PatternId, ObjectId OuterId,
        SampleDefinition Pcm) BuildSong(string kind)
    {
        SongDocument song = new();
        ObjectId sampleId = song.AllocateObjectId();
        SampleDefinition pcm = SampleDefinition.CreateImported(sampleId,
            "Four-frame loop", "loop.wav", FourFrameWave(8192));
        pcm.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4);
        song.Add(pcm);

        ObjectId envelopeId = song.AllocateObjectId();
        NodeEnvelopeDefinition volume = new(envelopeId, "Release");
        volume.ReplaceCurve([
            new(TimeSpan.Zero, 1),
            new(TimeSpan.FromMilliseconds(10), 1),
            new(TimeSpan.FromMilliseconds(40), 0),
        ], 1, 1, EnvelopeSustainLoopMode.Forward);
        song.Add(volume);

        ObjectId filterId = song.AllocateObjectId();
        FilterDefinition filterDefinition = new(filterId, "Graph IIR");
        song.Add(filterDefinition);
        FilterInstance lowpass = new(filterDefinition);
        lowpass.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(400));

        ObjectId dataId = song.AllocateObjectId();
        InstrumentDefinition data = new(dataId, "Data source");
        data.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sampleId, VolumeEnvelopeId = envelopeId,
        });
        data.ToneTable.Add(0);
        song.Add(data);

        ObjectId scriptId = song.AllocateObjectId();
        ScriptInstrumentDefinition script = new(scriptId, "Script source")
        {
            Source = "0",
            PitchExpression = "1.0",
        };
        script.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sampleId, VolumeEnvelopeId = envelopeId,
        });
        song.Add(script);

        ObjectId nestedId = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(nestedId, "Nested Instrument",
            new FmSynthGraph([
                new FmSoundSourceNode(0, dataId, C4),
                new FmFilterNode(1, 0, lowpass, tailSeconds: 0.008),
            ], 1)));

        ObjectId reference = kind switch
        {
            "Sample" => sampleId,
            "DataInstrument" => dataId,
            "ScriptInstrument" => scriptId,
            "RecursiveSynth" => nestedId,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        ObjectId outerId = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(outerId, kind,
            new FmSynthGraph([
                new FmSoundSourceNode(0, reference, C4),
                new FmFilterNode(1, 0, lowpass, tailSeconds: 0.008),
            ], 1)));
        ObjectId patternId = song.AllocateObjectId();
        DataPatternDefinition pattern = new(patternId, kind + " Pattern")
        {
            RowCount = 2, ChannelCount = 1,
        };
        PatternCell start = pattern.Grid.GetOrCreateCell(0, 0);
        start.SourceId = outerId;
        start.Note = new StartPatternNote();
        pattern.Grid.GetOrCreateCell(1, 0).Note = new PatternNoteOff();
        song.Add(pattern);
        ObjectId rootId = song.AllocateObjectId();
        DataSequenceDefinition root = new(rootId, "Root");
        root.Entries.Add(new SequenceEntry(patternId));
        song.Add(root);
        song.RootSequenceId = rootId;
        return (song, patternId, outerId, pcm);
    }

    private static RenderConfiguration Configuration(int channels)
        => channels == 1
            ? new RenderConfiguration(Rate, [
                new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0),
            ])
            : RenderConfiguration.Stereo(Rate);

    private static byte[] FourFrameWave(short value)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(44);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(8);
        for (int i = 0; i < 4; i++)
            writer.Write(value);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class GuardSamples : ISampleDataProvider
    {
        private readonly InMemorySampleDataProvider _inner = new();
        private int _calls;
        private int _callsDuringRender;
        public bool ForbidResolution { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public int CallsDuringRender => Volatile.Read(ref _callsDuringRender);
        public ISampleData GetSampleData(SampleDefinition sample)
        {
            Interlocked.Increment(ref _calls);
            if (ForbidResolution)
            {
                Interlocked.Increment(ref _callsDuringRender);
                throw new InvalidOperationException(
                    "Sample binding attempted after the frozen producer began PCM.");
            }
            return _inner.GetSampleData(sample);
        }
    }
}

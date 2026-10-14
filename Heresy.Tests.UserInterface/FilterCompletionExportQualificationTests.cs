using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
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
/// Production acceptance of the three distinct Filter Envelope completion
/// owners through frozen song preparation, actual live PCM and real PCM16
/// WAV encoding. None of the comparisons use an in-memory pretend file sink.
/// </summary>
[TestFixture]
public sealed class FilterCompletionExportQualificationTests
{
    private const int Rate = 1000;
    private const int LogicalFrames = 360;

    [TestCase(1)]
    [TestCase(2)]
    public async Task MixedNoteChannelAndFlattenedCompletionMatchesLiveChunksAndRealWav(
        int channels)
    {
        (SongDocument song, ObjectId pattern) = CreateSong(completionActions: true);
        RenderConfiguration configuration = Configuration(channels);

        PreparedIncrementalPlaybackFactory producer = new(configuration);
        using PreparedIncrementalPlaybackPlan prepared = producer.Create(song, pattern);
        using PreparedIncrementalPlaybackPlan partitioned = producer.Create(song, pattern);
        float[] reference = new float[LogicalFrames * channels];
        float[] chunks = new float[reference.Length];
        prepared.Source.Render(LogicalFrames, reference);
        int[] sizes = [1, 37, 2, 59, 3, 83, 7, 17];
        for (int frame = 0, block = 0; frame < LogicalFrames; block++)
        {
            int count = Math.Min(sizes[block % sizes.Length], LogicalFrames - frame);
            partitioned.Source.Render(count,
                chunks.AsSpan(frame * channels, count * channels));
            frame += count;
        }
        Assert.That(chunks, Is.EqualTo(reference).Within(1e-6f),
            "mixed note, host and flattened completion frames must survive arbitrary PCM blocks");
        Assert.That(reference.Take(10 * channels).Any(x => Math.Abs(x) > 0.001f),
            Is.True, "the qualification must not accidentally render silence");

        (SongDocument unbounded, ObjectId unboundedPattern) =
            CreateSong(completionActions: false);
        using PreparedIncrementalPlaybackPlan control =
            producer.Create(unbounded, unboundedPattern);
        float[] controlPcm = new float[reference.Length];
        control.Source.Render(LogicalFrames, controlPcm);
        Assert.That(Enumerable.Range(105 * channels, 10 * channels)
            .Any(i => Math.Abs(reference[i] - controlPcm[i]) > 0.02f),
            Is.True, "the suffixes must audibly change the output, not merely parse");

        PlaybackRequestAudioSourceFactory realtime = new(configuration);
        IAudioOutputSource live = realtime.Create(
            PatternPlaybackRequest.Create(song, pattern, repeat: false));
        using IDisposable liveOwner = (IDisposable)live;
        float[] livePcm = new float[reference.Length];
        int[] deviceSizes = [11, 1, 29, 3, 41, 7];
        for (int frame = 0, block = 0; frame < LogicalFrames; block++)
        {
            int count = Math.Min(deviceSizes[block % deviceSizes.Length],
                LogicalFrames - frame);
            live.Render(count, livePcm.AsSpan(frame * channels, count * channels));
            frame += count;
        }
        Assert.That(livePcm, Is.EqualTo(reference).Within(1e-6f),
            "the real playback-request source must agree with frozen production PCM");

        string path = Path.Combine(Path.GetTempPath(),
            $"heresy-filter-completion-{Guid.NewGuid():N}.wav");
        try
        {
            SongExportService service = new(
                new OfflineSongRenderPlanFactory(configuration));
            OfflineRenderResult exported = await service.ExportAsync(
                song, path, OfflineAudioFileFormat.Wave,
                waveBitDepth: WavePcmBitDepth.Pcm16);
            Assert.That(exported.LogicalFrameCount, Is.EqualTo(LogicalFrames));
            byte[] wav = await File.ReadAllBytesAsync(path);
            Assert.Multiple(() =>
            {
                Assert.That(Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
                Assert.That(Encoding.ASCII.GetString(wav, 8, 4), Is.EqualTo("WAVE"));
                Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)),
                    Is.EqualTo(channels));
                Assert.That(BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)),
                    Is.EqualTo(Rate));
                Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34, 2)),
                    Is.EqualTo(16));
                Assert.That(wav.Length, Is.GreaterThanOrEqualTo(
                    44 + reference.Length * sizeof(short)));
            });
            for (int i = 0; i < reference.Length; i++)
            {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(
                    wav.AsSpan(44 + 2 * i, 2));
                Assert.That(sample / 32768f, Is.EqualTo(reference[i])
                    .Within(2f / 32768f),
                    $"real WAV frame {i / channels}, channel {i % channels}");
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static (SongDocument Song, ObjectId Pattern) CreateSong(
        bool completionActions)
    {
        SongDocument song = new();
        ObjectId quickId = AddEnvelope("Quick", 40);
        ObjectId channelId = AddEnvelope("ChannelEnd", 60);
        ObjectId flatId = AddEnvelope("FlatEnd", 80);
        ObjectId filterId = song.AllocateObjectId();
        song.Add(new FilterDefinition(filterId, "Echo", [
            new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
        ]));
        ObjectId sampleId = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(sampleId,
            "Tone", "tone.wav", Wave(4096, 512)));

        ObjectId childId = song.AllocateObjectId();
        DataPatternDefinition child = new(childId, "Nested")
        {
            RowCount = 3, ChannelCount = 2,
        };
        child.Grid.GetOrCreateCell(0, 1).Note = new StartPatternNote(sampleId);
        child.Grid.GetOrCreateCell(2, 1).Note = new StartPatternNote(sampleId);
        song.Add(child);

        ObjectId patternId = song.AllocateObjectId();
        DataPatternDefinition parent = new(patternId, "Combined")
        {
            RowCount = 3, ChannelCount = 3,
        };
        PatternCell note = parent.Grid.GetOrCreateCell(0, 0);
        note.Note = new StartPatternNote(sampleId);
        note.Effects.Add(new SetNoteFilterPatternEffect(filterId));
        note.Effects.Add(new SetNoteFilterAssignmentsPatternEffect(
            Assignment("Quick", completionActions ? "THEN OFF" : "")));

        PatternCell host = parent.Grid.GetOrCreateCell(0, 1);
        host.Note = new StartPatternNote(sampleId);
        host.Effects.Add(new SetChannelFilterPatternEffect(filterId));
        host.Effects.Add(new SetChannelFilterAssignmentsPatternEffect(
            Assignment("ChannelEnd", completionActions ? "THEN CUT" : "")));

        PatternCell instigator = parent.Grid.GetOrCreateCell(0, 2);
        instigator.Note = new StartPatternNote(childId);
        instigator.Effects.Add(new SetNoteFilterPatternEffect(filterId));
        instigator.Effects.Add(new SetNoteFilterAssignmentsPatternEffect(
            Assignment("FlatEnd", completionActions ? "THEN CUT" : "")));
        song.Add(parent);

        ObjectId rootId = song.AllocateObjectId();
        DataSequenceDefinition root = new(rootId, "Root");
        root.Entries.Add(new SequenceEntry(patternId));
        song.Add(root);
        song.RootSequenceId = rootId;
        return (song, patternId);

        ObjectId AddEnvelope(string name, int durationMilliseconds)
        {
            ObjectId id = song.AllocateObjectId();
            NodeEnvelopeDefinition envelope = new(id, name);
            envelope.ReplaceCurve([
                new EnvelopeNode(TimeSpan.Zero, 0.4),
                new EnvelopeNode(TimeSpan.FromMilliseconds(durationMilliseconds), 0.4),
            ], 0, 0, EnvelopeSustainLoopMode.None);
            song.Add(envelope);
            return id;
        }
    }

    private static string Assignment(string name, string suffix)
        => $"echo.DelaySeconds = 0.002\necho.Mix = 1\necho.Feedforward = @\"{name}\""
            + (suffix.Length == 0 ? "" : " " + suffix);

    private static RenderConfiguration Configuration(int channels)
        => channels == 1
            ? new RenderConfiguration(Rate, [
                new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
              ])
            : RenderConfiguration.Stereo(Rate);

    private static byte[] Wave(short value, int frames)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + frames * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(Rate);
            writer.Write(Rate * 2);
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(frames * 2);
            for (int i = 0; i < frames; i++) writer.Write(value);
        }
        return stream.ToArray();
    }
}

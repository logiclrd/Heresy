using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class ParametricPatternPlaybackTests
{
    [TestCase(false, 0.5)]
    [TestCase(true, 0.25)]
    public void WallTimeEffectExpiresOrCommitsAtFixedFrame(bool commit, double after)
    {
        SongDocument song = SongWithTone(out ObjectId root);
        DataPatternDefinition pattern = (DataPatternDefinition)song.Objects[root];
        pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new ParametricPatternEffect(ParametricEffectParameter.GlobalVolume,
                TimeSpan.FromMilliseconds(100), "-0.5", commit));
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] pcm = new float[175];
        plan.Source.Render(pcm.Length, pcm);
        Assert.Multiple(() =>
        {
            Assert.That(pcm[10], Is.EqualTo(0.25f).Within(1e-4));
            Assert.That(pcm[99], Is.EqualTo(0.25f).Within(1e-4));
            Assert.That(pcm[100], Is.EqualTo((float)after).Within(1e-4));
            Assert.That(pcm[150], Is.EqualTo((float)after).Within(1e-4));
        });
    }

    [Test]
    public void IndependentOperatorsAddAndDoNotReplaceEachOthersBaselines()
    {
        SongDocument song = SongWithTone(out ObjectId root);
        DataPatternDefinition pattern = (DataPatternDefinition)song.Objects[root];
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.Effects.Add(new ParametricPatternEffect(
            ParametricEffectParameter.GlobalVolume,
            TimeSpan.FromMilliseconds(75), "-0.2", true));
        cell.Effects.Add(new ParametricPatternEffect(
            ParametricEffectParameter.GlobalVolume,
            TimeSpan.FromMilliseconds(130), "-0.3", false));
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] pcm = new float[180];
        plan.Source.Render(pcm.Length, pcm);
        Assert.Multiple(() =>
        {
            Assert.That(pcm[10], Is.EqualTo(0.25f).Within(1e-4));
            Assert.That(pcm[100], Is.EqualTo(0.25f).Within(1e-4));
            Assert.That(pcm[140], Is.EqualTo(0.4f).Within(1e-4));
        });
    }

    [TestCase(false, 0.5)]
    [TestCase(true, 0.375)]
    public void PhysicalChannelVolumeModulationRetainsCorrectExpiry(bool commit,
        double after)
    {
        SongDocument song = SongWithTone(out ObjectId root);
        DataPatternDefinition pattern = (DataPatternDefinition)song.Objects[root];
        pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new ParametricPatternEffect(
                ParametricEffectParameter.OverallChannelVolume,
                TimeSpan.FromMilliseconds(100), "-0.25", commit));
        using PreparedIncrementalPlaybackPlan whole =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        using PreparedIncrementalPlaybackPlan sliced =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] expected = new float[170], actual = new float[170];
        whole.Source.Render(expected.Length, expected);
        int[] chunks = [1, 19, 3, 47, 7];
        for (int at = 0, i = 0; at < actual.Length; i++)
        {
            int n = Math.Min(actual.Length - at, chunks[i % chunks.Length]);
            sliced.Source.Render(n, actual.AsSpan(at, n));
            at += n;
        }
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(expected[10], Is.EqualTo(0.375f).Within(1e-4));
            Assert.That(expected[99], Is.EqualTo(0.375f).Within(1e-4));
            Assert.That(expected[100], Is.EqualTo((float)after).Within(1e-4));
        });
    }

    [Test]
    public void ExpressionsAndPolicySurviveJsonAndSnapshotAndDoNotAffectLiveSong()
    {
        SongDocument song = SongWithTone(out ObjectId root);
        DataPatternDefinition pattern = (DataPatternDefinition)song.Objects[root];
        pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new ParametricPatternEffect(ParametricEffectParameter.GlobalVolume,
                TimeSpan.FromMilliseconds(50), "-0.2 + rowTime * 0.0", true));
        const string path = "/tmp/parametric-effects.hm.json";
        SongDocument round = Heresy.Core.Persistence.SongDocumentJson.Deserialize(
            Heresy.Core.Persistence.SongDocumentJson.Serialize(song, path), path);
        var restored = (ParametricPatternEffect)((DataPatternDefinition)
            round.Objects[root]).Grid[0, 0]!.Effects.Single();
        Assert.Multiple(() =>
        {
            Assert.That(restored.Parameter, Is.EqualTo(ParametricEffectParameter.GlobalVolume));
            Assert.That(restored.CommitOnExpire, Is.True);
            Assert.That(restored.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(50)));
            Assert.That(restored.Expression, Is.EqualTo("-0.2 + rowTime * 0.0"));
        });
        using PreparedIncrementalPlaybackPlan frozen =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        pattern.Grid[0, 0]!.Effects.Clear();
        song.MarkChanged(affectsAudio: true);
        float[] pcm = new float[25];
        frozen.Source.Render(25, pcm);
        Assert.That(pcm[10], Is.EqualTo(0.4f).Within(1e-4));
    }

    [Test]
    public void InvalidExpressionFailsDuringPreparationNotWhileRendering()
    {
        SongDocument song = SongWithTone(out ObjectId root);
        ((DataPatternDefinition)song.Objects[root]).Grid.GetOrCreateCell(0, 0)
            .Effects.Add(new ParametricPatternEffect(
                ParametricEffectParameter.GlobalVolume,
                TimeSpan.FromSeconds(1), "System.IO.File.ReadAllText(\"foo\")"));
        Assert.Throws<InvalidOperationException>(() =>
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root));
    }

    private static RenderConfiguration Mono() => new(1000,
        [new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0)]);

    private static SongDocument SongWithTone(out ObjectId root)
    {
        SongDocument song = new();
        ObjectId sample = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(sample,
            "Tone", "tone.wav", Wave()));
        root = song.AllocateObjectId();
        DataPatternDefinition pattern = new(root, "Parametric")
            { RowCount = 10, ChannelCount = 1 };
        pattern.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sample);
        song.Add(pattern);
        return song;
    }
    private static byte[] Wave()
    {
        const int frames = 3000;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + frames * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(1000); writer.Write(2000);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(frames * 2);
        for (int i = 0; i < frames; i++) writer.Write((short)16384);
        writer.Flush();
        return stream.ToArray();
    }
}

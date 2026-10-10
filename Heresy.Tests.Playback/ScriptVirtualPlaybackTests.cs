using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// Compiled restricted-C# virtual helper -> production recursive cursor ->
/// scoped PlaybackSession voice -> actual PCM, not a synthetic raw schedule.
/// </summary>
[TestFixture]
public sealed class ScriptVirtualPlaybackTests
{
    [Test]
    public void ScriptVirtualOffAndCutLeavePhysicalVoiceAndPcmChunkInvariant()
    {
        SongDocument song = new();
        ObjectId sampleId = AddSample(song);
        ObjectId id = song.AllocateObjectId();
        song.Add(new ScriptPatternDefinition(id, "Physical and virtual")
        {
            RowCount = 3, ChannelCount = 1,
            Source = $"""
                Note(0, 0, _O({sampleId.Value}));
                NoteVirtual(0, 7U, _O({sampleId.Value}));
                NoteVirtual(0, 8U, _O({sampleId.Value}));
                OffVirtual(0.25, 7U);
                CutVirtual(0.5, 8U);
                """,
        });
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan one = factory.Create(song, id);
        using PreparedIncrementalPlaybackPlan many = factory.Create(song, id);
        float[] a = new float[170];
        one.Source.Render(a.Length, a);
        float[] b = Render(many.Source, a.Length, [1, 7, 3, 19, 31, 2]);
        Assert.Multiple(() =>
        {
            Assert.That(b, Is.EqualTo(a).Within(1e-6f));
            Assert.That(a[15], Is.GreaterThan(a[45] * 1.45f),
                "The first virtual Off removes only its voice.");
            Assert.That(a[45], Is.GreaterThan(a[100] * 1.45f),
                "The second virtual Cut removes only its voice.");
            Assert.That(a[100], Is.GreaterThan(0.2f),
                "The ordinary physical note keeps playing.");
        });
    }

    [Test]
    public void SameFrameBroadcastAndReusedVirtualIdsDoNotCrossScopes()
    {
        SongDocument song = new();
        ObjectId sampleId = AddSample(song);
        ObjectId firstId = song.AllocateObjectId();
        ObjectId secondId = song.AllocateObjectId();
        song.Add(new ScriptPatternDefinition(firstId, "First virtual owner")
        {
            RowCount = 3, ChannelCount = 1,
            Source = $"""
                NoteVirtual(0, 7U, _O({sampleId.Value}));
                CutVirtualInScope(0);
                CutVirtualInScope(0.5);
                CutAllVirtual(1.5);
                """,
        });
        song.Add(new ScriptPatternDefinition(secondId, "Second virtual owner")
        {
            RowCount = 3, ChannelCount = 1,
            Source = $"NoteVirtual(0, 7U, _O({sampleId.Value}));",
        });
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan plan = factory.Create(song, firstId);
        plan.Timeline.AddRoot(secondId);
        float[] a = new float[230];
        plan.Source.Render(a.Length, a);
        Assert.Multiple(() =>
        {
            Assert.That(a[10], Is.GreaterThan(0.65f),
                "Same-frame broadcast must not cut the new virtual voice.");
            Assert.That(a[100], Is.GreaterThan(0.2f),
                "Scoped cut must not affect the sibling's virtual ID.");
            Assert.That(a[100], Is.LessThan(a[10] * 0.7f),
                "The first scope's previous voice was cut.");
            Assert.That(Math.Abs(a[210]), Is.LessThan(1e-4),
                "AllVirtual must reach the other scoped owner.");
        });
    }

    [Test]
    public void CancelingOneScriptCursorDropsItsVirtualVoiceAndDeferredEventsOnly()
    {
        SongDocument song = new();
        ObjectId sampleId = AddSample(song);
        ObjectId firstId = song.AllocateObjectId();
        ObjectId secondId = song.AllocateObjectId();
        song.Add(new ScriptPatternDefinition(firstId, "Canceled")
        {
            RowCount = 3, ChannelCount = 1,
            Source = $"""
                NoteVirtual(0, 7U, _O({sampleId.Value}));
                CutVirtual(1, 7U);
                """,
        });
        song.Add(new ScriptPatternDefinition(secondId, "Survivor")
        {
            RowCount = 3, ChannelCount = 1,
            Source = $"NoteVirtual(0, 7U, _O({sampleId.Value}));",
        });
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, firstId);
        long sibling = plan.Timeline.AddRoot(secondId);
        float[] before = new float[20];
        plan.Source.Render(before.Length, before);
        Assert.That(before[10], Is.GreaterThan(0.65f));

        Assert.That(plan.Source.Cancel(plan.RootInvocationId), Is.True);
        Assert.That(plan.Timeline.IsInvocationActive(sibling), Is.True);
        float[] after = new float[160];
        plan.Source.Render(after.Length, after);
        Assert.Multiple(() =>
        {
            Assert.That(after[25], Is.GreaterThan(0.2f),
                "The independently owned sibling keeps its virtual voice.");
            Assert.That(after[25], Is.LessThan(before[10] * 0.7f),
                "The canceled scope's virtual voice was cut.");
            Assert.That(after[140], Is.GreaterThan(0.2f),
                "Canceled producer's deferred Cut must not affect the sibling.");
        });
    }

    private static RenderConfiguration Mono()
        => new(1000, [new OutputChannelConfiguration(
            Vector3.Zero, positionalImportance: 0)]);

    private static ObjectId AddSample(SongDocument song)
    {
        ObjectId id = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(id, "Sustain",
            "sustain.wav", Wave()));
        return id;
    }

    private static byte[] Wave()
    {
        const int frames = 1000;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + frames * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(1000);
        writer.Write(2000);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(frames * 2);
        for (int i = 0; i < frames; i++)
            writer.Write((short)16000);
        writer.Flush();
        return stream.ToArray();
    }

    private static float[] Render(
        PreparedIncrementalAudioSource source, int frames, int[] chunks)
    {
        float[] pcm = new float[frames];
        for (int pos = 0, i = 0; pos < frames; i++)
        {
            int n = Math.Min(frames - pos, chunks[i % chunks.Length]);
            source.Render(n, pcm.AsSpan(pos, n));
            pos += n;
        }
        return pcm;
    }
}

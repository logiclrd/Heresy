using System;
using System.IO;
using System.Numerics;
using System.Text;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>Production Roslyn scripts exercising real scoped voice state.</summary>
[TestFixture]
public sealed class ScriptVirtualControlPlaybackTests
{
    [Test]
    public void VolumeNnaAndPastCutAffectOnlyDisplacedVoiceOfTargetVirtualId()
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        ObjectId patternId = AddPattern(document, $"""
            using var v7 = NoteVirtual(0, _O({sample.Value}));
            using var v8 = NoteVirtual(0, _O({sample.Value}));
            v7.Volume(0.25, 0.25);
            v7.Displacement(0.5, NoteDisplacementAction.Continue);
            v7.Note(0.75, _O({sample.Value}));
            v7.Past(1, TrackerPastNoteAction.Cut);
            """);
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan one = factory.Create(document, patternId);
        using PreparedIncrementalPlaybackPlan many = factory.Create(document, patternId);
        float[] expected = new float[200];
        one.Source.Render(110, expected.AsSpan(0, 110));
        Assert.That(one.Session.VirtualVoices.Count, Is.EqualTo(1),
            "The old voice must migrate when a new virtual note displaces it.");
        one.Source.Render(90, expected.AsSpan(110, 90));
        Assert.That(one.Session.VirtualVoices, Is.Empty,
            "PastVirtual(Cut) removes displaced NNA voices, not the current one.");
        float[] chunks = new float[200];
        int[] sizes = [1, 7, 19, 3, 61];
        for (int pos = 0, i = 0; pos < chunks.Length; i++)
        {
            int n = Math.Min(chunks.Length - pos, sizes[i % sizes.Length]);
            many.Source.Render(n, chunks.AsSpan(pos, n));
            pos += n;
        }
        Assert.Multiple(() =>
        {
            Assert.That(chunks, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(expected[12], Is.GreaterThan(0.9f),
                "Both initial voices sound.");
            Assert.That(expected[50], Is.GreaterThan(0.52f));
            Assert.That(expected[50], Is.LessThan(expected[12] * 0.8f),
                "VolumeVirtual reduces only virtual ID 7.");
            Assert.That(expected[105], Is.GreaterThan(expected[50] + 0.08f),
                "The displaced Continue voice remains audible.");
            Assert.That(expected[155], Is.LessThan(expected[105] - 0.08f),
                "PastVirtual cuts the displaced voice without cutting its replacement.");
            Assert.That(expected[155], Is.GreaterThan(0.5f),
                "The replacement and unrelated virtual ID 8 survive.");
        });
    }

    [Test]
    public void NativeFrameSeekAtDeferredWallDeadlineTargetsReplacementVoice()
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: true);
        ObjectId withOffset = AddPattern(document, $"""
            using var v7 = NoteVirtual(0, _O({sample.Value}));
            v7.SourceFrame(0.5, 300, timeOffsetSeconds: 0.04);
            v7.Note(0.75, _O({sample.Value}));
            using var v8 = NoteVirtual(0.75, _O({sample.Value}));
            """);
        ObjectId noOffset = AddPattern(document, $"""
            using var v7 = NoteVirtual(0, _O({sample.Value}));
            v7.Note(0.75, _O({sample.Value}));
            using var v8 = NoteVirtual(0.75, _O({sample.Value}));
            """);
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan seek = factory.Create(document, withOffset);
        using PreparedIncrementalPlaybackPlan baseline = factory.Create(document, noOffset);
        float[] a = new float[135];
        float[] b = new float[135];
        seek.Source.Render(a.Length, a);
        baseline.Source.Render(b.Length, b);
        Assert.Multiple(() =>
        {
            Assert.That(a[80], Is.EqualTo(b[80]).Within(1e-5),
                "The native source-frame offset is not applied at raw row 0.5.");
            Assert.That(a[95], Is.EqualTo(b[95]).Within(1e-5),
                "The replacement voice starts before the deferred wall-time deadline.");
            Assert.That(a[114], Is.GreaterThan(b[114] + 0.3f),
                "Only virtual ID 7's replacement jumps to source frame 300.");
        });
    }

    [Test]
    public void DeferredVirtualControlOfCanceledCursorCannotTouchSiblingId()
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        ObjectId first = AddPattern(document, $"""
            using var v7 = NoteVirtual(0, _O({sample.Value}));
            v7.Volume(0.5, 0.05, timeOffsetSeconds: 0.08);
            """);
        ObjectId second = AddPattern(document,
            $"using var v7 = NoteVirtual(0, _O({sample.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan plan = factory.Create(document, first);
        long sibling = plan.Timeline.AddRoot(second);
        float[] before = new float[30];
        plan.Source.Render(before.Length, before);
        Assert.That(plan.Source.Cancel(plan.RootInvocationId), Is.True);
        Assert.That(plan.Timeline.IsInvocationActive(sibling), Is.True);
        float[] after = new float[160];
        plan.Source.Render(after.Length, after);
        Assert.Multiple(() =>
        {
            Assert.That(after[145], Is.GreaterThan(0.42f),
                "The canceled deferred volume command cannot reach another cursor.");
            Assert.That(after[145], Is.LessThan(before[10] * 0.7f),
                "The canceled cursor's own voice is gone.");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroupControlsMatchExplicitHandleControlsWithoutTouchingUnrelatedVoice(bool relaxed)
    {
        SongDocument document = new();
        ObjectId sample = AddSample(document, stepped: false);
        string lookup = $"_O({sample.Value})";
        string prefix = relaxed ? "#order relaxed\n" : "";
        ObjectId groupPattern = AddPattern(document, prefix + $"""
            using var other = NoteVirtual(0, {lookup});
            using (var chord = NewNoteGroup())
            {
                chord += NoteVirtual(0, {lookup});
                chord += NoteVirtual(0, {lookup});
                chord.Volume(0.5, 0.25);
                chord.Cut(1, timeOffsetSeconds: 0.02);
            }
            using var replacement = NoteVirtual(1.1, {lookup});
            replacement.Cut(2);
            """);
        ObjectId individualPattern = AddPattern(document, prefix + $"""
            using var other = NoteVirtual(0, {lookup});
            using (var first = NoteVirtual(0, {lookup}))
            using (var second = NoteVirtual(0, {lookup}))
            {
                first.Volume(0.5, 0.25);
                second.Volume(0.5, 0.25);
                first.Cut(1, timeOffsetSeconds: 0.02);
                second.Cut(1, timeOffsetSeconds: 0.02);
            }
            using var replacement = NoteVirtual(1.1, {lookup});
            replacement.Cut(2);
            """);
        var factory = new PreparedIncrementalPlaybackFactory(Mono());
        using var reference = factory.Create(document, individualPattern);
        using var group = factory.Create(document, groupPattern);
        float[] expected = new float[400], actual = new float[400];
        reference.Source.Render(expected.Length, expected);
        for (int pos = 0; pos < actual.Length;)
        {
            int count = Math.Min(17, actual.Length - pos);
            group.Source.Render(count, actual.AsSpan(pos, count));
            pos += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
            Assert.That(actual[25], Is.GreaterThan(1.4f), "All three voices start.");
            Assert.That(actual[90], Is.EqualTo(16000f / 32768f * 1.5f).Within(1e-5f));
            Assert.That(actual[200], Is.EqualTo(16000f / 32768f * 2).Within(1e-5f),
                "The old group's deferred cuts leave the new generation and unrelated voice alive.");
            Assert.That(actual[320], Is.EqualTo(16000f / 32768f).Within(1e-5f));
            Assert.That(group.SequencingContext.Diagnostics.VirtualOwnershipWarnings, Is.Zero);
        });
    }

    private static RenderConfiguration Mono()
        => new(1000, [new OutputChannelConfiguration(
            Vector3.Zero, positionalImportance: 0)]);

    private static ObjectId AddPattern(SongDocument doc, string code)
    {
        ObjectId id = doc.AllocateObjectId();
        doc.Add(new ScriptPatternDefinition(id, "Virtual controls")
        {
            RowCount = 4, ChannelCount = 1, Source = code,
        });
        return id;
    }

    private static ObjectId AddSample(SongDocument doc, bool stepped)
    {
        ObjectId id = doc.AllocateObjectId();
        doc.Add(SampleDefinition.CreateImported(
            id, "Source", "virtual-control.wav", Wave(stepped)));
        return id;
    }

    private static byte[] Wave(bool stepped)
    {
        const int frames = 1200;
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
            writer.Write((short)(stepped && i < 200 ? 4000 : 16000));
        writer.Flush();
        return stream.ToArray();
    }
}

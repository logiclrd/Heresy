using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// Two nested Instruments select either a terminal Sample or a private
/// Sequence/Pattern mixdown. Actual production source preparation, the
/// coroutine timeline and the renderer jointly own release, S7x and NNA.
/// </summary>
[TestFixture]
public sealed class NestedInstrumentPrivateReleaseIntersectionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RepeatedOffDuringDelayedS77PauseAndS78ResumeKeepsReleaseOrigin(
        bool privateSequence)
    {
        SongDocument document = CreateSong(privateSequence,
            sourceFrames: 220, releaseMilliseconds: 40,
            out ObjectId instrumentId, out _);
        NoteSchedule notes = Schedule(
            At(0, new StartNoteCommand(instrumentId)),
            At(10, new NoteOffCommand()),
            At(20, new ApplyTrackerEnvelopeControlCommand(
                TrackerEnvelopeControlTarget.Volume, false)),
            At(30, new NoteOffCommand()),
            At(50, new ApplyTrackerEnvelopeControlCommand(
                TrackerEnvelopeControlTarget.Volume, true)));

        float[] big = Render(document, notes, 130, [130], out _);
        float[] split = Render(document, notes, 130,
            [1, 3, 7, 11, 2, 17], out bool quiescent);
        Assert.That(split, Is.EqualTo(big).Within(1e-6f));
        Assert.Multiple(() =>
        {
            Assert.That(big[0], Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(big[10], Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(big[19], Is.EqualTo(privateSequence
                ? 0.352625f : 0.3875f).Within(1e-6f));
            Assert.That(big[20], Is.EqualTo(privateSequence
                ? 0.3375f : 0.375f).Within(1e-6f));
            Assert.That(big[49], Is.EqualTo(privateSequence
                ? 0.22875f : 0.375f).Within(1e-6f),
                "S77 holds the outer envelope; a private leaf's own " +
                "release still advances during the pause and repeated Off.");
            Assert.That(big[50], Is.EqualTo(privateSequence
                ? 0.225f : 0.375f).Within(1e-6f));
            Assert.That(big[51], Is.EqualTo(privateSequence
                ? 0.213875f : 0.3625f).Within(1e-6f));
            Assert.That(big[79], Is.EqualTo(privateSequence
                ? 0.003875f : 0.0125f).Within(1e-6f));
            Assert.That(big[80], Is.Zero.Within(1e-6f),
                "Resume must finish the original 40-ms release at frame 80.");
            Assert.That(big[110], Is.Zero.Within(1e-6f));
            Assert.That(quiescent, Is.True,
                "A completed private or direct Instrument release must not linger.");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FiniteChildPcmEndsBeforePausedVolumeReleaseCompletes(
        bool privateSequence)
    {
        SongDocument document = CreateSong(privateSequence,
            sourceFrames: 65, releaseMilliseconds: 40,
            out ObjectId instrumentId, out _);
        NoteSchedule notes = Schedule(
            At(0, new StartNoteCommand(instrumentId)),
            At(10, new NoteOffCommand()),
            At(20, new ApplyTrackerEnvelopeControlCommand(
                TrackerEnvelopeControlTarget.Volume, false)),
            At(50, new ApplyTrackerEnvelopeControlCommand(
                TrackerEnvelopeControlTarget.Volume, true)));

        float[] pcm = Render(document, notes, 120, [3, 1, 29, 7, 13],
            out bool quiescent);
        Assert.Multiple(() =>
        {
            Assert.That(pcm[60], Is.GreaterThan(0),
                "The release remains audible before PCM natural end.");
            Assert.That(pcm[64], Is.GreaterThan(0));
            Assert.That(pcm[65], Is.Zero.Within(1e-6f),
                "Natural PCM end must cap envelope release for either "
                + "a Sample tone or a private Sequence selected by Instrument.");
            Assert.That(pcm[80], Is.Zero.Within(1e-6f));
            Assert.That(quiescent, Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NnaOffReleasesOldNestedInstrumentAndDelaysSecondEnvelope(
        bool privateSequence)
    {
        SongDocument document = CreateSong(privateSequence,
            sourceFrames: 220, releaseMilliseconds: 40,
            out ObjectId instrumentId, out _);
        NoteSchedule notes = Schedule(
            At(0, new StartNoteCommand(instrumentId)),
            At(20,
                new SetCurrentVoiceDisplacementActionCommand(
                    NoteDisplacementAction.Off),
                new StartNoteCommand(instrumentId)),
            At(30, new NoteOffCommand()),
            At(40, new ApplyTrackerEnvelopeControlCommand(
                TrackerEnvelopeControlTarget.Volume, false)),
            At(70, new ApplyTrackerEnvelopeControlCommand(
                TrackerEnvelopeControlTarget.Volume, true)));

        float[] whole = Render(document, notes, 160, [160],
            out bool quiescent);
        float[] chunks = Render(document, notes, 160,
            [1, 7, 13, 2, 31, 5], out _);
        Assert.That(chunks, Is.EqualTo(whole).Within(1e-6f));
        Assert.Multiple(() =>
        {
            Assert.That(whole[19], Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(whole[20], Is.EqualTo(1f).Within(1e-6f),
                "NNA Off must begin releasing the old Instrument note.");
            Assert.That(whole[30], Is.EqualTo(privateSequence
                ? 0.8375f : 0.875f).Within(1e-6f));
            Assert.That(whole[40], Is.EqualTo(privateSequence
                ? 0.5375f : 0.625f).Within(1e-6f));
            Assert.That(whole[49], Is.EqualTo(privateSequence
                ? 0.401375f : 0.5125f).Within(1e-6f));
            Assert.That(whole[60], Is.EqualTo(privateSequence
                ? 0.2625f : 0.375f).Within(1e-6f),
                "The old NNA tail finishes without truncating the new one.");
            Assert.That(whole[70], Is.EqualTo(privateSequence
                ? 0.225f : 0.375f).Within(1e-6f));
            Assert.That(whole[99], Is.EqualTo(privateSequence
                ? 0.003875f : 0.0125f).Within(1e-6f));
            Assert.That(whole[100], Is.Zero.Within(1e-6f));
            Assert.That(quiescent, Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ZeroReleaseAndEndInputRetireNestedInstrumentWithoutAnotherBlock(
        bool privateSequence)
    {
        SongDocument document = CreateSong(privateSequence,
            sourceFrames: 220, releaseMilliseconds: 0,
            out ObjectId instrumentId, out _);
        NoteSchedule notes = Schedule(
            At(0, new StartNoteCommand(instrumentId)),
            At(10, new NoteOffCommand()));
        using PreparedIncrementalPlaybackPlan plan =
            Factory().CreateAdHoc(SongDocumentSnapshot.Create(document), notes);
        float[] prefix = new float[10];
        plan.Source.Render(10, prefix);
        Assert.That(prefix, Is.All.EqualTo(0.5f).Within(1e-6f));
        float[] afterOff = new float[1];
        plan.Source.Render(1, afterOff);
        Assert.That(afterOff[0], Is.Zero.Within(1e-6f));
        plan.Source.EndInput();
        Assert.That(plan.Session.IsQuiescent, Is.True,
            "Zero release must finish immediately even through a private "
            + "Sequence tone, without an extra PCM block.");
    }

    private static SongDocument CreateSong(
        bool privateSequence, int sourceFrames, int releaseMilliseconds,
        out ObjectId instrumentId, out ObjectId sampleId)
    {
        SongDocument doc = new();
        sampleId = doc.AllocateObjectId();
        doc.Add(SampleDefinition.CreateImported(sampleId, "PCM",
            "pcm.wav", Wave(16384, sourceFrames)));

        ObjectId terminal = sampleId;
        if (privateSequence)
        {
            ObjectId patternId = doc.AllocateObjectId();
            DataPatternDefinition pattern = new(patternId, "Private leaf")
            {
                RowCount = 4, ChannelCount = 1,
            };
            // Private end-input sends Note Off to the child's *own*
            // voices. A bare Sample stops on that Off regardless of the
            // enclosing Instrument's volume envelope. Give the leaf its
            // own finite release so it can tail while the outer envelope
            // is paused/resumed. The two envelopes compose independently.
            ObjectId leafEnvelopeId = doc.AllocateObjectId();
            doc.Add(new AdsrEnvelopeDefinition(leafEnvelopeId, "Leaf release")
            {
                SustainLevel = 1.0,
                Release = TimeSpan.FromMilliseconds(100),
            });
            ObjectId leafInstrumentId = doc.AllocateObjectId();
            InstrumentDefinition leafInstrument =
                new(leafInstrumentId, "Private leaf Instrument");
            leafInstrument.ToneSpecifications.Add(new ToneSpecification
            {
                SourceId = sampleId,
                VolumeEnvelopeId = leafEnvelopeId,
            });
            leafInstrument.ToneTable.Add(0);
            doc.Add(leafInstrument);
            pattern.Grid.GetOrCreateCell(0, 0).Note =
                new StartPatternNote(leafInstrumentId);
            doc.Add(pattern);

            ObjectId sequenceId = doc.AllocateObjectId();
            DataSequenceDefinition sequence = new(sequenceId, "Private Sequence");
            sequence.Entries.Add(new SequenceEntry(patternId));
            doc.Add(sequence);
            terminal = sequenceId;
        }

        ObjectId innerId = doc.AllocateObjectId();
        InstrumentDefinition inner = new(innerId, "Inner Instrument");
        inner.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = terminal,
        });
        inner.ToneTable.Add(0);
        doc.Add(inner);

        ObjectId envelopeId = doc.AllocateObjectId();
        doc.Add(new AdsrEnvelopeDefinition(envelopeId, "Release")
        {
            SustainLevel = 1.0,
            Release = TimeSpan.FromMilliseconds(releaseMilliseconds),
        });
        instrumentId = doc.AllocateObjectId();
        InstrumentDefinition outer = new(instrumentId, "Outer Instrument");
        outer.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = innerId,
            VolumeEnvelopeId = envelopeId,
        });
        outer.ToneTable.Add(0);
        doc.Add(outer);
        return doc;
    }

    private static PreparedIncrementalPlaybackFactory Factory() =>
        new(new RenderConfiguration(1000,
            [new OutputChannelConfiguration(Vector3.Zero,
                positionalImportance: 0)]));

    private static float[] Render(SongDocument doc, NoteSchedule notes,
        int frames, int[] blocks, out bool quiescent)
    {
        using PreparedIncrementalPlaybackPlan plan =
            Factory().CreateAdHoc(SongDocumentSnapshot.Create(doc), notes);
        float[] pcm = new float[frames];
        for (int frame = 0, n = 0; frame < frames; n++)
        {
            int count = Math.Min(blocks[n % blocks.Length], frames - frame);
            plan.Source.Render(count, pcm.AsSpan(frame, count));
            frame += count;
        }
        plan.Source.EndInput();
        quiescent = plan.Session.IsQuiescent;
        return pcm;
    }

    private static NoteEvent At(int frame, params NoteCommand[] commands)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(frame), 0),
            ChannelTarget.Physical(0), commands);

    private static NoteSchedule Schedule(params NoteEvent[] events)
    {
        NoteScheduleBuilder builder = new();
        foreach (NoteEvent note in events)
            builder.Append(note);
        return builder.Freeze();
    }

    private static byte[] Wave(short amplitude, int frames)
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
            writer.Write(1000);
            writer.Write(2000);
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(frames * 2);
            for (int i = 0; i < frames; i++)
                writer.Write(amplitude);
        }
        return stream.ToArray();
    }
}

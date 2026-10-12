using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Sequences;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Playback;
using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class ScriptInstrumentPlaybackTests
{
    [TestCase(1.0, 0.5)]
    [TestCase(2.0, 0.25)]
    public void ScriptSelectsDifferentSourceForEachNotePitch(double pitch, double expected)
    {
        SongDocument song = new();
        ObjectId loud = Sample(song, 16384);
        ObjectId quiet = Sample(song, 8192);
        ObjectId inst = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(inst, "Key split")
        { Source = "pitchMultiplier < 1.5 ? 0 : 1" };
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = loud });
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = quiet });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({inst.Value}), pitchMultiplier: {pitch.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        float[] pcm = new float[10];
        plan.Source.Render(10, pcm);
        Assert.That(pcm, Is.All.EqualTo((float)expected).Within(1e-5f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ScriptToneFilterProcessesDirectAndPrivatePatternAudio(bool privatePattern)
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId source = privatePattern
            ? ScriptPattern(song, $"Note(0, 0, _O({sample.Value}));")
            : sample;
        FilterInstance filter = Echo(song, 0.001);
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition scripted = new(id, "Filtered script tone")
        { Source = "0", PitchExpression = "pitchMultiplier" };
        scripted.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = source,
            FilterConfiguration = filter,
        });
        song.Add(scripted);
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({id.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan whole = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan chunked = factory.Create(song, root);
        float[] expected = new float[12];
        float[] actual = new float[12];
        whole.Source.Render(12, expected);
        chunked.Source.Render(1, actual.AsSpan(0, 1));
        chunked.Source.Render(2, actual.AsSpan(1, 2));
        chunked.Source.Render(9, actual.AsSpan(3));
        Assert.That(expected[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(expected[1], Is.EqualTo(1.0f).Within(1e-5f),
            "the tone Filter must survive compiled script pitch selection");
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedDataToneFiltersComposeThroughPrivatePatternOrSequence(
        bool viaSequence)
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId pattern = ScriptPattern(song,
            $"Note(0, 0, _O({sample.Value}));");
        ObjectId terminal = pattern;
        if (viaSequence)
        {
            ObjectId sequenceId = song.AllocateObjectId();
            DataSequenceDefinition sequence = new(sequenceId, "Private sequence");
            sequence.Entries.Add(new SequenceEntry(pattern));
            song.Add(sequence);
            terminal = sequenceId;
        }
        ObjectId innerId = song.AllocateObjectId();
        InstrumentDefinition inner = new(innerId, "Inner");
        inner.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = terminal, FilterConfiguration = Echo(song, 0.001),
        });
        inner.ToneTable.Add(0);
        song.Add(inner);
        ObjectId outerId = song.AllocateObjectId();
        InstrumentDefinition outer = new(outerId, "Outer");
        outer.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = innerId, FilterConfiguration = Echo(song, 0.002),
        });
        outer.ToneTable.Add(0);
        song.Add(outer);
        ObjectId root = ScriptPattern(song,
            $"Note(0, 0, _O({outerId.Value}));");

        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan unsplit = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan partitioned = factory.Create(song, root);
        float[] actual = new float[12], reference = new float[12];
        unsplit.Source.Render(12, actual);
        for (int pos = 0; pos < reference.Length;)
        {
            int count = Math.Min(2, reference.Length - pos);
            partitioned.Source.Render(count, reference.AsSpan(pos, count));
            pos += count;
        }
        Assert.That(actual.Take(4),
            Is.EqualTo(new float[] { 0.5f, 1.0f, 1.5f, 2.0f })
                .Within(1e-5f),
            "two nested tone Filters apply to the private source PCM, inner to outer");
        Assert.That(reference, Is.EqualTo(actual).Within(1e-6f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ScriptNoteAndVirtualNoteSelectEventFilterWithoutSiblingLeak(
        bool useVirtual)
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance selected = Echo(song, 0.001);
        ObjectId id = ScriptPattern(song,
            useVirtual
                ? $"NoteVirtual(0, 7U, _O({sample.Value}), " +
                  $"eventFilterDefinitionId: _O({selected.DefinitionId.Value})); " +
                  $"Note(0, 0, _O({sample.Value}));"
                : $"Note(0, 0, _O({sample.Value}), " +
                  $"eventFilterDefinitionId: _O({selected.DefinitionId.Value})); " +
                  $"Note(0, 1, _O({sample.Value}));");
        if (!useVirtual)
            ((ScriptPatternDefinition)song.Objects[id]).ChannelCount = 2;
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan first = factory.Create(song, id);
        using PreparedIncrementalPlaybackPlan second = factory.Create(song, id);
        float[] whole = new float[8], partitioned = new float[8];
        first.Source.Render(whole.Length, whole);
        second.Source.Render(1, partitioned.AsSpan(0, 1));
        second.Source.Render(7, partitioned.AsSpan(1));
        Assert.That(whole[0], Is.EqualTo(1.0f).Within(1e-5f));
        Assert.That(whole[1], Is.EqualTo(1.5f).Within(1e-5f),
            "only the selected note acquires its extra delayed sample");
        Assert.That(partitioned, Is.EqualTo(whole).Within(1e-6f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SelectedNoteEventFilterIsBoundAcrossDirectAndPrivatePatternSources(
        bool privatePattern)
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId source = privatePattern
            ? ScriptPattern(song, $"Note(0, 0, _O({sample.Value}));")
            : sample;
        FilterInstance filter = Echo(song, 0.001);
        ObjectId id = ScriptPattern(song,
            $"Note(0, 0, _O({source.Value}), mixdown: {privatePattern.ToString().ToLowerInvariant()}, " +
            $"eventFilterDefinitionId: _O({filter.DefinitionId.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan plan = factory.Create(song, id);
        float[] pcm = new float[12];
        plan.Source.Render(pcm.Length, pcm);
        Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(1.0f).Within(1e-5f),
            "the outer event filter is applied to either terminal source");
    }

    [Test]
    public void DataPatternEventFilterIdPersistsAndResolvesDuringProductionPlayback()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        FilterInstance filter = Echo(song, 0.001);
        ObjectId id = song.AllocateObjectId();
        DataPatternDefinition pattern = new(id, "Data event")
        { RowCount = 3, ChannelCount = 1 };
        pattern.Grid.GetOrCreateCell(0, 0).Note =
            new StartPatternNote(sample,
                eventFilterDefinitionId: filter.DefinitionId);
        song.Add(pattern);
        string json = Heresy.Core.Persistence.SongDocumentJson.SerializePatternNote(
            pattern.Grid[0, 0]!.Note!);
        StartPatternNote roundtrip =
            (StartPatternNote)Heresy.Core.Persistence.SongDocumentJson.DeserializePatternNote(json);
        Assert.That(roundtrip.EventFilterDefinitionId,
            Is.EqualTo(filter.DefinitionId));
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, id);
        float[] pcm = new float[8];
        plan.Source.Render(pcm.Length, pcm);
        Assert.That(pcm[0], Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(pcm[1], Is.EqualTo(1.0f).Within(1e-5f));
    }

    [Test]
    public void UnknownScriptNoteFilterFailsExplicitlyRatherThanSilentlyBypassing()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId id = ScriptPattern(song,
            $"Note(0, 0, _O({sample.Value}), eventFilterDefinitionId: _O(987654));");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, id);
        Assert.Throws<InvalidOperationException>(() =>
            plan.Source.Render(4, new float[4]));
    }

    private static FilterInstance Echo(SongDocument song, double delay)
    {
        ObjectId filterId = song.AllocateObjectId();
        FilterDefinition definition = new(filterId, "Tone echo", [
            new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
        ]);
        song.Add(definition);
        FilterInstance instance = new(definition);
        instance.SetStageParameters(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(delay)),
            new("Feedforward", FilterParameterValue.FromNumber(1)),
            new("Mix", FilterParameterValue.FromNumber(1)),
        ]);
        return instance;
    }

    private static NodeEnvelopeDefinition HalfEnvelope(ObjectId id)
    {
        NodeEnvelopeDefinition envelope = new(id, "Half");
        envelope.ReplaceCurve([new EnvelopeNode(TimeSpan.Zero, 0.5)],
            0, 0, EnvelopeSustainLoopMode.Forward);
        return envelope;
    }

    [Test]
    public void ScriptSelectedNestedPatternRendersWithInheritedEnvelopeAndChunkParity()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId child = ScriptPattern(song, $"Note(0, 0, _O({sample.Value}));");
        ObjectId envelope = song.AllocateObjectId();
        song.Add(HalfEnvelope(envelope));
        ObjectId inst = song.AllocateObjectId();
        ScriptInstrumentDefinition definition = new(inst, "Recursive selector")
        { Source = "pitchMultiplier >= 1.0 ? 0 : -1" };
        definition.ToneSpecifications.Add(new ToneSpecification
        { SourceId = child, VolumeEnvelopeId = envelope });
        song.Add(definition);
        ObjectId root = ScriptPattern(song, $"Note(0, 0, _O({inst.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan whole = factory.Create(song, root);
        using PreparedIncrementalPlaybackPlan chunked = factory.Create(song, root);
        float[] first = new float[70], second = new float[70];
        whole.Source.Render(first.Length, first);
        for (int pos = 0; pos < second.Length;)
        {
            int count = Math.Min(second.Length - pos, 3);
            chunked.Source.Render(count, second.AsSpan(pos, count));
            pos += count;
        }
        Assert.Multiple(() =>
        {
            Assert.That(first[5], Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(second, Is.EqualTo(first).Within(1e-6f));
        });
    }

    [Test]
    public void ScriptInstrumentRejectsSelectedSelfCycle()
    {
        SongDocument song = new();
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(id, "Cycle")
        { Source = "0" };
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = id });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song, $"Note(0, 0, _O({id.Value}));");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        Assert.Throws<InvalidOperationException>(() =>
            plan.Source.Render(2, new float[2]));
    }

    [Test]
    public void DynamicPitchExpressionModulatesActualSamplePlaybackFrequency()
    {
        SongDocument song = new();
        ObjectId sample = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(
            sample, "Ramp", "ramp.wav", RampWave()));
        ObjectId fixedId = song.AllocateObjectId();
        ScriptInstrumentDefinition fixedInstrument = new(fixedId, "Fixed")
        { Source = "0", PitchExpression = "1.0" };
        fixedInstrument.ToneSpecifications.Add(new ToneSpecification { SourceId = sample });
        song.Add(fixedInstrument);
        ObjectId dynamicId = song.AllocateObjectId();
        ScriptInstrumentDefinition dynamicInstrument = new(dynamicId, "Double")
        { Source = "0", PitchExpression = "pitchMultiplier * 2.0" };
        dynamicInstrument.ToneSpecifications.Add(new ToneSpecification { SourceId = sample });
        song.Add(dynamicInstrument);
        ObjectId fixedRoot = ScriptPattern(song,
            $"Note(0, 0, _O({fixedId.Value}));");
        ObjectId dynamicRoot = ScriptPattern(song,
            $"Note(0, 0, _O({dynamicId.Value}));");
        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan ordinary = factory.Create(song, fixedRoot);
        using PreparedIncrementalPlaybackPlan shifted = factory.Create(song, dynamicRoot);
        float[] plain = new float[30];
        float[] pitched = new float[30];
        ordinary.Source.Render(30, plain);
        shifted.Source.Render(30, pitched);
        Assert.Multiple(() =>
        {
            Assert.That(plain[10], Is.GreaterThan(0.0f));
            Assert.That(pitched[10], Is.EqualTo(plain[10] * 2.0f).Within(1e-5f));
            Assert.That(pitched[20], Is.EqualTo(plain[20] * 2.0f).Within(1e-5f));
        });
    }

    [Test]
    public void NonPositiveDynamicPitchScaleFailsBeforeBindingSound()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(id, "Bad pitch")
        { Source = "0", PitchExpression = "0.0" };
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = sample });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song, $"Note(0, 0, _O({id.Value}));");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        InvalidOperationException? error = Assert.Throws<InvalidOperationException>(
            () => plan.Source.Render(2, new float[2]));
        Assert.That(error!.Message, Does.Contain("invalid pitch multiplier"));
    }

    [Test]
    public void InvalidIndexFailsExplicitlyAndNotAsAnArbitrarySource()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId id = song.AllocateObjectId();
        ScriptInstrumentDefinition instrument = new(id, "Bad index")
        { Source = "7" };
        instrument.ToneSpecifications.Add(new ToneSpecification { SourceId = sample });
        song.Add(instrument);
        ObjectId root = ScriptPattern(song, $"Note(0, 0, _O({id.Value}));");
        using PreparedIncrementalPlaybackPlan plan =
            new PreparedIncrementalPlaybackFactory(Mono()).Create(song, root);
        InvalidOperationException? error = Assert.Throws<InvalidOperationException>(
            () => plan.Source.Render(2, new float[2]));
        Assert.That(error!.Message, Does.Contain("invalid tone index"));
    }

    private static ObjectId ScriptPattern(SongDocument song, string source)
    {
        ObjectId id = song.AllocateObjectId();
        song.Add(new ScriptPatternDefinition(id, "Notes")
        { Source = source, RowCount = 3, ChannelCount = 1 });
        return id;
    }

    private static ObjectId Sample(SongDocument song, short amplitude)
    {
        ObjectId id = song.AllocateObjectId();
        song.Add(SampleDefinition.CreateImported(id, "PCM", "sample.wav",
            Wave(amplitude)));
        return id;
    }

    private static RenderConfiguration Mono()
        => new(1000, [new OutputChannelConfiguration(
            Vector3.Zero, positionalImportance: 0)]);

    private static byte[] RampWave()
    {
        const int frames = 256;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 2 * frames);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(1000); writer.Write(2000);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(2 * frames);
        for (int i = 0; i < frames; i++) writer.Write((short)(i * 100));
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Wave(short amplitude)
    {
        const int frames = 1000;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 2 * frames);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(1000); writer.Write(2000);
        writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(2 * frames);
        for (int i = 0; i < frames; i++) writer.Write(amplitude);
        writer.Flush();
        return stream.ToArray();
    }
}

using System;
using System.IO;
using System.Numerics;
using System.Text;

using Heresy.Core.Envelopes;
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

    [Test]
    public void ScriptSelectedNestedPatternRendersWithInheritedEnvelopeAndChunkParity()
    {
        SongDocument song = new();
        ObjectId sample = Sample(song, 16384);
        ObjectId child = ScriptPattern(song, $"Note(0, 0, _O({sample.Value}));");
        ObjectId envelope = song.AllocateObjectId();
        song.Add(new AdsrEnvelopeDefinition(envelope, "Half") { SustainLevel = 0.5 });
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

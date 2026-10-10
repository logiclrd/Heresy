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

[TestFixture]
public sealed class DirectScriptPlaybackControlTests
{
    [Test]
    public void ScriptFiltersAndSpatialControlsReachProductionPcmAtTheirActualFrames()
    {
        SongDocument document = new();
        ObjectId sample = document.AllocateObjectId();
        document.Add(SampleDefinition.CreateImported(
            sample, "Alternating PCM", "ramp.wav", AlternatingWave()));
        ObjectId root = document.AllocateObjectId();
        ScriptPatternDefinition pattern = new(root, "Live controls")
        {
            RowCount = 3, ChannelCount = 1,
            Source = $"""
                Note(0, 0, _O({sample.Value}));
                Pan(0.25, 0, -0.5);
                Filter(0.5, 0, 0.15, 0.4);
                FilterCutoff(0.75, 0, 0.25);
                FilterResonance(1, 0, 0.6);
                Seek(1.25, 0, 0.055);
                Surround(1.5, 0, true);
                """,
        };
        document.Add(pattern);

        PreparedIncrementalPlaybackFactory factory = new(Mono());
        using PreparedIncrementalPlaybackPlan allAtOnce =
            factory.Create(document, root);
        using PreparedIncrementalPlaybackPlan chunked =
            factory.Create(document, root);
        const int frames = 205;
        float[] continuous = new float[frames];
        allAtOnce.Source.Render(frames, continuous);
        float[] partial = new float[frames];
        int[] sizes = [1, 7, 13, 3, 53];
        // Pan occurs at frame 30; inspect it before S91-style surround
        // intentionally recenters the channel at frame 180.
        chunked.Source.Render(45, partial.AsSpan(0, 45));
        bool sawPanning = chunked.Session.GetChannelState(0).Position
            == new Vector3(-0.5f, 0, 0);
        for (int i = 45, block = 0; i < frames; block++)
        {
            int count = Math.Min(sizes[block % sizes.Length], frames - i);
            chunked.Source.Render(count, partial.AsSpan(i, count));
            i += count;
        }

        ScriptPatternDefinition plain = new(root, "Unfiltered")
        {
            RowCount = 3, ChannelCount = 1,
            Source = $"Note(0, 0, _O({sample.Value}));",
        };
        pattern.Source = plain.Source;
        using PreparedIncrementalPlaybackPlan unfiltered =
            factory.Create(document, root);
        float[] baseline = new float[frames];
        unfiltered.Source.Render(frames, baseline);

        var channel = chunked.Session.GetChannelState(0);
        Assert.Multiple(() =>
        {
            Assert.That(partial, Is.EqualTo(continuous).Within(1e-6f),
                "Direct script controls must not depend on audio block size.");
            Assert.That(partial.Take(25).ToArray(),
                Is.EqualTo(baseline.Take(25).ToArray()).Within(1e-6f),
                "No script effects have run before frame 30.");
            Assert.That(Math.Abs(partial[100] - baseline[100]),
                Is.GreaterThan(1e-3f),
                "The resonant filter must change audible output after its deadline.");
            Assert.That(sawPanning, Is.True,
                "Pan should take effect before the surround command.");
            Assert.That(channel.Position, Is.EqualTo(Vector3.Zero),
                "Surround intentionally recenters the channel.");
            Assert.That(channel.Surround, Is.True);
            Assert.That(channel.FilterParameters.Cutoff,
                Is.EqualTo(0.25).Within(1e-10));
            Assert.That(channel.FilterParameters.Resonance,
                Is.EqualTo(0.6).Within(1e-10));
            Assert.That(channel.CurrentVoice?.SoundState.PlaybackOffset,
                Is.EqualTo(TimeSpan.FromMilliseconds(55)));
        });
    }

    private static RenderConfiguration Mono()
        => new(1000, [new OutputChannelConfiguration(Vector3.Zero,
            positionalImportance: 0)]);

    private static byte[] RampWave()
    {
        const int frames = 500;
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
            writer.Write((short)(i % 2 == 0 ? 16000 : -16000));
        writer.Flush();
        return stream.ToArray();
    }
}

using System;
using System.Numerics;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class EditorPatternPlaybackTraceTests
{
    [Test]
    public void PatternRequestDeliversDueActionButOrdinaryRequestHasNoTrace()
    {
        SongDocument song = new();
        ScriptPatternDefinition script = new((ObjectId)5U, "Actions")
        {
            RowCount = 3, ChannelCount = 1,
            Source = "ChannelFilter(0, 0); ChannelFilter(1, 0);",
        };
        song.Add(script);
        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        Guid session = Guid.NewGuid();
        PatternPlaybackRequest trace = PatternPlaybackRequest.Create(
            song, script.Id) with { EditorCallsiteSession = session };

        PlaybackPatternPosition? Run(PatternPlaybackRequest request)
        {
            IAudioOutputSource source = factory.Create(request);
            try
            {
                Assert.That(factory.TryTakePlaybackPositionTimeline(
                    request, out PlaybackPositionTimeline? timeline), Is.True);
                float[] samples = new float[512];
                source.Render(samples.Length, samples);
                return timeline!.GetPositionAt(TimeSpan.Zero);
            }
            finally { (source as IDisposable)?.Dispose(); }
        }

        PlaybackPatternPosition? active = Run(trace);
        Assert.That(active, Is.Not.Null);
        Assert.That(active!.PatternId, Is.EqualTo(script.Id));
        Assert.That(active.EditorCallsiteSession, Is.EqualTo(session));
        Assert.That(active.EditorPatternCallsite,
            Is.EqualTo(new SequenceCallsite(0, "ChannelFilter(0, 0)".Length)));
        Assert.That(active.EditorPlayCallsite, Is.Null);

        PlaybackPatternPosition? ordinary = Run(
            PatternPlaybackRequest.Create(song, script.Id));
        Assert.That(ordinary, Is.Not.Null);
        Assert.That(ordinary!.PatternId, Is.EqualTo(script.Id));
        Assert.That(ordinary.EditorCallsiteSession, Is.Null);
        Assert.That(ordinary.EditorPatternCallsite, Is.Null);
    }
}

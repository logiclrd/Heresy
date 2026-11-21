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
public sealed class EditorSequencePlaybackTraceTests
{
    [Test]
    public void SourceFactoryDeliversPlayProvenanceOnlyForEditorTraceRequest()
    {
        SongDocument document = new();
        DataPatternDefinition pattern = new((ObjectId)17U, "Pattern")
        {
            RowCount = 2,
            ChannelCount = 1,
        };
        ScriptSequenceDefinition sequence = new((ObjectId)5U, "Script")
        {
            Source = "if (sequenceIndex == 0) return Play(_O(17)); return null;",
        };
        document.Add(pattern);
        document.Add(sequence);

        RenderConfiguration config = new(48000, new[]
        {
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0.0),
        });
        PlaybackRequestAudioSourceFactory factory = new(config);
        Guid session = Guid.NewGuid();
        SequencePlaybackRequest traceRequest =
            SequencePlaybackRequest.Create(document, sequence.Id) with
            {
                EditorCallsiteSession = session,
            };

        PlaybackPatternPosition? Run(SequencePlaybackRequest request)
        {
            IAudioOutputSource audio = factory.Create(request);
            try
            {
                Assert.That(factory.TryTakePlaybackPositionTimeline(
                    request, out PlaybackPositionTimeline? timeline), Is.True);
                Assert.That(timeline, Is.Not.Null);
                float[] samples = new float[512];
                audio.Render(samples.Length, samples);
                return timeline!.GetPositionAt(TimeSpan.Zero);
            }
            finally
            {
                (audio as IDisposable)?.Dispose();
            }
        }

        PlaybackPatternPosition? traced = Run(traceRequest);
        Assert.That(traced, Is.Not.Null);
        Assert.That(traced!.SequenceId, Is.EqualTo(sequence.Id));
        Assert.That(traced.SequenceEntryIndex, Is.Zero);
        Assert.That(traced.EditorCallsiteSession, Is.EqualTo(session));
        Assert.That(traced.EditorPlayCallsite,
            Is.EqualTo(new SequenceCallsite(
                sequence.Source.IndexOf("Play(", StringComparison.Ordinal),
                "Play(_O(17))".Length)));

        PlaybackPatternPosition? ordinary = Run(
            SequencePlaybackRequest.Create(document, sequence.Id));
        Assert.That(ordinary, Is.Not.Null);
        Assert.That(ordinary!.PatternId, Is.EqualTo(traced.PatternId));
        Assert.That(ordinary.PatternRow, Is.EqualTo(traced.PatternRow));
        Assert.That(ordinary.SequenceId, Is.EqualTo(traced.SequenceId));
        Assert.That(ordinary.EditorCallsiteSession, Is.Null);
        Assert.That(ordinary.EditorPlayCallsite, Is.Null);
    }
}

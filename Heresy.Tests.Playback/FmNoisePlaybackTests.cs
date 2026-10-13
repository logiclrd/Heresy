using System;
using System.Linq;
using System.Numerics;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Playback;
using Heresy.Render.Configuration;
using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class FmNoisePlaybackTests
{
    [Test]
    public void SimultaneousNoiseVoicesAreDistinctAndWholeSplitPatternRenderingAgrees()
    {
        SongDocument song = new();
        ObjectId synthId = song.AllocateObjectId();
        song.Add(new FmSynthDefinition(synthId, "White",
            new FmSynthGraph([new FmNoiseNode(0)], 0)));
        ObjectId singleId = song.AllocateObjectId();
        DataPatternDefinition single = new(singleId, "One")
        {
            RowCount = 1, ChannelCount = 1,
        };
        PatternCell first = single.Grid.GetOrCreateCell(0, 0);
        first.SourceId = synthId;
        first.Note = new StartPatternNote();
        song.Add(single);
        ObjectId doubleId = song.AllocateObjectId();
        DataPatternDefinition twice = new(doubleId, "Two")
        {
            RowCount = 1, ChannelCount = 2,
        };
        foreach (int channel in new[] { 0, 1 })
        {
            PatternCell cell = twice.Grid.GetOrCreateCell(0, channel);
            cell.SourceId = synthId;
            cell.Note = new StartPatternNote();
        }
        song.Add(twice);
        RenderConfiguration configuration = new(8000, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]);
        PreparedIncrementalPlaybackFactory factory = new(configuration);
        using PreparedIncrementalPlaybackPlan solo = factory.Create(song, singleId);
        using PreparedIncrementalPlaybackPlan whole = factory.Create(song, doubleId);
        using PreparedIncrementalPlaybackPlan split = factory.Create(song, doubleId);
        float[] one = new float[300], two = new float[300], segments = new float[300];
        solo.Source.Render(300, one);
        whole.Source.Render(300, two);
        split.Source.Render(17, segments.AsSpan(0, 17));
        split.Source.Render(283, segments.AsSpan(17, 283));
        Assert.Multiple(() =>
        {
            Assert.That(segments, Is.EqualTo(two));
            Assert.That(two.Zip(one, (both, a) => both - 2 * a)
                .Any(value => Math.Abs(value) > 0.1f), Is.True,
                "two simultaneous identical pitches must not reuse the same noise sequence");
            Assert.That(two.Take(300).Any(value => Math.Abs(value) > 0.05f), Is.True);
        });
    }
}

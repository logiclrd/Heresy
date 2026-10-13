using System;
using System.Collections.Generic;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PreparedToneFilterStageFactoryTests
{
    [Test]
    public void FrozenPlansReuseIdentityButGiveEachVoiceIndependentHistory()
    {
        FilterDefinition definition = new((ObjectId)11U, "FIR", [
            new FilterStageDefinition(1, "lp", FilterKind.FirLowPass),
        ]);
        FilterInstance tone = new(definition);
        tone.SetStageParameter(1, "TapCount",
            FilterParameterValue.FromNumber(65));
        PreparedToneFilterStageFactory plans = new(48000);
        plans.Prewarm(tone);
        plans.Prewarm(tone); // no duplicate plan, no additional FFT design
        plans.Freeze();

        FilterChain chain = new([tone]);
        StreamingFilterChain first = new(chain, 48000, 1, plans);
        StreamingFilterChain second = new(chain, 48000, 1, plans);
        Assert.Multiple(() =>
        {
            Assert.That(first.PreparedTailFrames, Is.EqualTo(64));
            Assert.That(second.PreparedTailFrames, Is.EqualTo(64));
            Assert.That(first.PreparedLatencyFrames, Is.EqualTo(32));
        });
        float[] a = new float[96];
        a[0] = 1;
        first.ProcessInterleaved(a);
        float[] b = new float[96];
        b[0] = 1;
        second.ProcessInterleaved(b);
        Assert.That(b, Is.EqualTo(a));

        // The second invocation must reproduce the same impulse from
        // a fresh convolution ring, rather than share the first's history.
        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void FrozenFactoryRejectsUnexpectedScriptedValuesAndOtherSampleRates()
    {
        FilterDefinition definition = new((ObjectId)12U, "Filtered");
        FilterInstance original = new(definition);
        original.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(900));
        PreparedToneFilterStageFactory plans = new(48000);
        plans.Prewarm(original);
        plans.Freeze();

        FilterInstance changed = original.Clone();
        changed.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(2500));
        Assert.Multiple(() =>
        {
            Assert.That(() => new StreamingFilterChain(
                new FilterChain([changed]), 48000, 1, plans),
                Throws.TypeOf<NotSupportedException>().With.Message
                    .Contains("not prepared"));
            Assert.That(() => new StreamingFilterChain(
                new FilterChain([original]), 44100, 1, plans),
                Throws.InvalidOperationException.With.Message
                    .Contains("sample rate"));
            Assert.That(() => plans.Prewarm(original),
                Throws.InvalidOperationException.With.Message
                    .Contains("already frozen"));
        });
    }

    [Test]
    public void RepeatedFilterOnNestedLayersUsesPreparedKernelWithoutSharingStates()
    {
        FilterDefinition definition = new((ObjectId)13U, "Double");
        FilterInstance instance = new(definition);
        PreparedToneFilterStageFactory plans = new(48000);
        plans.Prewarm(instance);
        plans.Freeze();
        FilterChain twice = new([instance, instance.Clone()]);
        StreamingFilterChain chain = new(twice, 48000, 1, plans);
        StreamingFilterChain reference = new(twice, 48000, 1);
        float[] a = [1, 1, 1, 1, 1, 1], b = [1, 1, 1, 1, 1, 1];
        chain.ProcessInterleaved(a);
        reference.ProcessInterleaved(b);
        Assert.That(a, Is.EqualTo(b));
    }
}

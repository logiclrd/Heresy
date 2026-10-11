using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FeedbackCombFilterTests
{
	private const int SampleRate = 48000;

	[Test]
	public void ImpulseHasExactDryWetDelayAndGeometricPositiveFeedbackEchoes()
	{
		const int delay = 48; // 0.001 seconds at 48 kHz.
		StreamingFilterChain filter = Runtime(CreateUse(0.001, 0.5, 0.25));
		float[] pcm = new float[delay * 5 + 1];
		pcm[0] = 1;
		filter.ProcessInterleaved(pcm);

		Assert.That(pcm[0], Is.EqualTo(0.75f));
		for (int k = 1; k <= 5; k++)
		{
			Assert.That(pcm[k * delay],
				Is.EqualTo((float)(0.25 * Math.Pow(0.5, k - 1)))
					.Within(1e-7), $"echo #{k}");
			if (k < 5)
				Assert.That(pcm.Skip(k * delay + 1).Take(delay - 1),
					Is.All.Zero, $"no extra echo frames after echo #{k}");
		}
		Assert.That(pcm.Skip(1).Take(delay - 1), Is.All.Zero);
	}

	[Test]
	public void NegativeFeedbackAlternatesPolarityAndPureWetIsARealDelay()
	{
		const int delay = 480; // 0.010 seconds.
		StreamingFilterChain negative = Runtime(CreateUse(0.01, -0.5, 1));
		float[] pcm = new float[delay * 4 + 1];
		pcm[0] = 1;
		negative.ProcessInterleaved(pcm);
		Assert.That(pcm[0], Is.Zero);
		Assert.That(pcm[delay], Is.EqualTo(1));
		Assert.That(pcm[delay * 2], Is.EqualTo(-0.5));
		Assert.That(pcm[delay * 3], Is.EqualTo(0.25));
		Assert.That(pcm[delay * 4], Is.EqualTo(-0.125));

		StreamingFilterChain noFeedback = Runtime(CreateUse(0.01, 0, 1));
		float[] single = new float[delay * 3];
		single[0] = 1;
		noFeedback.ProcessInterleaved(single);
		Assert.That(single[0], Is.Zero);
		Assert.That(single[delay], Is.EqualTo(1));
		Assert.That(single.Count(x => x != 0), Is.EqualTo(1));
		Assert.That(noFeedback.PreparedTailFrames, Is.EqualTo(delay),
			"Zero feedback still has one delayed wet echo.");
	}

	[Test]
	public void ZeroMixIsAudiblyDryAndNeedsNoTrailingWork()
	{
		StreamingFilterChain dry = Runtime(CreateUse(0.02, 0.95, 0));
		float[] source = { 1f, 0.5f, -0.2f, 0f };
		float[] result = (float[])source.Clone();
		dry.ProcessInterleaved(result);
		Assert.That(result, Is.EqualTo(source));
		Assert.That(dry.PreparedTailFrames, Is.Zero);
		dry.FinishInput();
		float[] tail = { 3f, 4f };
		Assert.That(dry.RenderTail(tail), Is.Zero);
		Assert.That(tail, Is.EqualTo(new float[2]));
	}

	[Test]
	public void RoundingToDiscreteFramesIsDeterministic()
	{
		// 0.0015 sec at 1000 Hz = 1.5 frames -> round to 2,
		// midway away from zero. This is structural even when another
		// nearby authored delay happens to round to the same count.
		StreamingFilterChain runtime =
			new(new FilterChain(new[] { CreateUse(0.0015, 0, 1) }), 1000, 1);
		float[] samples = { 1f, 0f, 0f, 0f };
		runtime.ProcessInterleaved(samples);
		Assert.That(samples, Is.EqualTo(new[] { 0f, 0f, 1f, 0f }));
		Assert.That(runtime.PreparedTailFrames, Is.EqualTo(2));

		StreamingFilterChain shortest =
			new(new FilterChain(new[] { CreateUse(0.001, 0, 1) }), 1000, 1);
		float[] one = { 1f, 0f, 0f };
		shortest.ProcessInterleaved(one);
		Assert.That(one, Is.EqualTo(new[] { 0f, 1f, 0f }));
	}

	[Test]
	public void CombHasPeaksAndNotchesAtExpectedHarmonics()
	{
		// For full wet, H(e^jw)=z^-D/(1-f*z^-D).
		// At the 100 Hz comb fundamental the delay phase is +1,
		// and at 50 Hz it is -1.
		double positivePeak = Magnitude(0.01, 0.6, 1, 100);
		double positiveDip = Magnitude(0.01, 0.6, 1, 50);
		double negativePeak = Magnitude(0.01, -0.6, 1, 50);
		double negativeDip = Magnitude(0.01, -0.6, 1, 100);
		Assert.That(positivePeak, Is.EqualTo(2.5).Within(0.02));
		Assert.That(positiveDip, Is.EqualTo(0.625).Within(0.01));
		Assert.That(negativePeak, Is.EqualTo(2.5).Within(0.02));
		Assert.That(negativeDip, Is.EqualTo(0.625).Within(0.01));

		double equalWetDry = Magnitude(0.01, 0.6, 0.5, 100);
		Assert.That(equalWetDry, Is.EqualTo(1.75).Within(0.02));
	}

	[Test]
	public void StructuralDelayEditsDiscardTheOldEchoRing()
	{
		FilterInstance instance = CreateUse(0.001, 0.7, 1);
		StreamingFilterChain running = Runtime(instance);
		running.ProcessFrame(new float[] { 1 });
		instance.SetStageParameter(1, "DelaySeconds",
			FilterParameterValue.FromNumber(0.002));
		running.Configure(new FilterChain(new[] { instance }));
		float[] silence = new float[48 * 3];
		running.ProcessInterleaved(silence);
		Assert.That(silence, Is.All.Zero,
			"Delayed echoes from the prior structural configuration must vanish.");

		float[] newImpulse = new float[97];
		newImpulse[0] = 1;
		running.ProcessInterleaved(newImpulse);
		Assert.That(newImpulse[0], Is.Zero);
		Assert.That(newImpulse[48], Is.Zero);
		Assert.That(newImpulse[96], Is.EqualTo(1));
	}

	[Test]
	public void NumericFeedbackAndMixChangesKeepEchoHistoryAndSmoothFor32Frames()
	{
		FilterInstance instance = CreateUse(0.001, 0.5, 1);
		StreamingFilterChain running = Runtime(instance);
		StreamingFilterChain unchanged = Runtime(instance);
		running.ProcessFrame(new float[] { 1 });
		unchanged.ProcessFrame(new float[] { 1 });

		instance.SetStageParameters(1, new[] {
			Pair("Feedback", -0.7),
			Pair("Mix", 0.75),
		});
		running.Configure(new FilterChain(new[] { instance }));
		float[] updated = new float[48 * 4];
		float[] prior = new float[updated.Length];
		running.ProcessInterleaved(updated);
		unchanged.ProcessInterleaved(prior);
		// The ring still contains the original excitation. Its first
		// wet echo is at the old delay, not 48 frames after a reset.
		Assert.That(updated[47], Is.EqualTo(0.75).Within(1e-6));
		Assert.That(prior[47], Is.EqualTo(1));
		Assert.That(updated[95], Is.LessThan(0),
			"After a full ramp, feedback polarity must affect echoes.");
		Assert.That(updated.Skip(1).Any(x => x != 0), Is.True);
		Assert.That(updated.All(float.IsFinite), Is.True);
	}

	[Test]
	public void AtomicInvalidSecondStageLeavesFirstStageUntouched()
	{
		// A very high sample rate is legal, but the stage must refuse
		// delays exceeding its fixed 1M-frame/channel memory budget.
		const int rate = 1_000_000;
		FilterInstance valid = CreateUse(0.001, 0.5, 1);
		StreamingFilterChain active = new(
			new FilterChain(new[] { valid, valid }), rate, 1);
		float[] original = new float[20];
		original[0] = 1;
		active.ProcessInterleaved(original);
		StreamingFilterChain fork = active.Fork();
		FilterInstance changed = valid.Clone();
		changed.SetStageParameter(1, "Feedback",
			FilterParameterValue.FromNumber(-0.75));
		FilterInstance tooLong = valid.Clone();
		tooLong.SetStageParameter(1, "DelaySeconds",
			FilterParameterValue.FromNumber(2));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			active.Configure(new FilterChain(new[] { changed, tooLong })));
		float[] followUp = new float[2100];
		followUp[0] = -0.3f;
		float[] expected = (float[])followUp.Clone();
		active.ProcessInterleaved(followUp);
		fork.ProcessInterleaved(expected);
		Assert.That(followUp, Is.EqualTo(expected),
			"No stage or channel state may change on failed preparation.");
	}

	[Test]
	public void ExtremeLegalFeedbackRingsDownAndTailsAreFinite()
	{
		foreach (double feedback in new[] { -0.95, 0, 0.95 })
		{
			StreamingFilterChain filter = Runtime(CreateUse(
				0.001, feedback, 1), maximumTailSeconds: 0.08);
			Assert.That(filter.PreparedTailFrames,
				Is.GreaterThan(0).And.LessThanOrEqualTo(3840));
			filter.ProcessFrame(new float[] { 1 });
			filter.FinishInput();
			int budget = filter.PreparedTailFrames;
			float[] tail = new float[budget + 4];
			Assert.That(filter.RenderTail(tail), Is.EqualTo(budget));
			Assert.That(tail.Skip(budget), Is.All.Zero);
			Assert.That(tail.All(x => float.IsFinite(x)
				&& (x == 0 || Math.Abs(x) >= 1e-20f)), Is.True);
			Assert.That(filter.TailRemainingFrames, Is.Zero);
			float[] more = { 1f, 2f };
			Assert.That(filter.RenderTail(more), Is.Zero);
			Assert.That(more, Is.EqualTo(new[] { 0f, 0f }));
		}
	}

	[Test]
	public void ImpulseTailIncludesFirstEchoAndFollowsExactFeedbackPowers()
	{
		StreamingFilterChain filter = Runtime(CreateUse(0.001, 0, 1),
			maximumTailSeconds: 0.1);
		filter.ProcessFrame(new float[] { 1 });
		filter.FinishInput();
		float[] tail = new float[48];
		Assert.That(filter.RenderTail(tail), Is.EqualTo(48));
		Assert.That(tail.Take(47), Is.All.Zero);
		Assert.That(tail[47], Is.EqualTo(1));
		Assert.That(filter.TailRemainingFrames, Is.Zero);

		StreamingFilterChain repeating = Runtime(CreateUse(0.001, 0.5, 1),
			maximumTailSeconds: 0.004);
		repeating.ProcessFrame(new float[] { 1 });
		repeating.FinishInput();
		float[] echoes = new float[192];
		Assert.That(repeating.RenderTail(echoes), Is.EqualTo(192));
		Assert.That(echoes[47], Is.EqualTo(1));
		Assert.That(echoes[95], Is.EqualTo(0.5));
		Assert.That(echoes[143], Is.EqualTo(0.25));
		Assert.That(echoes[191], Is.EqualTo(0.125));
	}

	[Test]
	public void RapidRetuningIsDeterministicAcrossStereoBlockPartitions()
	{
		FilterInstance instance = CreateUse(0.003, 0.4, 0.5);
		StreamingFilterChain blocks = Runtime(instance, channels: 2);
		StreamingFilterChain portions = Runtime(instance, channels: 2);
		for (int batch = 0; batch < 380; batch++)
		{
			double gain = batch % 13 == 0 ? -0.95
				: batch % 17 == 0 ? 0.95
				: (batch % 191) / 100.0 - 0.95;
			double mix = (batch % 101) / 100.0;
			instance.SetStageParameters(1, new[] {
				Pair("Feedback", gain),
				Pair("Mix", mix),
			});
			FilterChain updated = new(new[] { instance });
			blocks.Configure(updated);
			portions.Configure(updated);
			float[] a = new float[19 * 2];
			for (int n = 0; n < 19; n++)
			{
				a[n * 2] = (float)(0.3 * Math.Sin(
					2 * Math.PI * (batch * 19 + n) * 1737 / SampleRate));
				a[n * 2 + 1] = batch % 2 == 0 ? 0.15f : -0.2f;
			}
			float[] b = (float[])a.Clone();
			blocks.ProcessInterleaved(a);
			portions.ProcessInterleaved(b.AsSpan(0, 14));
			portions.ProcessInterleaved(b.AsSpan(14, 24));
			Assert.That(a, Is.EqualTo(b), $"batch {batch}");
			Assert.That(a.All(float.IsFinite), Is.True);
			Assert.That(a.All(x => Math.Abs(x) < 100f), Is.True);
		}
	}

	[Test]
	public void ForkCopiesRingPositionsEchoesAndInProgressGainTransitions()
	{
		FilterInstance instance = CreateUse(0.003, -0.5, 0.4);
		StreamingFilterChain original = Runtime(instance, channels: 3);
		float[] intro = Enumerable.Range(0, 3 * 390)
			.Select(i => (float)(0.4 * Math.Sin(
				2 * Math.PI * (i / 3) * (800 + (i % 3) * 1030) / SampleRate)))
			.ToArray();
		float[] rendered = (float[])intro.Clone();
		original.ProcessInterleaved(rendered);
		instance.SetStageParameters(1, new[] {
			Pair("Feedback", 0.85),
			Pair("Mix", 0.9),
		});
		original.Configure(new FilterChain(new[] { instance }));
		original.ProcessInterleaved(new float[3 * 7]);
		StreamingFilterChain fork = original.Fork();
		float[] suffix = Enumerable.Range(0, 3 * 300)
			.Select(i => (float)(0.2 * Math.Cos(
				2 * Math.PI * (i / 3) * (550 + (i % 3) * 1300) / SampleRate)))
			.ToArray();
		float[] forked = (float[])suffix.Clone();
		original.ProcessInterleaved(suffix);
		fork.ProcessInterleaved(forked);
		Assert.That(suffix, Is.EqualTo(forked),
			"Deep-clone must include ring, position, and in-flight ramp.");

		fork.Reset();
		fork.Configure(new FilterChain(new[] { CreateUse(0.003, -0.5, 0.4) }));
		fork.Reset();
		float[] replay = (float[])intro.Clone();
		fork.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(rendered));
	}

	[Test]
	public void IndependentChannelsAndInvocationsNeverShareEchoBuffers()
	{
		FilterInstance use = CreateUse(0.001, 0.5, 1);
		StreamingFilterChain first = Runtime(use, channels: 2);
		StreamingFilterChain second = Runtime(use, channels: 2);
		float[] frames = new float[49 * 2];
		frames[0] = 1;
		first.ProcessInterleaved(frames);
		Assert.That(frames[48 * 2], Is.EqualTo(1));
		Assert.That(frames[48 * 2 + 1], Is.Zero);
		float[] other = new float[49 * 2];
		other[1] = 1;
		second.ProcessInterleaved(other);
		Assert.That(other[48 * 2], Is.Zero);
		Assert.That(other[48 * 2 + 1], Is.EqualTo(1));

		float[] next = { 0f, 0f };
		first.ProcessFrame(next);
		Assert.That(next, Is.EqualTo(new[] { 0f, 0f }));
	}

	[Test]
	public void FullScaleAndNonfiniteInputCannotPoisonFeedbackRing()
	{
		StreamingFilterChain runtime = Runtime(CreateUse(0.001, -0.95, 1));
		float[] samples = Enumerable.Range(0, 400)
			.Select(i => i % 31 == 0 ? float.MaxValue
				: i % 37 == 0 ? -float.MaxValue
				: i % 41 == 0 ? float.NaN
				: i % 43 == 0 ? float.PositiveInfinity
				: (float)(0.8 * Math.Sin(
					2 * Math.PI * i * 2701 / SampleRate)))
			.ToArray();
		runtime.ProcessInterleaved(samples);
		Assert.That(samples.All(float.IsFinite), Is.True);
		runtime.Reset();
		runtime.ProcessFrame(new float[] { 1 });
		float[] decay = new float[6000];
		runtime.ProcessInterleaved(decay);
		Assert.That(decay.All(v => float.IsFinite(v)
			&& (v == 0 || Math.Abs(v) >= 1e-20f)), Is.True);
	}

	[Test]
	public void DelayLineMemoryLimitIsStrictAndTwoSecondsIsValidAt48k()
	{
		StreamingFilterChain supported = Runtime(CreateUse(2, 0, 1));
		Assert.That(supported.StageCount, Is.EqualTo(1));
		Assert.That(supported.PreparedTailFrames, Is.EqualTo(96000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] {
				CreateUse(2, 0.5, 1),
			}), 1_000_000, 1));
	}

	private static FilterInstance CreateUse(
		double seconds, double feedback, double mix)
	{
		FilterDefinition definition = new((ObjectId)82U, "Feedback Comb",
			new[] { new FilterStageDefinition(
				1, "echo", FilterKind.CombFeedback) });
		FilterInstance instance = new(definition);
		instance.SetStageParameters(1, new[] {
			Pair("DelaySeconds", seconds),
			Pair("Feedback", feedback),
			Pair("Mix", mix),
		});
		return instance;
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, double value)
		=> new(name, FilterParameterValue.FromNumber(value));

	private static StreamingFilterChain Runtime(
		FilterInstance use, int channels = 1, double maximumTailSeconds = 10)
		=> new(new FilterChain(new[] { use }), SampleRate, channels,
			maximumTailSeconds: maximumTailSeconds);

	private static double Magnitude(
		double seconds, double feedback, double mix, double frequency)
	{
		StreamingFilterChain runtime = Runtime(CreateUse(seconds, feedback, mix));
		const int warmup = 18000;
		const int measure = 8192;
		double before = 0;
		double after = 0;
		float[] frame = new float[1];
		for (int n = 0; n < warmup + measure; n++)
		{
			double angle = 2 * Math.PI * frequency * n / SampleRate;
			float sample = (float)Math.Sin(angle);
			frame[0] = sample;
			runtime.ProcessFrame(frame);
			if (n < warmup) continue;
			before += sample * sample;
			after += (double)frame[0] * frame[0];
		}
		return Math.Sqrt(after / before);
	}
}

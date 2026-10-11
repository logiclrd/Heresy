using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class SvfFilterTests
{
	private const int SampleRate = 48000;

	[TestCase(FilterKind.SvfLowPass)]
	[TestCase(FilterKind.SvfHighPass)]
	[TestCase(FilterKind.SvfBandPass)]
	[TestCase(FilterKind.SvfNotch)]
	public void EverySvfKindUsesRealStatefulDspAndHasFiniteDecayingImpulse(FilterKind kind)
	{
		FilterInstance use = CreateUse(kind, 3200, q: 0.9);
		StreamingFilterChain runtime = Runtime(use);
		float[] impulse = new float[4096];
		impulse[0] = 1f;
		runtime.ProcessInterleaved(impulse);
		Assert.That(runtime.StageCount, Is.EqualTo(1));
		Assert.That(runtime.PreparedTailFrames, Is.GreaterThan(0));
		Assert.That(impulse.All(float.IsFinite), Is.True);
		Assert.That(Math.Abs(impulse[0] - 1), Is.GreaterThan(1e-5),
			$"{kind} must not be silently dispatched to bypass.");
		Assert.That(Math.Abs(impulse[^1]), Is.LessThan(1e-6));
	}

	[Test]
	public void LowHighBandAndNotchHaveExpectedResponseAndExactComplements()
	{
		const double cutoff = 4000;
		const double q = 1 / Math.Sqrt(2);
		Assert.That(Magnitude(FilterKind.SvfLowPass, cutoff, 100, q),
			Is.InRange(0.98, 1.02));
		Assert.That(Magnitude(FilterKind.SvfLowPass, cutoff, 16000, q),
			Is.LessThan(0.025));
		Assert.That(Magnitude(FilterKind.SvfHighPass, cutoff, 100, q),
			Is.LessThan(0.002));
		Assert.That(Magnitude(FilterKind.SvfHighPass, cutoff, 16000, q),
			Is.InRange(0.98, 1.02));
		Assert.That(Magnitude(FilterKind.SvfLowPass, cutoff, cutoff, q),
			Is.EqualTo(1 / Math.Sqrt(2)).Within(0.01));
		Assert.That(Magnitude(FilterKind.SvfHighPass, cutoff, cutoff, q),
			Is.EqualTo(1 / Math.Sqrt(2)).Within(0.01));
		Assert.That(Magnitude(FilterKind.SvfBandPass, cutoff, cutoff, q),
			Is.EqualTo(1).Within(0.01),
			"SVF band-pass uses unity centre peak regardless of Q.");
		Assert.That(Magnitude(FilterKind.SvfBandPass, cutoff, 100, q),
			Is.LessThan(0.05));
		Assert.That(Magnitude(FilterKind.SvfNotch, cutoff, cutoff, q),
			Is.LessThan(0.001));
		Assert.That(Magnitude(FilterKind.SvfNotch, cutoff, 100, q),
			Is.InRange(0.98, 1.02));

		StreamingFilterChain low = Runtime(CreateUse(FilterKind.SvfLowPass, cutoff, q));
		StreamingFilterChain high = Runtime(CreateUse(FilterKind.SvfHighPass, cutoff, q));
		StreamingFilterChain band = Runtime(CreateUse(FilterKind.SvfBandPass, cutoff, q));
		StreamingFilterChain notch = Runtime(CreateUse(FilterKind.SvfNotch, cutoff, q));
		for (int frame = 0; frame < 256; frame++)
		{
			float source = (float)(0.65 * Math.Sin(
				2 * Math.PI * frame * 1501 / SampleRate)
				+ 0.25 * Math.Cos(2 * Math.PI * frame * 8911 / SampleRate));
			float[] lp = { source };
			float[] hp = { source };
			float[] bp = { source };
			float[] bs = { source };
			low.ProcessFrame(lp);
			high.ProcessFrame(hp);
			band.ProcessFrame(bp);
			notch.ProcessFrame(bs);
			Assert.That(lp[0] + hp[0] + bp[0],
				Is.EqualTo(source).Within(3e-7), $"sum {frame}");
			Assert.That(lp[0] + hp[0], Is.EqualTo(bs[0]).Within(3e-7),
				$"notch {frame}");
		}
	}

	[Test]
	public void EachSvfMatchesCorrespondingStaticBiquadButHasDifferentStateTopology()
	{
		foreach ((FilterKind svf, FilterKind biquad) in new[] {
			(FilterKind.SvfLowPass, FilterKind.BiquadLowPass),
			(FilterKind.SvfHighPass, FilterKind.BiquadHighPass),
			(FilterKind.SvfBandPass, FilterKind.BiquadBandPass),
			(FilterKind.SvfNotch, FilterKind.BiquadNotch),
		})
		{
			StreamingFilterChain tpt = Runtime(CreateUse(svf, 3700, q: 2.5));
			StreamingFilterChain rbj = Runtime(CreateUse(biquad, 3700, q: 2.5));
			float[] input = Enumerable.Range(0, 2400).Select(i =>
				(float)(0.5 * Math.Sin(2 * Math.PI * i * 2751 / SampleRate)
					+ 0.2 * Math.Cos(2 * Math.PI * i * 12001 / SampleRate)))
				.ToArray();
			float[] a = (float[])input.Clone();
			float[] b = (float[])input.Clone();
			tpt.ProcessInterleaved(a);
			rbj.ProcessInterleaved(b);
			for (int i = 0; i < a.Length; i++)
				Assert.That(a[i], Is.EqualTo(b[i]).Within(2e-5),
					$"{svf} vs {biquad} frame {i}");
		}
	}

	[Test]
	public void QControlsResonantPeakAndNotchAndBandWidth()
	{
		double highQ = Magnitude(FilterKind.SvfLowPass, 4000, 4000, q: 8);
		double lowQ = Magnitude(FilterKind.SvfLowPass, 4000, 4000, q: 0.5);
		Assert.That(highQ, Is.GreaterThan(lowQ * 10));
		double narrow = Magnitude(FilterKind.SvfBandPass, 4000, 1800, q: 8);
		double wide = Magnitude(FilterKind.SvfBandPass, 4000, 1800, q: 0.5);
		Assert.That(wide, Is.GreaterThan(narrow * 4));
		Assert.That(Magnitude(FilterKind.SvfBandPass, 4000, 4000, q: 8),
			Is.EqualTo(1).Within(0.01));
		double wideNotch = Magnitude(FilterKind.SvfNotch, 4000, 3100, q: 0.5);
		double narrowNotch = Magnitude(FilterKind.SvfNotch, 4000, 3100, q: 8);
		Assert.That(narrowNotch, Is.GreaterThan(wideNotch));
	}

	[Test]
	public void NyquistAndFactoryKindValidationAreAtomic()
	{
		FilterInstance valid = CreateUse(FilterKind.SvfLowPass, 2000);
		FilterInstance invalid = CreateUse(FilterKind.SvfHighPass, 20000);
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(
				new FilterChain(new[] { invalid }), 32000, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(
				new FilterChain(new[] { invalid }), 40000, 1));

		StreamingFilterChain active = new(
			new FilterChain(new[] { valid, valid }), 32000, 1);
		active.ProcessInterleaved(new float[] { 1f, 0.5f, -0.2f, 0f });
		StreamingFilterChain untouched = active.Fork();
		FilterInstance changed = valid.Clone();
		changed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(9000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			active.Configure(new FilterChain(new[] { changed, invalid })));
		float[] inputA = { 0f, 0.3f, 0f, -0.5f };
		float[] inputB = (float[])inputA.Clone();
		active.ProcessInterleaved(inputA);
		untouched.ProcessInterleaved(inputB);
		Assert.That(inputA, Is.EqualTo(inputB));

		Assert.Throws<ArgumentException>(() =>
			SvfFilterStageFactory.Instance.Prepare(
				new PreparedFilterStage(0, 0, (ObjectId)99U, 1,
					FilterKind.BiquadNotch, 0,
					Array.Empty<KeyValuePair<string, FilterParameterValue>>()),
				SampleRate));
	}

	[Test]
	public void CompatibleCutoffQEditsPreserveHistoryAndTransitionOver32Frames()
	{
		foreach (FilterKind kind in Enum.GetValues<FilterKind>()
			.Where(SvfFilterStageFactory.IsSvf))
		{
			FilterInstance use = CreateUse(kind, 1000, q: 1.4);
			StreamingFilterChain updated = Runtime(use);
			StreamingFilterChain original = Runtime(use);
			updated.ProcessFrame(new float[] { 1f });
			original.ProcessFrame(new float[] { 1f });
			use.SetStageParameters(1, new[] {
				Pair("CutoffHz", 8200),
				Pair("Q", 3.5),
			});
			updated.Configure(new FilterChain(new[] { use }));
			float[] actual = new float[48];
			float[] reference = new float[48];
			updated.ProcessInterleaved(actual);
			original.ProcessInterleaved(reference);
			Assert.That(actual[0], Is.Not.Zero,
				$"{kind}: histories must not be reset on compatible edits.");
			Assert.That(actual[0], Is.EqualTo(reference[0]).Within(0.12),
				$"{kind}: first interpolated sample must be close to old response.");
			Assert.That(actual.Zip(reference).Skip(1)
				.Any(pair => Math.Abs(pair.First - pair.Second) > 1e-6),
				Is.True, $"{kind}: numeric update must become effective.");

			StreamingFilterChain clean = Runtime(use);
			float[] zero = { 0f };
			clean.ProcessFrame(zero);
			Assert.That(zero[0], Is.Zero);
		}
	}

	[Test]
	public void RapidChangingQAndCutoffRemainStableAcrossStereoAndChunkSizes()
	{
		foreach (FilterKind kind in Enum.GetValues<FilterKind>()
			.Where(SvfFilterStageFactory.IsSvf))
		{
			FilterInstance use = CreateUse(kind, 1600, q: 2);
			StreamingFilterChain blocks = Runtime(use, channels: 2);
			StreamingFilterChain small = Runtime(use, channels: 2);
			for (int batch = 0; batch < 360; batch++)
			{
				// Explore extremes of both parameters, including changes
				// before the preceding 32-frame transition is complete.
				double cutoff = batch % 29 == 0 ? 1
					: batch % 31 == 0 ? 19999
					: 100 + ((batch * 7919) % 17500);
				double q = batch % 17 == 0 ? 0.1
					: batch % 19 == 0 ? 20
					: 0.2 + (batch % 15) * 1.28;
				use.SetStageParameters(1, new[] {
					Pair("CutoffHz", cutoff),
					Pair("Q", q),
				});
				FilterChain next = new(new[] { use });
				blocks.Configure(next);
				small.Configure(next);
				float[] signal = new float[2 * 19];
				for (int frame = 0; frame < 19; frame++)
				{
					signal[2 * frame] = (float)(0.35 * Math.Sin(
						2 * Math.PI * (batch * 19 + frame) * 4701 / SampleRate));
					signal[2 * frame + 1] = batch % 4 == 0 ? 0.23f : -0.13f;
				}
				float[] split = (float[])signal.Clone();
				blocks.ProcessInterleaved(signal);
				small.ProcessInterleaved(split.AsSpan(0, 14));
				small.ProcessInterleaved(split.AsSpan(14, 24));
				Assert.That(split, Is.EqualTo(signal),
					$"{kind} batch {batch} must be independent of chunking.");
				Assert.That(signal.All(float.IsFinite), Is.True);
				Assert.That(signal.All(sample => Math.Abs(sample) < 1000f),
					Is.True, $"{kind}: normal input must stay bounded.");
			}
		}
	}

	[Test]
	public void ForkCopiesIntegratorsAndPendingModulationResetReplaysExactly()
	{
		FilterInstance use = CreateUse(FilterKind.SvfBandPass, 3400, q: 2);
		StreamingFilterChain original = Runtime(use, channels: 3);
		float[] intro = Enumerable.Range(0, 600 * 3)
			.Select(i => (float)(0.45 * Math.Sin(
				2 * Math.PI * (i / 3) * (717 + (i % 3) * 2301) / SampleRate)))
			.ToArray();
		float[] processedIntro = (float[])intro.Clone();
		original.ProcessInterleaved(processedIntro);
		use.SetStageParameters(1, new[] { Pair("CutoffHz", 6500), Pair("Q", 6) });
		original.Configure(new FilterChain(new[] { use }));
		original.ProcessInterleaved(new float[3 * 9]);
		StreamingFilterChain fork = original.Fork();
		float[] tail = Enumerable.Range(0, 200 * 3)
			.Select(i => (float)(0.2 * Math.Cos(
				2 * Math.PI * (i / 3) * (1000 + (i % 3) * 450) / SampleRate)))
			.ToArray();
		float[] forkTail = (float[])tail.Clone();
		original.ProcessInterleaved(tail);
		fork.ProcessInterleaved(forkTail);
		Assert.That(forkTail, Is.EqualTo(tail),
			"Fork must copy both histories and in-flight parameter interpolation.");

		fork.Reset();
		float[] replay = (float[])intro.Clone();
		// Restoring original configuration then replaying is the host's
		// responsibility; Reset only zeroes the current prepared plan.
		FilterInstance initial = CreateUse(FilterKind.SvfBandPass, 3400, q: 2);
		fork.Configure(new FilterChain(new[] { initial }));
		fork.Reset();
		fork.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(processedIntro));
	}

	[Test]
	public void ResponseTypeChangeDiscardsIntegratorsAndRebuildsState()
	{
		FilterDefinition definition = new((ObjectId)77U, "SVF",
			new[] { new FilterStageDefinition(1, "stage1", FilterKind.SvfLowPass) });
		FilterInstance use = new(definition);
		StreamingFilterChain runtime = Runtime(use);
		runtime.ProcessFrame(new float[] { 1f });
		definition.ChangeStageKind(1, FilterKind.SvfNotch);
		runtime.Configure(new FilterChain(new[] { use }));
		float[] zero = { 0f };
		runtime.ProcessFrame(zero);
		Assert.That(zero[0], Is.Zero);
	}

	[Test]
	public void ExplicitSilenceDrainIsBoundedAndDenormalSafe()
	{
		StreamingFilterChain runtime = Runtime(
			CreateUse(FilterKind.SvfLowPass, 1700, q: 0.8),
			maximumTailSeconds: 0.25);
		Assert.That(runtime.PreparedTailFrames, Is.GreaterThan(0)
			.And.LessThanOrEqualTo(12000));
		runtime.ProcessFrame(new float[] { 1f });
		runtime.FinishInput();
		int budget = runtime.PreparedTailFrames;
		float[] tail = new float[budget + 5];
		Assert.That(runtime.RenderTail(tail), Is.EqualTo(budget));
		Assert.That(tail.Skip(budget), Is.All.Zero);
		Assert.That(tail.All(x => x == 0 || Math.Abs(x) >= 1e-20f), Is.True);
		Assert.That(runtime.TailRemainingFrames, Is.Zero);
		runtime.Reset();
		float[] bad = {
			float.NaN, float.NegativeInfinity, float.PositiveInfinity,
			float.MaxValue, -float.MaxValue, 0f,
		};
		runtime.ProcessInterleaved(bad);
		Assert.That(bad.All(float.IsFinite), Is.True);
		runtime.Reset();
		float[] impulse = { 1 };
		runtime.ProcessFrame(impulse);
		float[] silent = new float[4000];
		runtime.ProcessInterleaved(silent);
		Assert.That(silent.All(x => float.IsFinite(x)
			&& (x == 0 || Math.Abs(x) >= 1e-20f)), Is.True);
		Assert.That(silent[^1], Is.Zero);

		StreamingFilterChain capped = Runtime(
			CreateUse(FilterKind.SvfHighPass, 1, q: 20),
			maximumTailSeconds: 0.001);
		Assert.That(capped.PreparedTailFrames, Is.EqualTo(48));
		capped.ProcessFrame(new float[] { 1f });
		capped.FinishInput();
		float[] limited = new float[100];
		Assert.That(capped.RenderTail(limited), Is.EqualTo(48));
		Assert.That(limited.Skip(48), Is.All.Zero);
	}

	private static FilterInstance CreateUse(
		FilterKind kind, double cutoff, double q = 0.7071067811865476)
	{
		FilterDefinition definition = new((ObjectId)42U, "State variable", new[] {
			new FilterStageDefinition(1, "svf", kind),
		});
		FilterInstance instance = new(definition);
		instance.SetStageParameters(1, new[] {
			Pair("CutoffHz", cutoff),
			Pair("Q", q),
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
		FilterKind kind, double cutoff, double frequency, double q)
	{
		StreamingFilterChain filter = Runtime(CreateUse(kind, cutoff, q));
		const int settle = 4096;
		const int measure = 8192;
		double before = 0;
		double after = 0;
		float[] frame = new float[1];
		for (int n = 0; n < settle + measure; n++)
		{
			float sample = (float)Math.Sin(
				2 * Math.PI * frequency * n / SampleRate);
			frame[0] = sample;
			filter.ProcessFrame(frame);
			if (n < settle)
				continue;
			before += sample * sample;
			after += (double)frame[0] * frame[0];
		}
		return Math.Sqrt(after / before);
	}
}

using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class SaturatedLadderFilterTests
{
	private const int SampleRate = 48000;

	[TestCase("1x", 1)]
	[TestCase("2x", 2)]
	[TestCase("4x", 4)]
	public void AllThreeOversamplingChoicesProduceRealFiniteNonlinearPcm(
		string oversampling, int factor)
	{
		FilterInstance use = CreateUse(3500, 0.25, 5, oversampling);
		StreamingFilterChain runtime = Runtime(use);
		float[] impulse = new float[1024];
		impulse[0] = 1;
		runtime.ProcessInterleaved(impulse);
		Assert.That(runtime.StageCount, Is.EqualTo(1));
		Assert.That(runtime.PreparedTailFrames, Is.GreaterThan(0));
		Assert.That(impulse.All(float.IsFinite), Is.True);
		Assert.That(impulse.Any(v => Math.Abs(v) > 1e-8f), Is.True,
			$"Oversampling {factor}x must produce audio, not bypass.");
		Assert.That(impulse[^1], Is.EqualTo(0).Within(1e-6));
	}

	[Test]
	public void IncreasingDriveReducesLargeSignalAmplitudeButRetainsSmallSignalGain()
	{
		float soft = SteadyDc(CreateUse(2000, 0, 0.1, "1x"), 0.8f);
		float hard = SteadyDc(CreateUse(2000, 0, 10, "1x"), 0.8f);
		Assert.That(soft, Is.EqualTo(0.8f).Within(0.004));
		Assert.That(hard, Is.GreaterThan(0).And.LessThan(0.15f));
		Assert.That(soft, Is.GreaterThan(hard * 4));

		// Saturation occurs at the feedback/input summing junction, not
		// by limiting the output samples after a linear ladder.
		float withResonance = SteadyDc(CreateUse(2000, 0.9, 0.1, "1x"), 0.05f);
		float expected = 0.05f /
			(1 + (float)(0.9 * SaturatedLadderFilterStageFactory.MaximumFeedback));
		Assert.That(withResonance, Is.EqualTo(expected).Within(0.0015));
	}

	[Test]
	public void ANonlinearDriveCreatesHarmonicsThatLinearLadderDoesNot()
	{
		FilterInstance saturated = CreateUse(15000, 0, 10, "1x");
		FilterDefinition linearDefinition = new((ObjectId)67U, "Linear", new[]
		{
			new FilterStageDefinition(1, "linear", FilterKind.LadderLinear),
		});
		FilterInstance linear = new(linearDefinition);
		linear.SetStageParameters(1, new[] {
			Pair("CutoffHz", 15000),
			Pair("Resonance", 0),
		});
		float[] nonlinear = RenderSine(Runtime(saturated), 1500, 8192, 4096, 0.8);
		float[] neutral = RenderSine(Runtime(linear), 1500, 8192, 4096, 0.8);
		double nonlinearThird = Amplitude(nonlinear, 4500);
		double linearThird = Amplitude(neutral, 4500);
		Assert.That(nonlinearThird, Is.GreaterThan(0.002));
		Assert.That(linearThird, Is.LessThan(2e-5));
	}

	[Test]
	public void FourTimesOversamplingSuppressesHighHarmonicAliasing()
	{
		// At a 48 kHz host rate, the fifth harmonic of 9 kHz (45 kHz)
		// folds into 3 kHz when the tanh is evaluated at only 1x.
		// At 4x, harmonic energy is kept above the base Nyquist and
		// attenuated by four sub-rate reconstruction poles.
		float[] single = RenderSine(Runtime(
			CreateUse(17000, 0, 10, "1x")), 9000, 8192, 4096, 0.8);
		float[] oversampled = RenderSine(Runtime(
			CreateUse(17000, 0, 10, "4x")), 9000, 8192, 4096, 0.8);
		double aliasSingle = Amplitude(single, 3000);
		double aliasOversampled = Amplitude(oversampled, 3000);
		Assert.That(aliasSingle, Is.GreaterThan(0.001));
		Assert.That(aliasOversampled, Is.LessThan(aliasSingle * 0.75),
			"4x oversampling must actually reduce aliased harmonic energy, "
			+ "not merely execute repeated identical host frames.");
	}

	[Test]
	public void NearNyquistAndOutOfRangeSettingsRejectBeforePublication()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(
				new FilterChain(new[] { CreateUse(20000, 0, 1, "2x") }),
				32000, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(
				new FilterChain(new[] { CreateUse(20000, 0, 1, "4x") }),
				40000, 1));
		Assert.That(() => Runtime(CreateUse(20000, 1, 10, "4x")),
			Throws.Nothing);

		Assert.Throws<ArgumentException>(() =>
			SaturatedLadderFilterStageFactory.Instance.Prepare(
				new PreparedFilterStage(0, 0, (ObjectId)1U, 1,
					FilterKind.LadderLinear, 0,
					Array.Empty<KeyValuePair<string, FilterParameterValue>>()),
				SampleRate));
	}

	[Test]
	public void FailedSecondLayerPreparationCannotPartiallyRetuneFirstLayer()
	{
		FilterInstance use = CreateUse(2200, 0.5, 2, "2x");
		StreamingFilterChain active = new(
			new FilterChain(new[] { use, use }), 32000, 1);
		float[] warm = { 1f, 0f, -0.5f, 0.3f };
		active.ProcessInterleaved(warm);
		StreamingFilterChain reference = active.Fork();
		FilterInstance changed = use.Clone();
		changed.SetStageParameters(1, new[] {
			Pair("CutoffHz", 4000),
			Pair("Resonance", 0.85),
			Pair("Drive", 8),
		});
		FilterInstance invalid = use.Clone();
		invalid.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(20000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			active.Configure(new FilterChain(new[] { changed, invalid })));
		float[] a = { -0.1f, 0f, 0.3f, 0f };
		float[] b = (float[])a.Clone();
		active.ProcessInterleaved(a);
		reference.ProcessInterleaved(b);
		Assert.That(a, Is.EqualTo(b));
	}

	[Test]
	public void OversamplingIsStructuralButNumericRetuningPreservesHistories()
	{
		FilterInstance use = CreateUse(3000, 0.25, 1, "2x");
		StreamingFilterChain runtime = Runtime(use);
		StreamingFilterChain unchanged = Runtime(use);
		runtime.ProcessFrame(new float[] { 1f });
		unchanged.ProcessFrame(new float[] { 1f });

		use.SetStageParameters(1, new[] {
			Pair("CutoffHz", 8500),
			Pair("Resonance", 0.9),
			Pair("Drive", 6),
		});
		runtime.Configure(new FilterChain(new[] { use }));
		float[] evolved = new float[96];
		float[] original = new float[96];
		runtime.ProcessInterleaved(evolved);
		unchanged.ProcessInterleaved(original);
		Assert.That(evolved[0], Is.Not.Zero,
			"Numeric changes preserve existing integrators and reconstruction memory.");
		Assert.That(evolved.Zip(original).Skip(4)
			.Any(x => Math.Abs(x.First - x.Second) > 1e-6), Is.True);

		use.SetStageParameter(1, "Oversampling",
			FilterParameterValue.FromChoice("4x"));
		runtime.Configure(new FilterChain(new[] { use }));
		float[] fresh = { 0f };
		runtime.ProcessFrame(fresh);
		Assert.That(fresh[0], Is.Zero,
			"Changing structural oversampling resets the whole stage history.");
	}

	[Test]
	public void RapidNumericAutomationIsFiniteAndBitExactAcrossBlockPartitioning()
	{
		foreach (string rate in new[] { "1x", "2x", "4x" })
		{
			FilterInstance use = CreateUse(900, 0.6, 2, rate);
			StreamingFilterChain big = Runtime(use, channels: 2);
			StreamingFilterChain small = Runtime(use, channels: 2);
			for (int block = 0; block < 210; block++)
			{
				double cutoff = block % 27 == 0 ? 1
					: block % 31 == 0 ? 19999
					: 100 + ((block * 7919) % 17000);
				double resonance = block % 13 == 0 ? 1
					: (block % 101) / 100.0;
				double drive = block % 23 == 0 ? 10
					: 0.1 + (block % 20) * 0.49;
				use.SetStageParameters(1, new[] {
					Pair("CutoffHz", cutoff),
					Pair("Resonance", resonance),
					Pair("Drive", drive),
				});
				FilterChain configuration = new(new[] { use });
				big.Configure(configuration);
				small.Configure(configuration);
				float[] a = new float[17 * 2];
				for (int i = 0; i < 17; i++)
				{
					a[i * 2] = (float)(0.3 * Math.Sin(
						2 * Math.PI * (i + block * 17) * 3809 / SampleRate));
					a[i * 2 + 1] = 0.2f;
				}
				float[] b = (float[])a.Clone();
				big.ProcessInterleaved(a);
				small.ProcessInterleaved(b.AsSpan(0, 10));
				small.ProcessInterleaved(b.AsSpan(10, 24));
				Assert.That(a, Is.EqualTo(b), $"{rate}, batch {block}");
				Assert.That(a.All(float.IsFinite), Is.True);
				Assert.That(a.All(x => Math.Abs(x) < 1000f), Is.True);
			}
		}
	}

	[Test]
	public void ForkClonesAllMemoriesAndInFlightParameterInterpolation()
	{
		FilterInstance use = CreateUse(2200, 0.7, 1.5, "4x");
		StreamingFilterChain original = Runtime(use, channels: 3);
		float[] prefix = Enumerable.Range(0, 3 * 300)
			.Select(i => (float)(0.5 * Math.Sin(
				2 * Math.PI * (i / 3) * (700 + (i % 3) * 1200) / SampleRate)))
			.ToArray();
		float[] processed = (float[])prefix.Clone();
		original.ProcessInterleaved(processed);
		use.SetStageParameters(1, new[] {
			Pair("CutoffHz", 7000),
			Pair("Resonance", 1),
			Pair("Drive", 9),
		});
		original.Configure(new FilterChain(new[] { use }));
		original.ProcessInterleaved(new float[3 * 7]);
		StreamingFilterChain copy = original.Fork();
		float[] suffix = Enumerable.Range(0, 3 * 100)
			.Select(i => (float)(0.4 * Math.Cos(
				2 * Math.PI * (i / 3) * (800 + (i % 3) * 500) / SampleRate)))
			.ToArray();
		float[] suffixCopy = (float[])suffix.Clone();
		original.ProcessInterleaved(suffix);
		copy.ProcessInterleaved(suffixCopy);
		Assert.That(suffix, Is.EqualTo(suffixCopy));

		// Seek replay must use original configuration and input timeline.
		copy.Reset();
		copy.Configure(new FilterChain(new[] { CreateUse(2200, 0.7, 1.5, "4x") }));
		copy.Reset();
		float[] replay = (float[])prefix.Clone();
		copy.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(processed));
	}

	[Test]
	public void ResonanceMaximumAndFullScaleInputNeverBecomeNonfinite()
	{
		foreach (string rate in new[] { "1x", "2x", "4x" })
		{
			StreamingFilterChain runtime =
				Runtime(CreateUse(19000, 1, 10, rate));
			float[] stress = Enumerable.Range(0, 2000)
				.Select(i => i % 29 == 0 ? float.MaxValue
					: i % 31 == 0 ? -float.MaxValue
					: i % 37 == 0 ? float.NaN
					: i % 41 == 0 ? float.PositiveInfinity
					: (float)(0.9 * Math.Sin(2 * Math.PI * i * 12121 / SampleRate)))
				.ToArray();
			runtime.ProcessInterleaved(stress);
			Assert.That(stress.All(float.IsFinite), Is.True);
			Assert.That(stress.All(x => Math.Abs(x) <= float.MaxValue), Is.True);
			runtime.Reset();
			float[] impulse = { 1f };
			runtime.ProcessFrame(impulse);
			float[] silence = new float[12000];
			runtime.ProcessInterleaved(silence);
			Assert.That(silence.All(x => float.IsFinite(x)
				&& (x == 0 || Math.Abs(x) >= 1e-20f)), Is.True);
			Assert.That(silence[^1], Is.Zero);
		}
	}

	[Test]
	public void TailHasFiniteWorkCapAndDoesNotAllocateUnboundedOversampleBuffers()
	{
		foreach (string rate in new[] { "1x", "2x", "4x" })
		{
			StreamingFilterChain runtime = Runtime(
				CreateUse(1800, 1, 10, rate), maximumTailSeconds: 0.12);
			Assert.That(runtime.PreparedTailFrames,
				Is.GreaterThan(0).And.LessThanOrEqualTo(5760));
			runtime.ProcessFrame(new float[] { 1f });
			runtime.FinishInput();
			int budget = runtime.PreparedTailFrames;
			float[] tail = new float[budget + 8];
			Assert.That(runtime.RenderTail(tail), Is.EqualTo(budget));
			Assert.That(tail.Skip(budget), Is.All.Zero);
			Assert.That(tail.All(x => float.IsFinite(x)
				&& (x == 0 || Math.Abs(x) >= 1e-20f)), Is.True);
			Assert.That(runtime.TailRemainingFrames, Is.Zero);
		}
	}

	private static FilterInstance CreateUse(
		double cutoff, double resonance, double drive, string oversampling)
	{
		FilterDefinition definition = new((ObjectId)88U, "Saturated ladder",
			new[] { new FilterStageDefinition(
				1, "saturated", FilterKind.LadderSaturated) });
		FilterInstance instance = new(definition);
		instance.SetStageParameters(1, new[] {
			Pair("CutoffHz", cutoff),
			Pair("Resonance", resonance),
			Pair("Drive", drive),
			new KeyValuePair<string, FilterParameterValue>(
				"Oversampling", FilterParameterValue.FromChoice(oversampling)),
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

	private static float SteadyDc(FilterInstance use, float value)
	{
		StreamingFilterChain filter = Runtime(use);
		float[] input = Enumerable.Repeat(value, 4096).ToArray();
		filter.ProcessInterleaved(input);
		return input[^1];
	}

	private static float[] RenderSine(
		StreamingFilterChain filter, double frequency,
		int length, int warmup, double level)
	{
		float[] all = new float[length + warmup];
		for (int i = 0; i < all.Length; i++)
			all[i] = (float)(level * Math.Sin(
				2 * Math.PI * frequency * i / SampleRate));
		filter.ProcessInterleaved(all);
		return all.Skip(warmup).ToArray();
	}

	private static double Amplitude(IReadOnlyList<float> samples, double frequency)
	{
		double real = 0;
		double imaginary = 0;
		for (int i = 0; i < samples.Count; i++)
		{
			double angle = 2 * Math.PI * frequency * i / SampleRate;
			real += samples[i] * Math.Cos(angle);
			imaginary -= samples[i] * Math.Sin(angle);
		}
		return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Count;
	}
}

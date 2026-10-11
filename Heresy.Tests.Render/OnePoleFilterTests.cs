using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class OnePoleFilterTests
{
	private const int SampleRate = 48000;

	[Test]
	public void DefaultRuntimeUsesRealOnePoleButOtherKindsRemainNeutral()
	{
		FilterInstance low = CreateUse("LowPass", 3000);
		StreamingFilterChain processed =
			new(new FilterChain(new[] { low }), SampleRate, 1);
		StreamingFilterChain explicitlyBypassed =
			new(new FilterChain(new[] { low }), SampleRate, 1,
				BypassFilterStageFactory.Instance);
		float[] actual = { 1f, 0f, 0f };
		float[] unchanged = (float[])actual.Clone();
		processed.ProcessInterleaved(actual);
		explicitlyBypassed.ProcessInterleaved(unchanged);
		Assert.That(actual[0], Is.GreaterThan(0f).And.LessThan(1f));
		Assert.That(actual[1], Is.GreaterThan(0f));
		Assert.That(unchanged, Is.EqualTo(new[] { 1f, 0f, 0f }));

		FilterDefinition unimplemented = new((ObjectId)91U, "Pending", new[] {
			new FilterStageDefinition(1, "stage1", FilterKind.CombFeedforward),
		});
		StreamingFilterChain pending = new(
			new FilterChain(new[] { new FilterInstance(unimplemented) }),
			SampleRate, 1);
		float[] neutral = { -0.75f, 0.5f, 1f, 0f };
		pending.ProcessInterleaved(neutral);
		Assert.That(neutral, Is.EqualTo(new[] { -0.75f, 0.5f, 1f, 0f }));
		Assert.That(pending.PreparedTailFrames, Is.Zero);
	}

	[Test]
	public void OnePoleImpulseMatchesTptCoefficientsAndStepSettlesToDc()
	{
		const double cutoff = 3000;
		double k = Math.Tan(Math.PI * cutoff / SampleRate);
		double g = k / (1 + k);
		double pole = 1 - 2 * g;
		StreamingFilterChain low = CreateRuntime("LowPass", cutoff);
		float[] impulse = new float[4];
		impulse[0] = 1;
		low.ProcessInterleaved(impulse);

		Assert.That(impulse[0], Is.EqualTo(g).Within(2e-7));
		Assert.That(impulse[1], Is.EqualTo(2 * g * (1 - g)).Within(2e-7));
		Assert.That(impulse[2], Is.EqualTo(
			2 * g * (1 - g) * pole).Within(2e-7));
		Assert.That(impulse[3], Is.EqualTo(
			2 * g * (1 - g) * pole * pole).Within(2e-7));

		StreamingFilterChain stepLow = CreateRuntime("LowPass", cutoff);
		StreamingFilterChain stepHigh = CreateRuntime("HighPass", cutoff);
		float[] dcLow = Enumerable.Repeat(1f, 2048).ToArray();
		float[] dcHigh = (float[])dcLow.Clone();
		stepLow.ProcessInterleaved(dcLow);
		stepHigh.ProcessInterleaved(dcHigh);
		Assert.That(dcLow[^1], Is.EqualTo(1).Within(1e-6));
		Assert.That(dcHigh[^1], Is.EqualTo(0).Within(1e-6));
	}

	[Test]
	public void LowAndHighPassComplementOneAnotherAtEverySample()
	{
		StreamingFilterChain low = CreateRuntime("LowPass", 4500);
		StreamingFilterChain high = CreateRuntime("HighPass", 4500);
		for (int n = 0; n < 500; n++)
		{
			float sample = (float)(
				0.6 * Math.Sin(2 * Math.PI * n * 1717 / SampleRate)
				+ 0.25 * Math.Cos(2 * Math.PI * n * 8089 / SampleRate));
			float[] x = { sample };
			float[] y = { sample };
			low.ProcessFrame(x);
			high.ProcessFrame(y);
			Assert.That(x[0] + y[0], Is.EqualTo(sample).Within(2e-7),
				$"Complementary response at sample {n}");
		}
	}

	[Test]
	public void CornerFrequencyHasThreeDbGainAndExpectedSixDbPerOctaveRollOff()
	{
		const double cutoff = 3000;
		double cornerLow = MeasureAmplitude(CreateRuntime("LowPass", cutoff), 3000);
		double cornerHigh = MeasureAmplitude(CreateRuntime("HighPass", cutoff), 3000);
		double expected = 1 / Math.Sqrt(2);
		Assert.That(cornerLow, Is.EqualTo(expected).Within(0.002));
		Assert.That(cornerHigh, Is.EqualTo(expected).Within(0.002));

		double passLow = MeasureAmplitude(CreateRuntime("LowPass", cutoff), 100);
		double stopLow = MeasureAmplitude(CreateRuntime("LowPass", cutoff), 12000);
		double stopHigh = MeasureAmplitude(CreateRuntime("HighPass", cutoff), 100);
		double passHigh = MeasureAmplitude(CreateRuntime("HighPass", cutoff), 12000);
		Assert.That(passLow, Is.GreaterThan(0.995));
		Assert.That(stopLow, Is.LessThan(0.21));
		Assert.That(stopHigh, Is.LessThan(0.04));
		Assert.That(passHigh, Is.GreaterThan(0.97));

		// The digital low-pass response is first order. Far below Nyquist,
		// halving frequency in its stopband roughly halves attenuation.
		double lowAt300 = MeasureAmplitude(CreateRuntime("HighPass", cutoff), 300);
		double lowAt600 = MeasureAmplitude(CreateRuntime("HighPass", cutoff), 600);
		double octaveGain = 20 * Math.Log10(lowAt600 / lowAt300);
		Assert.That(octaveGain, Is.InRange(5.6, 6.3));
	}

	[Test]
	public void HighPassRejectsDcAndPreservesNyquistAlternation()
	{
		StreamingFilterChain high = CreateRuntime("HighPass", 3000);
		StreamingFilterChain low = CreateRuntime("LowPass", 3000);
		float[] alternating = Enumerable.Range(0, 4000)
			.Select(i => (i & 1) == 0 ? 1f : -1f).ToArray();
		float[] lowOutput = (float[])alternating.Clone();
		high.ProcessInterleaved(alternating);
		low.ProcessInterleaved(lowOutput);
		Assert.That(Math.Abs(alternating[^1]), Is.EqualTo(1).Within(0.002));
		Assert.That(Math.Abs(lowOutput[^1]), Is.LessThan(0.002));
	}

	[Test]
	public void SampleRateAndNyquistValidationOccursBeforePublication()
	{
		FilterInstance use = CreateUse("LowPass", 20000);
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { use }), 32000, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { use }), 40000, 1));

		StreamingFilterChain valid =
			new(new FilterChain(new[] { use }), SampleRate, 1);
		float[] input = { 1f };
		valid.ProcessFrame(input);
		Assert.That(input[0], Is.GreaterThan(0f).And.LessThan(1f));

		FilterInstance invalid = use.Clone();
		invalid.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(19999));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { invalid }), 20000, 1));

		Assert.Throws<ArgumentException>(() =>
			OnePoleFilterStageFactory.Instance.Prepare(
				new PreparedFilterStage(0, 0, (ObjectId)1U, 1,
					FilterKind.SvfLowPass, 0, Array.Empty<KeyValuePair<
						string, FilterParameterValue>>()), SampleRate));
	}

	[Test]
	public void CutoffChangesRetainHistoryAndModeChangesResetState()
	{
		FilterDefinition definition = new((ObjectId)13U, "Changing");
		FilterInstance use = new(definition);
		StreamingFilterChain runtime =
			new(new FilterChain(new[] { use }), SampleRate, 1);
		float[] impulse = { 1f };
		runtime.ProcessFrame(impulse);

		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3000));
		runtime.Configure(new FilterChain(new[] { use }));
		float[] withHistory = { 0f };
		runtime.ProcessFrame(withHistory);
		Assert.That(withHistory[0], Is.GreaterThan(0f));

		StreamingFilterChain freshlyPrepared =
			new(new FilterChain(new[] { use }), SampleRate, 1);
		float[] withNoHistory = { 0f };
		freshlyPrepared.ProcessFrame(withNoHistory);
		Assert.That(withNoHistory[0], Is.Zero);

		use.SetStageParameter(1, "Mode",
			FilterParameterValue.FromChoice("HighPass"));
		runtime.Configure(new FilterChain(new[] { use }));
		float[] afterStructuralChange = { 0f };
		runtime.ProcessFrame(afterStructuralChange);
		Assert.That(afterStructuralChange[0], Is.Zero);
	}

	[Test]
	public void FailedNyquistReconfigurationRetainsAllActiveHistories()
	{
		FilterDefinition definition = new((ObjectId)15U, "Atomic");
		FilterInstance first = new(definition);
		FilterInstance second = first.Clone();
		StreamingFilterChain runtime = new(
			new FilterChain(new[] { first, second }), 32000, 1);
		float[] warmup = { 1f, -1f, 0.5f, 0f };
		runtime.ProcessInterleaved((float[])warmup.Clone());

		// The first serial layer changes to a valid coefficient, while
		// the second is invalid at 32 kHz. Neither change may commit.
		FilterInstance changedFirst = first.Clone();
		changedFirst.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(2000));
		FilterInstance badSecond = second.Clone();
		badSecond.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(20000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			runtime.Configure(new FilterChain(new[] { changedFirst, badSecond })));

		StreamingFilterChain untouched = new(
			new FilterChain(new[] { first, second }), 32000, 1);
		untouched.ProcessInterleaved((float[])warmup.Clone());
		float[] nextA = { 0f, 0.25f, 0f, -0.5f };
		float[] nextB = (float[])nextA.Clone();
		runtime.ProcessInterleaved(nextA);
		untouched.ProcessInterleaved(nextB);
		Assert.That(nextA, Is.EqualTo(nextB),
			"Failed preparation must leave earlier stage coefficients and histories untouched.");
	}

	[Test]
	public void InterleavedChunksAndForkedHistoriesAreBitwiseDeterministic()
	{
		FilterChain configuration = new(new[] {
			CreateUse("HighPass", 4400), CreateUse("LowPass", 2600),
		});
		StreamingFilterChain continuous =
			new(configuration, SampleRate, 3);
		StreamingFilterChain chunked =
			new(configuration, SampleRate, 3);
		float[] input = Enumerable.Range(0, 8192 * 3)
			.Select(i => (float)(0.8 * Math.Sin(
				2 * Math.PI * (i / 3) * (113 + (i % 3) * 311) / SampleRate)))
			.ToArray();
		float[] a = (float[])input.Clone();
		float[] b = (float[])input.Clone();
		continuous.ProcessInterleaved(a);
		int cursor = 0;
		foreach (int size in new[] { 9, 87, 333, 6, 1200, 33 })
		{
			chunked.ProcessInterleaved(b.AsSpan(cursor, size));
			cursor += size;
		}
		chunked.ProcessInterleaved(b.AsSpan(cursor));
		Assert.That(b, Is.EqualTo(a));

		StreamingFilterChain fork = continuous.Fork();
		float[] suffix = { 0.1f, -0.2f, 0.9f, 0.4f, 0.3f, -0.7f };
		float[] forked = (float[])suffix.Clone();
		continuous.ProcessInterleaved(suffix);
		fork.ProcessInterleaved(forked);
		Assert.That(forked, Is.EqualTo(suffix));
		fork.Reset();
		float[] replay = (float[])input.Clone();
		fork.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(a));
	}

	[Test]
	public void TailIsFiniteDecayIsFlushedAndBudgetIsStrictlyBounded()
	{
		StreamingFilterChain low = CreateRuntime("LowPass", 1000);
		float[] impulse = { 1f };
		low.ProcessFrame(impulse);
		Assert.That(low.PreparedTailFrames, Is.GreaterThan(0).And.LessThan(960));
		int budget = low.PreparedTailFrames;
		low.FinishInput();
		float[] tail = new float[budget + 6];
		int advanced = low.RenderTail(tail);
		Assert.That(advanced, Is.EqualTo(budget));
		Assert.That(tail[0], Is.GreaterThan(0f));
		Assert.That(tail[^1], Is.Zero);
		Assert.That(tail[^2], Is.Zero);
		Assert.That(low.TailRemainingFrames, Is.Zero);
		float[] again = { 1f, 1f };
		Assert.That(low.RenderTail(again), Is.Zero);
		Assert.That(again, Is.EqualTo(new[] { 0f, 0f }));
		low.Reset();
		Assert.That(low.TailRemainingFrames, Is.Zero);

		StreamingFilterChain capped = new(
			new FilterChain(new[] { CreateUse("HighPass", 1) }),
			SampleRate, 1, maximumTailSeconds: 0.001);
		Assert.That(capped.PreparedTailFrames, Is.EqualTo(48));
		capped.ProcessFrame(new float[] { 1f });
		capped.FinishInput();
		float[] limited = new float[200];
		Assert.That(capped.RenderTail(limited), Is.EqualTo(48));
		Assert.That(limited.Skip(48), Is.All.Zero);
	}

	[Test]
	public void ExtremeAlternatingHighPassPcmSaturatesRatherThanEmittingInfinity()
	{
		StreamingFilterChain runtime = CreateRuntime("HighPass", 1);
		float[] alternating = Enumerable.Range(0, 32)
			.Select(i => (i & 1) == 0 ? float.MaxValue : -float.MaxValue)
			.ToArray();
		runtime.ProcessInterleaved(alternating);
		Assert.That(alternating.All(float.IsFinite), Is.True);
		Assert.That(alternating.Any(value =>
			Math.Abs(value) == float.MaxValue), Is.True,
			"Clipping extreme PCM is preferable to poisoning later stages with Infinity.");
	}

	[Test]
	public void RepeatedSilenceAndMalformedPcmNeverProduceDenormalsOrNonfiniteOutput()
	{
		StreamingFilterChain low = CreateRuntime("LowPass", 20000);
		float[] input = { float.NaN, float.PositiveInfinity,
			float.NegativeInfinity, 1f, float.MaxValue, 0f };
		low.ProcessInterleaved(input);
		Assert.That(input.All(float.IsFinite), Is.True);
		low.Reset();
		float[] tiny = { 1f };
		low.ProcessFrame(tiny);
		float[] silence = new float[1000];
		low.ProcessInterleaved(silence);
		Assert.That(silence.All(v => float.IsFinite(v)
			&& (v == 0f || Math.Abs(v) >= 1e-20f)), Is.True);
		Assert.That(silence[^1], Is.Zero);
	}

	private static FilterInstance CreateUse(string mode, double cutoff)
	{
		FilterDefinition definition = new((ObjectId)1U, "One-Pole");
		FilterInstance use = new(definition);
		use.SetStageParameters(1, new[] {
			Pair("Mode", FilterParameterValue.FromChoice(mode)),
			Pair("CutoffHz", FilterParameterValue.FromNumber(cutoff)),
		});
		return use;
	}

	private static StreamingFilterChain CreateRuntime(string mode, double cutoff)
		=> new(new FilterChain(new[] { CreateUse(mode, cutoff) }), SampleRate, 1);

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string key, FilterParameterValue value) => new(key, value);

	private static double MeasureAmplitude(StreamingFilterChain runtime,
		double frequency)
	{
		const int settling = 4096;
		const int measuring = 16384;
		double sourceEnergy = 0;
		double filteredEnergy = 0;
		float[] frame = new float[1];
		for (int n = 0; n < settling + measuring; n++)
		{
			float value = (float)Math.Sin(
				2 * Math.PI * frequency * n / SampleRate);
			frame[0] = value;
			runtime.ProcessFrame(frame);
			if (n >= settling)
			{
				sourceEnergy += value * value;
				filteredEnergy += frame[0] * frame[0];
			}
		}
		return Math.Sqrt(filteredEnergy / sourceEnergy);
	}
}

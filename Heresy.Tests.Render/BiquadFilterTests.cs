using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class BiquadFilterTests
{
	private const int SampleRate = 48000;

	[TestCase(FilterKind.BiquadLowPass)]
	[TestCase(FilterKind.BiquadHighPass)]
	[TestCase(FilterKind.BiquadLowShelf)]
	[TestCase(FilterKind.BiquadHighShelf)]
	[TestCase(FilterKind.BiquadPeaking)]
	[TestCase(FilterKind.BiquadBandPass)]
	[TestCase(FilterKind.BiquadNotch)]
	public void EachBiquadIsAudibleAndHasStableFiniteImpulseResponse(FilterKind kind)
	{
		FilterInstance use = CreateUse(kind, 3000, gainDb: 12);
		StreamingFilterChain runtime = Runtime(use);
		float[] impulse = new float[4096];
		impulse[0] = 1;
		runtime.ProcessInterleaved(impulse);

		Assert.That(runtime.StageCount, Is.EqualTo(1));
		Assert.That(runtime.PreparedTailFrames, Is.GreaterThan(0));
		Assert.That(impulse.All(float.IsFinite), Is.True);
		Assert.That(impulse[0], Is.Not.EqualTo(1f).Within(1e-5),
			$"{kind} must not remain the former implicit bypass.");
		Assert.That(Math.Abs(impulse[^1]), Is.LessThan(1e-5),
			$"{kind} must have a stable decaying impulse response.");
	}

	[Test]
	public void LowPassAndHighPassHaveSecondOrderFrequencySelectivity()
	{
		double lowPassLow = Magnitude(FilterKind.BiquadLowPass, 2000, 100);
		double lowPassHigh = Magnitude(FilterKind.BiquadLowPass, 2000, 12000);
		double highPassLow = Magnitude(FilterKind.BiquadHighPass, 2000, 100);
		double highPassHigh = Magnitude(FilterKind.BiquadHighPass, 2000, 12000);
		double cornerLow = Magnitude(FilterKind.BiquadLowPass, 2000, 2000);
		double cornerHigh = Magnitude(FilterKind.BiquadHighPass, 2000, 2000);

		Assert.That(lowPassLow, Is.InRange(0.98, 1.02));
		Assert.That(lowPassHigh, Is.LessThan(0.025));
		Assert.That(highPassLow, Is.LessThan(0.01));
		Assert.That(highPassHigh, Is.InRange(0.98, 1.02));
		Assert.That(cornerLow, Is.EqualTo(1 / Math.Sqrt(2)).Within(0.025));
		Assert.That(cornerHigh, Is.EqualTo(1 / Math.Sqrt(2)).Within(0.025));
		// Second-order roll-off, approximately 12 dB/octave away from
		// both the corner and Nyquist.
		double at300 = Magnitude(FilterKind.BiquadHighPass, 3000, 300);
		double at600 = Magnitude(FilterKind.BiquadHighPass, 3000, 600);
		Assert.That(20 * Math.Log10(at600 / at300), Is.InRange(11.1, 12.6));
	}

	[Test]
	public void BandPassHasUnitPeakAndNotchNullsAtCentre()
	{
		double peak = Magnitude(FilterKind.BiquadBandPass, 4000, 4000, q: 2);
		double low = Magnitude(FilterKind.BiquadBandPass, 4000, 200, q: 2);
		double high = Magnitude(FilterKind.BiquadBandPass, 4000, 15000, q: 2);
		double notch = Magnitude(FilterKind.BiquadNotch, 4000, 4000, q: 2);
		double notchAway = Magnitude(FilterKind.BiquadNotch, 4000, 100, q: 2);

		Assert.That(peak, Is.EqualTo(1).Within(0.01));
		Assert.That(low, Is.LessThan(0.03));
		Assert.That(high, Is.LessThan(0.18));
		Assert.That(notch, Is.LessThan(0.001));
		Assert.That(notchAway, Is.InRange(0.98, 1.02));

		// A wider/lower-Q band-pass admits more off-centre energy.
		double wide = Magnitude(FilterKind.BiquadBandPass, 4000, 1500, q: 0.5);
		double narrow = Magnitude(FilterKind.BiquadBandPass, 4000, 1500, q: 8);
		Assert.That(wide, Is.GreaterThan(narrow * 4));
	}

	[Test]
	public void PeakingAndBothShelvesRespectGainAndFrequency()
	{
		double boosted = Math.Pow(10, 12.0 / 20);
		Assert.That(Magnitude(FilterKind.BiquadPeaking, 3000, 3000, gainDb: 12),
			Is.EqualTo(boosted).Within(0.05));
		Assert.That(Magnitude(FilterKind.BiquadPeaking, 3000, 3000, gainDb: -12),
			Is.EqualTo(1 / boosted).Within(0.03));
		Assert.That(Magnitude(FilterKind.BiquadPeaking, 3000, 100, gainDb: 12),
			Is.InRange(0.98, 1.03));
		Assert.That(Magnitude(FilterKind.BiquadLowShelf, 3000, 100, gainDb: 12),
			Is.EqualTo(boosted).Within(0.04));
		Assert.That(Magnitude(FilterKind.BiquadLowShelf, 3000, 17000, gainDb: 12),
			Is.InRange(0.97, 1.03));
		Assert.That(Magnitude(FilterKind.BiquadHighShelf, 3000, 100, gainDb: 12),
			Is.InRange(0.97, 1.03));
		Assert.That(Magnitude(FilterKind.BiquadHighShelf, 3000, 17000, gainDb: 12),
			Is.EqualTo(boosted).Within(0.04));

		foreach (FilterKind kind in new[] {
			FilterKind.BiquadPeaking,
			FilterKind.BiquadLowShelf,
			FilterKind.BiquadHighShelf,
		})
			Assert.That(Magnitude(kind, 3000, 4500, gainDb: 0),
				Is.EqualTo(1).Within(0.001), $"0 dB {kind} must be unity.");
	}

	[Test]
	public void ShelfSlopeChangesTransitionWithoutChangingAsymptoticGain()
	{
		double shallow = Magnitude(
			FilterKind.BiquadLowShelf, 3000, 1200, gainDb: 12, shelfSlope: 0.3);
		double steep = Magnitude(
			FilterKind.BiquadLowShelf, 3000, 1200, gainDb: 12, shelfSlope: 1);
		Assert.That(steep, Is.GreaterThan(shallow));
		Assert.That(Magnitude(
			FilterKind.BiquadLowShelf, 3000, 70, gainDb: 12, shelfSlope: 0.3),
			Is.EqualTo(Math.Pow(10, 12.0 / 20)).Within(0.15));
	}

	[Test]
	public void InvalidNyquistAndUnrealizableShelfCombinationsRejectAtomically()
	{
		FilterInstance valid = CreateUse(FilterKind.BiquadPeaking, 2000, gainDb: 6);
		StreamingFilterChain runtime = new(
			new FilterChain(new[] { valid, valid }), 32000, 1);
		runtime.ProcessInterleaved(new float[] { 1f, 0f, 0f, 0f });
		StreamingFilterChain reference = runtime.Fork();

		FilterInstance updated = valid.Clone();
		updated.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3000));
		FilterInstance invalid = valid.Clone();
		invalid.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(20000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			runtime.Configure(new FilterChain(new[] { updated, invalid })));
		float[] after = { 0f, 0.4f, -0.2f, 0f };
		float[] expected = (float[])after.Clone();
		runtime.ProcessInterleaved(after);
		reference.ProcessInterleaved(expected);
		Assert.That(after, Is.EqualTo(expected));

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { invalid }),
				40000, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			Runtime(CreateUse(FilterKind.BiquadHighShelf, 3000,
				gainDb: 36, shelfSlope: 2)));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			BiquadFilterStageFactory.Instance.Prepare(
				new PreparedFilterStage(0, 0, (ObjectId)1U, 1,
					FilterKind.OnePole, 0,
					Array.Empty<KeyValuePair<string, FilterParameterValue>>()),
				SampleRate));
	}

	[Test]
	public void CutoffGainQualityAndSlopeUpdatesRetainHistoryAndSmoothCoefficients()
	{
		foreach (FilterKind kind in Enum.GetValues<FilterKind>()
			.Where(BiquadFilterStageFactory.IsBiquad))
		{
			FilterInstance use = CreateUse(kind, 1000, q: 2, gainDb: 6);
			StreamingFilterChain modified = Runtime(use);
			StreamingFilterChain reference = Runtime(use);
			float[] impulse = { 1f };
			modified.ProcessFrame(impulse);
			reference.ProcessFrame(new float[] { 1f });

			use.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(9000));
			if (kind is FilterKind.BiquadPeaking
				or FilterKind.BiquadHighShelf or FilterKind.BiquadLowShelf)
				use.SetStageParameter(1, "GainDb",
					FilterParameterValue.FromNumber(10));
			if (kind is FilterKind.BiquadHighShelf or FilterKind.BiquadLowShelf)
				use.SetStageParameter(1, "ShelfSlope",
					FilterParameterValue.FromNumber(0.6));
			else if (kind is FilterKind.BiquadPeaking or FilterKind.BiquadLowPass
				or FilterKind.BiquadHighPass or FilterKind.BiquadBandPass
				or FilterKind.BiquadNotch)
				use.SetStageParameter(1, "Q",
					FilterParameterValue.FromNumber(0.9));
			modified.Configure(new FilterChain(new[] { use }));
			float[] sampleA = { 0f, 0f, 0f, 0f };
			float[] sampleB = (float[])sampleA.Clone();
			modified.ProcessInterleaved(sampleA);
			reference.ProcessInterleaved(sampleB);
			Assert.That(sampleA.All(float.IsFinite), Is.True, kind.ToString());
			Assert.That(sampleA[0], Is.EqualTo(sampleB[0]).Within(2e-7),
				$"{kind}: the first post-update sample must preserve the old state.");
			Assert.That(sampleA.Skip(1).Any((value, index) =>
				Math.Abs(value - sampleB[index + 1]) > 1e-9), Is.True,
				$"{kind}: numeric changes should gradually take effect.");
			modified.Reset();
			float[] reset = { 0f };
			modified.ProcessFrame(reset);
			Assert.That(reset[0], Is.Zero);
		}
	}

	[Test]
	public void RapidRetuningRemainsFiniteAndIdenticalAcrossBlockPartitions()
	{
		foreach (FilterKind kind in Enum.GetValues<FilterKind>()
			.Where(BiquadFilterStageFactory.IsBiquad))
		{
			FilterInstance use = CreateUse(kind, 2200, q: 3, gainDb: 18);
			StreamingFilterChain whole = Runtime(use, channels: 2);
			StreamingFilterChain partitioned = Runtime(use, channels: 2);
			for (int block = 0; block < 260; block++)
			{
				double cutoff = 200 + ((block * 7919) % 12000);
				use.SetStageParameter(1, "CutoffHz",
					FilterParameterValue.FromNumber(cutoff));
				if (kind is FilterKind.BiquadLowPass
					or FilterKind.BiquadHighPass
					or FilterKind.BiquadPeaking
					or FilterKind.BiquadBandPass
					or FilterKind.BiquadNotch)
					use.SetStageParameter(1, "Q",
						FilterParameterValue.FromNumber(0.15 + (block % 9) * 2));
				FilterChain changed = new(new[] { use });
				whole.Configure(changed);
				partitioned.Configure(changed);
				float[] a = new float[2 * 17];
				for (int frame = 0; frame < 17; frame++)
				{
					a[frame * 2] = (float)(0.25 * Math.Sin(
						2 * Math.PI * (frame + block * 17) * 2401 / SampleRate));
					a[frame * 2 + 1] = 0.12f;
				}
				float[] b = (float[])a.Clone();
				whole.ProcessInterleaved(a);
				partitioned.ProcessInterleaved(b.AsSpan(0, 10));
				partitioned.ProcessInterleaved(b.AsSpan(10, 24));
				Assert.That(a, Is.EqualTo(b), $"partition {block} of {kind}");
				Assert.That(a.All(float.IsFinite), Is.True);
				Assert.That(a.All(x => Math.Abs(x) < 1000f), Is.True,
					$"{kind} must remain bounded on ordinary PCM under automation.");
			}
		}
	}

	[Test]
	public void ForksAndResetReplayAreIndependentAndDeterministic()
	{
		FilterChain chain = new(new[] {
			CreateUse(FilterKind.BiquadHighPass, 600, q: 1),
			CreateUse(FilterKind.BiquadPeaking, 5000, q: 3, gainDb: 8),
		});
		StreamingFilterChain a = new(chain, SampleRate, 3);
		StreamingFilterChain b = new(chain, SampleRate, 3);
		float[] input = Enumerable.Range(0, 3 * 1024)
			.Select(i => (float)(0.6 * Math.Cos(
				2 * Math.PI * (i / 3) * (300 + i % 3 * 1300) / SampleRate)))
			.ToArray();
		float[] outA = (float[])input.Clone();
		float[] outB = (float[])input.Clone();
		a.ProcessInterleaved(outA);
		for (int offset = 0; offset < outB.Length; offset += 3)
			b.ProcessFrame(outB.AsSpan(offset, 3));
		Assert.That(outA, Is.EqualTo(outB));

		StreamingFilterChain fork = a.Fork();
		float[] suffix = { 0.5f, -0.5f, 0.25f, 0f, 0f, 0f };
		float[] copy = (float[])suffix.Clone();
		a.ProcessInterleaved(suffix);
		fork.ProcessInterleaved(copy);
		Assert.That(copy, Is.EqualTo(suffix));
		fork.Reset();
		float[] replay = (float[])input.Clone();
		fork.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(outA));
	}

	[Test]
	public void TailIsBoundedAndDenormalSafeAndNonfiniteSamplesDoNotPoisonState()
	{
		StreamingFilterChain runtime = Runtime(
			CreateUse(FilterKind.BiquadLowPass, 1300, q: 2),
			maximumTailSeconds: 0.2);
		Assert.That(runtime.PreparedTailFrames, Is.GreaterThan(0).And.LessThanOrEqualTo(9600));
		float[] impulse = { 1f };
		runtime.ProcessFrame(impulse);
		runtime.FinishInput();
		int budget = runtime.PreparedTailFrames;
		float[] tail = new float[budget + 10];
		Assert.That(runtime.RenderTail(tail), Is.EqualTo(budget));
		Assert.That(tail.Skip(budget), Is.All.Zero);
		Assert.That(tail.All(x => x == 0 || Math.Abs(x) >= 1e-20f), Is.True);
		Assert.That(runtime.TailRemainingFrames, Is.Zero);
		runtime.Reset();
		float[] invalid = { float.NaN, float.PositiveInfinity,
			float.NegativeInfinity, float.MaxValue, -float.MaxValue, 0f };
		runtime.ProcessInterleaved(invalid);
		Assert.That(invalid.All(float.IsFinite), Is.True);
		float[] silence = new float[4000];
		runtime.ProcessInterleaved(silence);
		Assert.That(silence.All(x => float.IsFinite(x)
			&& (x == 0 || Math.Abs(x) >= 1e-20f)), Is.True);
	}

	private static FilterInstance CreateUse(
		FilterKind kind, double cutoff, double q = 0.7071067811865476,
		double gainDb = 0, double shelfSlope = 1)
	{
		FilterDefinition definition = new((ObjectId)23U, "EQ", new[] {
			new FilterStageDefinition(1, "eq", kind),
		});
		FilterInstance instance = new(definition);
		List<KeyValuePair<string, FilterParameterValue>> values = [
			new("CutoffHz", FilterParameterValue.FromNumber(cutoff)),
		];
		if (FilterCatalogue.GetParameters(kind).Any(p => p.Name == "Q"))
			values.Add(new("Q", FilterParameterValue.FromNumber(q)));
		if (FilterCatalogue.GetParameters(kind).Any(p => p.Name == "GainDb"))
			values.Add(new("GainDb", FilterParameterValue.FromNumber(gainDb)));
		if (FilterCatalogue.GetParameters(kind).Any(p => p.Name == "ShelfSlope"))
			values.Add(new("ShelfSlope", FilterParameterValue.FromNumber(shelfSlope)));
		instance.SetStageParameters(1, values);
		return instance;
	}

	private static StreamingFilterChain Runtime(
		FilterInstance instance, int channels = 1,
		double maximumTailSeconds = 10)
		=> new(new FilterChain(new[] { instance }), SampleRate, channels,
			maximumTailSeconds: maximumTailSeconds);

	private static double Magnitude(
		FilterKind kind, double cutoff, double frequency,
		double q = 0.7071067811865476, double gainDb = 0,
		double shelfSlope = 1)
	{
		StreamingFilterChain runtime =
			Runtime(CreateUse(kind, cutoff, q, gainDb, shelfSlope));
		const int warmup = 4096;
		const int length = 8192;
		double inputEnergy = 0;
		double outputEnergy = 0;
		float[] frame = new float[1];
		for (int i = 0; i < warmup + length; i++)
		{
			frame[0] = (float)Math.Sin(2 * Math.PI * frequency * i / SampleRate);
			runtime.ProcessFrame(frame);
			if (i < warmup)
				continue;
			double x = Math.Sin(2 * Math.PI * frequency * i / SampleRate);
			inputEnergy += x * x;
			outputEnergy += (double)frame[0] * frame[0];
		}
		return Math.Sqrt(outputEnergy / inputEnergy);
	}
}

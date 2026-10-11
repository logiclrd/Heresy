using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class LinearLadderFilterTests
{
	private const int SampleRate = 48000;

	[Test]
	public void FourPoleImpulseMatchesFourIndependentOnePoleStagesAtZeroResonance()
	{
		FilterInstance ladder = CreateUse(3400, 0);
		FilterDefinition fourStages = new((ObjectId)72U, "Four poles", new[]
		{
			new FilterStageDefinition(1, "p1", FilterKind.OnePole),
			new FilterStageDefinition(2, "p2", FilterKind.OnePole),
			new FilterStageDefinition(3, "p3", FilterKind.OnePole),
			new FilterStageDefinition(4, "p4", FilterKind.OnePole),
		});
		FilterInstance reference = new(fourStages);
		foreach (FilterStageDefinition stage in fourStages.Stages)
			reference.SetStageParameter(stage.StageId, "CutoffHz",
				FilterParameterValue.FromNumber(3400));

		StreamingFilterChain linear = Runtime(ladder);
		StreamingFilterChain cascaded = Runtime(reference);
		float[] impulseA = new float[1024];
		float[] impulseB = new float[1024];
		impulseA[0] = 1f;
		impulseB[0] = 1f;
		linear.ProcessInterleaved(impulseA);
		cascaded.ProcessInterleaved(impulseB);
		for (int i = 0; i < impulseA.Length; i++)
			Assert.That(impulseA[i], Is.EqualTo(impulseB[i]).Within(2e-6),
				$"four-pole cascade at sample {i}");

		Assert.That(impulseA[0], Is.GreaterThan(0f).And.LessThan(0.1f));
		Assert.That(impulseA[3], Is.GreaterThan(impulseA[0]));
		Assert.That(Math.Abs(impulseA[^1]), Is.LessThan(1e-6));
	}

	[Test]
	public void SpectralResponseHasFourPolesDcGainAndApproximately24DbPerOctave()
	{
		double dc = Measure(4000, 0, 60);
		double corner = Measure(4000, 0, 4000);
		double high = Measure(4000, 0, 14000);
		Assert.That(dc, Is.InRange(0.99, 1.01));
		Assert.That(corner, Is.EqualTo(0.25).Within(0.008));
		Assert.That(high, Is.LessThan(0.004));

		double lower = Measure(4000, 0, 8000);
		double upper = Measure(4000, 0, 16000);
		double octave = 20 * Math.Log10(lower / upper);
		Assert.That(octave, Is.InRange(25, 43),
			"The bilinear-warped digital four-pole roll-off steepens "
			+ "near Nyquist; the asymptotic analog order is 24 dB/octave.");
	}

	[Test]
	public void ResonanceBoostsPeakButDoesNotIntroduceInfiniteOscillation()
	{
		double unresonant = Measure(4000, 0, 4000);
		double resonant = Measure(4000, 1, 4000);
		Assert.That(resonant, Is.GreaterThan(unresonant * 12));
		double noResDc = Measure(4000, 0, 50);
		double resDc = Measure(4000, 1, 50);
		Assert.That(noResDc, Is.InRange(0.99, 1.01));
		Assert.That(resDc, Is.EqualTo(1 / (1 + LinearLadderFilterStageFactory.MaximumFeedback))
			.Within(0.01), "Classic linear feedback reduces DC passband gain.");

		StreamingFilterChain runtime = Runtime(CreateUse(4000, 1));
		float[] impulse = new float[20000];
		impulse[0] = 1;
		runtime.ProcessInterleaved(impulse);
		Assert.That(impulse.All(float.IsFinite), Is.True);
		Assert.That(impulse.Skip(15000).All(x => Math.Abs(x) < 1e-10f), Is.True,
			"Authored maximum resonance must ring down rather than self-oscillate.");
	}

	[Test]
	public void CutoffAndResonanceRejectNyquistAndInvalidStageKind()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { CreateUse(20000, 0) }),
				32000, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { CreateUse(20000, 1) }),
				40000, 1));
		Assert.That(() => Runtime(CreateUse(20000, 1)), Throws.Nothing);
		Assert.Throws<ArgumentException>(() =>
			LinearLadderFilterStageFactory.Instance.Prepare(
				new PreparedFilterStage(0, 0, (ObjectId)5U, 1,
					FilterKind.LadderSaturated, 0,
					Array.Empty<KeyValuePair<string, FilterParameterValue>>()),
				SampleRate));
	}

	[Test]
	public void FailedPreparationOfSecondLayerNeverChangesActiveHistory()
	{
		FilterInstance original = CreateUse(2100, 0.3);
		StreamingFilterChain active = new(
			new FilterChain(new[] { original, original }), 32000, 1);
		active.ProcessInterleaved(new float[] { 1f, 0.5f, -0.25f, 0f });
		StreamingFilterChain unchanged = active.Fork();

		FilterInstance changed = original.Clone();
		changed.SetStageParameters(1, new[]
		{
			Pair("CutoffHz", 7100),
			Pair("Resonance", 0.9),
		});
		FilterInstance invalid = original.Clone();
		invalid.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(20000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			active.Configure(new FilterChain(new[] { changed, invalid })));
		float[] after = { 0f, 0.15f, 0f, -0.3f };
		float[] expected = (float[])after.Clone();
		active.ProcessInterleaved(after);
		unchanged.ProcessInterleaved(expected);
		Assert.That(after, Is.EqualTo(expected));
	}

	[Test]
	public void NumericReconfigurationPreservesHistoryAndProgressivelyChangesResponse()
	{
		FilterInstance use = CreateUse(1000, 0.4);
		StreamingFilterChain changed = Runtime(use);
		StreamingFilterChain baseline = Runtime(use);
		changed.ProcessFrame(new float[] { 1f });
		baseline.ProcessFrame(new float[] { 1f });
		use.SetStageParameters(1, new[]
		{
			Pair("CutoffHz", 8000),
			Pair("Resonance", 0.85),
		});
		changed.Configure(new FilterChain(new[] { use }));
		float[] a = new float[64];
		float[] b = new float[64];
		changed.ProcessInterleaved(a);
		baseline.ProcessInterleaved(b);
		Assert.That(a[0], Is.Not.Zero);
		Assert.That(a[0], Is.EqualTo(b[0]).Within(0.002));
		Assert.That(a.Zip(b).Skip(2)
			.Any(pair => Math.Abs(pair.First - pair.Second) > 1e-6), Is.True);

		StreamingFilterChain clean = Runtime(use);
		float[] silence = { 0f };
		clean.ProcessFrame(silence);
		Assert.That(silence[0], Is.Zero);
	}

	[Test]
	public void AggressiveModulationRemainsFiniteAndIndependentOfPcmPartitioning()
	{
		FilterInstance use = CreateUse(1000, 0.5);
		StreamingFilterChain blocks = Runtime(use, channels: 2);
		StreamingFilterChain frames = Runtime(use, channels: 2);
		for (int batch = 0; batch < 350; batch++)
		{
			double cutoff = batch % 29 == 0 ? 1
				: batch % 31 == 0 ? 19999
				: 100 + ((batch * 7919) % 17600);
			double resonance = batch % 13 == 0 ? 1
				: batch % 17 == 0 ? 0
				: (batch % 101) / 100.0;
			use.SetStageParameters(1, new[]
			{
				Pair("CutoffHz", cutoff),
				Pair("Resonance", resonance),
			});
			FilterChain chain = new(new[] { use });
			blocks.Configure(chain);
			frames.Configure(chain);

			float[] input = new float[19 * 2];
			for (int i = 0; i < input.Length / 2; i++)
			{
				input[i * 2] = (float)(0.3 * Math.Sin(
					2 * Math.PI * (batch * 19 + i) * 3701 / SampleRate));
				input[i * 2 + 1] = 0.2f;
			}
			float[] split = (float[])input.Clone();
			blocks.ProcessInterleaved(input);
			frames.ProcessInterleaved(split.AsSpan(0, 12));
			frames.ProcessInterleaved(split.AsSpan(12, 26));
			Assert.That(split, Is.EqualTo(input), $"batch {batch}");
			Assert.That(input.All(float.IsFinite), Is.True);
			Assert.That(input.All(x => Math.Abs(x) < 1000), Is.True,
				"Linear ladder must remain bounded with ordinary input.");
		}
	}

	[Test]
	public void ForkCopiesAllFourChannelHistoriesAndInFlightRetune()
	{
		FilterInstance use = CreateUse(2600, 0.8);
		StreamingFilterChain original = Runtime(use, channels: 3);
		float[] intro = Enumerable.Range(0, 1800)
			.Select(i => (float)(0.35 * Math.Sin(
				2 * Math.PI * (i / 3) * (1771 + (i % 3) * 1203) / SampleRate)))
			.ToArray();
		float[] processed = (float[])intro.Clone();
		original.ProcessInterleaved(processed);
		use.SetStageParameters(1, new[]
		{
			Pair("CutoffHz", 7000),
			Pair("Resonance", 1),
		});
		original.Configure(new FilterChain(new[] { use }));
		original.ProcessInterleaved(new float[3 * 8]);
		StreamingFilterChain fork = original.Fork();
		float[] suffix = Enumerable.Range(0, 3 * 200)
			.Select(i => (float)(0.2 * Math.Cos(
				2 * Math.PI * (i / 3) * (610 + (i % 3) * 340) / SampleRate)))
			.ToArray();
		float[] forked = (float[])suffix.Clone();
		original.ProcessInterleaved(suffix);
		fork.ProcessInterleaved(forked);
		Assert.That(forked, Is.EqualTo(suffix));

		// A backward seek replays the initial configuration and input
		// timeline; resetting state alone is not a rewind of parameter events.
		fork.Reset();
		FilterInstance initial = CreateUse(2600, 0.8);
		fork.Configure(new FilterChain(new[] { initial }));
		fork.Reset();
		float[] replay = (float[])intro.Clone();
		fork.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(processed));
	}

	[Test]
	public void MaximumResonanceTailIsFiniteAndDenormalSafe()
	{
		StreamingFilterChain runtime = Runtime(
			CreateUse(3000, 1), maximumTailSeconds: 0.35);
		int budget = runtime.PreparedTailFrames;
		Assert.That(budget, Is.GreaterThan(0).And.LessThanOrEqualTo(16800));
		runtime.ProcessFrame(new float[] { 1f });
		runtime.FinishInput();
		float[] remaining = new float[budget + 8];
		Assert.That(runtime.RenderTail(remaining), Is.EqualTo(budget));
		Assert.That(remaining.Skip(budget), Is.All.Zero);
		Assert.That(remaining.All(x => x == 0 || Math.Abs(x) >= 1e-20f),
			Is.True);
		Assert.That(runtime.TailRemainingFrames, Is.Zero);
		runtime.Reset();

		float[] corrupted = { float.NaN, float.PositiveInfinity,
			float.NegativeInfinity, float.MaxValue, -float.MaxValue, 0 };
		runtime.ProcessInterleaved(corrupted);
		Assert.That(corrupted.All(float.IsFinite), Is.True);
		runtime.Reset();
		runtime.ProcessFrame(new float[] { 1f });
		float[] silence = new float[12000];
		runtime.ProcessInterleaved(silence);
		Assert.That(silence.All(x => float.IsFinite(x)
			&& (x == 0 || Math.Abs(x) >= 1e-20f)), Is.True);
		Assert.That(silence[^1], Is.Zero);
	}

	private static FilterInstance CreateUse(double cutoff, double resonance)
	{
		FilterDefinition definition = new((ObjectId)29U, "Linear Ladder",
			new[] { new FilterStageDefinition(1, "ladder", FilterKind.LadderLinear) });
		FilterInstance instance = new(definition);
		instance.SetStageParameters(1, new[]
		{
			Pair("CutoffHz", cutoff),
			Pair("Resonance", resonance),
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

	private static double Measure(double cutoff, double resonance, double frequency)
	{
		StreamingFilterChain runtime = Runtime(CreateUse(cutoff, resonance));
		const int settling = 6000;
		const int frames = 12000;
		double inputEnergy = 0;
		double outputEnergy = 0;
		float[] frame = new float[1];
		for (int i = 0; i < settling + frames; i++)
		{
			float source = (float)Math.Sin(
				2 * Math.PI * frequency * i / SampleRate);
			frame[0] = source;
			runtime.ProcessFrame(frame);
			if (i < settling)
				continue;
			inputEnergy += source * source;
			outputEnergy += (double)frame[0] * frame[0];
		}
		return Math.Sqrt(outputEnergy / inputEnergy);
	}
}

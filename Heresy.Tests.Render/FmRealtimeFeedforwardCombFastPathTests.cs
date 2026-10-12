using System;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmRealtimeFeedforwardCombFastPathTests
{
	private const int SampleRate = 1000;

	[TestCase(-1.0)]
	[TestCase(0.0)]
	[TestCase(1.0)]
	public void FixedDelayProducesExactlyTwoImpulseTaps(double feedforward)
	{
		StreamingFilterChain chain = new(
			new FilterChain([MakeComb(feedforward, 0.004)]), SampleRate, 1);
		float[] impulse = new float[24];
		impulse[0] = 1;
		chain.ProcessInterleaved(impulse);
		Assert.That(impulse[0], Is.EqualTo(1));
		Assert.That(impulse[4], Is.EqualTo((float)(0.65 * feedforward)));
		for (int i = 1; i < impulse.Length; i++)
			if (i != 4)
				Assert.That(impulse[i], Is.Zero, $"No recursive echo at frame {i}");
	}

	[Test]
	public void TwoIndependentNoteStatesDoNotShareCircularHistories()
	{
		FmSynthSound sound = Modulated(MakeComb(-0.7, 0.027), true, true);
		RenderContext context = Context();
		float[] baseline = new float[230];
		sound.Render(context, sound.CreateState(), 0, baseline.Length, baseline);

		SoundState one = sound.CreateState();
		SoundState two = sound.CreateState();
		float[] first = new float[230], second = new float[230];
		sound.Render(context, one, 0, 91, first.AsSpan(0, 91));
		sound.Render(context, two, 0, 67, second.AsSpan(0, 67));
		sound.Render(context, one, 91, 139, first.AsSpan(91));
		sound.Render(context, two, 67, 163, second.AsSpan(67));
		Assert.That(first, Is.EqualTo(baseline));
		Assert.That(second, Is.EqualTo(baseline));
	}

	[TestCase(true, false, -0.85)]
	[TestCase(false, true, 0.85)]
	[TestCase(true, true, -0.85)]
	[TestCase(true, true, 0.85)]
	public void PositiveAndNegativeTwoTapOutputsMatchFullReconfigurationFrameExactly(
		bool modulateFeedforward, bool modulateMix, double startingFeedforward)
	{
		FilterInstance instance = MakeComb(startingFeedforward);
		FmSynthSound sound = Modulated(instance, modulateFeedforward, modulateMix);
		float[] actual = new float[360];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);
		StreamingFilterChain reference = new(new FilterChain([instance]),
			SampleRate, 1);
		double feedforwardPhase = 0, mixPhase = 0;
		Span<float> frame = stackalloc float[1];
		for (int i = 0; i < actual.Length; i++)
		{
			double feedforward = Wave(ref feedforwardPhase, 19, 0, 0.85);
			double mix = Wave(ref mixPhase, 31, 0.5, 0.45);
			FilterInstance next = instance.Clone();
			if (modulateFeedforward && modulateMix)
				next.SetStageParameters(1, [
					new("Feedforward", FilterParameterValue.FromNumber(feedforward)),
					new("Mix", FilterParameterValue.FromNumber(mix)),
				]);
			else if (modulateFeedforward)
				next.SetStageParameter(1, "Feedforward",
					FilterParameterValue.FromNumber(feedforward));
			else
				next.SetStageParameter(1, "Mix",
					FilterParameterValue.FromNumber(mix));
			reference.Configure(new FilterChain([next]));
			frame[0] = Excitation(i);
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"feedforward Comb frame {i}, feedforward={modulateFeedforward}, "
				+ $"mix={modulateMix}, initial feedforward={startingFeedforward}");
			instance = next;
		}
	}

	[TestCase(0.001)]
	[TestCase(0.027)]
	[TestCase(0.198)]
	public void FastPathPreservesStructuralDelayRingAtDifferentLengths(double delay)
	{
		FilterInstance instance = MakeComb(-0.6, delay);
		FmSynthSound sound = Modulated(instance, true, true);
		float[] actual = new float[400];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);
		StreamingFilterChain reference = new(new FilterChain([instance]),
			SampleRate, 1);
		double feedforwardPhase = 0, mixPhase = 0;
		Span<float> frame = stackalloc float[1];
		for (int i = 0; i < actual.Length; i++)
		{
			double feedforward = Wave(ref feedforwardPhase, 19, 0, 0.85);
			double mix = Wave(ref mixPhase, 31, 0.5, 0.45);
			FilterInstance next = instance.Clone();
			next.SetStageParameters(1, [
				new("Feedforward", FilterParameterValue.FromNumber(feedforward)),
				new("Mix", FilterParameterValue.FromNumber(mix)),
			]);
			reference.Configure(new FilterChain([next]));
			frame[0] = Excitation(i);
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"delay {delay}s, frame {i}");
			instance = next;
		}
	}

	[Test]
	public void ContinuousFeedforwardAndMixModulationAllocatesNoObjectsPerFrame()
	{
		FmSynthSound sound = Modulated(MakeComb(-0.6), true, true);
		SoundState state = sound.CreateState();
		RenderContext context = Context();
		float[] warmup = new float[128];
		sound.Render(context, state, 0, warmup.Length, warmup);
		float[] pcm = new float[4096];
		long start = GC.GetAllocatedBytesForCurrentThread();
		sound.Render(context, state, 128, pcm.Length, pcm);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
		Assert.That(allocated, Is.LessThan(4096),
			$"feedforward Comb allocated {allocated} bytes across 4096 modulated frames");
		Assert.That(pcm.All(float.IsFinite), Is.True);
	}

	[TestCase(1.01, 0.5)]
	[TestCase(-1.01, 0.5)]
	[TestCase(0.3, -0.1)]
	[TestCase(0.3, 1.1)]
	public void InvalidCombControlRollsBackEarlierFamilyChanges(
		double feedforward, double mix)
	{
		FilterDefinition definition = new((ObjectId)188U, "Mixed", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "bell", FilterKind.BiquadPeaking),
			new FilterStageDefinition(3, "phase", FilterKind.AllPass),
			new FilterStageDefinition(4, "comb", FilterKind.CombFeedforward),
		]);
		FilterInstance use = new(definition);
		for (int id = 1; id <= 3; id++)
			use.SetStageParameter(id, "CutoffHz",
				FilterParameterValue.FromNumber(110));
		use.SetStageParameter(4, "DelaySeconds",
			FilterParameterValue.FromNumber(0.004));
		FmSynthSound invalid = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmConstantNode(2, 75),
			new FmConstantNode(3, 5),
			new FmConstantNode(4, 170),
			new FmConstantNode(5, feedforward),
			new FmConstantNode(6, mix),
			new FmFilterNode(7, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.BiquadPeaking,
					0, "GainDb", 3),
				new FmFilterParameterConnection(3, FilterKind.AllPass,
					0, "CutoffHz", 4),
				new FmFilterParameterConnection(4, FilterKind.CombFeedforward,
					0, "Feedforward", 5),
				new FmFilterParameterConnection(4, FilterKind.CombFeedforward,
					0, "Mix", 6),
			]),
		], 7), new EmptyResolver());
		FmSynthSound unchanged = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(7, 1, use),
		], 7), new EmptyResolver());
		float[] actual = new float[200], expected = new float[200];
		invalid.Render(Context(), invalid.CreateState(), 0,
			actual.Length, actual);
		unchanged.Render(Context(), unchanged.CreateState(), 0,
			expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected),
			"invalid feedforward Comb must not commit valid earlier stages");
	}

	[Test]
	public void ValidMixedFamilyUpdatesMatchFullAtomicConfiguration()
	{
		FilterDefinition definition = new((ObjectId)189U, "Mixed", [
			new FilterStageDefinition(1, "phase", FilterKind.AllPass),
			new FilterStageDefinition(2, "comb", FilterKind.CombFeedforward),
			new FilterStageDefinition(3, "pole", FilterKind.OnePole),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(120));
		use.SetStageParameter(2, "DelaySeconds",
			FilterParameterValue.FromNumber(0.004));
		use.SetStageParameter(3, "CutoffHz",
			FilterParameterValue.FromNumber(125));
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 17,
				minimum: 75, maximum: 165, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 19,
				minimum: -0.85, maximum: 0.85, constant: true),
			new FmOscillatorNode(4, FmOscillatorWaveform.Sine, 23,
				minimum: 0.05, maximum: 0.95, constant: true),
			new FmFilterNode(5, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.AllPass,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.CombFeedforward,
					0, "Feedforward", 3),
				new FmFilterParameterConnection(2, FilterKind.CombFeedforward,
					0, "Mix", 4),
				new FmFilterParameterConnection(3, FilterKind.OnePole,
					0, "CutoffHz", 2),
			]),
		], 5), new EmptyResolver());
		float[] actual = new float[300];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);
		StreamingFilterChain reference = new(new FilterChain([use]), SampleRate, 1);
		double cutoffPhase = 0, feedforwardPhase = 0, mixPhase = 0;
		Span<float> frame = stackalloc float[1];
		for (int i = 0; i < actual.Length; i++)
		{
			double cutoff = Wave(ref cutoffPhase, 17, 120, 45);
			double feedforward = Wave(ref feedforwardPhase, 19, 0, 0.85);
			double mix = Wave(ref mixPhase, 23, 0.5, 0.45);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			next.SetStageParameters(2, [
				new("Feedforward", FilterParameterValue.FromNumber(feedforward)),
				new("Mix", FilterParameterValue.FromNumber(mix)),
			]);
			next.SetStageParameter(3, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			reference.Configure(new FilterChain([next]));
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"valid mixed family frame {i}");
			use = next;
		}
	}

	[Test]
	public void UnmodulatedFeedforwardCombPreservesItsRingHistory()
	{
		FilterDefinition definition = new((ObjectId)190U, "TwoCombs", [
			new FilterStageDefinition(1, "moving", FilterKind.CombFeedforward),
			new FilterStageDefinition(2, "fixed", FilterKind.CombFeedforward),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("DelaySeconds", FilterParameterValue.FromNumber(0.003)),
			new("Feedforward", FilterParameterValue.FromNumber(-0.7)),
			new("Mix", FilterParameterValue.FromNumber(0.65)),
		]);
		use.SetStageParameters(2, [
			new("DelaySeconds", FilterParameterValue.FromNumber(0.007)),
			new("Feedforward", FilterParameterValue.FromNumber(0.85)),
			new("Mix", FilterParameterValue.FromNumber(0.45)),
		]);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 0.05, maximum: 0.95, constant: true),
			new FmFilterNode(3, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.CombFeedforward,
					0, "Mix", 2),
			]),
		], 3), new EmptyResolver());
		float[] actual = new float[340];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);
		StreamingFilterChain reference = new(new FilterChain([use]), SampleRate, 1);
		double phase = 0;
		Span<float> frame = stackalloc float[1];
		for (int i = 0; i < actual.Length; i++)
		{
			double mix = Wave(ref phase, 19, 0.5, 0.45);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "Mix",
				FilterParameterValue.FromNumber(mix));
			reference.Configure(new FilterChain([next]));
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"second delay-line history must persist, frame {i}");
			use = next;
		}
	}

	[Test]
	public void ChunkingAndBackwardSeekReconstructExactDelayedTaps()
	{
		FmSynthSound sound = Modulated(MakeComb(-0.8), true, true);
		float[] reference = new float[360];
		sound.Render(Context(), sound.CreateState(), 0,
			reference.Length, reference);
		float[] split = new float[360];
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, 49, split.AsSpan(0, 49));
		sound.Render(Context(), state, 49, 31, split.AsSpan(49, 31));
		sound.Render(Context(), state, 80, 280, split.AsSpan(80));
		Assert.That(split, Is.EqualTo(reference));
		float[] previous = new float[110];
		sound.Render(Context(), state, 43, previous.Length, previous);
		Assert.That(previous, Is.EqualTo(reference[43..153]));
	}

	[Test]
	public void DelayIsStructuralAndNotAnFmNumericPort()
	{
		FmFilterNode node = new(3, 1, MakeComb(0.6));
		Assert.That(node.GetPorts().Any(x =>
			x.ParameterName == "DelaySeconds"), Is.False);
		Assert.That(node.GetPorts().Any(x =>
			x.ParameterName == "Feedforward"), Is.True);
		Assert.That(node.GetPorts().Any(x =>
			x.ParameterName == "Mix"), Is.True);
	}

	private static FmSynthSound Modulated(FilterInstance use,
		bool feedforward, bool mix)
	{
		System.Collections.Generic.List<FmFilterParameterConnection> ports = [];
		if (feedforward)
			ports.Add(new FmFilterParameterConnection(1,
				FilterKind.CombFeedforward, 0, "Feedforward", 2));
		if (mix)
			ports.Add(new FmFilterParameterConnection(1,
				FilterKind.CombFeedforward, 0, "Mix", 3));
		return new FmSynthSound(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: -0.85, maximum: 0.85, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 31,
				minimum: 0.05, maximum: 0.95, constant: true),
			new FmFilterNode(4, 1, use, ports),
		], 4), new EmptyResolver());
	}

	private static FilterInstance MakeComb(double feedforward,
		double delaySeconds = 0.004)
	{
		FilterDefinition def = new((ObjectId)191U, "Comb", [
			new FilterStageDefinition(1, "comb", FilterKind.CombFeedforward),
		]);
		FilterInstance use = new(def);
		use.SetStageParameters(1, [
			new("DelaySeconds", FilterParameterValue.FromNumber(delaySeconds)),
			new("Feedforward", FilterParameterValue.FromNumber(feedforward)),
			new("Mix", FilterParameterValue.FromNumber(0.65)),
		]);
		return use;
	}

	private static float Excitation(int frame)
		=> 0.65f;

	private static double Wave(ref double phase, double frequency,
		double center, double amplitude)
	{
		double value = center + amplitude * Math.Sin(2 * Math.PI * phase);
		phase += frequency / SampleRate;
		phase -= Math.Floor(phase);
		return value;
	}

	private static RenderContext Context()
		=> new(new RenderConfiguration(SampleRate, [
			new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
		]));

	private sealed class EmptyResolver : IEnvelopeCurveResolver
	{
		public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
		{
			curve = null;
			return false;
		}
	}
}

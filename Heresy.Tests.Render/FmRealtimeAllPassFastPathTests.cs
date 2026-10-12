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
public sealed class FmRealtimeAllPassFastPathTests
{
	private const int SampleRate = 1000;

	[TestCase("1")]
	[TestCase("2")]
	public void CutoffModulationMatchesFullAtomicReconfigurationSampleForSample(
		string order)
	{
		FilterInstance use = MakeAllPass(order);
		FmSynthSound sound = MakeModulated(use);
		float[] actual = new float[320];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);

		StreamingFilterChain reference = new(new FilterChain([use]),
			SampleRate, 1);
		Span<float> frame = stackalloc float[1];
		double phase = 0;
		for (int i = 0; i < actual.Length; i++)
		{
			double cutoff = Wave(ref phase, 19, 120, 45);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			reference.Configure(new FilterChain([next]));
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"All-Pass order {order}: frame {i} differs from full Configure");
			use = next;
		}
	}

	[TestCase("1")]
	[TestCase("2")]
	public void HighRateModulationDoesNotAllocateForEachPcmFrame(string order)
	{
		FmSynthSound sound = MakeModulated(MakeAllPass(order));
		SoundState state = sound.CreateState();
		RenderContext context = Context();
		float[] warmup = new float[96];
		sound.Render(context, state, 0, warmup.Length, warmup);
		float[] pcm = new float[4096];
		long before = GC.GetAllocatedBytesForCurrentThread();
		sound.Render(context, state, 96, pcm.Length, pcm);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.LessThan(4096),
			$"All-Pass order {order} allocated {allocated} bytes over "
			+ "4096 continuously changing cutoff samples");
		foreach (float sample in pcm)
			Assert.That(float.IsFinite(sample), Is.True);
	}

	[TestCase("1", 500.0)]
	[TestCase("2", 700.0)]
	[TestCase("1", -3.0)]
	public void OutOfRangeAllPassPortRollsBackOtherFamilyUpdates(
		string order, double invalidCutoff)
	{
		FilterDefinition def = new((ObjectId)175U, "Mixed", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "bell", FilterKind.BiquadPeaking),
			new FilterStageDefinition(3, "linear", FilterKind.LadderLinear),
			new FilterStageDefinition(4, "saturated", FilterKind.LadderSaturated),
			new FilterStageDefinition(5, "phase", FilterKind.AllPass),
		]);
		FilterInstance use = new(def);
		for (int stage = 1; stage <= 5; stage++)
			use.SetStageParameter(stage, "CutoffHz",
				FilterParameterValue.FromNumber(120));
		use.SetStageParameter(5, "Order",
			FilterParameterValue.FromChoice(order));
		FmSynthSound invalid = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmConstantNode(2, 80),
			new FmConstantNode(3, 6),
			new FmConstantNode(4, 0.95),
			new FmConstantNode(5, 8),
			new FmConstantNode(6, invalidCutoff),
			new FmFilterNode(7, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.BiquadPeaking,
					0, "GainDb", 3),
				new FmFilterParameterConnection(3, FilterKind.LadderLinear,
					0, "Resonance", 4),
				new FmFilterParameterConnection(4, FilterKind.LadderSaturated,
					0, "Drive", 5),
				new FmFilterParameterConnection(5, FilterKind.AllPass,
					0, "CutoffHz", 6),
			]),
		], 7), new EmptyResolver());
		FmSynthSound baseline = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(7, 1, use),
		], 7), new EmptyResolver());
		float[] actual = new float[170];
		float[] expected = new float[170];
		invalid.Render(Context(), invalid.CreateState(), 0,
			actual.Length, actual);
		baseline.Render(Context(), baseline.CreateState(), 0,
			expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected),
			"Invalid All-Pass coefficient must not partially commit any family");
	}

	[TestCase("1")]
	[TestCase("2")]
	public void ValidMixedFamilyChangeMatchesFullAtomicReference(string order)
	{
		FilterDefinition definition = new((ObjectId)176U, "MixedValid", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "phase", FilterKind.AllPass),
			new FilterStageDefinition(3, "bell", FilterKind.BiquadPeaking),
		]);
		FilterInstance use = new(definition);
		for (int id = 1; id <= 3; id++)
			use.SetStageParameter(id, "CutoffHz",
				FilterParameterValue.FromNumber(120));
		use.SetStageParameter(2, "Order",
			FilterParameterValue.FromChoice(order));
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 75, maximum: 165, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 23,
				minimum: -8, maximum: 8, constant: true),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.AllPass,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(3, FilterKind.BiquadPeaking,
					0, "GainDb", 3),
			]),
		], 4), new EmptyResolver());
		float[] actual = new float[240];
		sound.Render(Context(), sound.CreateState(), 0,
			actual.Length, actual);
		StreamingFilterChain reference = new(
			new FilterChain([use]), SampleRate, 1);
		Span<float> sample = stackalloc float[1];
		double cutoffPhase = 0, gainPhase = 0;
		for (int i = 0; i < actual.Length; i++)
		{
			double cutoff = Wave(ref cutoffPhase, 19, 120, 45);
			double gain = Wave(ref gainPhase, 23, 0, 8);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			next.SetStageParameter(2, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			next.SetStageParameter(3, "GainDb",
				FilterParameterValue.FromNumber(gain));
			reference.Configure(new FilterChain([next]));
			sample[0] = 0.65f;
			reference.ProcessFrame(sample);
			Assert.That(actual[i], Is.EqualTo(sample[0]),
				$"mixed family All-Pass order {order} frame {i}");
			use = next;
		}
	}

	[Test]
	public void TwoAllPassStagesKeepDifferentOrdersAndUnchangedHistories()
	{
		FilterDefinition definition = new((ObjectId)177U, "TwoPhases", [
			new FilterStageDefinition(1, "first", FilterKind.AllPass),
			new FilterStageDefinition(2, "second", FilterKind.AllPass),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("CutoffHz", FilterParameterValue.FromNumber(120)),
			new("Order", FilterParameterValue.FromChoice("1")),
		]);
		use.SetStageParameters(2, [
			new("CutoffHz", FilterParameterValue.FromNumber(140)),
			new("Order", FilterParameterValue.FromChoice("2")),
		]);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 75, maximum: 165, constant: true),
			new FmFilterNode(3, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.AllPass,
					0, "CutoffHz", 2),
			]),
		], 3), new EmptyResolver());
		float[] actual = new float[220];
		sound.Render(Context(), sound.CreateState(), 0,
			actual.Length, actual);
		StreamingFilterChain reference = new(
			new FilterChain([use]), SampleRate, 1);
		Span<float> frame = stackalloc float[1];
		double phase = 0;
		for (int i = 0; i < actual.Length; i++)
		{
			double cutoff = Wave(ref phase, 19, 120, 45);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			reference.Configure(new FilterChain([next]));
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"unchanged second section unexpectedly reset at frame {i}");
			use = next;
		}
	}

	[TestCase("1")]
	[TestCase("2")]
	public void SplitBlocksAndBackwardSeekReproduceModulatedPcmExactly(string order)
	{
		FmSynthSound sound = MakeModulated(MakeAllPass(order));
		float[] expected = new float[350];
		sound.Render(Context(), sound.CreateState(), 0,
			expected.Length, expected);
		float[] partitioned = new float[350];
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, 45, partitioned.AsSpan(0, 45));
		sound.Render(Context(), state, 45, 23, partitioned.AsSpan(45, 23));
		sound.Render(Context(), state, 68, 282, partitioned.AsSpan(68));
		Assert.That(partitioned, Is.EqualTo(expected));
		float[] earlier = new float[100];
		sound.Render(Context(), state, 71, 100, earlier);
		Assert.That(earlier, Is.EqualTo(expected[71..171]));
	}

	[TestCase("1")]
	[TestCase("2")]
	public void AllPassOrderStaysStructuralInTheFmGraph(string order)
	{
		FilterInstance use = MakeAllPass(order);
		FmFilterNode node = new(3, 1, use);
		Assert.That(node.GetPorts().Any(p =>
			!p.IsAudio && p.ParameterName == "CutoffHz"), Is.True);
		Assert.That(node.GetPorts().Any(p =>
			p.ParameterName == "Order"), Is.False);
	}

	private static FmSynthSound MakeModulated(FilterInstance use)
		=> new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 75, maximum: 165, constant: true),
			new FmFilterNode(3, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.AllPass,
					0, "CutoffHz", 2),
			]),
		], 3), new EmptyResolver());

	private static FilterInstance MakeAllPass(string order)
	{
		FilterDefinition definition = new((ObjectId)178U, "Phase", [
			new FilterStageDefinition(1, "phase", FilterKind.AllPass),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("CutoffHz", FilterParameterValue.FromNumber(120)),
			new("Order", FilterParameterValue.FromChoice(order)),
		]);
		return use;
	}

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

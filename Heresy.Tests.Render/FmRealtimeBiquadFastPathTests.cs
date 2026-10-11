using System;
using System.Collections.Generic;
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
public sealed class FmRealtimeBiquadFastPathTests
{
	private const int SampleRate = 1000;

	[TestCase(FilterKind.BiquadLowPass)]
	[TestCase(FilterKind.BiquadHighPass)]
	[TestCase(FilterKind.BiquadBandPass)]
	[TestCase(FilterKind.BiquadNotch)]
	[TestCase(FilterKind.BiquadLowShelf)]
	[TestCase(FilterKind.BiquadHighShelf)]
	[TestCase(FilterKind.BiquadPeaking)]
	public void AllBiquadParametersMatchAtomicFullReconfigurationExactly(
		FilterKind kind)
	{
		bool shelf = IsShelf(kind);
		bool hasGain = shelf || kind == FilterKind.BiquadPeaking;
		FilterInstance use = OneBiquad(kind);
		List<FmFilterParameterConnection> wires =
		[
			new(1, kind, 0, "CutoffHz", 2),
			new(1, kind, 0, shelf ? "ShelfSlope" : "Q", shelf ? 4 : 3),
		];
		if (hasGain)
			wires.Add(new(1, kind, 0, "GainDb", 5));
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine,
				frequencyHz: 19, minimum: 80, maximum: 160, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine,
				frequencyHz: 31, minimum: 0.5, maximum: 1.5, constant: true),
			new FmOscillatorNode(4, FmOscillatorWaveform.Sine,
				frequencyHz: 29, minimum: 0.3, maximum: 1.2, constant: true),
			new FmOscillatorNode(5, FmOscillatorWaveform.Sine,
				frequencyHz: 23, minimum: -9, maximum: 9, constant: true),
			new FmFilterNode(6, 1, use, wires),
		], 6), new EmptyResolver());
		float[] actual = new float[240];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);

		StreamingFilterChain reference = new(new FilterChain([use]), SampleRate, 1);
		Span<float> sample = stackalloc float[1];
		double cutoffPhase = 0;
		double qPhase = 0;
		double slopePhase = 0;
		double gainPhase = 0;
		for (int frame = 0; frame < actual.Length; frame++)
		{
			double cutoff = Wave(ref cutoffPhase, 19, 120, 40);
			double q = Wave(ref qPhase, 31, 1, 0.5);
			double slope = Wave(ref slopePhase, 29, 0.75, 0.45);
			double gain = Wave(ref gainPhase, 23, 0, 9);
			FilterInstance next = use.Clone();
			List<KeyValuePair<string, FilterParameterValue>> parameters =
			[
				new("CutoffHz", FilterParameterValue.FromNumber(cutoff)),
				new(shelf ? "ShelfSlope" : "Q",
					FilterParameterValue.FromNumber(shelf ? slope : q)),
			];
			if (hasGain)
				parameters.Add(new("GainDb", FilterParameterValue.FromNumber(gain)));
			next.SetStageParameters(1, parameters);
			reference.Configure(new FilterChain([next]));
			sample[0] = 0.5f;
			reference.ProcessFrame(sample);
			Assert.That(actual[frame], Is.EqualTo(sample[0]),
				$"RBJ fast path differs from validated full Configure at frame {frame}, {kind}");
			use = next;
		}
	}

	[TestCase(FilterKind.BiquadLowPass)]
	[TestCase(FilterKind.BiquadHighPass)]
	[TestCase(FilterKind.BiquadBandPass)]
	[TestCase(FilterKind.BiquadNotch)]
	[TestCase(FilterKind.BiquadLowShelf)]
	[TestCase(FilterKind.BiquadHighShelf)]
	[TestCase(FilterKind.BiquadPeaking)]
	public void RealtimeBiquadCutoffDoesNotAllocatePerSample(FilterKind kind)
	{
		FilterInstance use = OneBiquad(kind);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.7),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine,
				frequencyHz: 19, minimum: 80, maximum: 160, constant: true),
			new FmFilterNode(3, 1, use, [
				new FmFilterParameterConnection(1, kind, 0, "CutoffHz", 2),
			]),
		], 3), new EmptyResolver());
		SoundState state = sound.CreateState();
		RenderContext context = Context();
		float[] warm = new float[80];
		sound.Render(context, state, 0, warm.Length, warm);
		float[] pcm = new float[4096];
		long before = GC.GetAllocatedBytesForCurrentThread();
		sound.Render(context, state, 80, pcm.Length, pcm);
		long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(bytes, Is.LessThan(4096),
			$"4096 audio-rate biquad modulation frames ({kind}) allocated {bytes} bytes.");
		Assert.That(float.IsFinite(pcm[0]), Is.True);
	}

	[Test]
	public void MixedTptAndBiquadUpdatesRejectInvalidSecondStageAtomically()
	{
		FilterDefinition definition = new((ObjectId)60U, "Mixed", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "bell", FilterKind.BiquadPeaking),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(100));
		use.SetStageParameter(2, "CutoffHz",
			FilterParameterValue.FromNumber(140));
		FmSynthSound invalid = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmConstantNode(2, 80),
			new FmConstantNode(3, 99),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.BiquadPeaking,
					0, "GainDb", 3),
			]),
		], 4), new EmptyResolver());
		FmSynthSound baseline = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmFilterNode(4, 1, use),
		], 4), new EmptyResolver());
		float[] a = new float[80];
		float[] b = new float[80];
		invalid.Render(Context(), invalid.CreateState(), 0, a.Length, a);
		baseline.Render(Context(), baseline.CreateState(), 0, b.Length, b);
		Assert.That(a, Is.EqualTo(b),
			"invalid biquad gain must leave upstream One-Pole history and settings untouched");
	}


	[Test]
	public void ShelfSlopeGainCombinationRejectsIndividuallyInRangeInvalidTarget()
	{
		FilterDefinition definition = new((ObjectId)64U, "Shelf constraints", [
			new FilterStageDefinition(1, "tpt", FilterKind.OnePole),
			new FilterStageDefinition(2, "shelf", FilterKind.BiquadHighShelf),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(100));
		use.SetStageParameters(2, [
			new("CutoffHz", FilterParameterValue.FromNumber(140)),
			new("GainDb", FilterParameterValue.FromNumber(30)),
		]);
		FmSynthSound invalid = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmConstantNode(2, 80),
			new FmConstantNode(3, 2),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.BiquadHighShelf,
					0, "ShelfSlope", 3),
			]),
		], 4), new EmptyResolver());
		FmSynthSound reference = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmFilterNode(4, 1, use),
		], 4), new EmptyResolver());
		float[] a = new float[64];
		float[] b = new float[64];
		invalid.Render(Context(), invalid.CreateState(), 0, a.Length, a);
		reference.Render(Context(), reference.CreateState(), 0, b.Length, b);
		Assert.That(a, Is.EqualTo(b),
			"30 dB gain plus slope 2 violates the RBJ shelf radicand; "
			+ "the in-range inputs must not partly retune the other stage");
	}

	[Test]
	public void RapidModulationWithUnchangedSecondBiquadPreservesInterpolation()
	{
		FilterDefinition definition = new((ObjectId)65U, "Two biquads", [
			new FilterStageDefinition(1, "first", FilterKind.BiquadLowPass),
			new FilterStageDefinition(2, "second", FilterKind.BiquadPeaking),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(120));
		use.SetStageParameter(2, "CutoffHz", FilterParameterValue.FromNumber(140));
		List<FmFilterParameterConnection> wires =
		[
			new(1, FilterKind.BiquadLowPass, 0, "CutoffHz", 2),
			new(2, FilterKind.BiquadPeaking, 0, "GainDb", 3),
		];
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.7),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine,
				frequencyHz: 17, minimum: 80, maximum: 160, constant: true),
			new FmConstantNode(3, 6),
			new FmFilterNode(4, 1, use, wires),
		], 4), new EmptyResolver());
		float[] actual = new float[180];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);

		StreamingFilterChain reference = new(new FilterChain([use]), SampleRate, 1);
		Span<float> sample = stackalloc float[1];
		double phase = 0;
		for (int frame = 0; frame < actual.Length; frame++)
		{
			double cutoff = Wave(ref phase, 17, 120, 40);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			next.SetStageParameter(2, "GainDb",
				FilterParameterValue.FromNumber(6));
			reference.Configure(new FilterChain([next]));
			sample[0] = 0.7f;
			reference.ProcessFrame(sample);
			Assert.That(actual[frame], Is.EqualTo(sample[0]),
				$"second unchanged stage must not be reconfigured at frame {frame}");
			use = next;
		}
	}

	[Test]
	public void ModulatedBiquadSplitAndBackwardSeekReconstructsSamePcm()
	{
		FilterInstance use = OneBiquad(FilterKind.BiquadLowShelf);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine,
				frequencyHz: 19, minimum: 80, maximum: 160, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine,
				frequencyHz: 23, minimum: -9, maximum: 9, constant: true),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.BiquadLowShelf,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(1, FilterKind.BiquadLowShelf,
					0, "GainDb", 3),
			]),
		], 4), new EmptyResolver());
		float[] reference = new float[300];
		sound.Render(Context(), sound.CreateState(), 0, reference.Length, reference);
		SoundState state = sound.CreateState();
		float[] split = new float[300];
		sound.Render(Context(), state, 0, 33, split.AsSpan(0, 33));
		sound.Render(Context(), state, 33, 267, split.AsSpan(33));
		Assert.That(split, Is.EqualTo(reference));
		float[] backwards = new float[100];
		sound.Render(Context(), state, 70, 100, backwards);
		Assert.That(backwards, Is.EqualTo(reference[70..170]));
	}

	private static bool IsShelf(FilterKind kind)
		=> kind is FilterKind.BiquadLowShelf or FilterKind.BiquadHighShelf;

	private static double Wave(ref double phase, double frequency,
		double midpoint, double amplitude)
	{
		double value = midpoint + amplitude * Math.Sin(2 * Math.PI * phase);
		phase += frequency / SampleRate;
		phase -= Math.Floor(phase);
		return value;
	}

	private static FilterInstance OneBiquad(FilterKind kind)
	{
		FilterDefinition definition = new((ObjectId)45U, "RBJ", [
			new FilterStageDefinition(1, "stage", kind),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(120));
		return use;
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

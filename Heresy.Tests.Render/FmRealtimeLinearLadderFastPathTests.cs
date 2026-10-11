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
public sealed class FmRealtimeLinearLadderFastPathTests
{
	private const int SampleRate = 1000;

	[TestCase(true, false)]
	[TestCase(false, true)]
	[TestCase(true, true)]
	public void HighRateCutoffAndResonanceMatchAtomicFullReconfigurationExactly(
		bool modulateCutoff, bool modulateResonance)
	{
		FilterInstance initial = MakeLadder();
		List<FmFilterParameterConnection> ports = [];
		if (modulateCutoff)
			ports.Add(new FmFilterParameterConnection(
				1, FilterKind.LadderLinear, 0, "CutoffHz", 2));
		if (modulateResonance)
			ports.Add(new FmFilterParameterConnection(
				1, FilterKind.LadderLinear, 0, "Resonance", 3));
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 75, maximum: 165, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 31,
				minimum: 0.02, maximum: 0.98, constant: true),
			new FmFilterNode(4, 1, initial, ports),
		], 4), new EmptyResolver());

		float[] actual = new float[340];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);

		StreamingFilterChain reference = new(
			new FilterChain([initial]), SampleRate, 1);
		FilterInstance previous = initial;
		double cutoffPhase = 0;
		double resonancePhase = 0;
		Span<float> frame = stackalloc float[1];
		for (int index = 0; index < actual.Length; index++)
		{
			double cutoff = Wave(ref cutoffPhase, 19, 120, 45);
			double resonance = Wave(ref resonancePhase, 31, 0.5, 0.48);
			FilterInstance next = previous.Clone();
			if (modulateCutoff && modulateResonance)
				next.SetStageParameters(1, [
					new("CutoffHz", FilterParameterValue.FromNumber(cutoff)),
					new("Resonance", FilterParameterValue.FromNumber(resonance)),
				]);
			else if (modulateCutoff)
				next.SetStageParameter(1, "CutoffHz",
					FilterParameterValue.FromNumber(cutoff));
			else
				next.SetStageParameter(1, "Resonance",
					FilterParameterValue.FromNumber(resonance));
			reference.Configure(new FilterChain([next]));
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[index], Is.EqualTo(frame[0]),
				$"linear Ladder frame {index}, cutoff={modulateCutoff}, "
				+ $"resonance={modulateResonance}");
			previous = next;
		}
	}

	[Test]
	public void ContinuousDualPortModulationAllocatesNoPerFrameObjects()
	{
		FilterInstance filter = MakeLadder();
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 75, maximum: 165, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 31,
				minimum: 0.02, maximum: 0.98, constant: true),
			new FmFilterNode(4, 1, filter, [
				new FmFilterParameterConnection(1, FilterKind.LadderLinear,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(1, FilterKind.LadderLinear,
					0, "Resonance", 3),
			]),
		], 4), new EmptyResolver());
		SoundState state = sound.CreateState();
		RenderContext context = Context();
		float[] warmup = new float[96];
		sound.Render(context, state, 0, warmup.Length, warmup);
		float[] pcm = new float[4096];
		long before = GC.GetAllocatedBytesForCurrentThread();
		sound.Render(context, state, 96, pcm.Length, pcm);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.LessThan(4096),
			"Linear Ladder cutoff+resonance must allocate no objects per "
			+ $"rendered frame (measured {allocated} bytes over 4096 frames).");
		foreach (float sample in pcm)
			Assert.That(float.IsFinite(sample), Is.True);
	}

	[TestCase(1.2)]
	[TestCase(-0.01)]
	public void InvalidResonanceRejectsEarlierMixedFamilyUpdates(double badResonance)
	{
		FilterDefinition definition = new((ObjectId)90U, "Mixed", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "bell", FilterKind.BiquadPeaking),
			new FilterStageDefinition(3, "ladder", FilterKind.LadderLinear),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(100));
		use.SetStageParameter(2, "CutoffHz",
			FilterParameterValue.FromNumber(110));
		use.SetStageParameters(3, [
			new("CutoffHz", FilterParameterValue.FromNumber(125)),
			new("Resonance", FilterParameterValue.FromNumber(0.35)),
		]);
		FmSynthSound invalid = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmConstantNode(2, 80),
			new FmConstantNode(3, 6),
			new FmConstantNode(4, badResonance),
			new FmFilterNode(5, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.BiquadPeaking,
					0, "GainDb", 3),
				new FmFilterParameterConnection(3, FilterKind.LadderLinear,
					0, "Resonance", 4),
			]),
		], 5), new EmptyResolver());
		FmSynthSound unchanged = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmFilterNode(5, 1, use),
		], 5), new EmptyResolver());
		float[] actual = new float[240];
		float[] expected = new float[240];
		invalid.Render(Context(), invalid.CreateState(), 0, actual.Length, actual);
		unchanged.Render(Context(), unchanged.CreateState(), 0,
			expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected),
			"invalid Ladder resonance must not change any other serial stage");
	}

	[Test]
	public void InvalidNyquistCutoffDoesNotChangeResonanceOrHistories()
	{
		FilterInstance use = MakeLadder();
		FmSynthSound invalid = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmConstantNode(2, 700),
			new FmConstantNode(3, 0.9),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.LadderLinear,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(1, FilterKind.LadderLinear,
					0, "Resonance", 3),
			]),
		], 4), new EmptyResolver());
		FmSynthSound baseline = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(4, 1, use),
		], 4), new EmptyResolver());
		float[] actual = new float[160];
		float[] expected = new float[160];
		invalid.Render(Context(), invalid.CreateState(), 0, actual.Length, actual);
		baseline.Render(Context(), baseline.CreateState(), 0,
			expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected));
	}

	[Test]
	public void MixedFamilyValidModulationMatchesFullConfiguration()
	{
		FilterDefinition definition = new((ObjectId)94U, "Three", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "ladder", FilterKind.LadderLinear),
			new FilterStageDefinition(3, "bell", FilterKind.BiquadPeaking),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(110));
		use.SetStageParameters(2, [
			new("CutoffHz", FilterParameterValue.FromNumber(120)),
			new("Resonance", FilterParameterValue.FromNumber(0.35)),
		]);
		use.SetStageParameter(3, "CutoffHz",
			FilterParameterValue.FromNumber(140));
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 17,
				minimum: 75, maximum: 165, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 29,
				minimum: 0.02, maximum: 0.98, constant: true),
			new FmOscillatorNode(4, FmOscillatorWaveform.Sine, 23,
				minimum: -8, maximum: 8, constant: true),
			new FmFilterNode(5, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.LadderLinear,
					0, "Resonance", 3),
				new FmFilterParameterConnection(3, FilterKind.BiquadPeaking,
					0, "GainDb", 4),
			]),
		], 5), new EmptyResolver());
		float[] actual = new float[200];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);
		StreamingFilterChain refChain = new(
			new FilterChain([use]), SampleRate, 1);
		Span<float> frame = stackalloc float[1];
		double cutPhase = 0, resPhase = 0, gainPhase = 0;
		for (int i = 0; i < actual.Length; i++)
		{
			double cutoff = Wave(ref cutPhase, 17, 120, 45);
			double resonance = Wave(ref resPhase, 29, 0.5, 0.48);
			double gain = Wave(ref gainPhase, 23, 0, 8);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			next.SetStageParameter(2, "Resonance",
				FilterParameterValue.FromNumber(resonance));
			next.SetStageParameter(3, "GainDb",
				FilterParameterValue.FromNumber(gain));
			refChain.Configure(new FilterChain([next]));
			frame[0] = 0.65f;
			refChain.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"mixed IIR frame {i}");
			use = next;
		}
	}

	[Test]
	public void ChunkingAndBackwardSeekAreDeterministicUnderDualPortModulation()
	{
		FilterInstance use = MakeLadder();
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 75, maximum: 165, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 31,
				minimum: 0.02, maximum: 0.98, constant: true),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.LadderLinear,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(1, FilterKind.LadderLinear,
					0, "Resonance", 3),
			]),
		], 4), new EmptyResolver());
		float[] reference = new float[350];
		sound.Render(Context(), sound.CreateState(), 0, reference.Length, reference);
		float[] split = new float[350];
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, 49, split.AsSpan(0, 49));
		sound.Render(Context(), state, 49, 17, split.AsSpan(49, 17));
		sound.Render(Context(), state, 66, 284, split.AsSpan(66));
		Assert.That(split, Is.EqualTo(reference));
		float[] backwards = new float[100];
		sound.Render(Context(), state, 75, 100, backwards);
		Assert.That(backwards, Is.EqualTo(reference[75..175]));
	}

	private static FilterInstance MakeLadder()
	{
		FilterDefinition definition = new((ObjectId)97U, "Ladder", [
			new FilterStageDefinition(1, "stage", FilterKind.LadderLinear),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("CutoffHz", FilterParameterValue.FromNumber(120)),
			new("Resonance", FilterParameterValue.FromNumber(0.35)),
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

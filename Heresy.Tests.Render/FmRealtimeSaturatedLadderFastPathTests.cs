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
public sealed class FmRealtimeSaturatedLadderFastPathTests
{
	private const int SampleRate = 1000;

	[TestCase("1x")]
	[TestCase("2x")]
	[TestCase("4x")]
	public void AllThreeNumericPortsMatchFullReconfigurationFrameForFrame(
		string oversampling)
	{
		FilterInstance use = MakeLadder(oversampling);
		FmSynthSound sound = Modulated(use);
		float[] actual = new float[300];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);

		StreamingFilterChain reference = new(
			new FilterChain([use]), SampleRate, 1);
		double cutoffPhase = 0, resonancePhase = 0, drivePhase = 0;
		Span<float> frame = stackalloc float[1];
		for (int i = 0; i < actual.Length; i++)
		{
			double cutoff = Wave(ref cutoffPhase, 19, 120, 45);
			double resonance = Wave(ref resonancePhase, 31, 0.5, 0.48);
			double drive = Wave(ref drivePhase, 23, 5.05, 4.95);
			FilterInstance next = use.Clone();
			next.SetStageParameters(1, [
				new("CutoffHz", FilterParameterValue.FromNumber(cutoff)),
				new("Resonance", FilterParameterValue.FromNumber(resonance)),
				new("Drive", FilterParameterValue.FromNumber(drive)),
			]);
			reference.Configure(new FilterChain([next]));
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"{oversampling}: frame {i} differs from full atomic Configure");
			use = next;
		}
	}

	[TestCase("1x")]
	[TestCase("2x")]
	[TestCase("4x")]
	public void DriveOnlyModulationPreservesAuthoredCutoffAndResonance(
		string oversampling)
	{
		FilterInstance use = MakeLadder(oversampling);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 23,
				minimum: 0.1, maximum: 10, constant: true),
			new FmFilterNode(3, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.LadderSaturated,
					0, "Drive", 2),
			]),
		], 3), new EmptyResolver());
		float[] actual = new float[165];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);
		StreamingFilterChain reference = new(new FilterChain([use]), SampleRate, 1);
		double phase = 0;
		Span<float> sample = stackalloc float[1];
		for (int frame = 0; frame < actual.Length; frame++)
		{
			double drive = Wave(ref phase, 23, 5.05, 4.95);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "Drive",
				FilterParameterValue.FromNumber(drive));
			reference.Configure(new FilterChain([next]));
			sample[0] = 0.65f;
			reference.ProcessFrame(sample);
			Assert.That(actual[frame], Is.EqualTo(sample[0]),
				$"{oversampling} drive-only frame {frame}");
			use = next;
		}
	}

	[TestCase("1x")]
	[TestCase("2x")]
	[TestCase("4x")]
	public void ThreePortAudioRateModulationDoesNotAllocatePerRenderedFrame(
		string oversampling)
	{
		FmSynthSound sound = Modulated(MakeLadder(oversampling));
		SoundState state = sound.CreateState();
		RenderContext context = Context();
		float[] warm = new float[100];
		sound.Render(context, state, 0, warm.Length, warm);
		float[] pcm = new float[4096];
		long before = GC.GetAllocatedBytesForCurrentThread();
		sound.Render(context, state, 100, pcm.Length, pcm);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.LessThan(4096),
			$"{oversampling}: {allocated} bytes for 4096 changing samples");
		foreach (float sample in pcm)
			Assert.That(float.IsFinite(sample), Is.True);
	}

	[TestCase("1x")]
	[TestCase("4x")]
	public void ValidMixedTptBiquadAndLadderUpdatesMatchFullConfiguration(
		string oversampling)
	{
		FilterDefinition definition = new((ObjectId)129U, "Mixed valid", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "bell", FilterKind.BiquadPeaking),
			new FilterStageDefinition(3, "linear", FilterKind.LadderLinear),
			new FilterStageDefinition(4, "saturated", FilterKind.LadderSaturated),
		]);
		FilterInstance use = new(definition);
		for (int stage = 1; stage <= 4; stage++)
			use.SetStageParameter(stage, "CutoffHz",
				FilterParameterValue.FromNumber(120));
		use.SetStageParameter(4, "Oversampling",
			FilterParameterValue.FromChoice(oversampling));
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 17,
				minimum: 75, maximum: 165, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 23,
				minimum: -9, maximum: 9, constant: true),
			new FmOscillatorNode(4, FmOscillatorWaveform.Sine, 29,
				minimum: 0.02, maximum: 0.98, constant: true),
			new FmOscillatorNode(5, FmOscillatorWaveform.Sine, 31,
				minimum: 0.1, maximum: 10, constant: true),
			new FmFilterNode(6, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.BiquadPeaking,
					0, "GainDb", 3),
				new FmFilterParameterConnection(3, FilterKind.LadderLinear,
					0, "Resonance", 4),
				new FmFilterParameterConnection(4, FilterKind.LadderSaturated,
					0, "Drive", 5),
			]),
		], 6), new EmptyResolver());
		float[] actual = new float[220];
		sound.Render(Context(), sound.CreateState(), 0, actual.Length, actual);

		StreamingFilterChain reference = new(new FilterChain([use]), SampleRate, 1);
		double cutoffPhase = 0, gainPhase = 0, resonancePhase = 0, drivePhase = 0;
		Span<float> sample = stackalloc float[1];
		for (int i = 0; i < actual.Length; i++)
		{
			double cutoff = Wave(ref cutoffPhase, 17, 120, 45);
			double gain = Wave(ref gainPhase, 23, 0, 9);
			double resonance = Wave(ref resonancePhase, 29, 0.5, 0.48);
			double drive = Wave(ref drivePhase, 31, 5.05, 4.95);
			FilterInstance next = use.Clone();
			next.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			next.SetStageParameter(2, "GainDb",
				FilterParameterValue.FromNumber(gain));
			next.SetStageParameter(3, "Resonance",
				FilterParameterValue.FromNumber(resonance));
			next.SetStageParameter(4, "Drive",
				FilterParameterValue.FromNumber(drive));
			reference.Configure(new FilterChain([next]));
			sample[0] = 0.65f;
			reference.ProcessFrame(sample);
			Assert.That(actual[i], Is.EqualTo(sample[0]),
				$"{oversampling}: mixed-family frame {i}");
			use = next;
		}
	}

	[TestCase("1x", "CutoffHz", 700.0)]
	[TestCase("2x", "Resonance", 1.1)]
	[TestCase("4x", "Drive", 11.0)]
	[TestCase("4x", "Drive", -0.5)]
	public void InvalidSaturatedPortRollsBackValidEarlierMixedFamilyStages(
		string oversampling, string invalidParameter, double invalidValue)
	{
		FilterDefinition def = new((ObjectId)119U, "Mixed", [
			new FilterStageDefinition(1, "one pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "bell", FilterKind.BiquadPeaking),
			new FilterStageDefinition(3, "linear", FilterKind.LadderLinear),
			new FilterStageDefinition(4, "nonlinear", FilterKind.LadderSaturated),
		]);
		FilterInstance use = new(def);
		for (int stage = 1; stage <= 4; stage++)
			use.SetStageParameter(stage, "CutoffHz",
				FilterParameterValue.FromNumber(120));
		use.SetStageParameter(4, "Oversampling",
			FilterParameterValue.FromChoice(oversampling));
		FmSynthSound invalid = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmConstantNode(2, 100),
			new FmConstantNode(3, 7),
			new FmConstantNode(4, 0.95),
			new FmConstantNode(5, invalidValue),
			new FmFilterNode(6, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.BiquadPeaking,
					0, "GainDb", 3),
				new FmFilterParameterConnection(3, FilterKind.LadderLinear,
					0, "Resonance", 4),
				new FmFilterParameterConnection(4, FilterKind.LadderSaturated,
					0, invalidParameter, 5),
			]),
		], 6), new EmptyResolver());
		FmSynthSound reference = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmFilterNode(6, 1, use),
		], 6), new EmptyResolver());
		float[] actual = new float[110];
		float[] expected = new float[110];
		invalid.Render(Context(), invalid.CreateState(), 0, actual.Length, actual);
		reference.Render(Context(), reference.CreateState(), 0,
			expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected),
			$"{oversampling} invalid {invalidParameter} must prevent all earlier commits");
	}

	[TestCase("1x")]
	[TestCase("2x")]
	[TestCase("4x")]
	public void BoundaryResonanceAndDriveRemainFiniteDuringFastModulation(
		string oversampling)
	{
		FilterInstance use = MakeLadder(oversampling);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.95),
			new FmConstantNode(2, 190),
			new FmConstantNode(3, 1),
			new FmConstantNode(4, 10),
			new FmFilterNode(5, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.LadderSaturated,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(1, FilterKind.LadderSaturated,
					0, "Resonance", 3),
				new FmFilterParameterConnection(1, FilterKind.LadderSaturated,
					0, "Drive", 4),
			]),
		], 5), new EmptyResolver());
		float[] pcm = new float[400];
		sound.Render(Context(), sound.CreateState(), 0, pcm.Length, pcm);
		foreach (float sample in pcm)
			Assert.That(float.IsFinite(sample), Is.True);
	}

	[TestCase("1x")]
	[TestCase("2x")]
	[TestCase("4x")]
	public void ChunkedAndBackwardSeekRenderExactlyMatchStraightThrough(
		string oversampling)
	{
		FmSynthSound sound = Modulated(MakeLadder(oversampling));
		float[] reference = new float[320];
		sound.Render(Context(), sound.CreateState(), 0,
			reference.Length, reference);
		SoundState state = sound.CreateState();
		float[] split = new float[320];
		sound.Render(Context(), state, 0, 23, split.AsSpan(0, 23));
		sound.Render(Context(), state, 23, 41, split.AsSpan(23, 41));
		sound.Render(Context(), state, 64, 256, split.AsSpan(64));
		Assert.That(split, Is.EqualTo(reference));
		float[] backward = new float[95];
		sound.Render(Context(), state, 67, backward.Length, backward);
		Assert.That(backward, Is.EqualTo(reference[67..162]));
	}

	private static FmSynthSound Modulated(FilterInstance use)
		=> new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 75, maximum: 165, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 31,
				minimum: 0.02, maximum: 0.98, constant: true),
			new FmOscillatorNode(4, FmOscillatorWaveform.Sine, 23,
				minimum: 0.1, maximum: 10, constant: true),
			new FmFilterNode(5, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.LadderSaturated,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(1, FilterKind.LadderSaturated,
					0, "Resonance", 3),
				new FmFilterParameterConnection(1, FilterKind.LadderSaturated,
					0, "Drive", 4),
			]),
		], 5), new EmptyResolver());

	private static FilterInstance MakeLadder(string oversampling)
	{
		FilterDefinition definition = new((ObjectId)121U, "Saturated", [
			new FilterStageDefinition(1, "nonlinear", FilterKind.LadderSaturated),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("CutoffHz", FilterParameterValue.FromNumber(120)),
			new("Resonance", FilterParameterValue.FromNumber(0.4)),
			new("Drive", FilterParameterValue.FromNumber(2)),
			new("Oversampling", FilterParameterValue.FromChoice(oversampling)),
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

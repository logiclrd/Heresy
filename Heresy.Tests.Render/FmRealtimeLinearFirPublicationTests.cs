using System;
using System.Collections.Generic;
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
public sealed class FmRealtimeLinearFirPublicationTests
{
	private const int Rate = 48000;
	private const int Boundary = 21;

	[TestCase(FilterKind.FirLowPass, 3)]
	[TestCase(FilterKind.FirLowPass, 2047)]
	[TestCase(FilterKind.FirHighPass, 127)]
	[TestCase(FilterKind.FirBandPass, 127)]
	[TestCase(FilterKind.FirNotch, 127)]
	public void AdmittedKernelMatchesFullConfigureExactlyIncludingConvolutionHistory(
		FilterKind kind, int taps)
	{
		FilterInstance use = Fir(kind, taps);
		FmSynthSound sound = Modulated(use, kind);
		SoundState state = sound.CreateState();
		RenderContext context = Context();
		float[] actual = new float[142];
		sound.Render(context, state, 0, Boundary, actual.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(context, state), Is.EqualTo(1),
			"admitted linear FIR batch is prepared only on the control side");
		sound.Render(context, state, Boundary, actual.Length - Boundary,
			actual.AsSpan(Boundary));

		StreamingFilterChain reference = new(new FilterChain([use]), Rate, 1);
		Span<float> frame = stackalloc float[1];
		for (int i = 0; i < actual.Length; i++)
		{
			if (i == Boundary)
			{
				use = Updated(use, kind);
				reference.Configure(new FilterChain([use]));
			}
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"{kind}, {taps} taps, sample {i}");
		}
		Assert.That(reference.PreparedTailFrames, Is.EqualTo(taps - 1));
		Assert.That(reference.PreparedLatencyFrames, Is.EqualTo((taps - 1) / 2));
	}

	[Test]
	public void PreparedFirAndValidIirChangesCommitTogether()
	{
		FilterDefinition definition = new((ObjectId)1001U, "Combined", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "fir", FilterKind.FirLowPass),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("CutoffHz", FilterParameterValue.FromNumber(1200)),
		]);
		use.SetStageParameters(2, [
			new("TapCount", FilterParameterValue.FromNumber(127)),
			new("CutoffHz", FilterParameterValue.FromNumber(3700)),
			new("TransitionWidthHz", FilterParameterValue.FromNumber(200)),
		]);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmConstantNode(2, 1700),
			new FmConstantNode(3, 4900),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.FirLowPass,
					0, "CutoffHz", 3),
			]),
		], 4), new EmptyResolver());
		float[] actual = new float[160];
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, Boundary, actual.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(Context(), state), Is.EqualTo(1));
		sound.Render(Context(), state, Boundary, actual.Length - Boundary,
			actual.AsSpan(Boundary));

		StreamingFilterChain reference = new(new FilterChain([use]), Rate, 1);
		Span<float> frame = stackalloc float[1];
		for (int i = 0; i < actual.Length; i++)
		{
			if (i == Boundary)
			{
				FilterInstance next = use.Clone();
				next.SetStageParameter(1, "CutoffHz",
					FilterParameterValue.FromNumber(1700));
				next.SetStageParameter(2, "CutoffHz",
					FilterParameterValue.FromNumber(4900));
				reference.Configure(new FilterChain([next]));
			}
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"mixed IIR/FIR sample {i}");
		}
	}

	[Test]
	public void InvalidIirPreventsPrepreparedFirFromBeingPublished()
	{
		FilterDefinition definition = new((ObjectId)1002U, "CombinedInvalid", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "fir", FilterKind.FirLowPass),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(1200));
		use.SetStageParameters(2, [
			new("TapCount", FilterParameterValue.FromNumber(127)),
			new("CutoffHz", FilterParameterValue.FromNumber(3700)),
		]);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmConstantNode(2, 30000),
			new FmConstantNode(3, 4900),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.FirLowPass,
					0, "CutoffHz", 3),
			]),
		], 4), new EmptyResolver());
		FmSynthSound reference = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(4, 1, use),
		], 4), new EmptyResolver());
		float[] actual = new float[160], baseline = new float[160];
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, Boundary, actual.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(Context(), state), Is.EqualTo(1));
		sound.Render(Context(), state, Boundary, actual.Length - Boundary,
			actual.AsSpan(Boundary));
		reference.Render(Context(), reference.CreateState(),
			0, baseline.Length, baseline);
		Assert.That(actual, Is.EqualTo(baseline));
	}

	[Test]
	public void ChangingAudioRateValuesRejectsStalePreparedKernel()
	{
		FilterInstance use = Fir(FilterKind.FirLowPass, 127);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 130,
				minimum: 3400, maximum: 5200, constant: true),
			new FmFilterNode(3, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.FirLowPass,
					0, "CutoffHz", 2),
			]),
		], 3), new EmptyResolver());
		SoundState state = sound.CreateState();
		float[] actual = new float[250], expected = new float[250];
		sound.Render(Context(), state, 0, Boundary,
			actual.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(Context(), state), Is.EqualTo(1));
		sound.Render(Context(), state, Boundary, actual.Length - Boundary,
			actual.AsSpan(Boundary));
		StreamingFilterChain baseline = new(new FilterChain([use]), Rate, 1);
		Span<float> frame = stackalloc float[1];
		for (int i = 0; i < expected.Length; i++)
		{
			frame[0] = 0.65f;
			baseline.ProcessFrame(frame);
			expected[i] = frame[0];
		}
		Assert.That(actual, Is.EqualTo(expected),
			"stale prepared candidates must not retune a newer cutoff sample");
	}

	[Test]
	public void PreparedLinearFirMatchesFullConfigureForStereoAndInterruptedRamps()
	{
		FilterInstance current = Fir(FilterKind.FirLowPass, 127);
		StreamingFilterChain fast = new(new FilterChain([current]), Rate, 2);
		StreamingFilterChain reference = new(new FilterChain([current]), Rate, 2);
		float[] before = new float[34 * 2];
		for (int i = 0; i < before.Length; i++)
			before[i] = (i % 9 == 0) ? 1.0f : 0.2f;
		float[] expectedBefore = (float[])before.Clone();
		fast.ProcessInterleaved(before);
		reference.ProcessInterleaved(expectedBefore);
		Assert.That(before, Is.EqualTo(expectedBefore));

		foreach ((int at, double cutoff) in new[] {
			(34, 5300.0), (43, 6200.0), (61, 4500.0)
		})
		{
			FirControlPreparationPolicy policy = new(Rate);
			Assert.That(policy.TryOffer([
				new FirControlRequest(0, 1, FilterKind.FirLowPass,
					cutoff, 0, 0, 127, 200, "Linear")
			]), Is.True);
			Assert.That(policy.TryPreparePending(current, out var batch), Is.True);
			Assert.That(fast.TryApplyRealtimeFirUpdates([
				new RealtimeFirStageUpdate(0, batch!.Stages[0].Plan)
			]), Is.True);
			current = current.Clone();
			current.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff));
			reference.Configure(new FilterChain([current]));
			int samples = (at == 34 ? 9 : at == 43 ? 18 : 50) * 2;
			float[] actual = new float[samples], expected = new float[samples];
			for (int i = 0; i < samples; i++)
				actual[i] = expected[i] = (i % 13 == 0 ? 0.8f : 0.15f);
			fast.ProcessInterleaved(actual);
			reference.ProcessInterleaved(expected);
			Assert.That(actual, Is.EqualTo(expected),
				$"interrupted crossfade at frame {at}");
		}
	}

	[Test]
	public void BackwardSeekAndExplicitRepreparationReproduceAcceptedPcm()
	{
		FmSynthSound sound = Modulated(Fir(FilterKind.FirBandPass, 127),
			FilterKind.FirBandPass);
		SoundState state = sound.CreateState();
		float[] first = new float[190], second = new float[190];
		sound.Render(Context(), state, 0, Boundary,
			first.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(Context(), state),
			Is.EqualTo(1));
		sound.Render(Context(), state, Boundary,
			first.Length - Boundary, first.AsSpan(Boundary));

		// A backward seek resets the note's mutable DSP and any pending
		// plans. Replay the same absolute control boundary to reproduce
		// the accepted coefficient and convolution history trajectory.
		sound.Render(Context(), state, 0, Boundary,
			second.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(Context(), state),
			Is.EqualTo(1));
		sound.Render(Context(), state, Boundary,
			second.Length - Boundary, second.AsSpan(Boundary));
		Assert.That(second, Is.EqualTo(first));
	}

	[Test]
	public void AcceptedPcmLoopIsAllocationFreeAndReplaysWithIdenticalControlBoundaries()
	{
		FmSynthSound sound = Modulated(Fir(FilterKind.FirLowPass, 127),
			FilterKind.FirLowPass);
		float[] a = Scheduled(sound), b = Scheduled(sound);
		Assert.That(a, Is.EqualTo(b));

		SoundState state = sound.CreateState();
		float[] warmup = new float[Boundary];
		sound.Render(Context(), state, 0, Boundary, warmup);
		Assert.That(sound.PreparePendingFilterControls(Context(), state), Is.EqualTo(1));
		float[] pcm = new float[4096];
		long before = GC.GetAllocatedBytesForCurrentThread();
		sound.Render(Context(), state, Boundary, pcm.Length, pcm);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.LessThan(4096),
			$"admitted 127-tap PCM crossfade allocated {allocated} bytes");
	}

	private static float[] Scheduled(FmSynthSound sound)
	{
		float[] result = new float[260];
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, Boundary, result.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(Context(), state), Is.EqualTo(1));
		sound.Render(Context(), state, Boundary, 43, result.AsSpan(Boundary, 43));
		sound.Render(Context(), state, 64, 196, result.AsSpan(64));
		return result;
	}

	private static FmSynthSound Modulated(FilterInstance instance, FilterKind kind)
	{
		bool band = kind is FilterKind.FirBandPass or FilterKind.FirNotch;
		return new FmSynthSound(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmConstantNode(2, band ? 3300 : 4900),
			new FmConstantNode(3, 8800),
			new FmFilterNode(4, 1, instance,
				band
					? [
						new FmFilterParameterConnection(1, kind, 0, "LowCutoffHz", 2),
						new FmFilterParameterConnection(1, kind, 0, "HighCutoffHz", 3),
					]
					: [new FmFilterParameterConnection(1, kind, 0, "CutoffHz", 2)]),
		], 4), new EmptyResolver());
	}

	private static FilterInstance Fir(FilterKind kind, int taps)
	{
		FilterDefinition definition = new((ObjectId)1000U, "Fir", [
			new FilterStageDefinition(1, "fir", kind),
		]);
		FilterInstance result = new(definition);
		result.SetStageParameters(1, [
			new("TapCount", FilterParameterValue.FromNumber(taps)),
			new("TransitionWidthHz", FilterParameterValue.FromNumber(200)),
			new("PhaseResponse", FilterParameterValue.FromChoice("Linear")),
		]);
		if (kind is FilterKind.FirBandPass or FilterKind.FirNotch)
			result.SetStageParameters(1, [
				new("LowCutoffHz", FilterParameterValue.FromNumber(2500)),
				new("HighCutoffHz", FilterParameterValue.FromNumber(8200)),
			]);
		else
			result.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(3700));
		return result;
	}

	private static FilterInstance Updated(FilterInstance use, FilterKind kind)
	{
		FilterInstance next = use.Clone();
		if (kind is FilterKind.FirBandPass or FilterKind.FirNotch)
			next.SetStageParameters(1, [
				new("LowCutoffHz", FilterParameterValue.FromNumber(3300)),
				new("HighCutoffHz", FilterParameterValue.FromNumber(8800)),
			]);
		else
			next.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(4900));
		return next;
	}

	private static RenderContext Context()
		=> new(new RenderConfiguration(Rate, [
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

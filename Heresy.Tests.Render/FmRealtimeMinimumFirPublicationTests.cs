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
public sealed class FmRealtimeMinimumFirPublicationTests
{
	private const int Rate = 48000;
	private const int Boundary = 21;

	[TestCase(FilterKind.FirLowPass, 31)]
	[TestCase(FilterKind.FirLowPass, 127)]
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
			"admitted minimum-phase FIR batch is prepared only on the control side");
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
		Assert.That(reference.PreparedLatencyFrames, Is.EqualTo(0));
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
			new("PhaseResponse", FilterParameterValue.FromChoice("Minimum")),
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
			new("PhaseResponse", FilterParameterValue.FromChoice("Minimum")),
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
	public void PreparedMinimumFirMatchesFullConfigureForStereoAndInterruptedRamps()
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
					cutoff, 0, 0, 127, 200, "Minimum")
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


	[Test]
	public void MinimumAndMaximumHaveSameFixedDelayButCannotPublishAcrossPhase()
	{
		FilterInstance minimum = Fir(FilterKind.FirLowPass, 127);
		StreamingFilterChain active = new(new FilterChain([minimum]), Rate, 1);
		StreamingFilterChain baseline = new(new FilterChain([minimum]), Rate, 1);
		float[] lead = Enumerable.Repeat(0.2f, 45).ToArray();
		float[] same = (float[])lead.Clone();
		active.ProcessInterleaved(lead);
		baseline.ProcessInterleaved(same);
		Assert.That(active.PreparedLatencyFrames, Is.Zero);
		Assert.That(active.PreparedTailFrames, Is.EqualTo(126));
		foreach (string phase in new[] { "Maximum", "Linear" })
		{
			FilterInstance other = minimum.Clone();
			other.SetStageParameter(1, "PhaseResponse",
				FilterParameterValue.FromChoice(phase));
			FirControlPreparationPolicy policy = new(Rate);
			Assert.That(policy.TryOffer([
				new FirControlRequest(0, 1, FilterKind.FirLowPass,
					4900, 0, 0, 127, 200, phase)
			]), Is.True);
			Assert.That(policy.TryPreparePending(other, out var batch), Is.True);
			Assert.That(active.TryApplyRealtimeFirUpdates([
				new RealtimeFirStageUpdate(0, batch!.Stages[0].Plan)
			]), Is.False, "a structural phase change must reset DSP history");
		}
		float[] after = Enumerable.Repeat(0.7f, 91).ToArray();
		float[] expected = (float[])after.Clone();
		active.ProcessInterleaved(after);
		baseline.ProcessInterleaved(expected);
		Assert.That(after, Is.EqualTo(expected));
	}

	[TestCase("Window")]
	[TestCase("TransitionWidthHz")]
	public void PreparedPlanCannotSilentlyReplaceOtherStructuralSettings(string property)
	{
		FilterInstance current = Fir(FilterKind.FirLowPass, 127);
		FilterInstance modified = current.Clone();
		if (property == "Window")
			modified.SetStageParameter(1, property,
				FilterParameterValue.FromChoice("Blackman"));
		else
			modified.SetStageParameter(1, property,
				FilterParameterValue.FromNumber(400));
		FirControlPreparationPolicy policy = new(Rate);
		Assert.That(policy.TryOffer([
			new FirControlRequest(0, 1, FilterKind.FirLowPass,
				4900, 0, 0, 127, property == "Window" ? 200 : 400, "Minimum")
		]), Is.True);
		Assert.That(policy.TryPreparePending(modified, out var batch), Is.True);
		StreamingFilterChain active = new(new FilterChain([current]), Rate, 1);
		Assert.That(active.TryApplyRealtimeFirUpdates([
			new RealtimeFirStageUpdate(0, batch!.Stages[0].Plan)
		]), Is.False);
	}

	[Test]
	public void MaximumIsNotYetConsumableAtFmControlBoundary()
	{
		FilterInstance maximum = Fir(FilterKind.FirLowPass, 127);
		maximum.SetStageParameter(1, "PhaseResponse",
			FilterParameterValue.FromChoice("Maximum"));
		FmSynthSound sound = Modulated(maximum, FilterKind.FirLowPass);
		FmSynthSound baselineSound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(4, 1, maximum),
		], 4), new EmptyResolver());
		float[] actual = new float[145], expected = new float[145];
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, Boundary, actual.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(Context(), state), Is.Zero);
		sound.Render(Context(), state, Boundary, actual.Length - Boundary,
			actual.AsSpan(Boundary));
		baselineSound.Render(Context(), baselineSound.CreateState(),
			0, expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected));
	}

	[Test]
	public void AboveBudgetMinimumPhaseUpdateRetainsOriginalKernel()
	{
		FilterInstance large = Fir(FilterKind.FirLowPass, 129);
		FmSynthSound sound = Modulated(large, FilterKind.FirLowPass);
		FmSynthSound baselineSound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(4, 1, large),
		], 4), new EmptyResolver());
		float[] actual = new float[180], expected = new float[180];
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, Boundary, actual.AsSpan(0, Boundary));
		Assert.That(sound.PreparePendingFilterControls(Context(), state), Is.Zero,
			"129 taps require an 8192-element FFT, exceeding the unchanged work budget");
		sound.Render(Context(), state, Boundary, actual.Length - Boundary,
			actual.AsSpan(Boundary));
		baselineSound.Render(Context(), baselineSound.CreateState(),
			0, expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected));
	}

	[Test]
	public void MinimumAndLinearPreparedStagesPublishTogetherAsOneBatch()
	{
		FilterDefinition definition = new((ObjectId)1003U, "TwoFir", [
			new FilterStageDefinition(1, "min", FilterKind.FirLowPass),
			new FilterStageDefinition(2, "lin", FilterKind.FirHighPass),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("TapCount", FilterParameterValue.FromNumber(127)),
			new("TransitionWidthHz", FilterParameterValue.FromNumber(200)),
			new("PhaseResponse", FilterParameterValue.FromChoice("Minimum")),
			new("CutoffHz", FilterParameterValue.FromNumber(3700)),
		]);
		use.SetStageParameters(2, [
			new("TapCount", FilterParameterValue.FromNumber(63)),
			new("TransitionWidthHz", FilterParameterValue.FromNumber(200)),
			new("PhaseResponse", FilterParameterValue.FromChoice("Linear")),
			new("CutoffHz", FilterParameterValue.FromNumber(9800)),
		]);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmConstantNode(2, 4900),
			new FmConstantNode(3, 8800),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.FirLowPass,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.FirHighPass,
					0, "CutoffHz", 3),
			]),
		], 4), new EmptyResolver());
		float[] actual = new float[158];
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
					FilterParameterValue.FromNumber(4900));
				next.SetStageParameter(2, "CutoffHz",
					FilterParameterValue.FromNumber(8800));
				reference.Configure(new FilterChain([next]));
			}
			frame[0] = 0.65f;
			reference.ProcessFrame(frame);
			Assert.That(actual[i], Is.EqualTo(frame[0]),
				$"mixed Minimum/Linear sample {i}");
		}
		Assert.That(reference.PreparedLatencyFrames, Is.EqualTo(31),
			"only the 63-tap linear stage has fixed reported group delay");
		Assert.That(reference.PreparedTailFrames, Is.EqualTo(188));
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
			new("PhaseResponse", FilterParameterValue.FromChoice("Minimum")),
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

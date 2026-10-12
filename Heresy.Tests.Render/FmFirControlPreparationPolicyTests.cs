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
public sealed class FmFirControlPreparationPolicyTests
{
	private const int Rate = 48000;

	[TestCase(FilterKind.FirLowPass, "Linear", 2047)]
	[TestCase(FilterKind.FirHighPass, "Linear", 127)]
	[TestCase(FilterKind.FirBandPass, "Minimum", 127)]
	[TestCase(FilterKind.FirNotch, "Maximum", 127)]
	public void ControlSideCanPrepareUnderBudgetWithoutTouchingActiveState(
		FilterKind kind, string phase, int taps)
	{
		FilterInstance authored = MakeFilter(kind, phase, taps);
		FirControlPreparationPolicy policy = new(Rate);
		FirControlRequest update = Request(kind, phase, taps,
			cutoff: 3700, low: 3500, high: 8300);
		Assert.That(policy.TryOffer([update]), Is.True);
		Assert.That(policy.PendingCount, Is.EqualTo(1));
		Assert.That(policy.TryPreparePending(authored, out var prepared), Is.True);
		Assert.That(prepared, Is.Not.Null);
		Assert.That(prepared!.Stages.Count, Is.EqualTo(1));
		Assert.That(prepared.Stages[0].StageIndex, Is.Zero);
		Assert.That(prepared.Stages[0].Plan.TailFrames,
			Is.EqualTo(taps - 1));
		Assert.That(policy.PendingCount, Is.Zero);
		Assert.That(policy.TryPreparePending(authored, out var exhausted), Is.False);
		Assert.That(exhausted, Is.Null);

		// Merely preparing a new plan cannot mutate an active chain.
		StreamingFilterChain running = new(new FilterChain([authored]), Rate, 1);
		StreamingFilterChain baseline = running.Fork();
		float[] inputA = new float[256], inputB = new float[256];
		inputA[0] = inputB[0] = 1;
		running.ProcessInterleaved(inputA);
		baseline.ProcessInterleaved(inputB);
		Assert.That(inputA, Is.EqualTo(inputB));
	}

	[Test]
	public void LargeSpectralFactorizationIsDeclinedBeforePreparation()
	{
		FirControlPreparationPolicy policy = new(Rate);
		FirControlRequest large = Request(FilterKind.FirLowPass,
			"Minimum", 2047, cutoff: 4000);
		Assert.That(policy.TryOffer([large]), Is.False);
		Assert.That(policy.PendingCount, Is.Zero);
		Assert.That(policy.TryPreparePending(
			MakeFilter(FilterKind.FirLowPass, "Minimum", 2047),
			out var plan), Is.False);
		Assert.That(plan, Is.Null);
		Assert.That(FirControlPreparationPolicy.MaximumPreparationWorkUnits,
			Is.EqualTo(131072));
		Assert.That(FirControlPreparationPolicy.MaximumTemporaryBytes,
			Is.EqualTo(2 * 1024 * 1024));
	}

	[TestCase(FilterKind.FirLowPass, 20001, 0, 0)]
	[TestCase(FilterKind.FirHighPass, 20, 0, 0)]
	[TestCase(FilterKind.FirBandPass, 0, 6000, 5000)]
	[TestCase(FilterKind.FirBandPass, 0, 50, 4200)]
	[TestCase(FilterKind.FirNotch, 0, 4100, 4200)]
	public void InvalidNyquistAndCoupledBandEdgesNeverEnterQueue(
		FilterKind kind, double cutoff, double low, double high)
	{
		FirControlPreparationPolicy policy = new(Rate);
		var valid = Request(FilterKind.FirLowPass, "Linear", 127,
			cutoff: 4200);
		Assert.That(policy.TryOffer([valid]), Is.True);
		Assert.That(policy.TryOffer([Request(kind, "Linear", 127,
			cutoff, low, high)]), Is.False);
		Assert.That(policy.PendingCount, Is.Zero,
			"invalid latest proposal cancels stale pending work");
	}

	[Test]
	public void LatestWinsAndNoUnboundedQueueOrHeapAllocation()
	{
		FirControlPreparationPolicy policy = new(Rate);
		FirControlRequest[] frame = [Request(FilterKind.FirLowPass,
			"Linear", 127, cutoff: 4000)];
		for (int i = 0; i < 150; i++)
		{
			frame[0] = Request(FilterKind.FirLowPass, "Linear", 127,
				cutoff: 4000 + i);
			Assert.That(policy.TryOffer(frame), Is.True);
			Assert.That(policy.PendingCount, Is.EqualTo(1));
		}
		FilterInstance authored = MakeFilter(FilterKind.FirLowPass,
			"Linear", 127);
		Assert.That(policy.TryPreparePending(authored, out var output), Is.True);
		Assert.That(output!.Stages.Count, Is.EqualTo(1));

		// Warm up JIT first, then measure 4096 sample-control offers with
		// no implementation-owned managed allocations.
		for (int i = 0; i < 32; i++)
			policy.TryOffer(frame);
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 4096; i++)
		{
			frame[0] = Request(FilterKind.FirLowPass, "Linear", 127,
				cutoff: 3000 + i % 1700);
			policy.TryOffer(frame);
		}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.LessThan(4096),
			$"PCM proposals allocated {allocated} bytes");
		Assert.That(policy.PendingCount, Is.EqualTo(1));
	}

	[Test]
	public void BatchStageCapacityAndDuplicateRequestsAreAtomic()
	{
		FirControlPreparationPolicy policy = new(Rate);
		FirControlRequest one = Request(FilterKind.FirLowPass,
			"Linear", 127, cutoff: 4000);
		Assert.That(policy.TryOffer([one]), Is.True);
		Assert.That(policy.TryOffer([one, one]), Is.False);
		Assert.That(policy.PendingCount, Is.Zero);

		FirControlRequest[] overfull = new FirControlRequest[5];
		for (int i = 0; i < overfull.Length; i++)
			overfull[i] = one with { StageIndex = i, StageId = i + 1 };
		Assert.That(policy.TryOffer(overfull), Is.False);
		Assert.That(policy.PendingCount, Is.Zero);
		Assert.That(FirControlPreparationPolicy.MaximumQueuedStages,
			Is.EqualTo(4));
	}

	[Test]
	public void MixedBatchNeverPublishesIfOneKernelCannotBePrepared()
	{
		FilterDefinition definition = new((ObjectId)904U, "TwoFir", [
			new FilterStageDefinition(1, "first", FilterKind.FirLowPass),
			new FilterStageDefinition(2, "second", FilterKind.FirLowPass),
		]);
		FilterInstance authored = new(definition);
		foreach (int id in new[] { 1, 2 })
		{
			authored.SetStageParameters(id, [
				new("CutoffHz", FilterParameterValue.FromNumber(4000)),
				new("TapCount", FilterParameterValue.FromNumber(3)),
				new("TransitionWidthHz", FilterParameterValue.FromNumber(1)),
			]);
		}
		// The structural/domain checks pass, but a 3-tap 10 Hz filter
		// cannot normalize its nearly zero passband.
		FirControlPreparationPolicy policy = new(Rate);
		FirControlRequest first = Request(FilterKind.FirLowPass,
			"Linear", 3, cutoff: 4200);
		FirControlRequest second = first with {
			StageIndex = 1, StageId = 2, CutoffHz = 10
		};
		Assert.That(policy.TryOffer([first, second]), Is.True);
		Assert.That(policy.TryPreparePending(authored,
			out PreparedFirControlBatch? prepared), Is.False);
		Assert.That(prepared, Is.Null);
		Assert.That(policy.PendingCount, Is.Zero);
	}

	[Test]
	public void LiveFmRequestsHoldOriginalFirKernelAndMixedIirUntilPreprepared()
	{
		FilterDefinition definition = new((ObjectId)905U, "FirWithIir", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
			new FilterStageDefinition(2, "fir", FilterKind.FirLowPass),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(2000));
		use.SetStageParameters(2, [
			new("TapCount", FilterParameterValue.FromNumber(127)),
			new("CutoffHz", FilterParameterValue.FromNumber(3800)),
		]);
		FmSynthSound modulated = new(new FmSynthGraph([
			new FmConstantNode(1, 0.7),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 4000, maximum: 6000, constant: true),
			new FmConstantNode(3, 500),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 3),
				new FmFilterParameterConnection(2, FilterKind.FirLowPass,
					0, "CutoffHz", 2),
			]),
		], 4), new EmptyResolver());
		FmSynthSound baseline = new(new FmSynthGraph([
			new FmConstantNode(1, 0.7),
			new FmFilterNode(4, 1, use),
		], 4), new EmptyResolver());
		float[] actual = new float[350], expected = new float[350];
		modulated.Render(Context(), modulated.CreateState(),
			0, actual.Length, actual);
		baseline.Render(Context(), baseline.CreateState(),
			0, expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected),
			"unprepared FIR changes must block any simultaneous IIR change");

		SoundState state = modulated.CreateState();
		float[] warm = new float[90];
		modulated.Render(Context(), state, 0, warm.Length, warm);
		float[] pcm = new float[4096];
		long before = GC.GetAllocatedBytesForCurrentThread();
		modulated.Render(Context(), state, 90, pcm.Length, pcm);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(allocated, Is.LessThan(4096),
			$"FIR+IIR live fallback allocated {allocated} bytes");
	}

	[Test]
	public void LiveFirFallbackIsIndependentOfChunkingAndBackwardSeeking()
	{
		FilterInstance use = MakeFilter(FilterKind.FirHighPass,
			"Linear", 127);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.6),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 2600, maximum: 5800, constant: true),
			new FmFilterNode(3, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.FirHighPass,
					0, "CutoffHz", 2),
			]),
		], 3), new EmptyResolver());
		float[] whole = new float[400], chunks = new float[400];
		sound.Render(Context(), sound.CreateState(), 0, 400, whole);
		SoundState state = sound.CreateState();
		sound.Render(Context(), state, 0, 37, chunks.AsSpan(0, 37));
		sound.Render(Context(), state, 37, 19, chunks.AsSpan(37, 19));
		sound.Render(Context(), state, 56, 344, chunks.AsSpan(56));
		Assert.That(chunks, Is.EqualTo(whole));
		float[] previous = new float[80];
		sound.Render(Context(), state, 33, 80, previous);
		Assert.That(previous, Is.EqualTo(whole[33..113]));
	}

	private static FirControlRequest Request(FilterKind kind, string phase,
		int taps, double cutoff = 4000, double low = 3000, double high = 8000)
		=> new(0, 1, kind, cutoff, low, high, taps, 200, phase);

	private static FilterInstance MakeFilter(FilterKind kind,
		string phase, int taps)
	{
		FilterDefinition definition = new((ObjectId)906U, "FIR", [
			new FilterStageDefinition(1, "stage", kind),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("TapCount", FilterParameterValue.FromNumber(taps)),
			new("PhaseResponse", FilterParameterValue.FromChoice(phase)),
		]);
		if (kind is FilterKind.FirBandPass or FilterKind.FirNotch)
			use.SetStageParameters(1, [
				new("LowCutoffHz", FilterParameterValue.FromNumber(3000)),
				new("HighCutoffHz", FilterParameterValue.FromNumber(8000)),
			]);
		else
			use.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(4000));
		return use;
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

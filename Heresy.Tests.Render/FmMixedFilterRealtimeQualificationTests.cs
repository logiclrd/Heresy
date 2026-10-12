using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

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

/// <summary>
/// Qualification workloads, not a universal real-time certification:
/// reproducible fixed-frame work, managed allocation, numerical rejection,
/// serial-family atomicity, prepared-FIR admission, invocation independence
/// and explicitly scheduled seek. Print runner platform and timings; hosted
/// CI is noisy and does not provide guaranteed audio-device deadlines.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class FmMixedFilterRealtimeQualificationTests
{
	private const int Rate = 48000;
	private const int Frames = 3072;
	private const int Block = 256;

	[TestCase(127, 1)]
	[TestCase(127, 4)]
	[TestCase(2047, 1)]
	[TestCase(2047, 4)]
	public void MixedEightNumericFamiliesMeasureRealtimeWorkerBudget(
		int taps, int voiceCount)
	{
		FmSynthSound sound = MixedSound(MakeMixed(taps), connectFir: false);
		RenderContext context = Context();
		SoundState[] states = Enumerable.Range(0, voiceCount)
			.Select(_ => sound.CreateState()).ToArray();
		float[][] pcm = Enumerable.Range(0, voiceCount)
			.Select(_ => new float[Frames]).ToArray();
		float[] warm = new float[128];
		foreach (SoundState state in states)
		{
			warm.AsSpan().Clear();
			sound.Render(context, state, 0, warm.Length, warm);
		}

		// Stopwatch ticks are captured per block without allocating within
		// the timed region. Multiple independent invocations exercise separate
		// chains, scratch, histories and the 4x nonlinear ladder solver.
		long[] blockTicks = new long[voiceCount * (Frames / Block)];
		long[] quantumTicks = new long[Frames / Block];
		Process process = Process.GetCurrentProcess();
		TimeSpan cpuBefore = process.TotalProcessorTime;
		long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		long wallBefore = Stopwatch.GetTimestamp();
		int measurement = 0;
		for (int offset = 0; offset < Frames; offset += Block)
		{
			long quantumStart = Stopwatch.GetTimestamp();
			for (int voice = 0; voice < voiceCount; voice++)
			{
				long tick = Stopwatch.GetTimestamp();
				sound.Render(context, states[voice], 128 + offset, Block,
					pcm[voice].AsSpan(offset, Block));
				blockTicks[measurement++] = Stopwatch.GetTimestamp() - tick;
			}
			quantumTicks[offset / Block] = Stopwatch.GetTimestamp() - quantumStart;
		}
		long elapsedTicks = Stopwatch.GetTimestamp() - wallBefore;
		long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
		Array.Sort(blockTicks);
		Array.Sort(quantumTicks);
		double quantumP95Ms = ToMilliseconds(quantumTicks[
			(int)Math.Ceiling(quantumTicks.Length * 0.95) - 1]);
		double quantumMaxMs = ToMilliseconds(quantumTicks[^1]);
		double wallMs = ToMilliseconds(elapsedTicks);
		double maximumBlockMs = ToMilliseconds(blockTicks[^1]);
		double p95BlockMs = ToMilliseconds(blockTicks[(int)Math.Ceiling(
			blockTicks.Length * 0.95) - 1]);
		double audioMs = 1000.0 * Frames / Rate;
		double computeToAudioRatio = wallMs / audioMs;
		double worstQuantumToDeadline = quantumMaxMs / (1000.0 * Block / Rate);
		TestContext.Progress.WriteLine(
			$"FM-REALTIME-QUALIFICATION platform={RuntimeInformation.OSDescription}; " +
			$"architecture={RuntimeInformation.ProcessArchitecture}; " +
			$"framework={RuntimeInformation.FrameworkDescription}; " +
			$"processors={Environment.ProcessorCount}; rate={Rate}; voices={voiceCount}; " +
			$"framesPerVoice={Frames}; block={Block}; stages=9; firTaps={taps}; " +
			$"ladderOversampling=4x; wallMs={wallMs:F3}; cpuMs={cpu.TotalMilliseconds:F3}; " +
			$"voiceBlockP95Ms={p95BlockMs:F4}; voiceBlockMaxMs={maximumBlockMs:F4}; " +
			$"quantumP95Ms={quantumP95Ms:F4}; quantumMaxMs={quantumMaxMs:F4}; " +
			$"wallToAudioRatio={computeToAudioRatio:F3}; " +
			$"maxQuantumToDeadlineRatio={worstQuantumToDeadline:F3}; " +
			$"managedBytes={allocated}; " +
			$"pcmFIRWorkBound={voiceCount * Frames * (long)taps} tap-multiply-adds " +
			"(plus 8 other serial DSP stages and FM graph per frame)");

		// A strict zero managed-allocation budget applies to the warmed
		// current-thread steady PCM region. Timing numbers are observations
		// only: shared-hosted CI timings must not assert RT deadline safety.
		Assert.That(allocated, Is.Zero,
			"steady PCM blocks of existing voice states must not allocate");
		foreach (float[] voice in pcm)
		{
			Assert.That(voice.All(float.IsFinite), Is.True);
			Assert.That(voice.Any(value => value != 0), Is.True);
		}
	}

	[Test]
	public void EightChangingIirFamiliesAreAtomicallyHeldWhen2047TapFirIsUnprepared()
	{
		FilterInstance configured = MakeMixed(2047);
		FmSynthSound modulated = MixedSound(configured, connectFir: true);
		FmSynthSound baseline = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(7, 1, configured),
		], 7), new EmptyResolver());
		RenderContext context = Context();
		float[] actual = new float[540], expected = new float[540];
		SoundState state = modulated.CreateState();
		modulated.Render(context, state, 0, 120, actual.AsSpan(0, 120));
		// No automatic preparation is legal at a Render/chunk boundary;
		// holding the 2047-tap FIR must also hold every other dirty stage.
		modulated.Render(context, state, 120, 420, actual.AsSpan(120));
		baseline.Render(context, baseline.CreateState(), 0,
			expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected));
		Assert.That(actual.All(float.IsFinite), Is.True);
		Assert.That(FirControlPreparationPolicy.MaximumPreparationWorkUnits,
			Is.EqualTo(131072));
		Assert.That(FirControlPreparationPolicy.MaximumTemporaryBytes,
			Is.EqualTo(2 * 1024 * 1024));
	}

	[Test]
	public void OverBudgetMaximumPhaseAndInvalidValuesNeverPartiallyUpdateTheChain()
	{
		FilterInstance configured = MakeMixed(129, "Maximum");
		FmSynthSound modulated = MixedSound(configured, connectFir: true);
		FmSynthSound baseline = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(7, 1, configured),
		], 7), new EmptyResolver());
		RenderContext context = Context();
		SoundState state = modulated.CreateState();
		float[] actual = new float[260], expected = new float[260];
		modulated.Render(context, state, 0, 70, actual.AsSpan(0, 70));
		Assert.That(modulated.PreparePendingFilterControls(context, state), Is.Zero,
			"129-tap nonlinear-phase FFT requires 132620 work units");
		modulated.Render(context, state, 70, 190, actual.AsSpan(70));
		baseline.Render(context, baseline.CreateState(), 0,
			expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected));
		FirControlPreparationPolicy policy = new(Rate);
		FirControlRequest valid = new(8, 9, FilterKind.FirLowPass,
			4900, 0, 0, 127, 200, "Maximum");
		Assert.That(policy.TryOffer([valid]), Is.True);
		foreach (double invalid in new[] {
			double.NaN, double.PositiveInfinity, double.NegativeInfinity,
			24000.0, 30000.0,
		})
		{
			Assert.That(policy.TryOffer([valid with { CutoffHz = invalid }]),
				Is.False, $"invalid cutoff {invalid} rejects the whole batch");
			Assert.That(policy.PendingCount, Is.Zero);
		}
	}

	[Test]
	public void ValidMixedMaximumFirUpdatesRespectAbsoluteBoundariesAndAllVoiceHistories()
	{
		// The supported 127-tap Maximum plan is prepared at absolute
		// frame 64, regardless of how the render caller partitions blocks.
		FmSynthSound sound = MixedSound(
			MakeMixed(127, "Maximum"), connectFir: true,
			constantFirControl: true);
		RenderContext context = Context();
		float[] straight = Scheduled(sound, context, sound.CreateState(), false);
		float[] partitioned = Scheduled(sound, context, sound.CreateState(), true);
		Assert.That(partitioned, Is.EqualTo(straight));
		SoundState seekState = sound.CreateState();
		float[] initial = Scheduled(sound, context, seekState, true);
		Assert.That(initial, Is.EqualTo(straight));
		// Rewind the *same* invocation and rebuild the same explicit
		// control boundary; state/ramp/history must reconstruct identically.
		float[] rewind = Scheduled(sound, context, seekState, false);
		Assert.That(rewind, Is.EqualTo(straight));

		SoundState[] independent = Enumerable.Range(0, 3)
			.Select(_ => sound.CreateState()).ToArray();
		for (int i = independent.Length - 1; i >= 0; i--)
			Assert.That(Scheduled(sound, context, independent[i], (i & 1) == 0),
				Is.EqualTo(straight), $"voice {i} must own isolated filter state");
	}

	[Test]
	public void MixedInvalidNyquistStageRejectsPrepreparedFirAndEveryValidPeer()
	{
		FilterInstance configured = MakeMixed(127, "Maximum");
		FmSynthSound rejected = MixedSound(
			configured, connectFir: true, constantFirControl: true,
			invalidOnePole: true);
		FmSynthSound baseline = new(new FmSynthGraph([
			new FmConstantNode(1, 0.65),
			new FmFilterNode(7, 1, configured),
		], 7), new EmptyResolver());
		RenderContext context = Context();
		float[] actual = new float[240], expected = new float[240];
		SoundState state = rejected.CreateState();
		rejected.Render(context, state, 0, 64, actual.AsSpan(0, 64));
		Assert.That(rejected.PreparePendingFilterControls(context, state),
			Is.EqualTo(1), "FIR plan is prepared before discovering invalid IIR");
		rejected.Render(context, state, 64, 176, actual.AsSpan(64));
		baseline.Render(context, baseline.CreateState(), 0,
			expected.Length, expected);
		Assert.That(actual, Is.EqualTo(expected),
			"Nyquist-invalid OnePole blocks valid peaking, ladder, comb and FIR updates");
	}

	private static float[] Scheduled(FmSynthSound sound,
		RenderContext context, SoundState state, bool split)
	{
		float[] result = new float[256];
		if (split)
		{
			sound.Render(context, state, 0, 16, result.AsSpan(0, 16));
			sound.Render(context, state, 16, 48, result.AsSpan(16, 48));
		}
		else
			sound.Render(context, state, 0, 64, result.AsSpan(0, 64));
		Assert.That(sound.PreparePendingFilterControls(context, state),
			Is.EqualTo(1));
		if (split)
		{
			sound.Render(context, state, 64, 1, result.AsSpan(64, 1));
			sound.Render(context, state, 65, 63, result.AsSpan(65, 63));
			sound.Render(context, state, 128, 128, result.AsSpan(128));
		}
		else
			sound.Render(context, state, 64, 192, result.AsSpan(64));
		return result;
	}

	private static double ToMilliseconds(long ticks)
		=> 1000.0 * ticks / Stopwatch.Frequency;

	private static RenderContext Context() => new(new RenderConfiguration(
		Rate, [new OutputChannelConfiguration(Vector3.Zero,
			positionalImportance: 0)]));

	private static FilterInstance MakeMixed(
		int taps, string phase = "Linear")
	{
		FilterKind[] kinds = [
			FilterKind.OnePole, FilterKind.SvfLowPass,
			FilterKind.BiquadPeaking, FilterKind.LadderLinear,
			FilterKind.LadderSaturated, FilterKind.AllPass,
			FilterKind.CombFeedback, FilterKind.CombFeedforward,
			FilterKind.FirLowPass,
		];
		FilterDefinition definition = new((ObjectId)9901U, "Realtime Mixed", [
			new FilterStageDefinition(1, "pole", kinds[0]),
			new FilterStageDefinition(2, "svf", kinds[1]),
			new FilterStageDefinition(3, "peaking", kinds[2]),
			new FilterStageDefinition(4, "ladder", kinds[3]),
			new FilterStageDefinition(5, "saturated", kinds[4]),
			new FilterStageDefinition(6, "allpass", kinds[5]),
			new FilterStageDefinition(7, "feedback", kinds[6]),
			new FilterStageDefinition(8, "feedforward", kinds[7]),
			new FilterStageDefinition(9, "fir", kinds[8]),
		]);
		FilterInstance instance = new(definition);
		for (int id = 1; id <= 6; id++)
			instance.SetStageParameter(id, "CutoffHz",
				FilterParameterValue.FromNumber(1700));
		instance.SetStageParameter(5, "Oversampling",
			FilterParameterValue.FromChoice("4x"));
		instance.SetStageParameter(5, "Drive",
			FilterParameterValue.FromNumber(2));
		instance.SetStageParameters(7, [
			new("DelaySeconds", FilterParameterValue.FromNumber(0.001)),
			new("Feedback", FilterParameterValue.FromNumber(0.2)),
			new("Mix", FilterParameterValue.FromNumber(0.3)),
		]);
		instance.SetStageParameters(8, [
			new("DelaySeconds", FilterParameterValue.FromNumber(0.001)),
			new("Feedforward", FilterParameterValue.FromNumber(0.3)),
			new("Mix", FilterParameterValue.FromNumber(0.3)),
		]);
		instance.SetStageParameters(9, [
			new("TapCount", FilterParameterValue.FromNumber(taps)),
			new("TransitionWidthHz", FilterParameterValue.FromNumber(200)),
			new("Window", FilterParameterValue.FromChoice("Hamming")),
			new("PhaseResponse", FilterParameterValue.FromChoice(phase)),
			new("CutoffHz", FilterParameterValue.FromNumber(3500)),
		]);
		return instance;
	}

	private static FmSynthSound MixedSound(FilterInstance instance,
		bool connectFir, bool constantFirControl = false,
		bool invalidOnePole = false)
	{
		List<FmSynthNode> nodes = [
			new FmConstantNode(1, 0.65),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 37,
				minimum: 1100, maximum: 1900, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 29,
				minimum: 0.2, maximum: 0.7, constant: true),
			new FmOscillatorNode(4, FmOscillatorWaveform.Sine, 23,
				minimum: 0.2, maximum: 0.8, constant: true),
			new FmOscillatorNode(5, FmOscillatorWaveform.Sine, 31,
				minimum: 7.5, maximum: 9.5, constant: true),
			new FmConstantNode(6, constantFirControl ? 4900 : 4500),
			new FmOscillatorNode(9, FmOscillatorWaveform.Sine, 17,
				minimum: 0.90, maximum: 0.98, constant: true),
		];
		if (invalidOnePole)
			nodes.Add(new FmConstantNode(8, 30000));
		List<FmFilterParameterConnection> connections = [
			new(1, FilterKind.OnePole, 0, "CutoffHz",
				invalidOnePole ? 8 : 2),
			new(2, FilterKind.SvfLowPass, 0, "CutoffHz", 2),
			new(3, FilterKind.BiquadPeaking, 0, "GainDb", 3),
			new(4, FilterKind.LadderLinear, 0, "Resonance", 9),
			new(5, FilterKind.LadderSaturated, 0, "Resonance", 9),
			new(5, FilterKind.LadderSaturated, 0, "Drive", 5),
			new(6, FilterKind.AllPass, 0, "CutoffHz", 2),
			new(7, FilterKind.CombFeedback, 0, "Feedback", 3),
			new(8, FilterKind.CombFeedforward, 0, "Mix", 4),
		];
		if (connectFir)
			connections.Add(new FmFilterParameterConnection(
				9, FilterKind.FirLowPass, 0, "CutoffHz", 6));
		nodes.Add(new FmFilterNode(7, 1, instance, connections));
		return new FmSynthSound(new FmSynthGraph(nodes, 7),
			new EmptyResolver());
	}

	private sealed class EmptyResolver : IEnvelopeCurveResolver
	{
		public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
		{
			curve = null;
			return false;
		}
	}
}

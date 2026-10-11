using System;
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
public sealed class FmRealtimeFilterFastPathTests
{
	private const int SampleRate = 1000;

	[TestCase(FilterKind.OnePole)]
	[TestCase(FilterKind.SvfLowPass)]
	[TestCase(FilterKind.SvfHighPass)]
	[TestCase(FilterKind.SvfBandPass)]
	[TestCase(FilterKind.SvfNotch)]
	public void FastNumericModulationMatchesAtomicFullReconfigurationSampleForSample(
		FilterKind kind)
	{
		FilterInstance use = MakeInstance(kind);
		FmFilterParameterConnection cutoff = new(
			1, kind, 0, "CutoffHz", 2);
		FmFilterParameterConnection[] wires = kind == FilterKind.OnePole
			? [cutoff]
			: [cutoff, new FmFilterParameterConnection(1, kind, 0, "Q", 3)];
		FmSynthSound fast = new(new FmSynthGraph([
			new FmConstantNode(1, 0.5),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 23,
				minimum: 80, maximum: 160, constant: true),
			new FmOscillatorNode(3, FmOscillatorWaveform.Sine, 37,
				minimum: 0.5, maximum: 2, constant: true),
			new FmFilterNode(4, 1, use, wires),
		], 4), new EmptyResolver());

		RenderContext context = Context();
		float[] actual = new float[320];
		fast.Render(context, fast.CreateState(), 0, actual.Length, actual);

		StreamingFilterChain reference = new(
			new FilterChain([use]), SampleRate, 1);
		double cutoffPhase = 0;
		double qPhase = 0;
		Span<float> frame = stackalloc float[1];
		for (int index = 0; index < actual.Length; index++)
		{
			double cutoffHz = 120 + 40 * Math.Sin(2 * Math.PI * cutoffPhase);
			double q = 1.25 + 0.75 * Math.Sin(2 * Math.PI * qPhase);
			cutoffPhase += 23.0 / SampleRate;
			cutoffPhase -= Math.Floor(cutoffPhase);
			qPhase += 37.0 / SampleRate;
			qPhase -= Math.Floor(qPhase);

			FilterInstance next = use.Clone();
			if (kind == FilterKind.OnePole)
				next.SetStageParameter(1, "CutoffHz",
					FilterParameterValue.FromNumber(cutoffHz));
			else
				next.SetStageParameters(1, [
					new("CutoffHz", FilterParameterValue.FromNumber(cutoffHz)),
					new("Q", FilterParameterValue.FromNumber(q)),
				]);
			reference.Configure(new FilterChain([next]));
			frame[0] = 0.5f;
			reference.ProcessFrame(frame);
			Assert.That(actual[index], Is.EqualTo(frame[0]),
				$"frame {index} for {kind} differs from the atomic reference");
			use = next;
		}
	}

	[TestCase(FilterKind.OnePole)]
	[TestCase(FilterKind.SvfLowPass)]
	public void ContinuousHighRateModulationDoesNotAllocatePerRenderedFrame(
		FilterKind kind)
	{
		FilterInstance filter = MakeInstance(kind);
		FmSynthSound sound = new(new FmSynthGraph([
			new FmConstantNode(1, 0.75),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 19,
				minimum: 90, maximum: 150, constant: true),
			new FmFilterNode(3, 1, filter, [
				new FmFilterParameterConnection(1, kind, 0, "CutoffHz", 2),
			]),
		], 3), new EmptyResolver());
		RenderContext context = Context();
		SoundState state = sound.CreateState();
		// Force first-use JIT, lazy DSP preparation, and scratch allocation
		// outside the measured interval.
		float[] warmup = new float[64];
		sound.Render(context, state, 0, warmup.Length, warmup);
		float[] pcm = new float[4096];

		long before = GC.GetAllocatedBytesForCurrentThread();
		sound.Render(context, state, 64, pcm.Length, pcm);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.That(allocated, Is.LessThan(4096),
			$"{kind}: 4096 rapidly changing cutoff samples must not "
			+ "allocate on the steady-state FM render path.");

		Assert.That(pcm[0], Is.GreaterThan(0));
	}

	[Test]
	public void MultipleOnePolesWithInvalidSecondPortRetainEveryPriorStage()
	{
		FilterDefinition shared = new((ObjectId)83U, "Dual", [
			new FilterStageDefinition(1, "first", FilterKind.OnePole),
			new FilterStageDefinition(2, "second", FilterKind.OnePole),
		]);
		FilterInstance use = new(shared);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(100));
		use.SetStageParameter(2, "CutoffHz",
			FilterParameterValue.FromNumber(200));

		FmSynthSound invalid = new(new FmSynthGraph([
			new FmConstantNode(1, 1),
			new FmConstantNode(2, 80),
			new FmConstantNode(3, 800),
			new FmFilterNode(4, 1, use, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
				new FmFilterParameterConnection(2, FilterKind.OnePole,
					0, "CutoffHz", 3),
			]),
		], 4), new EmptyResolver());
		FmSynthSound reference = new(new FmSynthGraph([
			new FmConstantNode(1, 1),
			new FmFilterNode(4, 1, use),
		], 4), new EmptyResolver());
		float[] a = new float[120];
		float[] b = new float[120];
		invalid.Render(Context(), invalid.CreateState(), 0, a.Length, a);
		reference.Render(Context(), reference.CreateState(), 0, b.Length, b);
		Assert.That(a, Is.EqualTo(b));
	}

	private static FilterInstance MakeInstance(FilterKind kind)
	{
		FilterDefinition definition = new((ObjectId)44U, "TPT", [
			new FilterStageDefinition(1, "stage", kind),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(120));
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

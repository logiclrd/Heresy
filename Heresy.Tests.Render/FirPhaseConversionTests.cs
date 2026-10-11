using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FirPhaseConversionTests
{
	private const int SampleRate = 48000;

	[TestCase(FilterKind.FirLowPass, "Hann")]
	[TestCase(FilterKind.FirHighPass, "Blackman")]
	[TestCase(FilterKind.FirBandPass, "Hamming")]
	[TestCase(FilterKind.FirNotch, "Hamming")]
	public void MinimumAndMaximumPreserveMagnitudeButRedistributeImpulseEnergy(
		FilterKind kind, string window)
	{
		const int taps = 255;
		float[] linear = Impulse(CreateUse(kind, "Linear", taps, window));
		float[] minimum = Impulse(CreateUse(kind, "Minimum", taps, window));
		float[] maximum = Impulse(CreateUse(kind, "Maximum", taps, window));
		double linearCentre = EnergyCentroid(linear);
		double minimumCentre = EnergyCentroid(minimum);
		double maximumCentre = EnergyCentroid(maximum);

		Assert.That(linearCentre, Is.EqualTo((taps - 1) / 2.0).Within(1e-5),
			$"{kind}: linear symmetric design");
		Assert.That(minimumCentre, Is.LessThan(0.55 * linearCentre),
			$"{kind}: minimum phase must shift most energy earlier.");
		Assert.That(maximumCentre, Is.GreaterThan(
			(taps - 1) - 0.55 * linearCentre),
			$"{kind}: maximum phase must shift most energy later.");
		Assert.That(maximumCentre + minimumCentre,
			Is.EqualTo(taps - 1).Within(1e-7));
		Assert.That(minimum.Skip(1).Any((v) => Math.Abs(v) > 1e-6), Is.True);
		for (int i = 0; i < taps; i++)
			Assert.That(maximum[i],
				Is.EqualTo(minimum[taps - 1 - i]).Within(1e-7),
				$"{kind}: maximum phase is exact time reversal.");

		// Direct DFT of each finite impulse is independent of the
		// conversion's FFT. Compare linear/min/max at passband, transition
		// and stopband frequencies, including where the response is tiny.
		foreach (double frequency in new[] {
			0.0, 400.0, 1400.0, 3000.0, 5000.0,
			7500.0, 11000.0, 17000.0, 22000.0,
		})
		{
			double linearGain = Response(linear, frequency).Magnitude;
			double minimumGain = Response(minimum, frequency).Magnitude;
			double maximumGain = Response(maximum, frequency).Magnitude;
			Assert.That(minimumGain, Is.EqualTo(linearGain).Within(0.025),
				$"{kind} minimum magnitude at {frequency} Hz");
			Assert.That(maximumGain,
				Is.EqualTo(minimumGain).Within(1e-5),
				$"{kind} max/min magnitude at {frequency} Hz");
		}
	}

	[Test]
	public void MinimumAndMaximumExhibitTheCorrectPhaseRelationAtEveryFrequency()
	{
		const int taps = 127;
		float[] minimum = Impulse(CreateUse(
			FilterKind.FirLowPass, "Minimum", taps));
		float[] maximum = Impulse(CreateUse(
			FilterKind.FirLowPass, "Maximum", taps));
		float[] linear = Impulse(CreateUse(
			FilterKind.FirLowPass, "Linear", taps));

		foreach (double frequency in new[] {
			211.0, 833.0, 2301.0, 4100.0, 7800.0,
		})
		{
			double omega = 2 * Math.PI * frequency / SampleRate;
			Complex min = Response(minimum, frequency);
			Complex max = Response(maximum, frequency);
			Complex expectedMax = Complex.FromPolarCoordinates(
				1, -omega * (taps - 1)) * Complex.Conjugate(min);
			Assert.That((max - expectedMax).Magnitude,
				Is.LessThan(2e-6), $"{frequency}Hz time reversal phase");

			Complex linearResponse = Response(linear, frequency);
			Assert.That((min - linearResponse).Magnitude,
				Is.GreaterThan(0.005),
				$"Minimum phase must not silently run linear-phase DSP at {frequency} Hz.");
		}
	}

	[Test]
	public void PhaseModesPreserveReferenceBandGainAndExactFiniteLength()
	{
		foreach (FilterKind kind in new[] {
			FilterKind.FirLowPass, FilterKind.FirHighPass,
			FilterKind.FirBandPass, FilterKind.FirNotch,
		})
		foreach (string phase in new[] { "Linear", "Minimum", "Maximum" })
		{
			FilterInstance instance = CreateUse(kind, phase);
			StreamingFilterChain runtime = Runtime(instance);
			float[] impulse = new float[139];
			impulse[0] = 1;
			runtime.ProcessInterleaved(impulse);
			Assert.That(impulse.All(float.IsFinite), Is.True);
			Assert.That(impulse.Skip(127), Is.All.Zero);
			Assert.That(runtime.PreparedTailFrames, Is.EqualTo(126));
			Assert.That(runtime.PreparedLatencyFrames,
				Is.EqualTo(phase == "Linear" ? 63 : 0));
			double reference = kind switch {
				FilterKind.FirLowPass or FilterKind.FirNotch => 0.0,
				FilterKind.FirHighPass => SampleRate / 2.0,
				_ => 5000.0,
			};
			Assert.That(Response(impulse, reference).Magnitude,
				Is.EqualTo(1).Within(0.002),
				$"{kind}, {phase} reference gain");
		}
	}

	[Test]
	public void ChangingPhaseModeDiscardsOldSamplesEvenAtMatchingTapCounts()
	{
		FilterInstance use = CreateUse(FilterKind.FirLowPass, "Linear");
		StreamingFilterChain runtime = Runtime(use);
		runtime.ProcessFrame(new float[] { 1 });
		foreach (string phase in new[] { "Minimum", "Maximum", "Linear" })
		{
			use.SetStageParameter(1, "PhaseResponse",
				FilterParameterValue.FromChoice(phase));
			runtime.Configure(new FilterChain(new[] { use }));
			float[] zeros = new float[130];
			runtime.ProcessInterleaved(zeros);
			Assert.That(zeros, Is.All.Zero,
				$"{phase}: structural phase change must clear old FIR ring.");

			float[] impulse = { 1f };
			runtime.ProcessFrame(impulse);
			Assert.That(float.IsFinite(impulse[0]), Is.True);
		}
		Assert.That(FilterCatalogue.GetParameters(FilterKind.FirLowPass)
			.Single(d => d.Name == "PhaseResponse").IsStructural, Is.True);
	}

	[Test]
	public void FailedLaterPhasePreparationDoesNotPublishEarlierNumericChanges()
	{
		FilterInstance use = CreateUse(FilterKind.FirLowPass, "Minimum");
		StreamingFilterChain runtime = new(
			new FilterChain(new[] { use, use }), 32000, 1);
		float[] head = new float[190];
		head[0] = 1;
		runtime.ProcessInterleaved(head);
		StreamingFilterChain expected = runtime.Fork();
		FilterInstance changed = use.Clone();
		changed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(4500));
		FilterInstance invalid = use.Clone();
		invalid.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(20000));
		Assert.Throws<ArgumentOutOfRangeException>(() => runtime.Configure(
			new FilterChain(new[] { changed, invalid })));
		float[] actual = Enumerable.Range(0, 260)
			.Select(i => (float)(0.35 * Math.Sin(
				2 * Math.PI * i * 2700 / 32000))).ToArray();
		float[] reference = (float[])actual.Clone();
		runtime.ProcessInterleaved(actual);
		expected.ProcessInterleaved(reference);
		Assert.That(actual, Is.EqualTo(reference));
	}

	[Test]
	public void CompatibleCutoffAutomationPreservesHistoryAndForkCopiesMidRamp()
	{
		foreach (string phase in new[] { "Minimum", "Maximum" })
		{
			FilterInstance use = CreateUse(FilterKind.FirBandPass, phase);
			StreamingFilterChain updated = Runtime(use, channels: 2);
			StreamingFilterChain reference = Runtime(use, channels: 2);
			float[] prefix = Enumerable.Range(0, 2 * 256)
				.Select(i => (float)(0.3 * Math.Sin(
					2 * Math.PI * (i / 2) * (780 + (i % 2) * 2100) / SampleRate)))
				.ToArray();
			updated.ProcessInterleaved((float[])prefix.Clone());
			reference.ProcessInterleaved((float[])prefix.Clone());

			use.SetStageParameters(1, new[] {
				Pair("LowCutoffHz", 900),
				Pair("HighCutoffHz", 7000),
			});
			updated.Configure(new FilterChain(new[] { use }));
			updated.ProcessInterleaved(new float[2 * 9]);
			StreamingFilterChain fork = updated.Fork();
			float[] next = Enumerable.Range(0, 2 * 280)
				.Select(i => (float)(0.25 * Math.Cos(
					2 * Math.PI * (i / 2) * (2100 + (i % 2) * 530) / SampleRate)))
				.ToArray();
			float[] cloneNext = (float[])next.Clone();
			updated.ProcessInterleaved(next);
			fork.ProcessInterleaved(cloneNext);
			Assert.That(cloneNext, Is.EqualTo(next), $"{phase} fork after retune");

			float[] referenceNext = new float[next.Length];
			reference.ProcessInterleaved(referenceNext);
			Assert.That(next.Zip(referenceNext).Any(pair =>
				Math.Abs(pair.First - pair.Second) > 1e-5), Is.True,
				$"{phase} retuned audio is not an identity/bypass.");
			Assert.That(next.All(float.IsFinite), Is.True);
		}
	}

	[Test]
	public void MinimumAndMaximumRetuningIsBitwiseIndependentOfBlockPartition()
	{
		foreach (string phase in new[] { "Minimum", "Maximum" })
		{
			FilterInstance use = CreateUse(FilterKind.FirLowPass, phase);
			StreamingFilterChain blocks = Runtime(use, channels: 2);
			StreamingFilterChain portions = Runtime(use, channels: 2);
			for (int block = 0; block < 40; block++)
			{
				use.SetStageParameter(1, "CutoffHz",
					FilterParameterValue.FromNumber(
						500 + (block * 1237) % 14000));
				FilterChain chain = new(new[] { use });
				blocks.Configure(chain);
				portions.Configure(chain);
				float[] a = new float[38];
				for (int n = 0; n < 19; n++)
				{
					a[2 * n] = (float)(0.32 * Math.Sin(
						2 * Math.PI * (block * 19 + n) * 1547 / SampleRate));
					a[2 * n + 1] = 0.15f;
				}
				float[] b = (float[])a.Clone();
				blocks.ProcessInterleaved(a);
				portions.ProcessInterleaved(b.AsSpan(0, 12));
				portions.ProcessInterleaved(b.AsSpan(12, 26));
				Assert.That(b, Is.EqualTo(a),
					$"{phase} batch {block} exact block invariance");
			}
		}
	}

	[Test]
	public void MaximumTapPhaseDesignIsFiniteAndBoundedAndTailIsExact()
	{
		foreach (string phase in new[] { "Minimum", "Maximum" })
		{
			StreamingFilterChain runtime = Runtime(CreateUse(
				FilterKind.FirLowPass, phase, taps: 2047));
			Assert.That(runtime.PreparedTailFrames, Is.EqualTo(2046));
			Assert.That(runtime.PreparedLatencyFrames, Is.Zero);
			float[] response = new float[2048];
			response[0] = 1;
			runtime.ProcessInterleaved(response);
			Assert.That(response.All(float.IsFinite), Is.True);
			Assert.That(response[^1], Is.Zero);
			runtime.Reset();
			runtime.ProcessFrame(new float[] { 1 });
			runtime.FinishInput();
			float[] tail = new float[2050];
			Assert.That(runtime.RenderTail(tail), Is.EqualTo(2046));
			Assert.That(tail.Skip(2046), Is.All.Zero);
		}
	}

	private static FilterInstance CreateUse(
		FilterKind kind, string phase, int taps = 127,
		string window = "Hamming")
	{
		FilterDefinition definition = new((ObjectId)411U, "Phased FIR", new[] {
			new FilterStageDefinition(1, "phase", kind),
		});
		FilterInstance use = new(definition);
		List<KeyValuePair<string, FilterParameterValue>> values = [
			Pair("TapCount", taps),
			Pair("TransitionWidthHz", 600),
			new("Window", FilterParameterValue.FromChoice(window)),
			new("PhaseResponse", FilterParameterValue.FromChoice(phase)),
		];
		if (kind is FilterKind.FirLowPass or FilterKind.FirHighPass)
			values.Add(Pair("CutoffHz", 3000));
		else
		{
			values.Add(Pair("LowCutoffHz", 2000));
			values.Add(Pair("HighCutoffHz", 8000));
		}
		use.SetStageParameters(1, values);
		return use;
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, double value)
		=> new(name, FilterParameterValue.FromNumber(value));

	private static StreamingFilterChain Runtime(
		FilterInstance instance, int channels = 1)
		=> new(new FilterChain(new[] { instance }), SampleRate, channels);

	private static float[] Impulse(FilterInstance instance)
	{
		StreamingFilterChain runtime = Runtime(instance);
		float[] result = new float[runtime.PreparedTailFrames + 1];
		result[0] = 1;
		runtime.ProcessInterleaved(result);
		return result;
	}

	private static Complex Response(IReadOnlyList<float> impulse, double frequency)
	{
		Complex total = Complex.Zero;
		for (int n = 0; n < impulse.Count; n++)
			total += impulse[n] * Complex.FromPolarCoordinates(1,
				-2 * Math.PI * frequency * n / SampleRate);
		return total;
	}

	private static double EnergyCentroid(IReadOnlyList<float> impulse)
	{
		double moment = 0;
		double energy = 0;
		for (int i = 0; i < impulse.Count; i++)
		{
			double v2 = (double)impulse[i] * impulse[i];
			moment += i * v2;
			energy += v2;
		}
		return moment / energy;
	}
}

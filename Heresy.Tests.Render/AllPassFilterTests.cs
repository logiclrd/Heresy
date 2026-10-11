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
public sealed class AllPassFilterTests
{
	private const int SampleRate = 48000;

	[TestCase("1")]
	[TestCase("2")]
	public void FirstAndSecondOrderImpulseMatchAnalyticAllPassRecurrence(string order)
	{
		const double cutoff = 3000;
		double g = Math.Tan(Math.PI * cutoff / SampleRate);
		double a = (g - 1) / (g + 1);
		StreamingFilterChain runtime = Runtime(CreateUse(cutoff, order));
		float[] impulse = new float[1024];
		impulse[0] = 1f;
		runtime.ProcessInterleaved(impulse);
		if (order == "1")
		{
			Assert.That(impulse[0], Is.EqualTo(a).Within(1e-7));
			Assert.That(impulse[1], Is.EqualTo(1 - a * a).Within(1e-7));
			Assert.That(impulse[2], Is.EqualTo(-a * (1 - a * a)).Within(1e-7));
		}
		else
		{
			Assert.That(impulse[0], Is.EqualTo(a * a).Within(1e-7));
			Assert.That(impulse[1], Is.EqualTo(
				2 * a * (1 - a * a)).Within(1e-7));
		}
		Assert.That(impulse.All(float.IsFinite), Is.True);
		Assert.That(impulse[0], Is.Not.EqualTo(1f));
		Assert.That(Math.Abs(impulse[^1]), Is.LessThan(1e-7));
		Assert.That(runtime.PreparedTailFrames, Is.GreaterThan(0));
	}

	[TestCase("1")]
	[TestCase("2")]
	public void EverySteadyStateFrequencyHasUnityMagnitude(string order)
	{
		foreach (double frequency in new[] { 125.0, 500.0, 3000.0,
			7000.0, 12000.0, 20000.0 })
		{
			Complex response = MeasureResponse(
				Runtime(CreateUse(3000, order)), frequency);
			Assert.That(response.Magnitude, Is.EqualTo(1).Within(0.002),
				$"All-pass {order}, {frequency} Hz");
		}
	}

	[TestCase("1", -Math.PI / 2)]
	[TestCase("2", -Math.PI)]
	public void CutoffHasSpecifiedPhaseRotation(string order, double expected)
	{
		Complex result = MeasureResponse(
			Runtime(CreateUse(3000, order)), 3000);
		Assert.That(result.Magnitude, Is.EqualTo(1).Within(0.001));
		Assert.That(WrappedDifference(result.Phase, expected),
			Is.EqualTo(0).Within(0.002));
	}

	[TestCase("1")]
	[TestCase("2")]
	public void PhaseMatchesTheoreticalDigitalTransferFunction(string order)
	{
		foreach (double cutoff in new[] { 350.0, 3000.0, 15000.0 })
		{
			double g = Math.Tan(Math.PI * cutoff / SampleRate);
			double a = (g - 1) / (g + 1);
			foreach (double frequency in new[] {
				300.0, 1700.0, 4500.0, 12000.0, 19000.0,
			})
			{
				double omega = 2 * Math.PI * frequency / SampleRate;
				Complex delayed = Complex.FromPolarCoordinates(1, -omega);
				Complex expected = (a + delayed) / (1 + a * delayed);
				if (order == "2")
					expected *= expected;
				Complex actual = MeasureResponse(
					Runtime(CreateUse(cutoff, order)), frequency);
				Assert.That(actual.Magnitude,
					Is.EqualTo(1).Within(0.003));
				Assert.That(WrappedDifference(actual.Phase, expected.Phase),
					Is.EqualTo(0).Within(0.008),
					$"order {order}, cutoff {cutoff}Hz, tone {frequency}Hz");
			}
		}
	}

	[Test]
	public void FirstOrderLowAndHighFrequencyPhaseEndpointsAreCorrect()
	{
		Complex nearDc = MeasureResponse(
			Runtime(CreateUse(3000, "1")), 125);
		Complex nearNyquist = MeasureResponse(
			Runtime(CreateUse(3000, "1")), 23000);
		Assert.That(Math.Abs(nearDc.Phase), Is.LessThan(0.12));
		Assert.That(Math.Abs(Math.Abs(nearNyquist.Phase) - Math.PI),
			Is.LessThan(0.2));
	}

	[Test]
	public void StructuralOrderChangeResetsStateAndDoublesPhaseRotation()
	{
		FilterInstance use = CreateUse(3000, "1");
		StreamingFilterChain runtime = Runtime(use);
		runtime.ProcessFrame(new float[] { 1f });
		use.SetStageParameter(1, "Order", FilterParameterValue.FromChoice("2"));
		runtime.Configure(new FilterChain(new[] { use }));
		float[] reset = { 0f };
		runtime.ProcessFrame(reset);
		Assert.That(reset[0], Is.Zero);
		Complex response = MeasureResponse(Runtime(use), 3000);
		Assert.That(WrappedDifference(response.Phase, -Math.PI),
			Is.EqualTo(0).Within(0.002));

		FilterParameterDescriptor descriptor = FilterCatalogue.GetParameters(
			FilterKind.AllPass).Single(d => d.Name == "Order");
		Assert.That(descriptor.IsStructural, Is.True);
		Assert.That(descriptor.Choices, Is.EquivalentTo(new[] { "1", "2" }));
	}

	[Test]
	public void CompatibleCutoffEditsPreserveBothHistoriesAndSmoothCoeffTransitions()
	{
		foreach (string order in new[] { "1", "2" })
		{
			FilterInstance use = CreateUse(1200, order);
			StreamingFilterChain updated = Runtime(use);
			StreamingFilterChain unmodified = Runtime(use);
			updated.ProcessFrame(new float[] { 1f });
			unmodified.ProcessFrame(new float[] { 1f });
			use.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(9600));
			updated.Configure(new FilterChain(new[] { use }));
			float[] changed = new float[90];
			float[] original = new float[90];
			updated.ProcessInterleaved(changed);
			unmodified.ProcessInterleaved(original);
			Assert.That(Math.Abs(changed[0] - original[0]),
				Is.LessThan(0.045), $"order {order} first ramp sample");
			Assert.That(changed.Zip(original).Skip(3)
				.Any(pair => Math.Abs(pair.First - pair.Second) > 1e-5),
				Is.True, $"order {order} cutoff change must take effect");
			Assert.That(changed.All(float.IsFinite), Is.True);

			StreamingFilterChain freshlyConstructed = Runtime(use);
			float[] zero = { 0 };
			freshlyConstructed.ProcessFrame(zero);
			Assert.That(zero[0], Is.Zero);
		}
	}

	[Test]
	public void NyquistValidationAndMultiLayerAtomicFailurePreserveActiveAudio()
	{
		FilterInstance invalid = CreateUse(20000, "2");
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { invalid }), 32000, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] { invalid }), 40000, 1));
		Assert.That(() => Runtime(invalid), Throws.Nothing);
		Assert.Throws<ArgumentException>(() =>
			AllPassFilterStageFactory.Instance.Prepare(
				new PreparedFilterStage(0, 0, (ObjectId)29U, 1,
					FilterKind.OnePole, 0,
					Array.Empty<KeyValuePair<string, FilterParameterValue>>()),
				SampleRate));

		FilterInstance normal = CreateUse(3000, "1");
		StreamingFilterChain running = new(
			new FilterChain(new[] { normal, normal }), 32000, 1);
		float[] warmup = { 1f, 0f, -0.4f, 0f };
		running.ProcessInterleaved(warmup);
		StreamingFilterChain fork = running.Fork();
		FilterInstance proposed = normal.Clone();
		proposed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(6800));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			running.Configure(new FilterChain(new[] { proposed, invalid })));
		float[] subsequent = { 0f, 0.1f, -0.3f, 0f };
		float[] reference = (float[])subsequent.Clone();
		running.ProcessInterleaved(subsequent);
		fork.ProcessInterleaved(reference);
		Assert.That(subsequent, Is.EqualTo(reference));
	}

	[Test]
	public void RapidModulationIsFiniteAndExactlyIndependentOfBlockSize()
	{
		foreach (string order in new[] { "1", "2" })
		{
			FilterInstance use = CreateUse(1000, order);
			StreamingFilterChain blocks = Runtime(use, 2);
			StreamingFilterChain frames = Runtime(use, 2);
			for (int batch = 0; batch < 280; batch++)
			{
				double cutoff = batch % 17 == 0 ? 1
					: batch % 19 == 0 ? 19999
					: 100 + ((batch * 7919) % 17500);
				use.SetStageParameter(1, "CutoffHz",
					FilterParameterValue.FromNumber(cutoff));
				FilterChain chain = new(new[] { use });
				blocks.Configure(chain);
				frames.Configure(chain);
				float[] a = new float[38];
				for (int n = 0; n < 19; n++)
				{
					a[2 * n] = (float)(0.25 * Math.Sin(
						2 * Math.PI * (n + batch * 19) * 2899 / SampleRate));
					a[2 * n + 1] = batch % 2 == 0 ? 0.15f : -0.2f;
				}
				float[] b = (float[])a.Clone();
				blocks.ProcessInterleaved(a);
				frames.ProcessInterleaved(b.AsSpan(0, 12));
				frames.ProcessInterleaved(b.AsSpan(12, 26));
				Assert.That(b, Is.EqualTo(a), $"{order} / batch {batch}");
				Assert.That(a.All(float.IsFinite), Is.True);
				Assert.That(a.All(v => Math.Abs(v) < 5),
					Is.True, "Ordinary PCM under a stable all-pass remains bounded.");
			}
		}
	}

	[Test]
	public void ForkCopiesBothStatesAndInProgressCoefficientRampForThreeChannels()
	{
		FilterInstance use = CreateUse(2000, "2");
		StreamingFilterChain runtime = Runtime(use, 3);
		float[] input = Enumerable.Range(0, 3 * 650)
			.Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * (i / 3)
				* (277 + (i % 3) * 3300) / SampleRate))).ToArray();
		float[] processed = (float[])input.Clone();
		runtime.ProcessInterleaved(processed);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(9000));
		runtime.Configure(new FilterChain(new[] { use }));
		runtime.ProcessInterleaved(new float[3 * 7]);

		StreamingFilterChain fork = runtime.Fork();
		float[] subsequent = Enumerable.Range(0, 3 * 120)
			.Select(i => (float)(0.35 * Math.Cos(2 * Math.PI * (i / 3)
				* (1500 + (i % 3) * 400) / SampleRate))).ToArray();
		float[] copied = (float[])subsequent.Clone();
		runtime.ProcessInterleaved(subsequent);
		fork.ProcessInterleaved(copied);
		Assert.That(copied, Is.EqualTo(subsequent));

		// Deterministic backward seek requires restoring the original
		// configuration and replaying prior PCM; resetting alone does
		// not reconstruct earlier parameter-change events.
		fork.Reset();
		fork.Configure(new FilterChain(new[] { CreateUse(2000, "2") }));
		fork.Reset();
		float[] replay = (float[])input.Clone();
		fork.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(processed));
	}

	[Test]
	public void TailDrainIsBoundedAndDenormalSafeInBothOrders()
	{
		foreach (string order in new[] { "1", "2" })
		{
			StreamingFilterChain runtime = Runtime(
				CreateUse(350, order), maximumTailSeconds: 0.3);
			int budget = runtime.PreparedTailFrames;
			Assert.That(budget, Is.GreaterThan(0).And.LessThanOrEqualTo(14400));
			runtime.ProcessFrame(new float[] { 1f });
			runtime.FinishInput();
			float[] drained = new float[budget + 6];
			Assert.That(runtime.RenderTail(drained), Is.EqualTo(budget));
			Assert.That(drained.Skip(budget), Is.All.Zero);
			Assert.That(drained.All(value => float.IsFinite(value)
				&& (value == 0 || Math.Abs(value) >= 1e-20f)), Is.True);
			Assert.That(runtime.RenderTail(new float[4]), Is.Zero);
			runtime.Reset();
			float[] invalid = { float.NaN, float.PositiveInfinity,
				float.NegativeInfinity, float.MaxValue, -float.MaxValue, 0f };
			runtime.ProcessInterleaved(invalid);
			Assert.That(invalid.All(float.IsFinite), Is.True);
			runtime.Reset();
			runtime.ProcessFrame(new float[] { 1f });
			float[] zeros = new float[3500];
			runtime.ProcessInterleaved(zeros);
			Assert.That(zeros.All(value => float.IsFinite(value)
				&& (value == 0 || Math.Abs(value) >= 1e-20f)), Is.True);
			Assert.That(zeros[^1], Is.Zero);
		}

		StreamingFilterChain capped = Runtime(
			CreateUse(1, "2"), maximumTailSeconds: 0.001);
		Assert.That(capped.PreparedTailFrames, Is.EqualTo(48));
		capped.ProcessFrame(new float[] { 1 });
		capped.FinishInput();
		float[] destination = new float[70];
		Assert.That(capped.RenderTail(destination), Is.EqualTo(48));
		Assert.That(destination.Skip(48), Is.All.Zero);
	}

	private static FilterInstance CreateUse(double cutoff, string order)
	{
		FilterDefinition definition = new((ObjectId)76U, "Phase shift", new[] {
			new FilterStageDefinition(1, "phase", FilterKind.AllPass),
		});
		FilterInstance instance = new(definition);
		instance.SetStageParameters(1, new[] {
			new KeyValuePair<string, FilterParameterValue>("CutoffHz",
				FilterParameterValue.FromNumber(cutoff)),
			new KeyValuePair<string, FilterParameterValue>("Order",
				FilterParameterValue.FromChoice(order)),
		});
		return instance;
	}

	private static StreamingFilterChain Runtime(FilterInstance use, int channels = 1,
		double maximumTailSeconds = 10)
		=> new(new FilterChain(new[] { use }), SampleRate, channels,
			maximumTailSeconds: maximumTailSeconds);

	/// <summary>Complex gain from projection against a steady-state tone;
	/// the input projection cancels phase offsets and partial cycles.</summary>
	private static Complex MeasureResponse(StreamingFilterChain runtime, double frequency)
	{
		const int settling = 8000;
		const int frames = 8192;
		Complex input = Complex.Zero;
		Complex output = Complex.Zero;
		float[] frame = new float[1];
		for (int n = 0; n < settling + frames; n++)
		{
			double angle = 2 * Math.PI * frequency * n / SampleRate;
			double excitation = Math.Sin(angle);
			frame[0] = (float)excitation;
			runtime.ProcessFrame(frame);
			if (n < settling)
				continue;
			Complex reference = Complex.FromPolarCoordinates(1, -angle);
			input += excitation * reference;
			output += frame[0] * reference;
		}
		return output / input;
	}

	private static double WrappedDifference(double a, double b)
		=> Math.Atan2(Math.Sin(a - b), Math.Cos(a - b));
}

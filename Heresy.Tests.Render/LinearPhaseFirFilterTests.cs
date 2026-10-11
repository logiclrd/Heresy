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
public sealed class LinearPhaseFirFilterTests
{
	private const int SampleRate = 48000;

	[TestCase(FilterKind.FirLowPass)]
	[TestCase(FilterKind.FirHighPass)]
	[TestCase(FilterKind.FirBandPass)]
	[TestCase(FilterKind.FirNotch)]
	public void EveryResponseHasAnExactlySymmetricFiniteImpulse(FilterKind kind)
	{
		const int count = 127;
		StreamingFilterChain filter = Runtime(CreateUse(kind,
			taps: count, width: 700, window: "Hamming"));
		float[] impulse = new float[count + 12];
		impulse[0] = 1;
		filter.ProcessInterleaved(impulse);
		Assert.That(impulse.Take(count).Any(value => Math.Abs(value) > 1e-5),
			Is.True, kind.ToString());
		for (int n = 0; n < count; n++)
			Assert.That(impulse[n], Is.EqualTo(impulse[count - 1 - n])
				.Within(2e-7), $"{kind} tap {n}");
		Assert.That(impulse.Skip(count), Is.All.Zero);
		Assert.That(filter.PreparedTailFrames, Is.EqualTo(count - 1));
		Assert.That(filter.PreparedLatencyFrames, Is.EqualTo((count - 1) / 2));
	}

	[Test]
	public void AllFourDesignsExhibitTheirIntendedPassAndRejectBands()
	{
		FilterInstance lp = CreateUse(FilterKind.FirLowPass,
			taps: 511, width: 1200, cutoff: 4000);
		FilterInstance hp = CreateUse(FilterKind.FirHighPass,
			taps: 511, width: 1200, cutoff: 4000);
		FilterInstance bp = CreateUse(FilterKind.FirBandPass,
			taps: 511, width: 1200, low: 2000, high: 8000);
		FilterInstance notch = CreateUse(FilterKind.FirNotch,
			taps: 511, width: 1200, low: 2000, high: 8000);

		Assert.That(Magnitude(lp, 500), Is.InRange(0.97, 1.03));
		Assert.That(Magnitude(lp, 13000), Is.LessThan(0.02));
		Assert.That(Magnitude(hp, 500), Is.LessThan(0.02));
		Assert.That(Magnitude(hp, 13000), Is.InRange(0.97, 1.03));

		Assert.That(Magnitude(bp, 4500), Is.InRange(0.96, 1.04));
		Assert.That(Magnitude(bp, 400), Is.LessThan(0.02));
		Assert.That(Magnitude(bp, 13000), Is.LessThan(0.02));

		Assert.That(Magnitude(notch, 400), Is.InRange(0.97, 1.03));
		Assert.That(Magnitude(notch, 4500), Is.LessThan(0.02));
		Assert.That(Magnitude(notch, 15000), Is.InRange(0.97, 1.03));
	}

	[TestCase(FilterKind.FirLowPass, 1500)]
	[TestCase(FilterKind.FirHighPass, 12000)]
	[TestCase(FilterKind.FirBandPass, 4500)]
	[TestCase(FilterKind.FirNotch, 12000)]
	public void ConstantGroupDelayFollowsFromMeasuredComplexResponse(
		FilterKind kind, double frequency)
	{
		const int taps = 255;
		FilterInstance filter = CreateUse(kind, taps: taps, width: 900);
		Complex actual = MeasureResponse(Runtime(filter), frequency);
		int latency = LinearPhaseFirFilterStageFactory.GroupDelayFrames(taps);
		double expected = -2 * Math.PI * frequency * latency / SampleRate;
		double error = Math.Atan2(
			Math.Sin(actual.Phase - expected), Math.Cos(actual.Phase - expected));
		Assert.That(actual.Magnitude, Is.InRange(0.94, 1.06));
		Assert.That(error, Is.EqualTo(0).Within(0.005),
			$"{kind}: phase must equal -omega*(N-1)/2 in the passband.");
	}

	[Test]
	public void TransitionWidthAndWindowBothAffectThePreparedImpulse()
	{
		float[] narrow = Impulse(CreateUse(FilterKind.FirLowPass,
			taps: 255, cutoff: 4000, width: 300, window: "Hamming"));
		float[] wide = Impulse(CreateUse(FilterKind.FirLowPass,
			taps: 255, cutoff: 4000, width: 2000, window: "Hamming"));
		Assert.That(narrow.Zip(wide).Any(pair =>
			Math.Abs(pair.First - pair.Second) > 1e-5), Is.True,
			"TransitionWidthHz must not be a silently ignored parameter.");
		// At the same nominal passband boundary, a wider transition
		// shifts the ideal low-pass midpoint upward, retaining more of
		// a tone near the originally requested boundary.
		Assert.That(Magnitude(CreateUse(FilterKind.FirLowPass, taps: 255,
			cutoff: 4000, width: 2000), 4500),
			Is.GreaterThan(Magnitude(CreateUse(FilterKind.FirLowPass,
				taps: 255, cutoff: 4000, width: 300), 4500) + 0.15));

		float[] hann = Impulse(CreateUse(FilterKind.FirLowPass,
			taps: 255, cutoff: 4000, width: 900, window: "Hann"));
		float[] hamming = Impulse(CreateUse(FilterKind.FirLowPass,
			taps: 255, cutoff: 4000, width: 900, window: "Hamming"));
		float[] blackman = Impulse(CreateUse(FilterKind.FirLowPass,
			taps: 255, cutoff: 4000, width: 900, window: "Blackman"));
		Assert.That(hann.Zip(hamming).Any(pair =>
			Math.Abs(pair.First - pair.Second) > 1e-7), Is.True);
		Assert.That(blackman.Zip(hamming).Any(pair =>
			Math.Abs(pair.First - pair.Second) > 1e-7), Is.True);
		Assert.That(hann[0], Is.Zero);
		Assert.That(blackman[0], Is.EqualTo(0).Within(1e-8));
	}

	[Test]
	public void NyquistTransitionAndUnresolvableBandsRejectDuringPreparation()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(
				new FilterChain(new[] { CreateUse(FilterKind.FirLowPass,
					cutoff: 3800, width: 500) }), 8000, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			Runtime(CreateUse(FilterKind.FirHighPass,
				cutoff: 100, width: 500)));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			Runtime(CreateUse(FilterKind.FirBandPass,
				low: 400, high: 4500, width: 1000)));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			Runtime(CreateUse(FilterKind.FirNotch,
				low: 2900, high: 3600, width: 1000)));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			Runtime(CreateUse(FilterKind.FirLowPass,
				taps: 3, cutoff: 10, width: 1)));

		Assert.Throws<ArgumentException>(() =>
			LinearPhaseFirFilterStageFactory.Instance.Prepare(
				new PreparedFilterStage(0, 0, (ObjectId)1U, 1,
					FilterKind.CombFeedforward, 0,
					Array.Empty<KeyValuePair<string, FilterParameterValue>>()),
				SampleRate));
	}

	[Test]
	public void MinimumAndMaximumPhaseAreExplicitlyDeferredNotSilentlyRenderedLinear()
	{
		foreach (string phase in new[] { "Minimum", "Maximum" })
		{
			FilterInstance instance = CreateUse(FilterKind.FirLowPass,
				taps: 127, phase: phase);
			Assert.Throws<NotSupportedException>(() => Runtime(instance),
				$"{phase} needs the separate FIR phase-conversion milestone.");
		}
	}

	[Test]
	public void NumericCutoffRetuningKeepsPcmHistoryAndSmoothsCoefficients()
	{
		foreach (FilterKind kind in Enum.GetValues<FilterKind>()
			.Where(LinearPhaseFirFilterStageFactory.IsStandardFir))
		{
			FilterInstance instance = CreateUse(kind, taps: 127, width: 500);
			StreamingFilterChain updated = Runtime(instance);
			StreamingFilterChain unchanged = Runtime(instance);
			// Fill the circular history with a nonzero impulse.
			float[] first = { 1f };
			updated.ProcessFrame(first);
			unchanged.ProcessFrame(new float[] { 1f });
			switch (kind)
			{
				case FilterKind.FirLowPass:
				case FilterKind.FirHighPass:
					instance.SetStageParameter(1, "CutoffHz",
						FilterParameterValue.FromNumber(5400));
					break;
				default:
					instance.SetStageParameters(1, new[] {
						Pair("LowCutoffHz", 850),
						Pair("HighCutoffHz", 5700),
					});
					break;
			}
			updated.Configure(new FilterChain(new[] { instance }));
			float[] changed = new float[160];
			float[] original = new float[160];
			updated.ProcessInterleaved(changed);
			unchanged.ProcessInterleaved(original);
			Assert.That(changed[0], Is.EqualTo(original[0]).Within(0.002),
				$"{kind}: first frame in the 32-frame ramp stays near old response.");
			Assert.That(changed.Zip(original).Any(pair =>
				Math.Abs(pair.First - pair.Second) > 1e-5), Is.True,
				$"{kind}: design coefficients must actually change.");
			Assert.That(changed.All(float.IsFinite), Is.True);
			StreamingFilterChain newInvocation = Runtime(instance);
			float[] empty = { 0f };
			newInvocation.ProcessFrame(empty);
			Assert.That(empty[0], Is.Zero);
		}
	}

	[Test]
	public void StructuralTapWindowAndWidthChangesResetCircularHistory()
	{
		foreach (string structural in new[] {
			"TapCount", "Window", "TransitionWidthHz",
		})
		{
			FilterInstance instance = CreateUse(FilterKind.FirLowPass, taps: 127);
			StreamingFilterChain runtime = Runtime(instance);
			float[] warmup = { 1f };
			runtime.ProcessFrame(warmup);
			switch (structural)
			{
				case "TapCount":
					instance.SetStageParameter(1, structural,
						FilterParameterValue.FromNumber(255));
					break;
				case "Window":
					instance.SetStageParameter(1, structural,
						FilterParameterValue.FromChoice("Blackman"));
					break;
				default:
					instance.SetStageParameter(1, structural,
						FilterParameterValue.FromNumber(600));
					break;
			}
			runtime.Configure(new FilterChain(new[] { instance }));
			float[] silence = new float[256];
			runtime.ProcessInterleaved(silence);
			Assert.That(silence, Is.All.Zero,
				$"{structural} must discard the old FIR history.");
		}
	}

	[Test]
	public void InvalidSecondStageCannotPublishPartialFirstStageRetuning()
	{
		FilterInstance valid = CreateUse(FilterKind.FirLowPass, taps: 127,
			cutoff: 2400, width: 500);
		StreamingFilterChain runtime = new(
			new FilterChain(new[] { valid, valid }), 8000, 1);
		float[] warmup = new float[100];
		warmup[0] = 1;
		runtime.ProcessInterleaved(warmup);
		StreamingFilterChain unchanged = runtime.Fork();

		FilterInstance changed = valid.Clone();
		changed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3000));
		FilterInstance invalid = valid.Clone();
		invalid.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3900));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			runtime.Configure(new FilterChain(new[] { changed, invalid })));
		float[] after = new float[160];
		after[0] = -0.25f;
		float[] expected = (float[])after.Clone();
		runtime.ProcessInterleaved(after);
		unchanged.ProcessInterleaved(expected);
		Assert.That(after, Is.EqualTo(expected));
	}

	[Test]
	public void FixedDelayMetadataAddsAcrossSerialStagesAndSurvivesFork()
	{
		FilterInstance first = CreateUse(FilterKind.FirLowPass, taps: 63);
		FilterInstance second = CreateUse(FilterKind.FirNotch, taps: 127);
		StreamingFilterChain runtime = new(
			new FilterChain(new[] { first, second }), SampleRate, 2);
		Assert.That(runtime.PreparedLatencyFrames, Is.EqualTo(31 + 63));
		Assert.That(runtime.PreparedTailFrames, Is.EqualTo(62 + 126));
		StreamingFilterChain cloned = runtime.Fork();
		Assert.That(cloned.PreparedLatencyFrames, Is.EqualTo(94));
		Assert.That(cloned.PreparedTailFrames, Is.EqualTo(188));
		runtime.Reset();
		Assert.That(runtime.PreparedLatencyFrames, Is.EqualTo(94));

		Assert.That(LinearPhaseFirFilterStageFactory.GroupDelayFrames(2047),
			Is.EqualTo(1023));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LinearPhaseFirFilterStageFactory.GroupDelayFrames(2048));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LinearPhaseFirFilterStageFactory.GroupDelayFrames(2));
	}

	[Test]
	public void StereoFastModulationAndBlockPartitioningAreBitIdentical()
	{
		FilterInstance instance = CreateUse(FilterKind.FirLowPass,
			taps: 127, width: 500);
		StreamingFilterChain full = Runtime(instance, channels: 2);
		StreamingFilterChain split = Runtime(instance, channels: 2);
		for (int block = 0; block < 120; block++)
		{
			instance.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(
					700 + ((block * 3917) % 14000)));
			FilterChain chain = new(new[] { instance });
			full.Configure(chain);
			split.Configure(chain);
			float[] pcm = new float[2 * 19];
			for (int n = 0; n < 19; n++)
			{
				pcm[n * 2] = (float)(0.27 * Math.Sin(
					2 * Math.PI * (block * 19 + n) * 4011 / SampleRate));
				pcm[n * 2 + 1] = block % 2 == 0 ? 0.13f : -0.18f;
			}
			float[] partition = (float[])pcm.Clone();
			full.ProcessInterleaved(pcm);
			split.ProcessInterleaved(partition.AsSpan(0, 12));
			split.ProcessInterleaved(partition.AsSpan(12, 26));
			Assert.That(partition, Is.EqualTo(pcm), $"FIR batch {block}");
			Assert.That(pcm.All(float.IsFinite), Is.True);
		}
	}

	[Test]
	public void ForkCopiesCircularSamplesPositionAndMidRampCoefficients()
	{
		FilterInstance instance = CreateUse(FilterKind.FirBandPass,
			taps: 127, width: 600);
		StreamingFilterChain live = Runtime(instance, channels: 3);
		float[] prefix = Enumerable.Range(0, 3 * 250)
			.Select(n => (float)(0.3 * Math.Sin(
				2 * Math.PI * (n / 3) * (807 + (n % 3) * 551) / SampleRate)))
			.ToArray();
		float[] expectedPrefix = (float[])prefix.Clone();
		live.ProcessInterleaved(expectedPrefix);
		instance.SetStageParameters(1, new[] {
			Pair("LowCutoffHz", 1000),
			Pair("HighCutoffHz", 6200),
		});
		live.Configure(new FilterChain(new[] { instance }));
		live.ProcessInterleaved(new float[3 * 7]);
		StreamingFilterChain fork = live.Fork();
		float[] continuation = Enumerable.Range(0, 3 * 180)
			.Select(n => (float)(0.18 * Math.Cos(
				2 * Math.PI * (n / 3) * (441 + (n % 3) * 1701) / SampleRate)))
			.ToArray();
		float[] cloned = (float[])continuation.Clone();
		live.ProcessInterleaved(continuation);
		fork.ProcessInterleaved(cloned);
		Assert.That(cloned, Is.EqualTo(continuation));

		fork.Reset();
		fork.Configure(new FilterChain(new[] {
			CreateUse(FilterKind.FirBandPass, taps: 127, width: 600),
		}));
		fork.Reset();
		float[] replay = (float[])prefix.Clone();
		fork.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(expectedPrefix));
	}

	[TestCase(FilterKind.FirLowPass)]
	[TestCase(FilterKind.FirHighPass)]
	[TestCase(FilterKind.FirBandPass)]
	[TestCase(FilterKind.FirNotch)]
	public void FiniteTailClearsAfterExactlyNMinusOneFrames(FilterKind kind)
	{
		const int taps = 127;
		StreamingFilterChain runtime = Runtime(CreateUse(kind,
			taps: taps, width: 500));
		runtime.ProcessFrame(new float[] { 1f });
		runtime.FinishInput();
		float[] tail = new float[taps + 10];
		Assert.That(runtime.RenderTail(tail), Is.EqualTo(taps - 1));
		Assert.That(tail.Skip(taps - 1), Is.All.Zero);
		Assert.That(runtime.TailRemainingFrames, Is.Zero);
		Assert.That(tail.All(v => float.IsFinite(v)
			&& (v == 0 || Math.Abs(v) >= 1e-20f)), Is.True);
		runtime.Reset();
		float[] corrupted = { float.NaN, float.PositiveInfinity,
			float.NegativeInfinity, float.MaxValue, -float.MaxValue, 0 };
		runtime.ProcessInterleaved(corrupted);
		Assert.That(corrupted.All(float.IsFinite), Is.True);
		StreamingFilterChain capped = Runtime(
			CreateUse(kind, taps: 127, width: 500),
			maximumTailSeconds: 0.001);
		Assert.That(capped.PreparedTailFrames, Is.EqualTo(48));
		capped.ProcessFrame(new float[] { 1 });
		capped.FinishInput();
		float[] shorter = new float[70];
		Assert.That(capped.RenderTail(shorter), Is.EqualTo(48));
		Assert.That(shorter.Skip(48), Is.All.Zero);
	}

	[Test]
	public void Maximum2047TapConfigurationIsBoundedAndStillDeterministic()
	{
		FilterInstance instance = CreateUse(FilterKind.FirHighPass,
			taps: 2047, width: 1000, cutoff: 4000);
		StreamingFilterChain one = Runtime(instance);
		StreamingFilterChain two = one.Fork();
		Assert.That(one.PreparedTailFrames, Is.EqualTo(2046));
		Assert.That(one.PreparedLatencyFrames, Is.EqualTo(1023));
		float[] impulse = new float[32];
		impulse[0] = 1;
		float[] copied = (float[])impulse.Clone();
		one.ProcessInterleaved(impulse);
		two.ProcessInterleaved(copied);
		Assert.That(impulse, Is.EqualTo(copied));
	}

	private static FilterInstance CreateUse(FilterKind kind,
		int taps = 127, double width = 500, double cutoff = 3000,
		double low = 2000, double high = 8000, string window = "Hamming",
		string phase = "Linear")
	{
		FilterDefinition definition = new((ObjectId)71U, "FIR", new[] {
			new FilterStageDefinition(1, "windowed", kind),
		});
		FilterInstance instance = new(definition);
		List<KeyValuePair<string, FilterParameterValue>> values = [
			Pair("TapCount", taps),
			Pair("TransitionWidthHz", width),
			new("Window", FilterParameterValue.FromChoice(window)),
			new("PhaseResponse", FilterParameterValue.FromChoice(phase)),
		];
		if (kind is FilterKind.FirLowPass or FilterKind.FirHighPass)
			values.Add(Pair("CutoffHz", cutoff));
		else
		{
			values.Add(Pair("LowCutoffHz", low));
			values.Add(Pair("HighCutoffHz", high));
		}
		instance.SetStageParameters(1, values);
		return instance;
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, double value)
		=> new(name, FilterParameterValue.FromNumber(value));

	private static StreamingFilterChain Runtime(FilterInstance instance,
		int channels = 1, double maximumTailSeconds = 10)
		=> new(new FilterChain(new[] { instance }), SampleRate, channels,
			maximumTailSeconds: maximumTailSeconds);

	private static float[] Impulse(FilterInstance instance)
	{
		StreamingFilterChain filter = Runtime(instance);
		float[] result = new float[filter.PreparedTailFrames + 1];
		result[0] = 1;
		filter.ProcessInterleaved(result);
		return result;
	}

	private static double Magnitude(FilterInstance instance, double frequency)
		=> MeasureResponse(Runtime(instance), frequency).Magnitude;

	private static Complex MeasureResponse(
		StreamingFilterChain runtime, double frequency)
	{
		const int settling = 4096;
		const int frames = 8192;
		Complex source = Complex.Zero;
		Complex filtered = Complex.Zero;
		float[] frame = new float[1];
		for (int i = 0; i < settling + frames; i++)
		{
			double theta = 2 * Math.PI * frequency * i / SampleRate;
			double excitation = Math.Sin(theta);
			frame[0] = (float)excitation;
			runtime.ProcessFrame(frame);
			if (i < settling)
				continue;
			Complex probe = Complex.FromPolarCoordinates(1, -theta);
			source += excitation * probe;
			filtered += frame[0] * probe;
		}
		return filtered / source;
	}
}

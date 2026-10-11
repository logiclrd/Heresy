using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FeedforwardCombFilterTests
{
	private const int SampleRate = 48000;

	[TestCase(0.6, 0.5, 0.3)]
	[TestCase(-1.0, 1.0, -1.0)]
	[TestCase(0.0, 1.0, 0.0)]
	[TestCase(1.0, 0.0, 0.0)]
	public void ImpulseHasExactlyOneDelayedTap(
		double feedforward, double mix, double echo)
	{
		StreamingFilterChain filter = Runtime(CreateUse(0.001, feedforward, mix));
		float[] pcm = new float[48 * 4 + 1];
		pcm[0] = 1;
		filter.ProcessInterleaved(pcm);
		Assert.That(pcm[0], Is.EqualTo(1));
		Assert.That(pcm[48], Is.EqualTo((float)echo).Within(1e-7));
		Assert.That(pcm.Skip(1).Where((value, index) => index != 47),
			Is.All.Zero);
		Assert.That(filter.PreparedTailFrames, Is.EqualTo(48));
		Assert.That(filter.PreparedLatencyFrames, Is.Zero);
	}

	[Test]
	public void DelayRoundsHalfFramesAwayFromZeroAndEnforcesMemoryLimit()
	{
		StreamingFilterChain half =
			new(new FilterChain(new[] { CreateUse(0.0015, 1, 1) }), 1000, 1);
		float[] pcm = { 1f, 0, 0, 0 };
		half.ProcessInterleaved(pcm);
		Assert.That(pcm, Is.EqualTo(new[] { 1f, 0f, 1f, 0f }));
		Assert.That(half.PreparedTailFrames, Is.EqualTo(2));
		Assert.That(Runtime(CreateUse(2, 1, 1)).PreparedTailFrames,
			Is.EqualTo(96000));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new StreamingFilterChain(new FilterChain(new[] {
				CreateUse(2, 1, 1),
			}), 1_000_000, 1));
	}

	[Test]
	public void MeasuredPeaksNotchesAndComplexPhaseMatchTwoTapTransfer()
	{
		Assert.That(Magnitude(0.01, 0.5, 1, 100),
			Is.EqualTo(1.5).Within(0.002));
		Assert.That(Magnitude(0.01, 0.5, 1, 50),
			Is.EqualTo(0.5).Within(0.002));
		Assert.That(Magnitude(0.01, -1, 1, 50),
			Is.EqualTo(2).Within(0.002));
		Assert.That(Magnitude(0.01, -1, 1, 100),
			Is.LessThan(0.002));
		(double magnitude, double phase) = Response(0.01, 0.5, 1, 25);
		Assert.That(magnitude, Is.EqualTo(Math.Sqrt(1.25)).Within(0.002));
		Assert.That(phase, Is.EqualTo(-Math.Atan(0.5)).Within(0.002));
	}

	[Test]
	public void StructuralDelayChangeResetsEvenForSameRoundedLength()
	{
		FilterInstance use = CreateUse(0.001001, 1, 1);
		StreamingFilterChain filter = Runtime(use);
		filter.ProcessFrame(new float[] { 1 });
		use.SetStageParameter(1, "DelaySeconds",
			FilterParameterValue.FromNumber(0.001002));
		filter.Configure(new FilterChain(new[] { use }));
		float[] zeros = new float[150];
		filter.ProcessInterleaved(zeros);
		Assert.That(zeros, Is.All.Zero);
		float[] fresh = new float[49];
		fresh[0] = 1;
		filter.ProcessInterleaved(fresh);
		Assert.That(fresh[48], Is.EqualTo(1));
	}

	[Test]
	public void GainRetuningKeepsDelayHistoryAndSmoothsFor32Frames()
	{
		FilterInstance use = CreateUse(0.001, 1, 1);
		StreamingFilterChain filter = Runtime(use);
		filter.ProcessFrame(new float[] { 1 });
		filter.ProcessInterleaved(new float[39]);
		use.SetStageParameter(1, "Feedforward",
			FilterParameterValue.FromNumber(-1));
		filter.Configure(new FilterChain(new[] { use }));
		float[] remaining = new float[9];
		filter.ProcessInterleaved(remaining);
		Assert.That(remaining.Take(8), Is.All.Zero);
		Assert.That(remaining[8], Is.EqualTo(0.4375f).Within(1e-7),
			"ninth ramp frame uses gain 1 - 2*9/32.");
	}

	[Test]
	public void FinishInputDrainsLastEchoAndSupportsExplicitCap()
	{
		StreamingFilterChain full = Runtime(CreateUse(0.001, 1, 1));
		full.ProcessFrame(new float[] { 1 });
		full.FinishInput();
		float[] samples = new float[52];
		Assert.That(full.RenderTail(samples), Is.EqualTo(48));
		Assert.That(samples.Take(47), Is.All.Zero);
		Assert.That(samples[47], Is.EqualTo(1));
		Assert.That(samples.Skip(48), Is.All.Zero);

		StreamingFilterChain limited = Runtime(
			CreateUse(0.001, 1, 1), maximumTailSeconds: 0.0005);
		limited.ProcessFrame(new float[] { 1 });
		limited.FinishInput();
		float[] capped = new float[48];
		Assert.That(limited.RenderTail(capped), Is.EqualTo(24));
		Assert.That(capped, Is.All.Zero);
	}

	[Test]
	public void NeutralTargetStillBudgetsPossibleInFlightTransition()
	{
		FilterInstance use = CreateUse(0.001, 1, 1);
		StreamingFilterChain filter = Runtime(use);
		filter.ProcessFrame(new float[] { 1 });
		use.SetStageParameter(1, "Mix",
			FilterParameterValue.FromNumber(0));
		filter.Configure(new FilterChain(new[] { use }));
		filter.FinishInput();
		Assert.That(filter.PreparedTailFrames, Is.EqualTo(48));
		float[] tail = new float[48];
		filter.RenderTail(tail);
		Assert.That(tail[47], Is.Zero);
	}

	[Test]
	public void InvalidSecondStageRollsBackEarlierStageAndHistories()
	{
		const int rate = 1_000_000;
		FilterInstance original = CreateUse(0.001, 1, 1);
		StreamingFilterChain running = new(
			new FilterChain(new[] { original, original }), rate, 1);
		float[] intro = new float[80];
		intro[0] = 1;
		running.ProcessInterleaved(intro);
		StreamingFilterChain copy = running.Fork();

		FilterInstance changed = original.Clone();
		changed.SetStageParameter(1, "Feedforward",
			FilterParameterValue.FromNumber(-1));
		FilterInstance illegal = original.Clone();
		illegal.SetStageParameter(1, "DelaySeconds",
			FilterParameterValue.FromNumber(2));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			running.Configure(new FilterChain(new[] { changed, illegal })));
		float[] next = new float[1100];
		next[0] = 0.75f;
		float[] reference = (float[])next.Clone();
		running.ProcessInterleaved(next);
		copy.ProcessInterleaved(reference);
		Assert.That(next, Is.EqualTo(reference));
	}

	[Test]
	public void StereoPartitioningAndRapidRetunesAreBitIdentical()
	{
		FilterInstance use = CreateUse(0.003, 0.2, 0.5);
		StreamingFilterChain single = Runtime(use, channels: 2);
		StreamingFilterChain split = Runtime(use, channels: 2);
		for (int batch = 0; batch < 180; batch++)
		{
			use.SetStageParameters(1, new[] {
				Pair("Feedforward", (batch % 11 - 5) / 5.0),
				Pair("Mix", (batch % 17) / 16.0),
			});
			FilterChain update = new(new[] { use });
			single.Configure(update);
			split.Configure(update);
			float[] a = Enumerable.Range(0, 42)
				.Select(n => (float)(0.4 * Math.Sin(
					2 * Math.PI * (batch * 21 + n / 2) *
					(n % 2 == 0 ? 1500 : 2700) / SampleRate)))
				.ToArray();
			float[] b = (float[])a.Clone();
			single.ProcessInterleaved(a);
			split.ProcessInterleaved(b.AsSpan(0, 14));
			split.ProcessInterleaved(b.AsSpan(14, 28));
			Assert.That(a, Is.EqualTo(b), $"batch {batch}");
		}
	}

	[Test]
	public void ForkCopiesRingAndRampAndResetReplaysDeterministically()
	{
		FilterInstance use = CreateUse(0.002, 0.8, 0.5);
		StreamingFilterChain original = Runtime(use, channels: 3);
		float[] start = Enumerable.Range(0, 3 * 230)
			.Select(i => (float)(0.25 * Math.Sin(
				2 * Math.PI * (i / 3) * (400 + (i % 3) * 300) / SampleRate)))
			.ToArray();
		float[] rendered = (float[])start.Clone();
		original.ProcessInterleaved(rendered);
		use.SetStageParameters(1, new[] {
			Pair("Mix", 0.9), Pair("Feedforward", -0.7),
		});
		original.Configure(new FilterChain(new[] { use }));
		original.ProcessInterleaved(new float[3 * 5]);
		StreamingFilterChain fork = original.Fork();
		float[] a = Enumerable.Range(0, 3 * 205)
			.Select(i => (float)(0.2 * Math.Cos(
				2 * Math.PI * (i / 3) * 701 / SampleRate))).ToArray();
		float[] b = (float[])a.Clone();
		original.ProcessInterleaved(a);
		fork.ProcessInterleaved(b);
		Assert.That(a, Is.EqualTo(b));

		fork.Reset();
		fork.Configure(new FilterChain(new[] {
			CreateUse(0.002, 0.8, 0.5),
		}));
		fork.Reset();
		float[] replay = (float[])start.Clone();
		fork.ProcessInterleaved(replay);
		Assert.That(replay, Is.EqualTo(rendered));
	}

	[Test]
	public void ChannelsAndInvocationsHaveIndependentRings()
	{
		FilterInstance use = CreateUse(0.001, 1, 1);
		StreamingFilterChain first = Runtime(use, channels: 2);
		StreamingFilterChain second = Runtime(use, channels: 2);
		float[] left = new float[49 * 2];
		left[0] = 1;
		first.ProcessInterleaved(left);
		Assert.That(left[48 * 2], Is.EqualTo(1));
		Assert.That(left[48 * 2 + 1], Is.Zero);
		float[] right = new float[49 * 2];
		right[1] = 1;
		second.ProcessInterleaved(right);
		Assert.That(right[48 * 2], Is.Zero);
		Assert.That(right[48 * 2 + 1], Is.EqualTo(1));
	}

	[Test]
	public void NonfiniteAndExtremeInputCannotPoisonRing()
	{
		StreamingFilterChain filter = Runtime(CreateUse(0.001, 1, 1));
		float[] input = new float[200];
		input[0] = float.MaxValue;
		input[1] = float.NaN;
		input[2] = float.PositiveInfinity;
		input[3] = float.NegativeInfinity;
		filter.ProcessInterleaved(input);
		Assert.That(input.All(float.IsFinite), Is.True);
		Assert.That(input[48], Is.EqualTo(float.MaxValue));
		Assert.That(input[49], Is.Zero);
		filter.Reset();
		float[] fresh = new float[50];
		fresh[0] = 1;
		filter.ProcessInterleaved(fresh);
		Assert.That(fresh[48], Is.EqualTo(1));
	}

	private static FilterInstance CreateUse(
		double seconds, double feedforward, double mix)
	{
		FilterDefinition definition = new((ObjectId)83U,
			"Feedforward Comb", new[] {
				new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
			});
		FilterInstance use = new(definition);
		use.SetStageParameters(1, new[] {
			Pair("DelaySeconds", seconds),
			Pair("Feedforward", feedforward),
			Pair("Mix", mix),
		});
		return use;
	}

	private static KeyValuePair<string, FilterParameterValue> Pair(
		string name, double value)
		=> new(name, FilterParameterValue.FromNumber(value));

	private static StreamingFilterChain Runtime(
		FilterInstance use, int channels = 1, double maximumTailSeconds = 10)
		=> new(new FilterChain(new[] { use }), SampleRate, channels,
			maximumTailSeconds: maximumTailSeconds);

	private static double Magnitude(
		double seconds, double feedforward, double mix, double frequency)
		=> Response(seconds, feedforward, mix, frequency).magnitude;

	private static (double magnitude, double phase) Response(
		double seconds, double feedforward, double mix, double frequency)
	{
		StreamingFilterChain filter = Runtime(CreateUse(
			seconds, feedforward, mix));
		const int warmup = 4800;
		const int measure = 9600;
		double sinIn = 0, cosIn = 0, sinOut = 0, cosOut = 0;
		float[] frame = new float[1];
		for (int n = 0; n < warmup + measure; n++)
		{
			double angle = 2 * Math.PI * frequency * n / SampleRate;
			double s = Math.Sin(angle);
			double c = Math.Cos(angle);
			frame[0] = (float)s;
			filter.ProcessFrame(frame);
			if (n < warmup) continue;
			sinIn += s * s;
			cosIn += s * c;
			sinOut += frame[0] * s;
			cosOut += frame[0] * c;
		}
		double real = sinOut / sinIn;
		double imaginary = (cosOut - real * cosIn) / sinIn;
		return (Math.Sqrt(real * real + imaginary * imaginary),
			Math.Atan2(imaginary, real));
	}
}

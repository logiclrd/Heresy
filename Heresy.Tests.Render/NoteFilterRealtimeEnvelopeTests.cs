using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
[NonParallelizable]
public sealed class NoteFilterRealtimeEnvelopeTests
{
	private const int Rate = 48000;

	[Test]
	public void ScalarEnvelopeFastPathMatchesFullAtomicConfigureAndAllocatesNoManagedBytes()
	{
		FilterInstance baseUse = Mixed();
		StreamingFilterChain actual = new(new FilterChain([baseUse]), Rate, 1);
		StreamingFilterChain expected = new(new FilterChain([baseUse]), Rate, 1);
		NoteFilterEnvelopeRealtimeControls controls =
			new(new FilterChain([baseUse]), Rate);
		IEnvelopeCurve cutoff = new WaveCurve(2500, 400, 0.029);
		IEnvelopeCurve comb = new WaveCurve(0.2, 0.3, 0.021);
		IEnvelopeCurve gain = new WaveCurve(1.0, 5.0, 0.017);
		controls.SetBindings([
			new(0, 1, "Feedforward", new(comb, 0, Rate)),
			new(0, 2, "CutoffHz", new(cutoff, 0, Rate)),
			new(0, 3, "GainDb", new(gain, 0, Rate)),
		]);
		float[] fastFrame = [0], slowFrame = [0];
		for (int frame = 0; frame < 192; frame++)
		{
			controls.Apply(frame, actual, null);
			FilterInstance updated = baseUse.Clone();
			updated.SetStageParameter(1, "Feedforward",
				FilterParameterValue.FromNumber(comb.GetValue(frame, Rate, null)));
			updated.SetStageParameter(2, "CutoffHz",
				FilterParameterValue.FromNumber(cutoff.GetValue(frame, Rate, null)));
			updated.SetStageParameter(3, "GainDb",
				FilterParameterValue.FromNumber(gain.GetValue(frame, Rate, null)));
			expected.Configure(new FilterChain([updated]));
			fastFrame[0] = slowFrame[0] = frame % 11 == 0 ? 1 : 0;
			actual.ProcessFrame(fastFrame);
			expected.ProcessFrame(slowFrame);
			Assert.That(fastFrame[0], Is.EqualTo(slowFrame[0]).Within(1e-5f),
				$"frame {frame}");
		}

		// Measure only the audio-rate retune path after all construction,
		// binding and warmup. No FilterParameterValue objects or DSP forks
		// should be created by any legal scalar sample.
		for (int frame = 192; frame < 512; frame++)
			controls.Apply(frame, actual, null);
		Stopwatch clock = new();
		long before = GC.GetAllocatedBytesForCurrentThread();
		clock.Start();
		for (int frame = 512; frame < 4608; frame++)
			controls.Apply(frame, actual, null);
		clock.Stop();
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		TestContext.Out.WriteLine(
			$"note envelope scalar 4096 frames: {clock.Elapsed.TotalMilliseconds:F3} ms, allocated {allocated} bytes");
		Assert.That(allocated, Is.EqualTo(0));
	}

	[Test]
	public void InvalidMixedStageSampleNeverPartiallyPublishesOtherStage()
	{
		FilterInstance use = Mixed();
		StreamingFilterChain running = new(new FilterChain([use]), Rate, 1);
		StreamingFilterChain unchanged = new(new FilterChain([use]), Rate, 1);
		NoteFilterEnvelopeRealtimeControls controls =
			new(new FilterChain([use]), Rate);
		controls.SetBindings([
			new(0, 1, "Feedforward",
				new(new FixedCurve(2.0), 0, Rate)),
			new(0, 2, "CutoffHz",
				new(new FixedCurve(4000), 0, Rate)),
		]);
		List<string> diagnostics = [];
		for (int frame = 0; frame < 16; frame++)
		{
			controls.Apply(frame, running, diagnostics.Add);
			float[] actual = [frame == 0 ? 1 : 0];
			float[] reference = [frame == 0 ? 1 : 0];
			running.ProcessFrame(actual);
			unchanged.ProcessFrame(reference);
			Assert.That(actual, Is.EqualTo(reference).Within(1e-6f));
		}
		Assert.That(diagnostics, Is.Not.Empty);
	}

	[Test]
	public void PreparedFirPlanPublishesOnlyAtMatchingControlBoundary()
	{
		FilterInstance use = Fir("Linear", 127);
		StreamingFilterChain running = new(new FilterChain([use]), Rate, 1);
		StreamingFilterChain reference = new(new FilterChain([use]), Rate, 1);
		NoteFilterEnvelopeRealtimeControls controls =
			new(new FilterChain([use]), Rate);
		controls.SetBindings([
			new(0, 1, "CutoffHz",
				new(new FixedCurve(4500), 0, Rate)),
		]);
		controls.Apply(0, running, null); // queue, hold last valid
		float[] a = [1], b = [1];
		running.ProcessFrame(a);
		reference.ProcessFrame(b);
		Assert.That(a, Is.EqualTo(b).Within(1e-6f));
		Assert.That(controls.TryPreparePendingFirs(), Is.True);
		FilterInstance updated = use.Clone();
		updated.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(4500));
		reference.Configure(new FilterChain([updated]));
		for (int frame = 1; frame < 90; frame++)
		{
			controls.Apply(frame, running, null);
			a[0] = b[0] = 0;
			running.ProcessFrame(a);
			reference.ProcessFrame(b);
			Assert.That(a[0], Is.EqualTo(b[0]).Within(1e-6f),
				$"FIR frame {frame}");
		}
	}

	[Test]
	public void NonlinearFirOverBudgetHoldsLastValidPlanAndReportsOnce()
	{
		FilterInstance use = Fir("Minimum", 129);
		StreamingFilterChain running = new(new FilterChain([use]), Rate, 1);
		StreamingFilterChain unchanged = new(new FilterChain([use]), Rate, 1);
		NoteFilterEnvelopeRealtimeControls controls =
			new(new FilterChain([use]), Rate);
		controls.SetBindings([
			new(0, 1, "CutoffHz",
				new(new FixedCurve(4500), 0, Rate)),
		]);
		List<string> diagnostics = [];
		for (int frame = 0; frame < 64; frame++)
		{
			controls.Apply(frame, running, diagnostics.Add);
			float[] actual = [frame == 0 ? 1 : 0];
			float[] reference = [frame == 0 ? 1 : 0];
			running.ProcessFrame(actual);
			unchanged.ProcessFrame(reference);
			Assert.That(actual, Is.EqualTo(reference).Within(1e-6f));
		}
		Assert.That(controls.TryPreparePendingFirs(), Is.False);
		Assert.That(diagnostics, Has.Count.EqualTo(1));
		Assert.That(diagnostics[0], Does.Contain("budget"));
	}

    [Test]
    public void MovingEnvelopeLatchesOneBoundedAsyncFirTargetUntilPublished()
    {
        FilterInstance use = Fir("Linear", 127);
        StreamingFilterChain actual = new(new FilterChain([use]), Rate, 1);
        StreamingFilterChain expected = new(new FilterChain([use]), Rate, 1);
        NoteFilterEnvelopeRealtimeControls controls =
            new(new FilterChain([use]), Rate);
        controls.SetBindings([
            new(0, 1, "CutoffHz",
                new(new WaveCurve(4500, 800, 0.17), 0, Rate)),
        ]);
        float[] a = [1], b = [1];
        controls.Apply(0, actual, null);
        actual.ProcessFrame(a);
        expected.ProcessFrame(b);
        Assert.That(a, Is.EqualTo(b));
        using FirPreparationScheduler worker = new();
        Assert.That(controls.QueuePendingFirs(worker), Is.True);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedPreparationCount > 0,
            TimeSpan.FromSeconds(4)), Is.True);
        Assert.That(controls.AdoptPreparedFirs(), Is.True);

        FilterInstance updated = use.Clone();
        updated.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(4500));
        expected.Configure(new FilterChain([updated]));
        for (int i = 1; i < 60; i++)
        {
            controls.Apply(i, actual, null);
            a[0] = b[0] = i % 11 == 0 ? 0.75f : 0;
            actual.ProcessFrame(a);
            expected.ProcessFrame(b);
            Assert.That(a[0], Is.EqualTo(b[0]).Within(1e-6f),
                $"held FIR frame {i}");
        }
        Assert.That(controls.QueuePendingFirs(worker), Is.True);
        Assert.That(worker.Fault, Is.Null);
    }

	[Test]
	public void DuplicateStageIdsInDistinctInheritedLayersRetuneIndependently()
	{
		FilterInstance inner = OnePole(23001, 1000);
		FilterInstance outer = OnePole(23002, 1600);
		FilterChain chain = new([inner, outer]);
		StreamingFilterChain running = new(chain, Rate, 1);
		StreamingFilterChain reference = new(chain, Rate, 1);
		NoteFilterEnvelopeRealtimeControls controls = new(chain, Rate);
		controls.SetBindings([
			new(0, 1, "CutoffHz", new(new FixedCurve(2000), 0, Rate)),
			new(1, 1, "CutoffHz", new(new FixedCurve(3000), 0, Rate)),
		]);
		FilterInstance newInner = inner.Clone(), newOuter = outer.Clone();
		newInner.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(2000));
		newOuter.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(3000));
		reference.Configure(new FilterChain([newInner, newOuter]));
		for (int frame = 0; frame < 48; frame++)
		{
			controls.Apply(frame, running, null);
			float[] a = [frame == 0 ? 1 : 0];
			float[] b = [frame == 0 ? 1 : 0];
			running.ProcessFrame(a);
			reference.ProcessFrame(b);
			Assert.That(a, Is.EqualTo(b).Within(1e-6f));
		}
	}


	[Test]
	public void FourVoiceRealtimeEnvelopeQualificationRecordsCpuAndZeroSteadyAllocations()
	{
		FilterInstance use = Mixed();
		NoteFilterEnvelopeRealtimeControls[] automation = new NoteFilterEnvelopeRealtimeControls[4];
		StreamingFilterChain[] filters = new StreamingFilterChain[4];
		float[][] samples = new float[4][];
		for (int i = 0; i < 4; i++)
		{
			FilterChain chain = new([use]);
			filters[i] = new StreamingFilterChain(chain, Rate, 1);
			automation[i] = new NoteFilterEnvelopeRealtimeControls(chain, Rate);
			automation[i].SetBindings([
				new(0, 1, "Feedforward",
					new(new WaveCurve(0.2, 0.3, 0.029), 0, Rate)),
				new(0, 2, "CutoffHz",
					new(new WaveCurve(3000, 800, 0.017), 0, Rate)),
				new(0, 3, "GainDb",
					new(new WaveCurve(0, 5, 0.031), 0, Rate)),
			]);
			samples[i] = new float[1];
		}
		for (int frame = 0; frame < 192; frame++)
			for (int voice = 0; voice < 4; voice++)
			{
				automation[voice].Apply(frame, filters[voice], null);
				samples[voice][0] = frame % 7 == 0 ? 1 : 0;
				filters[voice].ProcessFrame(samples[voice]);
			}

		const int measuredFrames = 4096;
		long before = GC.GetAllocatedBytesForCurrentThread();
		long start = Stopwatch.GetTimestamp();
		for (int frame = 192; frame < 192 + measuredFrames; frame++)
		{
			automation[0].Apply(frame, filters[0], null);
			samples[0][0] = frame % 7 == 0 ? 1 : 0;
			filters[0].ProcessFrame(samples[0]);
		}
		long midpoint = Stopwatch.GetTimestamp();
		for (int frame = 192 + measuredFrames;
			frame < 192 + 2 * measuredFrames; frame++)
			for (int voice = 0; voice < 4; voice++)
			{
				automation[voice].Apply(frame, filters[voice], null);
				samples[voice][0] = frame % 7 == 0 ? 1 : 0;
				filters[voice].ProcessFrame(samples[voice]);
			}
		long end = Stopwatch.GetTimestamp();
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		double oneMs = (midpoint - start) * 1000.0 / Stopwatch.Frequency;
		double fourMs = (end - midpoint) * 1000.0 / Stopwatch.Frequency;
		double audioMs = measuredFrames * 1000.0 / Rate;
		TestContext.Out.WriteLine(
			$"note envelope multivoice: {Environment.OSVersion}; .NET {Environment.Version}; " +
			$"processors {Environment.ProcessorCount}; audio {audioMs:F2}ms; " +
			$"one voice {oneMs:F2}ms ({oneMs / audioMs:F2}x); " +
			$"four voices {fourMs:F2}ms ({fourMs / audioMs:F2}x); " +
			$"managed allocation {allocated} bytes");
		Assert.That(allocated, Is.EqualTo(0));
	}

	private static FilterInstance OnePole(uint id, double cutoff)
	{
		FilterDefinition definition = new((ObjectId)id, "pole", [
			new FilterStageDefinition(1, "pole", FilterKind.OnePole),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(cutoff));
		return use;
	}

	private static FilterInstance Fir(string phase, int taps)
	{
		FilterDefinition definition = new((ObjectId)23003U, "fir", [
			new FilterStageDefinition(1, "fir", FilterKind.FirLowPass),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("TapCount", FilterParameterValue.FromNumber(taps)),
			new("TransitionWidthHz", FilterParameterValue.FromNumber(200)),
			new("PhaseResponse", FilterParameterValue.FromChoice(phase)),
			new("CutoffHz", FilterParameterValue.FromNumber(4000)),
		]);
		return use;
	}

	private static FilterInstance Mixed()
	{
		FilterDefinition definition = new((ObjectId)23004U, "mixed", [
			new FilterStageDefinition(1, "comb", FilterKind.CombFeedforward),
			new FilterStageDefinition(2, "pole", FilterKind.OnePole),
			new FilterStageDefinition(3, "eq", FilterKind.BiquadPeaking),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("DelaySeconds", FilterParameterValue.FromNumber(0.002)),
			new("Mix", FilterParameterValue.FromNumber(0.75)),
			new("Feedforward", FilterParameterValue.FromNumber(0.2)),
		]);
		use.SetStageParameter(2, "CutoffHz",
			FilterParameterValue.FromNumber(2500));
		use.SetStageParameters(3, [
			new("CutoffHz", FilterParameterValue.FromNumber(1900)),
			new("GainDb", FilterParameterValue.FromNumber(1)),
		]);
		return use;
	}

	private sealed class FixedCurve(double value) : IEnvelopeCurve
	{
		public double GetValue(long activeFrame, int sampleRate, long? noteOff)
			=> value;
	}

	private sealed class WaveCurve(double centre, double amplitude, double omega)
		: IEnvelopeCurve
	{
		public double GetValue(long activeFrame, int sampleRate, long? noteOff)
			=> centre + amplitude * Math.Sin(activeFrame * omega);
	}
}

using System;
using System.Diagnostics;
using System.Threading;

using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render;

/// <summary>
/// C5(a): deterministic clock/underrun invariants at the real buffered
/// producer/consumer boundary. Elapsed-time data is diagnostic, not a
/// timing threshold for shared CI hosts or proof of device deadlines.
/// </summary>
[TestFixture]
public sealed class BufferedAudioDeadlineRecoveryTests
{
    [Test]
    public void StalledProducerCountsUnderrunsThenResumesTheOriginalMusicalFrame()
    {
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        SequenceSource source = new(entered, release);
        using BufferedAudioOutputSource output = new(source,
            capacityFrames: 16, blockFrames: 4, profileProducerRender: true);
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "the producer must be blocked inside source.Render");
            float[] callback = new float[4];

            // The first underrun also warms the device-facing callback path.
            output.Render(4, callback);
            Assert.That(callback, Is.All.Zero);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long callbackStarted = Stopwatch.GetTimestamp();
            output.Render(4, callback);
            long callbackTicks = Stopwatch.GetTimestamp() - callbackStarted;
            long callbackAllocated = GC.GetAllocatedBytesForCurrentThread()
                - allocatedBefore;
            Assert.Multiple(() =>
            {
                Assert.That(callback, Is.All.Zero);
                Assert.That(output.UnderrunCount, Is.EqualTo(2),
                    "each short callback increments the underrun counter once");
                Assert.That(output.BufferedFrames, Is.Zero);
                Assert.That(source.CompletedRenders, Is.Zero,
                    "a callback cannot generate PCM or advance a blocked source");
                Assert.That(callbackAllocated, Is.Zero,
                    "device-facing copies must allocate no managed memory");
            });

            // Force an intentionally missed producer block budget. This
            // does not rely on how fast the CI host happens to render DSP.
            Thread.Sleep(30);
            release.Set();
            Assert.That(SpinWait.SpinUntil(
                () => output.BufferedFrames == output.CapacityFrames
                    || output.RenderingFault is not null,
                TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(output.RenderingFault, Is.Null);
            Assert.That(output.ProducerRenderBlockCount,
                Is.GreaterThanOrEqualTo(4));
            Assert.That(output.ProducerRenderedFrames,
                Is.GreaterThanOrEqualTo(16));
            Assert.That(output.ProducerBlocksOverBudget,
                Is.GreaterThanOrEqualTo(1),
                "the deliberately stalled 4-frame render exceeded its budget");
            Assert.That(output.PeakProducerRenderBlockTime,
                Is.GreaterThan(TimeSpan.FromSeconds(4.0 / 48000)));

            // The two underflow callbacks consumed no musical frames.
            // Every later frame is still the exact first original frame,
            // followed by a contiguous, nonduplicated timeline.
            float[] recovered = new float[16];
            output.Render(16, recovered);
            for (int frame = 0; frame < recovered.Length; frame++)
                Assert.That(recovered[frame], Is.EqualTo(frame + 1),
                    $"recovered original playback frame {frame}");
            Assert.That(output.UnderrunCount, Is.EqualTo(2));
            Assert.That(SpinWait.SpinUntil(
                () => output.BufferedFrames == 16
                    || output.RenderingFault is not null,
                TimeSpan.FromSeconds(5)), Is.True);
            float[] subsequent = new float[16];
            output.Render(16, subsequent);
            for (int frame = 0; frame < subsequent.Length; frame++)
                Assert.That(subsequent[frame], Is.EqualTo(frame + 17),
                    $"subsequent original playback frame {frame}");
            Assert.That(output.UnderrunCount, Is.EqualTo(2));
            Assert.That(output.RenderingFault, Is.Null);

            TestContext.Progress.WriteLine(
                $"C5(a) forced producer stall: underflows={output.UnderrunCount}, " +
                $"producer blocks={output.ProducerRenderBlockCount}, " +
                $"produced frames={output.ProducerRenderedFrames}, " +
                $"blocks over budget={output.ProducerBlocksOverBudget}, " +
                $"peak block ms={output.PeakProducerRenderBlockTime.TotalMilliseconds:F3}, " +
                $"callback ms={(1000.0 * callbackTicks / Stopwatch.Frequency):F4}, " +
                $"callback allocated bytes={callbackAllocated}");
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public void OptInProducerProfilingDoesNotAlterAudioOrUnprofiledCounters()
    {
        SequenceSource normalSource = new();
        SequenceSource measuredSource = new();
        using BufferedAudioOutputSource normal = new(normalSource,
            capacityFrames: 12, blockFrames: 4);
        using BufferedAudioOutputSource measured = new(measuredSource,
            capacityFrames: 12, blockFrames: 4, profileProducerRender: true);
        Assert.That(SpinWait.SpinUntil(
            () => normal.BufferedFrames == 12 && measured.BufferedFrames == 12
                || normal.RenderingFault is not null
                || measured.RenderingFault is not null,
            TimeSpan.FromSeconds(5)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(normal.RenderingFault, Is.Null);
            Assert.That(measured.RenderingFault, Is.Null);
            Assert.That(normal.ProducerRenderBlockCount, Is.Zero,
                "opt-in Stopwatch measurement is disabled by default");
            Assert.That(normal.ProducerRenderedFrames, Is.Zero);
            Assert.That(normal.ProducerBlocksOverBudget, Is.Zero);
            Assert.That(normal.ProducerRenderElapsedTime, Is.EqualTo(TimeSpan.Zero));
            Assert.That(normal.PeakProducerRenderBlockTime, Is.EqualTo(TimeSpan.Zero));
            Assert.That(measured.ProducerRenderBlockCount,
                Is.GreaterThanOrEqualTo(3));
            Assert.That(measured.ProducerRenderedFrames,
                Is.GreaterThanOrEqualTo(12));
        });
        float[] expected = new float[12];
        float[] actual = new float[12];
        normal.Render(12, expected);
        measured.Render(12, actual);
        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected).AsCollection,
                "diagnostic timing must not mutate the musical timeline");
            Assert.That(normal.UnderrunCount, Is.Zero);
            Assert.That(measured.UnderrunCount, Is.Zero);
            Assert.That(measured.RenderingFault, Is.Null);
        });
    }

    private sealed class SequenceSource : IAudioOutputSource
    {
        private readonly ManualResetEventSlim? _entered;
        private readonly ManualResetEventSlim? _release;
        private long _frame;
        private int _completedRenders;

        public SequenceSource(ManualResetEventSlim? entered = null,
            ManualResetEventSlim? release = null)
        {
            _entered = entered;
            _release = release;
        }

        public AudioOutputFormat Format => new(48000, 1);
        public int CompletedRenders => Volatile.Read(ref _completedRenders);

        public void Render(int frameCount, Span<float> destination)
        {
            _entered?.Set();
            if (_release is not null
                && !_release.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("C5(a) stall gate timed out.");
            for (int i = 0; i < frameCount; i++)
                destination[i] = Interlocked.Increment(ref _frame);
            Interlocked.Increment(ref _completedRenders);
        }
    }
}

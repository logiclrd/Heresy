using System;
using System.Threading;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
[NonParallelizable]
public sealed class FirPreparationSchedulerTests
{
    private const int Rate = 48000;

    [Test]
    public void OffPcmWorkerPreparesLatestRequestAndRejectsSupersededSnapshot()
    {
        FilterChain authored = Chain();
        FirControlPreparationPolicy producer = new(Rate);
        FirPreparationMailbox mailbox = new(authored, Rate);
        using FirPreparationScheduler scheduler = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        int workerId = 0, invocations = 0;
        mailbox.BeforePrepare = () =>
        {
            workerId = Environment.CurrentManagedThreadId;
            if (Interlocked.Increment(ref invocations) == 1)
            {
                entered.Set();
                Assert.That(resume.Wait(TimeSpan.FromSeconds(4)), Is.True);
            }
        };
        Assert.That(producer.TryOffer([Request(2000)]), Is.True);
        Assert.That(mailbox.CapturePending(producer, scheduler), Is.True);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(4)), Is.True);

        // The first calculation is still in flight. Replace its snapshot
        // without any alloc-heavy FIR design on this caller.
        Assert.That(producer.TryOffer([Request(3500)]), Is.True);
        Assert.That(mailbox.CapturePending(producer, scheduler), Is.True);
        Assert.That(mailbox.TakeReady(), Is.Null);
        resume.Set();
        Assert.That(SpinWait.SpinUntil(
            () => scheduler.CompletedPreparationCount >= 1,
            TimeSpan.FromSeconds(4)), Is.True);
        PreparedFirControlBatch? ready = mailbox.TakeReady();
        Assert.That(ready, Is.Not.Null);
        Assert.That(ready!.Matches([Request(3500)]), Is.True);
        Assert.That(ready.Matches([Request(2000)]), Is.False);
        Assert.That(workerId, Is.Not.EqualTo(Environment.CurrentManagedThreadId));
        Assert.That(scheduler.Fault, Is.Null);
    }

    [Test]
    public void RepeatedIdenticalPendingTargetDoesNotRestartInFlightPreparation()
    {
        FirControlPreparationPolicy producer = new(Rate);
        FirPreparationMailbox mailbox = new(Chain(), Rate);
        using FirPreparationScheduler scheduler = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        mailbox.BeforePrepare = () =>
        {
            entered.Set();
            Assert.That(resume.Wait(TimeSpan.FromSeconds(4)), Is.True);
        };

        Assert.That(producer.TryOffer([Request(3000)]), Is.True);
        Assert.That(mailbox.CapturePending(producer, scheduler), Is.True);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(4)), Is.True);
        for (int i = 0; i < 100; i++)
        {
            Assert.That(producer.TryOffer([Request(3000)]), Is.True);
            Assert.That(mailbox.CapturePending(producer, scheduler), Is.False);
        }
        resume.Set();
        Assert.That(SpinWait.SpinUntil(
            () => scheduler.CompletedPreparationCount >= 1,
            TimeSpan.FromSeconds(4)), Is.True);
        Assert.That(mailbox.TakeReady()!.Matches([Request(3000)]), Is.True);
        Assert.That(scheduler.CompletedPreparationCount, Is.EqualTo(1));
    }

    [Test]
    public void SaturatedMultiQueueRefusesExcessAdmissionButRecoversWhenDrained()
    {
        // Hold the *real* preparation worker off-PCM. Exactly 64 queued
        // Multi admissions can wait behind its already-dequeued first
        // request. The next candidate must remain with its caller for a
        // later producer boundary, not be silently discarded or enqueued
        // beyond the cap.
        using FirPreparationScheduler scheduler = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        int visits = 0;
        scheduler.BeforePrepareWorkItem = () =>
        {
            if (Interlocked.Increment(ref visits) != 1) return;
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("C5(b) queue gate timed out");
        };
        CounterWorkItem active = new();
        CounterWorkItem[] queued =
            new CounterWorkItem[FirPreparationScheduler.MaximumQueuedMultiExpansions];
        CounterWorkItem deferred = new();
        try
        {
            Assert.That(scheduler.TryEnqueueMultiExpansion(active), Is.True);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            for (int i = 0; i < queued.Length; i++)
            {
                queued[i] = new CounterWorkItem();
                Assert.That(scheduler.TryEnqueueMultiExpansion(queued[i]),
                    Is.True, $"admission {i} below cap");
            }
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.TryEnqueueMultiExpansion(deferred), Is.False,
                    "the queue cannot grow past its bounded Multi admission cap");
                Assert.That(scheduler.PeakQueuedWorkItems,
                    Is.EqualTo(FirPreparationScheduler.MaximumQueuedMultiExpansions));
                Assert.That(scheduler.CompletedPreparationCount, Is.Zero,
                    "nothing can be committed while the worker is stopped");
            });
        }
        finally { resume.Set(); }

        Assert.That(SpinWait.SpinUntil(
            () => scheduler.CompletedPreparationCount == queued.Length + 1,
            TimeSpan.FromSeconds(10)), Is.True,
            "the admitted work must drain after the gate opens");
        Assert.That(scheduler.TryEnqueueMultiExpansion(deferred), Is.True,
            "a rejected pending request must be retryable after the queue drains");
        Assert.That(SpinWait.SpinUntil(
            () => scheduler.CompletedPreparationCount == queued.Length + 2,
            TimeSpan.FromSeconds(10)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(active.Prepared, Is.EqualTo(1));
            Assert.That(queued, Is.All.Matches<CounterWorkItem>(
                item => item.Prepared == 1));
            Assert.That(deferred.Prepared, Is.EqualTo(1));
            Assert.That(scheduler.PeakQueuedWorkItems,
                Is.EqualTo(FirPreparationScheduler.MaximumQueuedMultiExpansions));
            Assert.That(scheduler.Fault, Is.Null);
        });
    }

    [Test]
    public void ManyFirMailboxesCoalesceRapidSupersessionIntoLatestFrozenSnapshots()
    {
        // Multiple independent Filter invocations compete for one worker.
        // Repeated edits to *each* mailbox must not multiply its queued
        // slots, or make an older prepared kernel visible after adoption.
        const int invocationCount = 12;
        const int revisionsPerInvocation = 25;
        using FirPreparationScheduler scheduler = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        int visits = 0;
        scheduler.BeforePrepareWorkItem = () =>
        {
            if (Interlocked.Increment(ref visits) != 1) return;
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("C5(b) FIR gate timed out");
        };
        FirControlPreparationPolicy[] producers =
            new FirControlPreparationPolicy[invocationCount];
        FirPreparationMailbox[] mailboxes =
            new FirPreparationMailbox[invocationCount];
        double[] latest = new double[invocationCount];
        try
        {
            for (int i = 0; i < invocationCount; i++)
            {
                producers[i] = new FirControlPreparationPolicy(Rate);
                mailboxes[i] = new FirPreparationMailbox(Chain(), Rate);
                Assert.That(producers[i].TryOffer([Request(1700 + i)]), Is.True);
                Assert.That(mailboxes[i].CapturePending(producers[i], scheduler),
                    Is.True);
                if (i == 0)
                    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            }
            for (int revision = 0; revision < revisionsPerInvocation; revision++)
                for (int i = 0; i < invocationCount; i++)
                {
                    double cutoff = 2500 + i * 40 + revision;
                    latest[i] = cutoff;
                    Assert.That(producers[i].TryOffer([Request(cutoff)]), Is.True);
                    Assert.That(mailboxes[i].CapturePending(
                        producers[i], scheduler), Is.True);
                    Assert.That(mailboxes[i].TakeReady(), Is.Null,
                        "superseded kernels cannot be published before worker runs");
                }
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.PeakQueuedWorkItems,
                    Is.LessThanOrEqualTo(invocationCount),
                    "revisions must share their invocation's one queued slot");
                Assert.That(scheduler.CompletedPreparationCount, Is.Zero);
            });
        }
        finally { resume.Set(); }

        Assert.That(SpinWait.SpinUntil(
            () => scheduler.CompletedPreparationCount == invocationCount,
            TimeSpan.FromSeconds(10)), Is.True);
        for (int i = 0; i < invocationCount; i++)
        {
            PreparedFirControlBatch? batch = mailboxes[i].TakeReady();
            Assert.That(batch, Is.Not.Null, $"mailbox {i} latest kernel");
            Assert.That(batch!.Matches([Request(latest[i])]), Is.True,
                $"mailbox {i} must publish its last immutable revision only");
            Assert.That(batch.Matches([Request(1700 + i)]), Is.False,
                "the oldest snapshot cannot reappear after supersession");
            Assert.That(batch.Matches([Request(latest[i] - 1)]), Is.False);
            Assert.That(mailboxes[i].TakeReady(), Is.Null);
        }
        Assert.Multiple(() =>
        {
            Assert.That(scheduler.PeakQueuedWorkItems,
                Is.LessThanOrEqualTo(invocationCount));
            Assert.That(scheduler.CompletedPreparationCount,
                Is.EqualTo(invocationCount));
            Assert.That(scheduler.Fault, Is.Null);
        });
        TestContext.Progress.WriteLine(
            $"C5(b) FIR worker: {invocationCount} queued invocations, " +
            $"{revisionsPerInvocation} later edits per invocation, " +
            $"peak queued={scheduler.PeakQueuedWorkItems}, " +
            $"prepared latest={scheduler.CompletedPreparationCount}");
    }

    private sealed class CounterWorkItem : IRealtimePreparationWorkItem
    {
        private int _prepared;
        internal int Prepared => Volatile.Read(ref _prepared);
        public void Prepare(FirPreparationScheduler scheduler)
        {
            Interlocked.Increment(ref _prepared);
            scheduler.RecordCompleted();
        }
        public void MarkFailed(FirPreparationScheduler scheduler) { }
    }

    [Test]
    public void DisposingHostStopsWorkerAfterActivePreparationWithoutTouchingPcmState()
    {
        FirControlPreparationPolicy producer = new(Rate);
        FirPreparationMailbox mailbox = new(Chain(), Rate);
        FirPreparationScheduler scheduler = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        mailbox.BeforePrepare = () =>
        {
            entered.Set();
            resume.Wait(TimeSpan.FromSeconds(4));
        };
        try
        {
            producer.TryOffer([Request(2300)]);
            mailbox.CapturePending(producer, scheduler);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(4)), Is.True);
            // Replacing/disposing an invocation is safe because the worker
            // owns the frozen Filter definitions rather than a live voice.
            resume.Set();
        }
        finally
        {
            resume.Set();
            scheduler.Dispose();
        }
        Assert.That(scheduler.Fault, Is.Null);
    }

    [Test]
    public void BufferedAudioHostRunsAsyncFIRControlHandoffBetweenBlocks()
    {
        RealtimeProbe source = new();
        using BufferedAudioOutputSource buffer =
            new(source, capacityFrames:64, blockFrames:16);
        float[] pcm = new float[16];
        bool delivered = SpinWait.SpinUntil(() =>
        {
            buffer.Render(16, pcm);
            Thread.Sleep(1);
            return source.Adopted > 0;
        }, TimeSpan.FromSeconds(5));
        Assert.That(delivered, Is.True, "the PCM host must later adopt a worker-produced plan");
        Assert.That(source.Rendered, Is.GreaterThan(0));
        Assert.That(source.Queued, Is.GreaterThan(0));
        Assert.That(buffer.RenderingFault, Is.Null);
    }

    private sealed class RealtimeProbe : IAudioOutputSource, IFirPreparationAudioSource
    {
        private readonly FirControlPreparationPolicy _policy = new(Rate);
        private readonly FirPreparationMailbox _mailbox = new(Chain(), Rate);
        private int _submitted;
        private int _queued;
        private int _rendered;
        private int _adopted;
        public AudioOutputFormat Format => new(Rate, 1);
        public int Queued => Volatile.Read(ref _queued);
        public int Rendered => Volatile.Read(ref _rendered);
        public int Adopted => Volatile.Read(ref _adopted);

        public void Render(int frameCount, Span<float> destination)
        {
            Interlocked.Increment(ref _rendered);
            destination.Fill(0.25f);
            if (Interlocked.Exchange(ref _submitted, 1) == 0)
                _policy.TryOffer([Request(3500)]);
        }

        public void AdoptPreparedFirControls()
        {
            if (_mailbox.TakeReady() is not null)
                Interlocked.Increment(ref _adopted);
        }

        public void QueuePendingFirControls(FirPreparationScheduler scheduler)
        {
            if (_mailbox.CapturePending(_policy, scheduler))
                Interlocked.Increment(ref _queued);
        }
    }

    private static FirControlRequest Request(double cutoff)
        => new(0, 1, FilterKind.FirLowPass, cutoff, 0, 0,
            31, 200, "Linear");

    private static FilterChain Chain()
    {
        FilterDefinition definition = new((ObjectId)28001U, "FIR", [
            new FilterStageDefinition(1, "low", FilterKind.FirLowPass),
        ]);
        FilterInstance use = new(definition);
        use.SetStageParameters(1, [
            new("CutoffHz", FilterParameterValue.FromNumber(1200)),
            new("TapCount", FilterParameterValue.FromNumber(31)),
            new("TransitionWidthHz", FilterParameterValue.FromNumber(200)),
        ]);
        return new FilterChain([use]);
    }
}

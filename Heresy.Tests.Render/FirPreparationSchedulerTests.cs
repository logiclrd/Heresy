using System;
using System.Threading;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
[NonParallelizable]
public sealed class FirPreparationSchedulerTests
{
    private const int Rate = 48000;

    [Test]
    [Timeout(10000)]
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
    [Timeout(10000)]
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
    [Timeout(10000)]
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

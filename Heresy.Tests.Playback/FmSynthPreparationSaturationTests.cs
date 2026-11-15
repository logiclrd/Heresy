using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Filters;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// C5(b): actual running-note Multi preparation cancellation with a saturated
/// off-PCM queue. No hardware/throughput claim: worker concurrency is gated.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class FmSynthPreparationSaturationTests
{
    [Test]
    public void RetiredHeldNoteCancelsQueuedExpansionBehindSaturatedMultiWorker()
    {
        const int sampleRate = 48000;
        SongDocument document = new();
        ObjectId synthId = document.AllocateObjectId();
        static FmSynthGraph Graph(int voices) => new([
            new FmConstantNode(0, 0.015625),
            new FmMultiNode(1, 0, voiceCount: voices, detune: 0,
                randomizePhase: false, gainCompensation: false)], 1);
        FmSynthDefinition synth = new(synthId, "C5(b) queued Multi", Graph(2));
        document.Add(synth);
        PlaybackRequestAudioSourceFactory factory = new(
            new RenderConfiguration(sampleRate, [
                new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0),
            ]));
        IAudioOutputSource output = factory.Create(
            AdHocPlaybackRequest.Create(document,
                new NoteScheduleBuilder().Freeze(),
                reserveSynthMultiHeadroom: true));
        using FirPreparationScheduler scheduler = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim resume = new(false);
        int gates = 0;
        scheduler.BeforePrepareWorkItem = () =>
        {
            if (Interlocked.Increment(ref gates) != 1) return;
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("C5(b) Multi gate timed out");
        };
        try
        {
            ILiveAudioOutputSource live = (ILiveAudioOutputSource)output;
            IFirPreparationAudioSource preparing = (IFirPreparationAudioSource)output;
            live.EnqueueLiveEvent(ChannelTarget.Virtual(242),
                [new StartNoteCommand(synthId)]);
            float[] first = new float[80];
            output.Render(first.Length, first);
            for (int i = 0; i < first.Length; i++)
                Assert.That(first[i], Is.EqualTo(0.03125f).Within(1e-6f));

            // Beyond the four live-reserved voices: off-PCM construction
            // must be required and must not occur inside Render.
            synth.Graph = Graph(9);
            document.MarkChanged(affectsAudio: true);
            ((ILiveSynthSourceRevisionOutput)output).EnqueueLiveSynthParameters(
                synthId, factory.PrepareLiveSynthSource(
                    SongDocumentSnapshot.Create(document), synthId));
            float[] pending = new float[8];
            output.Render(pending.Length, pending);
            Assert.That(pending, Is.All.EqualTo(0.03125f).Within(1e-6f));
            preparing.QueuePendingFirControls(scheduler);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "the real Multi worker request must be queued and blocked");

            // Fill all 64 remaining slots while the real request is still
            // active, then require a 65th attempt to be rejected.
            CountingWorkItem[] filler =
                new CountingWorkItem[FirPreparationScheduler.MaximumQueuedMultiExpansions];
            for (int i = 0; i < filler.Length; i++)
            {
                filler[i] = new CountingWorkItem();
                Assert.That(scheduler.TryEnqueueMultiExpansion(filler[i]),
                    Is.True);
            }
            CountingWorkItem refused = new();
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.TryEnqueueMultiExpansion(refused), Is.False);
                Assert.That(scheduler.PeakQueuedWorkItems,
                    Is.EqualTo(FirPreparationScheduler.MaximumQueuedMultiExpansions));
                Assert.That(scheduler.PreparedMultiConstituentCount, Is.Zero);
            });

            // Retire the note before the real worker can allocate any
            // children; the queued version must be discarded. New Notes
            // still receive the authored nine-voice graph.
            live.EnqueueLiveEvent(ChannelTarget.Virtual(242),
                [new NoteCutCommand()]);
            output.Render(8, new float[8]);
            resume.Set();
            Assert.That(SpinWait.SpinUntil(
                () => scheduler.CancelledMultiExpansionCount >= 1
                    && scheduler.CompletedPreparationCount == filler.Length,
                TimeSpan.FromSeconds(10)), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.PreparedMultiConstituentCount, Is.Zero,
                    "retired Multi must not keep costly prepared children");
                Assert.That(scheduler.CancelledMultiExpansionCount,
                    Is.EqualTo(1));
                Assert.That(scheduler.Fault, Is.Null);
            });
            Assert.That(scheduler.TryEnqueueMultiExpansion(refused), Is.True,
                "rejected work is retryable once cancelled/queued work drains");
            Assert.That(SpinWait.SpinUntil(
                () => scheduler.CompletedPreparationCount == filler.Length + 1,
                TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(refused.Completed, Is.EqualTo(1));

            output.Render(512, new float[512]); // retire NoteCut's declick tail
            live.EnqueueLiveEvent(ChannelTarget.Virtual(243),
                [new StartNoteCommand(synthId)]);
            float[] fresh = new float[64];
            output.Render(fresh.Length, fresh);
            Assert.Multiple(() =>
            {
                Assert.That(fresh, Is.All.InRange(0.13f, 0.15f),
                    "fresh note must use the nine-constituent authored graph");
                Assert.That(scheduler.PreparedMultiConstituentCount, Is.Zero);
                Assert.That(scheduler.Fault, Is.Null);
            });
            TestContext.Progress.WriteLine(
                $"C5(b) queued Multi: peak={scheduler.PeakQueuedWorkItems}, " +
                $"cancelled={scheduler.CancelledMultiExpansionCount}, " +
                $"prepared children={scheduler.PreparedMultiConstituentCount}, " +
                $"completed fillers={scheduler.CompletedPreparationCount}");
        }
        finally
        {
            resume.Set();
            (output as IDisposable)?.Dispose();
        }
    }

    private sealed class CountingWorkItem : IRealtimePreparationWorkItem
    {
        private int _completed;
        public int Completed => Volatile.Read(ref _completed);
        public void Prepare(FirPreparationScheduler scheduler)
        {
            Interlocked.Increment(ref _completed);
            scheduler.RecordCompleted();
        }
        public void MarkFailed(FirPreparationScheduler scheduler) { }
    }
}

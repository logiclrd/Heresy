using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

using Heresy.Core.Filters;
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
/// C5(d): mixed-owner/nested Multi + Filter latest-wins, scheduler saturation,
/// release interruption, and sticky structural generations. The PCM is the
/// actual live factory source; worker stalls are deterministic, not timing
/// assertions about a shared CI host or an audio device.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class FmSynthConcurrentRevisionStressTests
{
    private const float Gain = 0.015625f;

    [Test]
    public void SaturatedNestedMultiRevisionsRetireInterruptedOwnersAndKeepStickyOlderAudioCorrect()
    {
        SongDocument song = new();
        ObjectId filterId = song.AllocateObjectId();
        FilterDefinition filter = new(filterId, "Nested three-stage", [
            new FilterStageDefinition(1, "first", FilterKind.OnePole,
                [new("CutoffHz", FilterParameterValue.FromNumber(850))]),
            new FilterStageDefinition(2, "second", FilterKind.OnePole,
                [new("CutoffHz", FilterParameterValue.FromNumber(1100))]),
            new FilterStageDefinition(3, "third", FilterKind.OnePole,
                [new("CutoffHz", FilterParameterValue.FromNumber(1350))]),
        ]);
        song.Add(filter);
        FmSynthGraph Graph(int innerCount, bool additionalNode = false,
            double gain = Gain)
        {
            List<FmSynthNode> nodes = [
                new FmConstantNode(0, gain),
                new FmFilterNode(1, 0, new FilterInstance(filter)),
                new FmMultiNode(2, 1, voiceCount: innerCount, detune: 0,
                    randomizePhase: false, gainCompensation: false),
                new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                    randomizePhase: false, gainCompensation: false),
            ];
            if (additionalNode)
                nodes.Add(new FmConstantNode(4, 0)); // structural generation
            return new FmSynthGraph(nodes, 3);
        }

        ObjectId synthId = song.AllocateObjectId();
        FmSynthDefinition synth = new(synthId, "C5(d) nested Filter/Multi",
            Graph(2));
        song.Add(synth);
        PlaybackRequestAudioSourceFactory factory = new(
            new RenderConfiguration(48000, [
                new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0),
            ]));
        AdHocPlaybackRequest request = AdHocPlaybackRequest.Create(song,
            new NoteScheduleBuilder().Freeze(), reserveSynthMultiHeadroom: true);
        IAudioOutputSource output = factory.Create(request);
        using FirPreparationScheduler worker = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        int gates = 0;
        worker.BeforePrepareWorkItem = () =>
        {
            if (Interlocked.Increment(ref gates) != 1)
                return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("C5(d) nested Multi worker gate");
        };
        try
        {
            ILiveAudioOutputSource live = (ILiveAudioOutputSource)output;
            ILiveSynthSourceRevisionOutput revisions =
                (ILiveSynthSourceRevisionOutput)output;
            IFirPreparationAudioSource preparing =
                (IFirPreparationAudioSource)output;
            void Publish(int inner, bool structural = false,
                double gain = Gain)
            {
                synth.Graph = Graph(inner, structural, gain);
                song.MarkChanged(affectsAudio: true);
                revisions.EnqueueLiveSynthParameters(synthId,
                    factory.PrepareLiveSynthSource(
                        SongDocumentSnapshot.Create(song), synthId));
            }
            void AssertOlder(int count, string reason)
            {
                Assert.That(factory.TryGetOlderAudioVoiceCount(request,
                    out int actual), Is.True);
                Assert.That(actual, Is.EqualTo(count), reason);
            }
            void RenderSettled(float level, string reason)
            {
                // Full 100-ms smoothstep plus generous Filter/declick tail.
                float[] pcm = new float[5500];
                output.Render(pcm.Length, pcm);
                for (int i = pcm.Length - 128; i < pcm.Length; i++)
                    Assert.That(pcm[i], Is.EqualTo(level).Within(0.0015f),
                        reason + " at output frame " + i);
                AssertOlder(0, "compatible or pending revisions cannot report older audio: " + reason);
            }

            // Three independent original physical histories, each owning
            // two outer x two inner Multi children and three-stage Filters.
            live.EnqueueLiveEvent(ChannelTarget.Virtual(401),
                [new StartNoteCommand(synthId)]);
            live.EnqueueLiveEvent(ChannelTarget.Virtual(402),
                [new StartNoteCommand(synthId)]);
            live.EnqueueLiveEvent(ChannelTarget.Virtual(403),
                [new StartNoteCommand(synthId)]);
            RenderSettled(12 * Gain, "three original nested notes");

            // Two outer children in each of three notes each need an
            // off-PCM expansion past the reserved inner +4 headroom.
            Publish(9);
            output.Render(1, new float[1]);
            preparing.QueuePendingFirControls(worker);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "the real nested expansion must park on the worker");
            Assert.That(worker.CompletedMultiExpansionCount, Is.Zero);
            AssertOlder(0, "a queued expansion is not itself older audio");

            // Saturate the remaining waiting slots *behind* a real nested
            // Multi job. No scheduling or busy waiting in the PCM owner.
            List<CountingWorkItem> fillers = [];
            for (int i = 0; i < FirPreparationScheduler.MaximumQueuedMultiExpansions; i++)
            {
                CountingWorkItem item = new();
                if (!worker.TryEnqueueMultiExpansion(item))
                    break;
                fillers.Add(item);
            }
            CountingWorkItem refused = new();
            Assert.That(worker.TryEnqueueMultiExpansion(refused), Is.False,
                "the real nested jobs and lightweight fillers must occupy all 64 waiting slots");
            Assert.That(worker.PeakQueuedWorkItems,
                Is.EqualTo(FirPreparationScheduler.MaximumQueuedMultiExpansions));
            Assert.That(fillers.Count, Is.LessThan(64),
                "multiple real nested requests are waiting ahead of filler traffic");

            // Supersede the blocked 9-child work, then interrupt two
            // independent owners before any stale worker result can land.
            Publish(12);
            output.Render(16, new float[16]);
            preparing.QueuePendingFirControls(worker);
            live.EnqueueLiveEvent(ChannelTarget.Virtual(402),
                [new NoteCutCommand()]);
            live.EnqueueLiveEvent(ChannelTarget.Virtual(403),
                [new NoteOffCommand()]); // no sustain -> definite cut
            output.Render(512, new float[512]);
            AssertOlder(0, "the remaining original owner has not missed an adopted generation");

            // Supersede yet again with a count within the original four
            // reserved audition slots. Must not wait for the saturated
            // stale expansion queue to use the valid latest authoring.
            Publish(4);
            RenderSettled(8 * Gain,
                "one surviving original owner after nested 9->12->4");
            release.Set();
            Assert.That(SpinWait.SpinUntil(
                () => worker.CompletedPreparationCount >= fillers.Count
                    && worker.CancelledMultiExpansionCount >= 1
                    || worker.Fault is not null,
                TimeSpan.FromSeconds(10)), Is.True,
                "queued obsolete Multi work must drain or cancel");
            Assert.That(worker.Fault, Is.Null);
            foreach (CountingWorkItem item in fillers)
                Assert.That(item.Completed, Is.EqualTo(1),
                    "independent queued work must not be lost");
            output.Render(1000, new float[1000]);
            preparing.AdoptPreparedFirControls();
            RenderSettled(8 * Gain,
                "stale worker output must not overwrite latest reduced graph");
            Assert.That(worker.CancelledMultiExpansionCount,
                Is.GreaterThanOrEqualTo(1));

            live.EnqueueLiveEvent(ChannelTarget.Virtual(404),
                [new StartNoteCommand(synthId)]);
            RenderSettled(16 * Gain,
                "new owner receives current four inner voices, original still sounding");

            // Structural edge/node-list change is a deliberate generation
            // barrier. The two still-sounding old notes must be counted
            // as older, while a new note uses the new graph.
            Publish(4, structural: true);
            output.Render(64, new float[64]);
            AssertOlder(2, "two actual old-generation sounding notes remain");
            live.EnqueueLiveEvent(ChannelTarget.Virtual(405),
                [new StartNoteCommand(synthId)]);
            float[] structuralPcm = new float[5500];
            output.Render(structuralPcm.Length, structuralPcm);
            Assert.That(structuralPcm[^1], Is.EqualTo(24 * Gain).Within(0.0015f));
            AssertOlder(2, "newly started note must not be classified older");

            // A later compatible edit to the new structural generation
            // must not erase missed-generation status for the other two,
            // nor retune those voices behind their original graph.
            Publish(4, structural: true, gain: 0.01);
            float[] retuned = new float[5500];
            output.Render(retuned.Length, retuned);
            Assert.That(retuned[^1], Is.EqualTo(16 * Gain + 8 * 0.01f)
                .Within(0.0015f));
            AssertOlder(2, "missed structural generations remain sticky");

            live.EnqueueLiveEvent(ChannelTarget.Virtual(401),
                [new NoteCutCommand()]);
            output.Render(512, new float[512]);
            AssertOlder(1, "cut retires one of two old-generation owners");
            live.EnqueueLiveEvent(ChannelTarget.Virtual(404),
                [new NoteOffCommand()]);
            output.Render(512, new float[512]);
            AssertOlder(0, "definite NoteOff retires remaining sustainless old owner");
            float[] newest = new float[1024];
            output.Render(newest.Length, newest);
            Assert.That(newest[^1], Is.EqualTo(0.08f).Within(0.0015f),
                "newest voice survives independent old-owner interruption");
            live.EnqueueLiveEvent(ChannelTarget.Virtual(405),
                [new NoteCutCommand()]);
            float[] silence = new float[1024];
            output.Render(silence.Length, silence);
            Assert.That(silence[^1], Is.EqualTo(0).Within(0.0015f));

            TestContext.Progress.WriteLine(
                $"C5(d) saturated nested Multi+Filter: initial owners=3, " +
                $"inner voice path=2->9->12->4, outer=2, " +
                $"worker peak waiting={worker.PeakQueuedWorkItems}, " +
                $"cancelled Multi={worker.CancelledMultiExpansionCount}, " +
                $"prepared constituents={worker.PreparedMultiConstituentCount}, " +
                $"completed fillers={worker.CompletedPreparationCount}, " +
                $"older-audio counts 0->2->1->0, no worker fault");
        }
        finally
        {
            release.Set();
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

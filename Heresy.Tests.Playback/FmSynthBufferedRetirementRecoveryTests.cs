using System;
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
/// C5(c): one original held note must recover after a real off-PCM 3->17->3
/// Multi revision while the buffered PCM producer is deliberately stalled.
/// Producer elapsed times are observations, never host-independent budgets.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class FmSynthBufferedRetirementRecoveryTests
{
    private const int SampleRate = 48000;
    private const int BlockFrames = 64;
    private const float ConstituentOutput = 0.015625f;

    [Test]
    public void HeldFilteredMultiRecoversProducerAfterSeventeenVoicesAndForcedUnderrun()
    {
        SongDocument document = new();
        ObjectId filterId = document.AllocateObjectId();
        // Actual child filter histories, rather than a cheap zero-gain or
        // Constant-only approximation of the high-voice DSP workload.
        FilterDefinition filter = new(filterId, "C5(c) three-stage lowpass", [
            new FilterStageDefinition(1, "first", FilterKind.OnePole,
                [new("CutoffHz", FilterParameterValue.FromNumber(850))]),
            new FilterStageDefinition(2, "second", FilterKind.OnePole,
                [new("CutoffHz", FilterParameterValue.FromNumber(1100))]),
            new FilterStageDefinition(3, "third", FilterKind.OnePole,
                [new("CutoffHz", FilterParameterValue.FromNumber(1350))]),
        ]);
        document.Add(filter);
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(0, ConstituentOutput),
            new FmFilterNode(1, 0, new FilterInstance(filter)),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        ObjectId synthId = document.AllocateObjectId();
        FmSynthDefinition synth = new(synthId, "C5(c) held filtered Multi",
            Graph(3));
        document.Add(synth);

        PlaybackRequestAudioSourceFactory factory = new(
            new RenderConfiguration(SampleRate, [
                new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0),
            ]));
        AdHocPlaybackRequest request = AdHocPlaybackRequest.Create(
            document, new NoteScheduleBuilder().Freeze(),
            reserveSynthMultiHeadroom: true);
        IAudioOutputSource raw = factory.Create(request);
        try
        {
            using GatedRenderSource gated = new(raw);
            using BufferedAudioOutputSource buffered = new(gated,
                capacityFrames: BlockFrames * 4,
                blockFrames: BlockFrames, profileProducerRender: true);
            try
            {
                float[] block = new float[BlockFrames];
                buffered.EnqueueLiveEvent(ChannelTarget.Virtual(310),
                    [new StartNoteCommand(synthId)]);
                AwaitStableLevel(buffered, block, 3 * ConstituentOutput,
                    "initial three-constituent held note");
                PhaseProfile before = MeasureStableBlocks(buffered, block,
                    3 * ConstituentOutput);

                // Exceeds the four reserved audition children: at least
                // ten additional invocations must be constructed off PCM.
                PublishCount(17);
                AwaitStableLevel(buffered, block, 17 * ConstituentOutput,
                    "original note after worker-prepared 3->17 adoption");
                Assert.That(buffered.CompletedMultiExpansions,
                    Is.GreaterThanOrEqualTo(1));
                Assert.That(buffered.PreparedMultiConstituents,
                    Is.GreaterThanOrEqualTo(10));
                PhaseProfile high = MeasureStableBlocks(buffered, block,
                    17 * ConstituentOutput);

                // Park precisely the *real* source.Render call, after all
                // seventeen Filter/Multi histories have been sounding.
                // The ring can drain, but no musical time may be consumed
                // by device callbacks while this producer is blocked.
                gated.GateNextRender();
                buffered.Render(BlockFrames, block); // free one ring slot
                Assert.That(gated.WaitUntilBlocked(TimeSpan.FromSeconds(10)),
                    Is.True, "the live producer must reach the stress gate");
                long underrunsBefore = buffered.UnderrunCount;
                buffered.Render(buffered.CapacityFrames,
                    new float[buffered.CapacityFrames]);
                float[] emptyCallback = new float[BlockFrames];
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                buffered.Render(BlockFrames, emptyCallback);
                long callbackBytes = GC.GetAllocatedBytesForCurrentThread()
                    - allocatedBefore;
                Assert.Multiple(() =>
                {
                    Assert.That(emptyCallback, Is.All.Zero,
                        "a stalled producer cannot fill a drained ring");
                    Assert.That(buffered.UnderrunCount,
                        Is.GreaterThan(underrunsBefore));
                    Assert.That(callbackBytes, Is.Zero,
                        "the device callback must remain allocation-free");
                });
                // A deterministic over-budget interval, rather than a
                // brittle assertion about the CI runner's natural speed.
                Thread.Sleep(25);
                gated.Release();
                AwaitStableLevel(buffered, block, 17 * ConstituentOutput,
                    "original note resumes after forced producer underrun");
                Assert.That(buffered.ProducerBlocksOverBudget,
                    Is.GreaterThanOrEqualTo(1));
                Assert.That(buffered.RenderingFault, Is.Null);

                // Shrink *that same still-held note*, without NoteCut,
                // NoteOff, or a replacement StartNote. Let the full 100-ms
                // smoothstep retire the fourteen costly extra children.
                PublishCount(3);
                AwaitStableLevel(buffered, block, 3 * ConstituentOutput,
                    "same held note recovered after 17->3");
                PhaseProfile recovered = MeasureStableBlocks(buffered, block,
                    3 * ConstituentOutput);
                Assert.That(buffered.RenderingFault, Is.Null);
                Assert.That(buffered.BufferedFrames, Is.InRange(0,
                    buffered.CapacityFrames));
                Assert.That(factory.TryGetOlderAudioVoiceCount(request,
                    out int older), Is.True);
                Assert.That(older, Is.Zero,
                    "compatible worker-prepared edits must not mark the original older");

                // A separate owner starts only AFTER the shrink. Its
                // authored three constituents add to the still-sounding
                // original three; it must not inherit the old 17-child graph.
                buffered.EnqueueLiveEvent(ChannelTarget.Virtual(311),
                    [new StartNoteCommand(synthId)]);
                AwaitStableLevel(buffered, block, 6 * ConstituentOutput,
                    "fresh current-three note plus original current-three note");
                buffered.EnqueueLiveEvent(ChannelTarget.Virtual(311),
                    [new NoteCutCommand()]);
                AwaitStableLevel(buffered, block, 3 * ConstituentOutput,
                    "original three-voice note survives fresh-owner cut");

                Assert.Multiple(() =>
                {
                    Assert.That(buffered.CompletedMultiExpansions,
                        Is.GreaterThanOrEqualTo(1));
                    Assert.That(buffered.RenderingFault, Is.Null);
                    Assert.That(recovered.Blocks, Is.GreaterThan(0));
                    Assert.That(recovered.Frames, Is.GreaterThan(0));
                    Assert.That(buffered.ProducerRenderedFrames,
                        Is.GreaterThan(recovered.Frames));
                });
                TestContext.Progress.WriteLine(
                    $"C5(c): held 3->17->3, 3-stage OnePole x constituents, " +
                    $"48kHz mono, ring={buffered.CapacityFrames}, quantum={BlockFrames}; " +
                    $"three-before {before}, seventeen {high}, three-recovered {recovered}; " +
                    $"underruns={buffered.UnderrunCount}, " +
                    $"over-budget blocks={buffered.ProducerBlocksOverBudget}, " +
                    $"peak producer ms={buffered.PeakProducerRenderBlockTime.TotalMilliseconds:F3}, " +
                    $"worker completed={buffered.CompletedMultiExpansions}, " +
                    $"worker prepared children={buffered.PreparedMultiConstituents}, " +
                    $"worker cancelled={buffered.CancelledMultiExpansions}, " +
                    $"worker peak queue={buffered.PeakPreparationQueueDepth}, " +
                    $"worker allocated bytes={buffered.MultiPreparationAllocatedBytes}, " +
                    $"worker elapsed ms={buffered.MultiPreparationElapsedTime.TotalMilliseconds:F3}, " +
                    $"callback allocated bytes={callbackBytes}");
            }
            finally
            {
                gated.Release(); // never dispose a producer with its gate shut
            }

            void PublishCount(int count)
            {
                synth.Graph = Graph(count);
                document.MarkChanged(affectsAudio: true);
                ((ILiveSynthSourceRevisionOutput)raw).EnqueueLiveSynthParameters(
                    synthId, factory.PrepareLiveSynthSource(
                        SongDocumentSnapshot.Create(document), synthId));
            }
        }
        finally
        {
            (raw as IDisposable)?.Dispose();
        }
    }

    private static void AwaitStableLevel(BufferedAudioOutputSource buffered,
        float[] block, float level, string phase)
    {
        long started = Stopwatch.GetTimestamp();
        int blocks = 0;
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
        {
            Assert.That(SpinWait.SpinUntil(
                () => buffered.BufferedFrames >= block.Length
                    || buffered.RenderingFault is not null,
                TimeSpan.FromSeconds(5)), Is.True,
                $"producer did not refill the ring during {phase}");
            Assert.That(buffered.RenderingFault, Is.Null, phase);
            buffered.Render(block.Length, block);
            blocks++;
            bool stable = true;
            foreach (float value in block)
            {
                if (!float.IsFinite(value) || Math.Abs(value - level) > 0.0015f)
                {
                    stable = false;
                    break;
                }
            }
            if (stable)
                return;
        }
        Assert.Fail($"C5(c) did not reach {phase} after {blocks} output blocks; " +
            $"target={level}, last={block[^1]}, " +
            $"worker completed={buffered.CompletedMultiExpansions}, " +
            $"render fault={buffered.RenderingFault}");
    }

    private static PhaseProfile MeasureStableBlocks(
        BufferedAudioOutputSource buffered, float[] block, float level)
    {
        long count = buffered.ProducerRenderBlockCount;
        long frames = buffered.ProducerRenderedFrames;
        TimeSpan elapsed = buffered.ProducerRenderElapsedTime;
        long overBudget = buffered.ProducerBlocksOverBudget;
        for (int i = 0; i < 16; i++)
        {
            Assert.That(SpinWait.SpinUntil(
                () => buffered.BufferedFrames >= block.Length
                    || buffered.RenderingFault is not null,
                TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(buffered.RenderingFault, Is.Null);
            buffered.Render(block.Length, block);
            foreach (float sample in block)
                Assert.That(sample, Is.EqualTo(level).Within(0.0015f),
                    "the measured interval must actually be settled at its requested voice count");
        }
        return new PhaseProfile(
            buffered.ProducerRenderBlockCount - count,
            buffered.ProducerRenderedFrames - frames,
            buffered.ProducerRenderElapsedTime - elapsed,
            buffered.ProducerBlocksOverBudget - overBudget);
    }

    private readonly record struct PhaseProfile(
        long Blocks, long Frames, TimeSpan Elapsed, long OverBudget)
    {
        public override string ToString() =>
            $"blocks={Blocks}, frames={Frames}, " +
            $"us/frame={(Frames == 0 ? 0 : Elapsed.TotalMilliseconds * 1000 / Frames):F3}, " +
            $"over-budget={OverBudget}";
    }

    private sealed class GatedRenderSource(IAudioOutputSource raw) :
        ILiveAudioOutputSource, IFirPreparationAudioSource, IDisposable
    {
        private readonly ManualResetEventSlim _blocked = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _gateNext;
        public AudioOutputFormat Format => raw.Format;

        public void EnqueueLiveEvent(ChannelTarget target,
            System.Collections.Generic.IReadOnlyList<NoteCommand> commands) =>
            ((ILiveAudioOutputSource)raw).EnqueueLiveEvent(target, commands);

        public void GateNextRender()
        {
            _blocked.Reset();
            _release.Reset();
            Interlocked.Exchange(ref _gateNext, 1);
        }

        public bool WaitUntilBlocked(TimeSpan timeout) => _blocked.Wait(timeout);
        public void Release() => _release.Set();

        public void Render(int frames, Span<float> destination)
        {
            if (Interlocked.Exchange(ref _gateNext, 0) == 1)
            {
                _blocked.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("C5(c) producer stress gate timed out");
            }
            raw.Render(frames, destination);
        }

        public void AdoptPreparedFirControls() =>
            ((IFirPreparationAudioSource)raw).AdoptPreparedFirControls();
        public void QueuePendingFirControls(FirPreparationScheduler worker) =>
            ((IFirPreparationAudioSource)raw).QueuePendingFirControls(worker);

        public void Dispose()
        {
            _release.Set();
            _blocked.Dispose();
            _release.Dispose();
        }
    }
}

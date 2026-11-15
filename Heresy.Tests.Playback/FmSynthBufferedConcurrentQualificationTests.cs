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
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

/// <summary>
/// C5(d): actual dedicated buffered PCM producer with multiple live owners,
/// nested filtered Multi children, rapid frozen revisions and interruptions.
/// Steady PCM, current generations and callback allocation are correctness;
/// elapsed-time/underrun totals are diagnostic, not host-dependent gates.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class FmSynthBufferedConcurrentQualificationTests
{
    private const float Gain = 0.00390625f;

    [Test]
    public void ConcurrentFilteredNestedMultiLatestWinsAndOriginalNoteSurvivesInterruptions()
    {
        SongDocument song = new();
        ObjectId filterId = song.AllocateObjectId();
        FilterDefinition filter = new(filterId, "C5(d) buffered IIR", [
            new FilterStageDefinition(1, "low", FilterKind.OnePole,
                [new("CutoffHz", FilterParameterValue.FromNumber(950))]),
            new FilterStageDefinition(2, "second", FilterKind.OnePole,
                [new("CutoffHz", FilterParameterValue.FromNumber(1200))]),
        ]);
        song.Add(filter);
        FmSynthGraph Graph(int inner) => new([
            new FmConstantNode(0, Gain),
            new FmFilterNode(1, 0, new FilterInstance(filter)),
            new FmMultiNode(2, 1, voiceCount: inner, detune: 0,
                randomizePhase: false, gainCompensation: false),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 3);
        ObjectId synthId = song.AllocateObjectId();
        FmSynthDefinition synth = new(synthId, "C5(d) concurrent buffered",
            Graph(2));
        song.Add(synth);
        PlaybackRequestAudioSourceFactory factory = new(
            new RenderConfiguration(48000, [
                new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0),
            ]));
        AdHocPlaybackRequest request = AdHocPlaybackRequest.Create(song,
            new NoteScheduleBuilder().Freeze(), reserveSynthMultiHeadroom: true);
        IAudioOutputSource raw = factory.Create(request);
        using BufferedAudioOutputSource buffered = new(raw,
            capacityFrames: 512, blockFrames: 64, profileProducerRender: true);
        try
        {
            void Publish(int inner)
            {
                synth.Graph = Graph(inner);
                song.MarkChanged(affectsAudio: true);
                ((ILiveSynthSourceRevisionOutput)raw).EnqueueLiveSynthParameters(
                    synthId, factory.PrepareLiveSynthSource(
                        SongDocumentSnapshot.Create(song), synthId));
            }
            float[] pcm = new float[64];
            void AwaitLevel(float expected, string stage)
            {
                long started = Stopwatch.GetTimestamp();
                long checkedFrames = 0;
                while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
                {
                    Assert.That(SpinWait.SpinUntil(
                        () => buffered.BufferedFrames >= pcm.Length
                            || buffered.RenderingFault is not null,
                        TimeSpan.FromSeconds(5)), Is.True, stage + ": producer stalled");
                    Assert.That(buffered.RenderingFault, Is.Null, stage);
                    buffered.Render(pcm.Length, pcm);
                    checkedFrames += pcm.Length;
                    bool matched = true;
                    foreach (float sample in pcm)
                    {
                        if (!float.IsFinite(sample)
                            || Math.Abs(sample - expected) > 0.0015f)
                        {
                            matched = false;
                            break;
                        }
                    }
                    if (!matched) continue;
                    Assert.That(factory.TryGetOlderAudioVoiceCount(
                        request, out int older), Is.True);
                    Assert.That(older, Is.Zero,
                        "compatible snapshots must not report false older audio at " + stage);
                    TestContext.Progress.WriteLine(
                        $"C5(d) buffered: {stage}, level={expected:F6}, " +
                        $"observed frames={checkedFrames}, " +
                        $"producer blocks={buffered.ProducerRenderBlockCount}, " +
                        $"worker completions={buffered.CompletedMultiExpansions}");
                    return;
                }
                Assert.Fail($"C5(d) buffered failed to settle: {stage}; " +
                    $"expected={expected}, last={pcm[^1]}, " +
                    $"worker completed={buffered.CompletedMultiExpansions}, " +
                    $"fault={buffered.RenderingFault}");
            }

            // Three distinct currently sounding owners, each with an
            // original 2 inner x 2 outer Filter-backed constituent history.
            for (uint owner = 501; owner <= 503; owner++)
                buffered.EnqueueLiveEvent(ChannelTarget.Virtual(owner),
                    [new StartNoteCommand(synthId)]);
            AwaitLevel(3 * 2 * 2 * Gain, "three initial owners");

            // Propose two genuine off-PCM nested growth versions in rapid
            // succession. Worker may prepare/cancel either intermediate;
            // a completed 12 must reach all three actual held owners.
            Publish(10);
            Publish(12);
            AwaitLevel(3 * 12 * 2 * Gain, "latest nested 12 for all three owners");
            Assert.That(buffered.CompletedMultiExpansions,
                Is.GreaterThanOrEqualTo(6),
                "both outer child histories in each of three owners need worker expansion");
            Assert.That(buffered.PreparedMultiConstituents,
                Is.GreaterThan(0));
            Assert.That(buffered.RenderingFault, Is.Null);

            // Different commands on two owners must leave the third
            // unmodified, with its genuine original nested child histories.
            buffered.EnqueueLiveEvent(ChannelTarget.Virtual(501),
                [new NoteCutCommand()]);
            buffered.EnqueueLiveEvent(ChannelTarget.Virtual(502),
                [new NoteOffCommand()]); // no sustain -> definite cut
            AwaitLevel(12 * 2 * Gain, "one surviving original owner");

            // Shrink that *same held* voice after all expanded children
            // have been contributing; it should keep output and history,
            // not need a replacement Note On or preserve invisible work.
            Publish(3);
            AwaitLevel(3 * 2 * Gain, "same surviving owner after nested shrink");
            buffered.EnqueueLiveEvent(ChannelTarget.Virtual(504),
                [new StartNoteCommand(synthId)]);
            AwaitLevel(2 * 3 * 2 * Gain,
                "fresh three-inner owner plus continuous original three");
            buffered.EnqueueLiveEvent(ChannelTarget.Virtual(504),
                [new NoteCutCommand()]);
            AwaitLevel(3 * 2 * Gain, "original survives independent new-note cut");

            // Warm buffered callback; its own array copy/underrun path
            // must not allocate even under the concurrent DSP load.
            Assert.That(SpinWait.SpinUntil(
                () => buffered.BufferedFrames >= pcm.Length
                    || buffered.RenderingFault is not null,
                TimeSpan.FromSeconds(5)), Is.True);
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            buffered.Render(pcm.Length, pcm);
            long callbackAllocated = GC.GetAllocatedBytesForCurrentThread()
                - beforeAlloc;
            Assert.Multiple(() =>
            {
                Assert.That(callbackAllocated, Is.Zero);
                Assert.That(buffered.RenderingFault, Is.Null);
                Assert.That(buffered.ProducerRenderedFrames,
                    Is.GreaterThan(0));
                Assert.That(buffered.ProducerRenderBlockCount,
                    Is.GreaterThan(0));
            });

            TestContext.Progress.WriteLine(
                $"C5(d) buffered concurrent 3 owners, nested 2x2 -> 12x2 -> 3x2: " +
                $"ring={buffered.CapacityFrames}, quantum={buffered.RenderBlockFrames}, " +
                $"producer blocks={buffered.ProducerRenderBlockCount}, " +
                $"frames={buffered.ProducerRenderedFrames}, " +
                $"peak block ms={buffered.PeakProducerRenderBlockTime.TotalMilliseconds:F3}, " +
                $"overbudget={buffered.ProducerBlocksOverBudget}, " +
                $"underruns={buffered.UnderrunCount}, " +
                $"worker expansions={buffered.CompletedMultiExpansions}, " +
                $"worker cancelled={buffered.CancelledMultiExpansions}, " +
                $"prepared children={buffered.PreparedMultiConstituents}, " +
                $"queue peak={buffered.PeakPreparationQueueDepth}, " +
                $"worker allocated bytes={buffered.MultiPreparationAllocatedBytes}, " +
                $"callback allocated bytes={callbackAllocated}");
        }
        finally
        {
            buffered.Dispose(); // stop producer before disposing its source
            (raw as IDisposable)?.Dispose();
        }
    }
}

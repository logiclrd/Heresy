using System;
using System.Diagnostics;
using System.Numerics;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.Realtime;

using NUnit.Framework;

namespace Heresy.Tests.Playback;

[TestFixture]
public sealed class SynthProducerScalingProfileTests
{
    // This is a single-thread, steady-state PCM-producer scaling profile,
    // deliberately separate from paced callback / operating-system jitter.
    // Durations and allocations are observations, never correctness budgets.
    [Test]
    public void CompareDirectSourceAndMultiRenderCostsWithoutTimingAssertions()
    {
        const int sampleRate = 48000;
        const int blockFrames = 256;
        const int measuredBlocks = 8;

        // Each case gets a new document, fresh prepared sound and fresh voices.
        // The 8x64 cases give an unexpanded baseline for the separate
        // paced 2->64 live-publication regression.
        (string name, int notes, int constituents, bool oscillator,
            bool source, bool reserve)[] cases =
        [
            ("constant", 1, 0, false, false, false),
            ("constant", 8, 0, false, false, false),
            ("oscillator", 1, 0, true, false, false),
            ("oscillator", 8, 0, true, false, false),
            ("nested-source-oscillator", 1, 0, true, true, false),
            ("nested-source-oscillator", 8, 0, true, true, false),
            ("multi-constant", 1, 2, false, false, false),
            ("multi-constant", 1, 64, false, false, false),
            ("multi-constant", 8, 2, false, false, false),
            ("multi-constant", 8, 8, false, false, false),
            ("multi-constant", 8, 32, false, false, false),
            ("multi-constant", 8, 64, false, false, false),
            ("multi-constant-live-reserve", 8, 64, false, false, true),
            ("multi-oscillator", 1, 64, true, false, false),
            ("multi-oscillator", 8, 64, true, false, false),
        ];

        PlaybackRequestAudioSourceFactory factory = new(
            new RenderConfiguration(sampleRate,
            [
                new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)
            ]));
        foreach (var entry in cases)
        {
            SongDocument document = new();
            ObjectId rootId = document.AllocateObjectId();
            FmSynthGraph graph;
            if (entry.source)
            {
                ObjectId childId = document.AllocateObjectId();
                document.Add(new FmSynthDefinition(childId, "Profiling child",
                    new FmSynthGraph(
                        [new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 220)],
                        0)));
                graph = new FmSynthGraph([
                    new FmSoundSourceNode(0, childId,
                        FmMultiNode.NeutralFrequencyHz)], 0);
            }
            else
            {
                FmSynthNode baseNode = entry.oscillator
                    ? new FmOscillatorNode(0, FmOscillatorWaveform.Sine, 220)
                    : new FmConstantNode(0, 0.01);
                graph = entry.constituents == 0
                    ? new FmSynthGraph([baseNode], 0)
                    : new FmSynthGraph([
                        baseNode,
                        new FmMultiNode(1, 0, voiceCount: entry.constituents,
                            detune: 0, randomizePhase: false,
                            gainCompensation: false)], 1);
            }
            document.Add(new FmSynthDefinition(rootId, "Profiling root", graph));
            IAudioOutputSource output = factory.Create(
                AdHocPlaybackRequest.Create(document,
                    new NoteScheduleBuilder().Freeze(),
                    reserveSynthMultiHeadroom: entry.reserve));
            try
            {
                ILiveAudioOutputSource notes = (ILiveAudioOutputSource)output;
                for (int note = 0; note < entry.notes; note++)
                    notes.EnqueueLiveEvent(ChannelTarget.Virtual(300 + (uint)note),
                        [new StartNoteCommand(rootId)]);

                float[] buffer = new float[blockFrames];
                output.Render(blockFrames, buffer); // warm-up outside timing
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int block = 0; block < measuredBlocks; block++)
                {
                    Array.Clear(buffer); // producer owns a fresh, cleared mix bus
                    output.Render(blockFrames, buffer);
                }
                TimeSpan duration = Stopwatch.GetElapsedTime(started);
                long allocated = GC.GetAllocatedBytesForCurrentThread()
                    - allocatedBefore;

                foreach (float sample in buffer)
                    Assert.That(float.IsFinite(sample), Is.True,
                        "profiling must never normalize non-finite PCM");
                double frames = measuredBlocks * blockFrames;
                TestContext.Progress.WriteLine(
                    $"Synth producer scaling: {entry.name}, notes={entry.notes}, " +
                    $"constituents={entry.constituents}, reserve={entry.reserve}, " +
                    $"frames={(int)frames}, elapsed ms={duration.TotalMilliseconds:F3}, " +
                    $"us/output frame={duration.TotalMilliseconds * 1000 / frames:F3}, " +
                    $"render-thread allocated bytes={allocated}");
            }
            finally
            {
                (output as IDisposable)?.Dispose();
            }
        }
    }
}

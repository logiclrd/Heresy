using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Filters;
using Heresy.Core.Samples;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Filters;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthMultiTests
{
    [Test]
    public void GainCompensationMeansArithmeticAverageNotAutomaticSummation()
    {
        FmSynthGraph avg = new([
            new FmConstantNode(1, 0.5),
            new FmMultiNode(2, 1, voiceCount: 7,
                randomizePhase: false, gainCompensation: true),
        ], 2);
        FmSynthGraph sum = new([
            new FmConstantNode(1, 0.5),
            new FmMultiNode(2, 1, voiceCount: 7,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        Render(avg, 128).Should().OnlyContain(x => Math.Abs(x - 0.5f) < 1e-6);
        Render(sum, 128).Should().OnlyContain(x => Math.Abs(x - 3.5f) < 1e-6);
    }

    [Test]
    public void HarmonicAndChordRetuneIndependentOscillatorInstances()
    {
        foreach (FmMultiNode node in new[]
        {
            new FmMultiNode(2, 1, FmMultiDistribution.Harmonic,
                harmonicComponents: "1 2", randomizePhase: false,
                gainCompensation: false),
            new FmMultiNode(2, 1, FmMultiDistribution.Chord,
                chordComponents: "0 12", randomizePhase: false,
                gainCompensation: false),
        })
        {
            FmSynthGraph graph = new([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 100),
                node,
            ], 2);
            float[] output = Render(graph, 512);
            for (int n = 0; n < 512; n++)
            {
                double expected = Math.Sin(2 * Math.PI * 100 * n / 48000)
                    + Math.Sin(2 * Math.PI * 200 * n / 48000);
                output[n].Should().BeApproximately((float)expected, 3e-4f);
            }
        }
    }

    [Test]
    public void OscillatorOnlyMultiMatchesGeneralGraphThroughRandomPhaseLiveEditAndRelease()
    {
        // The optimized child is a single oscillator; the reference's
        // identity Multiply forces the general child render and lifetime
        // paths. The authored operator must not change audio, even when
        // phase skips, compatible edits and Note Off overlap.
        foreach (FmOscillatorWaveform waveform in Enum.GetValues<FmOscillatorWaveform>())
        {
            FmSynthGraph Graph(bool general, FmOscillatorWaveform wave,
                double frequency, double curvature)
            {
                FmOscillatorNode oscillator = new(1, wave, frequency,
                    minimum: -0.65, maximum: 0.85, curvature: curvature);
                FmMultiNode multi = new(4, general ? 3 : 1,
                    voiceCount: 4, detune: 0.07, randomizePhase: true,
                    cycleLength: 73, gainCompensation: true);
                return general ? new FmSynthGraph([
                    oscillator, new FmConstantNode(2, 1.0),
                    new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
                    multi,
                ], 4) : new FmSynthGraph([oscillator, multi], 4);
            }

            FmSynthSound optimized = new(
                Graph(false, waveform, 330, 1.5), new EmptyEnvelopes());
            FmSynthSound reference = new(
                Graph(true, waveform, 330, 1.5), new EmptyEnvelopes());
            SoundState fastState = optimized.CreateState();
            SoundState referenceState = reference.CreateState();
            foreach (SoundState state in new[] { fastState, referenceState })
            {
                state.SetNoiseSeed(7721);
                state.PitchMultiplier = 1.3;
                state.PlaybackSpeedMultiplier = 0.8;
                state.PitchTrajectory.SetMultiplier(83, 1.2);
                state.PitchTrajectory.SetMultiplier(163, 0.95);
            }

            RenderContext context = Mono();
            float[] fast = new float[340];
            float[] generic = new float[340];
            optimized.Render(context, fastState, 0, 111, fast.AsSpan(0, 111));
            reference.Render(context, referenceState, 0, 111,
                generic.AsSpan(0, 111));
            FmOscillatorWaveform revisedWaveform =
                waveform == FmOscillatorWaveform.Sine
                    ? FmOscillatorWaveform.Square : FmOscillatorWaveform.Sine;
            FmSynthSound optimizedRevision = new(
                Graph(false, revisedWaveform, 440, -1.0), new EmptyEnvelopes());
            FmSynthSound referenceRevision = new(
                Graph(true, revisedWaveform, 440, -1.0), new EmptyEnvelopes());
            optimized.TryPrepareRunningState(optimizedRevision, fastState)
                .Should().BeTrue();
            reference.TryPrepareRunningState(referenceRevision, referenceState)
                .Should().BeTrue();
            optimized.CommitPreparedRunningState(optimizedRevision, fastState);
            reference.CommitPreparedRunningState(referenceRevision, referenceState);

            // Continue through several held frames after the live edit.
            // No oscillator child has a finite end until Note Off, even
            // across pitch changes and its randomized initial phase skip.
            int offset = 111;
            foreach (int count in new[] { 1, 51, 30 })
            {
                optimizedRevision.Render(context, fastState, offset, count,
                    fast.AsSpan(offset, count));
                referenceRevision.Render(context, referenceState, offset,
                    count, generic.AsSpan(offset, count));
                offset += count;
            }
            offset.Should().Be(193);

            // Skip fourteen outer frames. Both the optimized and general
            // graph must catch up child oscillator phase/history exactly.
            offset = 207;
            TimeSpan release = TimeSpan.FromSeconds(229.0 / 48000);
            fastState.NoteOffTime = release;
            referenceState.NoteOffTime = release;
            foreach (int count in new[] { 1, 21, 67, 44 })
            {
                optimizedRevision.Render(context, fastState, offset, count,
                    fast.AsSpan(offset, count));
                referenceRevision.Render(context, referenceState, offset,
                    count, generic.AsSpan(offset, count));
                offset += count;
            }
            offset.Should().Be(fast.Length);
            fast.Should().Equal(generic,
                "single-oscillator lifecycle shortcuts must not alter " +
                "randomized phase, live-edit continuity or release timing");
            fast[^1].Should().Be(0);
        }
    }

    [Test]
    public void MultiSharesOuterPitchTrajectoryAcrossConstituentsWithoutChangingPhase()
    {
        // Two identical Multi children should match a doubled standalone
        // oscillator even as the parent's piecewise pitch changes. This
        // guards the per-frame trajectory lookup against accidentally
        // reusing an old pitch, changing multiplication semantics or
        // advancing independent child clocks at the wrong frame.
        FmOscillatorNode oscillator = new(1, FmOscillatorWaveform.Sine,
            frequencyHz: 330, minimum: -0.7, maximum: 0.8, curvature: 2);
        FmSynthSound standalone = new(new FmSynthGraph([oscillator], 1),
            new EmptyEnvelopes());
        FmSynthSound multi = new(new FmSynthGraph([
            oscillator,
            new FmMultiNode(2, 1, voiceCount: 2, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2), new EmptyEnvelopes());

        SoundState soloState = standalone.CreateState();
        SoundState multiState = multi.CreateState();
        foreach (SoundState state in new[] { soloState, multiState })
        {
            state.PitchMultiplier = 1.5;
            state.PlaybackSpeedMultiplier = 0.5;
            state.PitchTrajectory.SetMultiplier(53, 1.25);
            state.PitchTrajectory.SetMultiplier(113, 0.75);
            state.PitchTrajectory.SetMultiplier(241, 1.875);
            state.NoteOffTime = TimeSpan.FromSeconds(301.0 / 48000);
        }

        const int frames = 360;
        float[] solo = new float[frames];
        float[] combined = new float[frames];
        RenderContext context = Mono();
        standalone.Render(context, soloState, 0, frames, solo);
        int offset = 0;
        foreach (int length in new[] { 1, 52, 60, 128, 58, 61 })
        {
            multi.Render(context, multiState, offset, length,
                combined.AsSpan(offset, length));
            offset += length;
        }
        offset.Should().Be(frames);
        for (int i = 0; i < frames; i++)
            combined[i].Should().BeApproximately(solo[i] * 2, 1e-5f,
                "a shared outer trajectory must retune every Multi child " +
                "without changing oscillator integration or release frames");
        combined[^1].Should().Be(0);
    }

    [Test]
    public void NestedMultiCreatesIndependentSubgraphInvocations()
    {
        FmSynthGraph graph = new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, FmMultiDistribution.Harmonic,
                harmonicComponents: "1 2 3", randomizePhase: false,
                gainCompensation: false),
            new FmMultiNode(3, 2, FmMultiDistribution.Chord,
                chordComponents: "0 7", randomizePhase: false,
                gainCompensation: false),
        ], 3);
        Render(graph, 32).Should()
            .OnlyContain(x => Math.Abs(x - 1.5f) < 1e-6);
    }

    [Test]
    public void RandomizedWholeSubgraphOffsetsAndFadesAreDeterministic()
    {
        FmSynthGraph graph = new([
            new FmConstantNode(1, 1),
            new FmMultiNode(2, 1, voiceCount: 7,
                cycleLength: 4096.0, randomizePhase: true,
                gainCompensation: true),
        ], 2);
        float[] first = Render(graph, 4096, seed: 123);
        float[] replay = Render(graph, 4096, seed: 123);
        first.Should().Equal(replay);
        first[0].Should().BeLessThan(1.0f,
            "skipped subgraphs have a voice-local fade at the common note onset");
        first.Last().Should().BeApproximately(1.0f, 1e-6f);
        float[] alternateSeed = Render(graph, 4096, seed: 456);
        first.SequenceEqual(alternateSeed).Should().BeFalse();
    }

    [Test]
    public void DownstreamEnvelopeReleaseKeepsWrappedOscillatorAudible()
    {
        ObjectId envelopeId = (ObjectId)801U;
        FmSynthGraph graph = new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Square, 50),
            new FmMultiNode(2, 1, voiceCount: 1, randomizePhase: false),
            new FmEnvelopeNode(3, envelopeId),
            new FmOperatorNode(4, FmOperatorKind.Multiply, [2, 3]),
        ], 4);
        FmSynthSound sound = new(graph, new TenFrameRelease(envelopeId));
        SoundState state = sound.CreateState();
        RenderContext context = new(new RenderConfiguration(1000, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));
        float[] output = new float[20];
        // Start with a held oscillator, then publish Note Off at the
        // exact frame of release. The child must inherit the downstream
        // Multiply-envelope's tail rather than stop at its own boundary.
        sound.Render(context, state, 0, 3, output.AsSpan(0, 3));
        state.NoteOffTime = TimeSpan.FromSeconds(3.0 / 1000);
        sound.Render(context, state, 3, output.Length - 3,
            output.AsSpan(3));
        Math.Abs(output[8]).Should().BeGreaterThan(0.1f,
            "an audible downstream release envelope must keep the internal oscillator alive");
        Math.Abs(output[12]).Should().BeGreaterThan(0.1f);
        output[13].Should().Be(0);
        output[18].Should().Be(0);
    }

    [Test]
    public void SampleBackedSquareWaveReceivesRealIndependentOffsetsAndReplaysExactly()
    {
        ObjectId sampleId = (ObjectId)408U;
        // The PCM carries a 100-sample square-wave cycle even though no
        // oscillator node exposes that period to the graph.
        float[] pcm = Enumerable.Range(0, 4000)
            .Select(i => i % 100 < 50 ? 1.0f : -1.0f).ToArray();
        SampleSound sample = new(new SampleDefinition(sampleId, "PCM square",
            new ExternalAssetReference("square.wav")),
            new MemorySampleData(48000, 1, pcm));
        SourceResolver resolver = new(sampleId, sample);
        FmSynthGraph alignedGraph = new([
            new FmSoundSourceNode(1, sampleId, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 7,
                cycleLength: 100, randomizePhase: false,
                gainCompensation: true),
        ], 2);
        FmSynthGraph randomizedGraph = new([
            new FmSoundSourceNode(1, sampleId, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 7,
                cycleLength: 100, randomizePhase: true,
                gainCompensation: true),
        ], 2);
        FmSynthSound aligned = new(alignedGraph, resolver, soundResolver: resolver);
        FmSynthSound randomized = new(randomizedGraph, resolver, soundResolver: resolver);
        float[] withoutRandomization = new float[400];
        aligned.Render(Mono(), aligned.CreateState(), 0, 400, withoutRandomization);
        withoutRandomization[0].Should().BeApproximately(1.0f, 1e-6f);
        SoundState voice = randomized.CreateState();
        voice.SetNoiseSeed(42);
        float[] withRandomization = new float[400];
        randomized.Render(Mono(), voice, 0, 400, withRandomization);
        withRandomization[0].Should().BeLessThan(0.5f);
        // Reconstructed independent sample playheads must follow the same
        // exact sub-frame pitch integration and time displacement.
        randomized.PrepareReplay(Mono(), voice, targetFrame: 200);
        float[] replay = new float[200];
        randomized.Render(Mono(), voice, 200, replay.Length, replay);
        replay.Should().Equal(withRandomization.AsSpan(200).ToArray());
    }

    [Test]
    public void ConstituentNoteOffDoesNotMoveWithTheInitialSkip()
    {
        FmSynthGraph graph = new([
            new FmConstantNode(1, 1),
            new FmMultiNode(2, 1, voiceCount: 3, randomizePhase: true,
                cycleLength: 128),
        ], 2);
        FmSynthSound sound = new(graph, new EmptyEnvelopes());
        SoundState state = sound.CreateState();
        state.SetNoiseSeed(17);
        state.NoteOffTime = TimeSpan.FromSeconds(256.0 / 48000);
        float[] output = new float[280];
        sound.Render(Mono(), state, 0, output.Length, output);
        output[275].Should().Be(0.0f);
    }

    [Test]
    public void SequentialMonoMultiChildrenMatchSplitBlocksThroughRandomPhaseAndNoteOff()
    {
        FmSynthGraph graph = new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 330),
            new FmMultiNode(2, 1, voiceCount: 3, detune: 0.1,
                randomizePhase: true, cycleLength: 97,
                gainCompensation: true),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 3);
        FmSynthSound full = new(graph, new EmptyEnvelopes());
        FmSynthSound segmented = new(graph, new EmptyEnvelopes());
        SoundState oneVoice = full.CreateState();
        SoundState splitVoice = segmented.CreateState();
        oneVoice.SetNoiseSeed(871);
        splitVoice.SetNoiseSeed(871);
        oneVoice.NoteOffTime = TimeSpan.FromMilliseconds(10);
        splitVoice.NoteOffTime = TimeSpan.FromMilliseconds(10);
        RenderContext context = Mono();
        float[] expected = new float[620];
        float[] actual = new float[620];
        full.Render(context, oneVoice, 0, expected.Length, expected);
        int offset = 0;
        foreach (int count in new[] { 1, 7, 93, 31, 233, 54, 201 })
        {
            segmented.Render(context, splitVoice, offset, count,
                actual.AsSpan(offset, count));
            offset += count;
        }
        offset.Should().Be(expected.Length);
        actual.Should().Equal(expected,
            "per-frame Multi child rendering must preserve phase, seeded onset, " +
            "split-block continuity and the exact note-release boundary");
        actual[^1].Should().Be(0);
    }

    [Test]
    public void MonoMultiChildrenRetainSampleEOFAndPitchHistoryAcrossBlockSplits()
    {
        ObjectId sampleId = (ObjectId)1703U;
        float[] data = Enumerable.Range(0, 240)
            .Select(i => (float)Math.Sin(2.0 * Math.PI * i / 41.0)).ToArray();
        SampleSound sample = new(new SampleDefinition(sampleId, "Finite PCM",
            new ExternalAssetReference("finite.wav")),
            new MemorySampleData(48000, 1, data));
        SourceResolver resolver = new(sampleId, sample);
        FmSynthGraph graph = new([
            new FmSoundSourceNode(1, sampleId, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 5, detune: 0,
                randomizePhase: true, cycleLength: 61,
                gainCompensation: true),
        ], 2);
        FmSynthSound full = new(graph, resolver, soundResolver: resolver);
        FmSynthSound segmented = new(graph, resolver, soundResolver: resolver);
        SoundState oneVoice = full.CreateState();
        SoundState splitVoice = segmented.CreateState();
        oneVoice.SetNoiseSeed(29);
        splitVoice.SetNoiseSeed(29);
        RenderContext context = Mono();
        float[] expected = new float[420];
        float[] actual = new float[420];
        full.Render(context, oneVoice, 0, expected.Length, expected);
        int offset = 0;
        foreach (int count in new[] { 1, 17, 42, 111, 61, 188 })
        {
            segmented.Render(context, splitVoice, offset, count,
                actual.AsSpan(offset, count));
            offset += count;
        }
        offset.Should().Be(expected.Length);
        actual.Should().Equal(expected,
            "a finite nested Sample must discover its natural end and preserve " +
            "its forward-integrated playback position across block boundaries");
        actual[^1].Should().Be(0);
    }

    [Test]
    public void OrdinarySynthUsesOnlyAuthoredMultiCapacityUnlessAuditionOptsIn()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        FmSynthSound ordinary = new(Graph(2), new EmptyEnvelopes());
        FmSynthSound audition = new(Graph(2), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        FmSynthSound revision = new(Graph(4), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState ordinaryVoice = ordinary.CreateState();
        SoundState auditionVoice = audition.CreateState();

        ordinary.CanAdoptRunningState(revision, ordinaryVoice).Should().BeFalse(
            "ordinary playback has no dormant children for live growth");
        ordinary.TryPrepareRunningState(revision, ordinaryVoice).Should().BeFalse();
        audition.CanAdoptRunningState(revision, auditionVoice).Should().BeTrue(
            "live audition reserves dormant children at note creation");
        audition.TryPrepareRunningState(revision, auditionVoice).Should().BeTrue();

        float[] standard = new float[4], live = new float[4];
        ordinary.Render(Mono(), ordinaryVoice, 0, 4, standard);
        audition.Render(Mono(), auditionVoice, 0, 4, live);
        standard.Should().Equal(live,
            "dormant constituents must not affect the authored audio");
    }

    [Test]
    public void LiveMultiCountGrowthAndRetirementCrossfadeWithoutRebuildingChildVoices()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);

        RenderContext context = Mono();
        FmSynthSound current = new(Graph(2), new EmptyEnvelopes(), reserveMultiHeadroom: true);
        SoundState voice = current.CreateState();
        float[] before = new float[80];
        current.Render(context, voice, 0, 80, before);
        before.Should().OnlyContain(x => Math.Abs(x - 0.5f) < 1e-6f);

        FmSynthSound grown = new(Graph(4), new EmptyEnvelopes());
        current.CanAdoptRunningState(grown, voice).Should().BeTrue();
        current.TryPrepareRunningState(grown, voice).Should().BeTrue();
        current.CommitPreparedRunningState(grown, voice);
        float[] growth = new float[130];
        grown.Render(context, voice, 80, growth.Length, growth);
        growth[0].Should().BeApproximately(0.5f, 1e-6f,
            "new children must fade in, not start at full output");
        growth[32].Should().BeInRange(0.5f, 1.0f);
        growth[100].Should().BeApproximately(1.0f, 1e-6f);

        FmSynthSound shrunk = new(Graph(1), new EmptyEnvelopes());
        grown.TryPrepareRunningState(shrunk, voice).Should().BeTrue();
        grown.CommitPreparedRunningState(shrunk, voice);
        float[] shrink = new float[130];
        shrunk.Render(context, voice, 210, shrink.Length, shrink);
        shrink[0].Should().BeApproximately(1.0f, 1e-6f);
        shrink[32].Should().BeInRange(0.25f, 1.0f);
        shrink[100].Should().BeApproximately(0.25f, 1e-6f);

        // Previously retired children keep advancing their own states
        // silently, so reactivating within the original reserve needs no
        // note restart, child allocation, or catch-up replay.
        FmSynthSound revived = new(Graph(3), new EmptyEnvelopes());
        shrunk.TryPrepareRunningState(revived, voice).Should().BeTrue();
        shrunk.CommitPreparedRunningState(revived, voice);
        float[] revival = new float[130];
        revived.Render(context, voice, 340, revival.Length, revival);
        revival[0].Should().BeApproximately(0.25f, 1e-6f);
        revival[100].Should().BeApproximately(0.75f, 1e-6f);
    }

    [Test]
    public void LiveDistributionAndDetuneEditsGlideConstituentsWithoutResettingPhases()
    {
        FmSynthGraph Graph(FmMultiNode multi) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 150),
            multi,
        ], 2);
        FmMultiNode original = new(2, 1, detune: 0, voiceCount: 2,
            randomizePhase: false, gainCompensation: false);
        FmMultiNode harmonic = new(2, 1,
            distribution: FmMultiDistribution.Harmonic,
            harmonicComponents: "1 2", randomizePhase: true,
            gainCompensation: false, cycleLength: 300);
        FmSynthSound old = new(Graph(original), new EmptyEnvelopes());
        FmSynthSound control = new(Graph(original), new EmptyEnvelopes());
        SoundState voice = old.CreateState();
        SoundState comparison = control.CreateState();
        RenderContext context = Mono();
        old.Render(context, voice, 0, 117, new float[117]);
        control.Render(context, comparison, 0, 117, new float[117]);

        FmSynthSound next = new(Graph(harmonic), new EmptyEnvelopes());
        old.CanAdoptRunningState(next, voice).Should().BeTrue();
        old.TryPrepareRunningState(next, voice).Should().BeTrue();
        old.CommitPreparedRunningState(next, voice);
        float[] edited = new float[300], reference = new float[300];
        next.Render(context, voice, 117, 300, edited);
        control.Render(context, comparison, 117, 300, reference);
        edited[0].Should().BeApproximately(reference[0], 1e-5f,
            "the first sample after publication must continue both old phases");
        edited.Should().OnlyContain(x => float.IsFinite(x));
        Enumerable.Range(130, 170)
            .Max(i => Math.Abs(edited[i] - reference[i]))
            .Should().BeGreaterThan(0.2f,
                "the second constituent must take its new harmonic pitch");
    }

    [Test]
    public void LiveMultiRandomPhaseAndCycleChangesKeepOldOffsetsAndAffectNewNotes()
    {
        FmSynthGraph Graph(bool random, double cycle) => new([
            new FmConstantNode(1, 1),
            new FmMultiNode(2, 1, detune: 0, voiceCount: 3,
                randomizePhase: random, cycleLength: cycle,
                gainCompensation: true),
        ], 2);
        FmSynthSound old = new(Graph(false, 100), new EmptyEnvelopes());
        SoundState voice = old.CreateState();
        RenderContext context = Mono();
        old.Render(context, voice, 0, 140, new float[140]);
        FmSynthSound updated = new(Graph(true, 1024), new EmptyEnvelopes());
        old.TryPrepareRunningState(updated, voice).Should().BeTrue();
        old.CommitPreparedRunningState(updated, voice);
        float[] retained = new float[200];
        updated.Render(context, voice, 140, retained.Length, retained);
        retained.Should().OnlyContain(x => Math.Abs(x - 1.0f) < 1e-6f,
            "already-initialized constituents retain their established offsets");
        SoundState fresh = updated.CreateState();
        fresh.SetNoiseSeed(17);
        float[] onset = new float[1];
        updated.Render(context, fresh, 0, 1, onset);
        onset[0].Should().BeLessThan(1.0f,
            "new notes use the revised randomized onset and cycle policy");
    }

    [Test]
    public void HeldOscillatorMultiRephasesThroughWorkerPreparedSixtyFourFrameCrossfade()
    {
        FmSynthGraph Graph(bool random, double cycle) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 185),
            new FmMultiNode(2, 1, detune: 0, voiceCount: 3,
                randomizePhase: random, cycleLength: cycle,
                gainCompensation: true),
        ], 2);
        RenderContext context = Mono();
        FmSynthSound original = new(Graph(false, 128), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState held = original.CreateState();
        SoundState referenceState = original.CreateState();
        held.SetNoiseSeed(17);
        referenceState.SetNoiseSeed(17);
        original.Render(context, held, 0, 80, new float[80]);
        original.Render(context, referenceState, 0, 80, new float[80]);

        FmSynthSound edited = new(Graph(true, 820), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        original.TryPrepareRunningState(edited, held).Should().BeFalse(
            "an initialized oscillator onset change needs a detached worker state");
        original.RequestMultiExpansionPreparation(edited, held).Should().BeTrue();

        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(edited, held).Should().BeTrue();
        original.CommitPreparedRunningState(edited, held);
        float[] actual = new float[160];
        float[] reference = new float[160];
        edited.Render(context, held, 80, actual.Length, actual);
        original.Render(context, referenceState, 80, reference.Length, reference);
        actual[0].Should().Be(reference[0],
            "the new phase has zero mix on the first publication frame");
        actual.Should().OnlyContain(x => float.IsFinite(x));
        Enumerable.Range(64, 96).Max(i => Math.Abs(actual[i] - reference[i]))
            .Should().BeGreaterThan(0.05f,
                "the held oscillators must actually adopt the revised onset");
        worker.PreparedMultiConstituentCount.Should().Be(3,
            "three detached active replacements, not per-sample construction");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void SecondMultiRephaseWaitsForPriorFadeWithoutLosingPreparedWork()
    {
        FmSynthGraph Graph(bool random, double cycle) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 110),
            new FmMultiNode(2, 1, voiceCount: 2, detune: 0,
                randomizePhase: random, cycleLength: cycle,
                gainCompensation: true),
        ], 2);
        RenderContext context = Mono();
        FmSynthSound original = new(Graph(false, 120), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        SoundState held = original.CreateState();
        held.SetNoiseSeed(41);
        original.Render(context, held, 0, 80, new float[80]);
        FmSynthSound first = new(Graph(true, 800), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound second = new(Graph(false, 120), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        using FirPreparationScheduler worker = new();

        void Prepare(FmSynthSound previous, FmSynthSound target, int completed)
        {
            previous.RequestMultiExpansionPreparation(target, held).Should().BeTrue();
            previous.QueuePendingFirControls(held, worker);
            Assert.That(SpinWait.SpinUntil(
                () => worker.CompletedMultiExpansionCount >= completed,
                TimeSpan.FromSeconds(5)), Is.True);
            previous.AdoptPreparedFirControls(held);
        }

        Prepare(original, first, 1);
        original.TryPrepareRunningState(first, held).Should().BeTrue();
        original.CommitPreparedRunningState(first, held);
        Prepare(first, second, 2);
        first.TryPrepareRunningState(second, held).Should().BeFalse(
            "a second onset must not form an unbounded transition chain");

        // The old/new pair is mixed for frames 80..143. At frame 144 the
        // prior transition completes; the prepared latest revision waits.
        first.Render(context, held, 80, 65, new float[65]);
        first.TryPrepareRunningState(second, held).Should().BeTrue();
        first.CommitPreparedRunningState(second, held);
        float[] continued = new float[100];
        second.Render(context, held, 145, continued.Length, continued);
        continued.Should().OnlyContain(x => float.IsFinite(x));
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void MultiRephaseMigratesBothIndependentOscillatorsBehindStatelessOperators()
    {
        FmSynthGraph Graph(bool random) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 109),
            new FmOscillatorNode(2, FmOscillatorWaveform.Triangle, 236,
                constant: true),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            new FmConstantNode(4, 0.4),
            new FmOperatorNode(5, FmOperatorKind.Multiply, [3, 4]),
            new FmMultiNode(6, 5, voiceCount: 2, detune: 0,
                randomizePhase: random, cycleLength: 840,
                gainCompensation: true),
        ], 6);
        FmSynthSound original = new(Graph(false), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound revised = new(Graph(true), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState referenceState = original.CreateState();
        held.SetNoiseSeed(0xC0FFEE);
        referenceState.SetNoiseSeed(0xC0FFEE);
        original.Render(context, held, 0, 128, new float[128]);
        original.Render(context, referenceState, 0, 128, new float[128]);

        original.TryPrepareRunningState(revised, held).Should().BeFalse(
            "every initialized oscillator in the graph needs a worker-prepared phase");
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        float[] after = new float[160];
        float[] reference = new float[160];
        long renderBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 128, after.Length, after);
        long renderBytes = GC.GetAllocatedBytesForCurrentThread()
            - renderBytesBefore;
        renderBytes.Should().Be(0L,
            "worker-prepared multi-oscillator crossfades must allocate " +
            "nothing on the realtime PCM producer");
        original.Render(context, referenceState, 128, reference.Length, reference);
        after[0].Should().Be(reference[0],
            "the first publication frame must contain only the outgoing graph");
        after.Should().OnlyContain(x => float.IsFinite(x));
        Enumerable.Range(65, 95).Max(i => Math.Abs(after[i] - reference[i]))
            .Should().BeGreaterThan(0.05f,
                "both oscillator phases must adopt the seeded onset after the fade");
        worker.PreparedMultiConstituentCount.Should().Be(2);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void MultiOnsetCrossfadeReleasesBothChildHistoriesAtTheOriginalNoteEnd()
    {
        FmSynthGraph Graph(bool random) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 170),
            new FmOscillatorNode(2, FmOscillatorWaveform.Square, 230),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
            new FmMultiNode(4, 3, voiceCount: 3, detune: 0,
                randomizePhase: random, cycleLength: 1100,
                gainCompensation: true),
        ], 4);
        FmSynthSound original = new(Graph(false), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound revised = new(Graph(true), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState referenceState = original.CreateState();
        held.SetNoiseSeed(44);
        referenceState.SetNoiseSeed(44);
        original.Render(context, held, 0, 80, new float[80]);
        original.Render(context, referenceState, 0, 80, new float[80]);
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);

        // Note Off occurs at frame 96, during the 80..143 crossfade.
        // All children must end at frame 97, not acquire a fresh 64-frame
        // release tail or extend playback to the new random phase offset.
        held.NoteOffTime = TimeSpan.FromSeconds(96.0 / 48000);
        referenceState.NoteOffTime = held.NoteOffTime;
        float[] after = new float[120];
        float[] reference = new float[120];
        revised.Render(context, held, 80, after.Length, after);
        original.Render(context, referenceState, 80, reference.Length, reference);
        after[0].Should().Be(reference[0]);
        after.Should().OnlyContain(x => float.IsFinite(x));
        after.Skip(17).Should().OnlyContain(x => x == 0f,
            "the original note end must silence both fade histories");
        revised.GetEndFrameExclusive(context, held).Should().Be(97,
            "the transition must not prolong the natural release");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void ModulatedOscillatorMultiRetainsOldOnsetWithoutAHistoryMigrationPolicy()
    {
        FmSynthGraph Graph(bool random) => new([
            new FmConstantNode(1, 2),
            new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 140,
                multiplierNodeId: 1),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                randomizePhase: random, cycleLength: 1250,
                gainCompensation: true),
        ], 3);
        FmSynthSound original = new(Graph(false), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound reference = new(Graph(false), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound updated = new(Graph(true), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        SoundState held = original.CreateState();
        SoundState referenceState = reference.CreateState();
        original.Render(context, held, 0, 96, new float[96]);
        reference.Render(context, referenceState, 0, 96, new float[96]);
        original.RequestMultiExpansionPreparation(updated, held).Should().BeFalse(
            "FM history cannot yet be reconstructed from a phase-offset delta");
        original.TryPrepareRunningState(updated, held).Should().BeTrue();
        original.CommitPreparedRunningState(updated, held);
        float[] after = new float[192];
        float[] expected = new float[192];
        updated.Render(context, held, 96, after.Length, after);
        reference.Render(context, referenceState, 96, expected.Length, expected);
        after.Should().Equal(expected,
            "unsupported modulation retains already-playing onset history");
    }

    [Test]
    public void PreparedMultiOnePoleRephaseCopiesLiveIirHistoryWithoutPcmAllocations()
    {
        // A cycle length smaller than one sample rounds to one frame:
        // both old and new onset skips are precisely zero. The incoming
        // Filter must therefore produce the same PCM as the outgoing
        // Filter if and only if its accumulated integrator was copied.
        FilterDefinition definition = new((ObjectId)6401U, "IIR", [
            new FilterStageDefinition(1, "low", FilterKind.OnePole, [
                new("CutoffHz", FilterParameterValue.FromNumber(110))]),
            new FilterStageDefinition(2, "second", FilterKind.OnePole, [
                new("CutoffHz", FilterParameterValue.FromNumber(380))])]);
        FmSynthGraph Graph(bool randomized) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 315,
                minimum: 0.2, maximum: 0.8, constant: true),
            new FmFilterNode(2, 1, new FilterInstance(definition)),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                randomizePhase: randomized, cycleLength: 0.001,
                gainCompensation: true),
        ], 3);
        RenderContext context = Mono();
        FmSynthSound original = new(Graph(false), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound revised = new(Graph(true), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        SoundState held = original.CreateState();
        SoundState referenceState = original.CreateState();
        held.SetNoiseSeed(0x4801UL);
        referenceState.SetNoiseSeed(0x4801UL);
        original.Render(context, held, 0, 192, new float[192]);
        original.Render(context, referenceState, 0, 192, new float[192]);

        original.TryPrepareRunningState(revised, held).Should().BeFalse(
            "the live OnePole integrator requires a prepared independent chain");
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        float[] after = new float[160];
        float[] expected = new float[160];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, 192, after.Length, after);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        allocated.Should().Be(0L,
            "the worker must allocate the destination OnePole chain before PCM");
        original.Render(context, referenceState, 192, expected.Length, expected);
        after.Should().Equal(expected,
            "both serial OnePole integrators must match continuous held history");
        worker.PreparedMultiConstituentCount.Should().Be(2);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void OnePoleMultiCrossfadePreservesFirstSampleThenAdoptsNewPhase()
    {
        FilterDefinition definition = new((ObjectId)6402U, "IIR", [
            new FilterStageDefinition(1, "low", FilterKind.OnePole, [
                new("CutoffHz", FilterParameterValue.FromNumber(1200))])]);
        FmSynthGraph Graph(bool randomized) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 1250),
            new FmFilterNode(2, 1, new FilterInstance(definition)),
            new FmMultiNode(3, 2, voiceCount: 3, detune: 0,
                randomizePhase: randomized, cycleLength: 950,
                gainCompensation: true),
        ], 3);
        RenderContext context = Mono();
        FmSynthSound original = new(Graph(false), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        FmSynthSound revised = new(Graph(true), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState held = original.CreateState();
        SoundState referenceState = original.CreateState();
        held.SetNoiseSeed(0x4802UL);
        referenceState.SetNoiseSeed(0x4802UL);
        original.Render(context, held, 0, 100, new float[100]);
        original.Render(context, referenceState, 0, 100, new float[100]);
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);

        float[] after = new float[192];
        float[] reference = new float[192];
        revised.Render(context, held, 100, after.Length, after);
        original.Render(context, referenceState, 100, reference.Length, reference);
        after[0].Should().Be(reference[0],
            "the prepared OnePole histories must not alter the publication frame");
        after.Should().OnlyContain(x => float.IsFinite(x));
        Enumerable.Range(65, 127).Max(i => Math.Abs(after[i] - reference[i]))
            .Should().BeGreaterThan(0.02f,
                "the incoming chain must evolve independently with its revised onset");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void UnpublishedMultiOnsetPreparationNeverMutatesFmDrivenFilterHistory()
    {
        FilterDefinition definition = new((ObjectId)6403U, "Controlled", [
            new FilterStageDefinition(1, "low", FilterKind.OnePole, [
                new("CutoffHz", FilterParameterValue.FromNumber(300))])]);
        FmSynthGraph Graph(bool randomized)
        {
            FmFilterNode filter = new(3, 2, new FilterInstance(definition));
            filter = filter.WithParameterConnection(
                filter.GetPorts().Single(p => p.ParameterName == "CutoffHz"), 1);
            return new FmSynthGraph([
                new FmConstantNode(1, 300),
                new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 800),
                filter,
                new FmMultiNode(4, 3, voiceCount: 2, detune: 0,
                    randomizePhase: randomized, cycleLength: 950,
                    gainCompensation: true),
            ], 4);
        }
        RenderContext context = Mono();
        FmSynthSound original = new(Graph(false), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound updated = new(Graph(true), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound reference = new(Graph(false), new EmptyEnvelopes());
        SoundState held = original.CreateState();
        SoundState uninterrupted = reference.CreateState();
        original.Render(context, held, 0, 80, new float[80]);
        reference.Render(context, uninterrupted, 0, 80, new float[80]);
        // A preparation request, whether admitted now or in a future
        // more general Filter-history implementation, must NEVER
        // alter the active child's FM-driven state before publication.
        original.RequestMultiExpansionPreparation(updated, held);
        float[] actual = new float[128];
        float[] expected = new float[128];
        updated.Render(context, held, 80, actual.Length, actual);
        reference.Render(context, uninterrupted, 80, expected.Length, expected);
        actual.Should().Equal(expected,
            "unpublished revisions must not alter existing FM Filter histories");
    }

    [Test]
    public void RephasedOnePoleChildrenKeepTheAuthoredTailThroughNoteOff()
    {
        FilterDefinition definition = new((ObjectId)6404U, "Tail", [
            new FilterStageDefinition(1, "low", FilterKind.OnePole, [
                new("CutoffHz", FilterParameterValue.FromNumber(550))])]);
        FmSynthGraph Graph(bool randomized) => new([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sawtooth, 320),
            new FmFilterNode(2, 1, new FilterInstance(definition),
                tailSeconds: 0.002),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                randomizePhase: randomized, cycleLength: 1050,
                gainCompensation: true),
        ], 3);
        RenderContext context = Mono();
        FmSynthSound original = new(Graph(false), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        FmSynthSound revised = new(Graph(true), new EmptyEnvelopes(),
            enableLiveMultiExpansion: true);
        SoundState held = original.CreateState();
        SoundState unchanged = original.CreateState();
        held.SetNoiseSeed(0xBEEFUL);
        unchanged.SetNoiseSeed(0xBEEFUL);
        original.Render(context, held, 0, 80, new float[80]);
        original.Render(context, unchanged, 0, 80, new float[80]);
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);

        // The original note stops exciting the Filter at frame 97 and
        // provides 96 additional authored tail frames: exclusive end 193.
        // Neither incoming nor outgoing child is allowed to truncate or
        // prolong this shared lifetime during the 80..143 onset fade.
        held.NoteOffTime = TimeSpan.FromSeconds(96.0 / 48000);
        unchanged.NoteOffTime = held.NoteOffTime;
        float[] output = new float[150];
        float[] reference = new float[150];
        revised.Render(context, held, 80, output.Length, output);
        original.Render(context, unchanged, 80, reference.Length, reference);
        output[0].Should().Be(reference[0]);
        output.Should().OnlyContain(x => float.IsFinite(x));
        output.Skip(113).Should().OnlyContain(x => x == 0f,
            "Note Off and authored IIR tail must bound both crossfade histories");
        revised.GetEndFrameExclusive(context, held).Should().Be(193,
            "the two prepared Filter histories share the original release end");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void LiveSampleOnsetAcceptsFreshWrappersOverTheSameFrozenPcm()
    {
        // Production revisions use a new PlaybackSnapshotSoundResolver and
        // therefore a different SampleSound wrapper. Frozen song snapshots
        // share immutable SamplePcmData, not their mutable SoundState.
        ObjectId id = (ObjectId)7604U;
        const int frames = 4096;
        float[] values = Enumerable.Range(0, frames)
            .Select(i => (float)Math.Sin(2.0 * Math.PI * i / 79.0))
            .ToArray();
        using System.IO.MemoryStream wave = new();
        using (System.IO.BinaryWriter writer = new(wave,
            System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + frames * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(48000);
            writer.Write(96000);
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(frames * 2);
            for (int i = 0; i < frames; i++)
                writer.Write((short)Math.Round(values[i] * 30000));
        }
        SampleDefinition originalDefinition = SampleDefinition.CreateImported(
            id, "Frozen", "frozen.wav", wave.ToArray());
        SampleDefinition revisedDefinition = SampleDefinition.CreateImportedCopy(
            id, originalDefinition, "frozen-copy.wav", wave.ToArray());
        ReferenceEquals(originalDefinition, revisedDefinition).Should().BeFalse();
        ReferenceEquals(originalDefinition.PcmData, revisedDefinition.PcmData)
            .Should().BeTrue();

        SampleSound originalSample = new(originalDefinition,
            new FrozenPcmData(originalDefinition.PcmData!));
        SampleSound revisedSample = new(revisedDefinition,
            new FrozenPcmData(revisedDefinition.PcmData!));
        SourceResolver oldResolver = new(id, originalSample);
        SourceResolver revisedResolver = new(id, revisedSample);
        FmSynthGraph Graph(bool random) => new([
            new FmSoundSourceNode(1, id, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 2, detune: 0,
                randomizePhase: random, cycleLength: 0.001,
                gainCompensation: true),
        ], 2);
        FmSynthSound original = new(Graph(false), oldResolver,
            soundResolver: oldResolver, enableLiveMultiExpansion: true);
        FmSynthSound revised = new(Graph(true), revisedResolver,
            soundResolver: revisedResolver, enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        SoundState state = original.CreateState();
        SoundState unchanged = original.CreateState();
        original.Render(context, state, 0, 160, new float[160]);
        original.Render(context, unchanged, 0, 160, new float[160]);
        original.RequestMultiExpansionPreparation(revised, state).Should().BeTrue(
            "the same immutable SamplePcmData authorizes onset migration across wrappers");
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(state, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(state);
        original.TryPrepareRunningState(revised, state).Should().BeTrue();
        original.CommitPreparedRunningState(revised, state);
        float[] actual = new float[160], expected = new float[160];
        revised.Render(context, state, 160, actual.Length, actual);
        original.Render(context, unchanged, 160, expected.Length, expected);
        actual.Should().Equal(expected,
            "recompiling a frozen wrapper must not reset the Sample's integrated position");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void DirectSampleMultiLiveRephasePreservesExactIntegratedPositionWithoutPcmAllocations()
    {
        // Cycle length rounds to one frame. Although the authored
        // Randomize Phase flag changes, the actual skip stays zero,
        // making every output sample an exact history-copy oracle.
        ObjectId id = (ObjectId)7601U;
        float[] pcm = Enumerable.Range(0, 4096)
            .Select(i => (float)(0.35 * Math.Sin(2 * Math.PI * i / 71.0)
                + 0.1 * Math.Cos(2 * Math.PI * i / 19.0))).ToArray();
        SampleSound sample = new(new SampleDefinition(id, "PCM",
            new ExternalAssetReference("live-direct-sample.wav")),
            new MemorySampleData(48000, 1, pcm));
        SourceResolver resolver = new(id, sample);
        FmSynthGraph Graph(bool random) => new([
            new FmSoundSourceNode(1, id, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 2, detune: 0,
                randomizePhase: random, cycleLength: 0.001,
                gainCompensation: true),
        ], 2);
        FmSynthSound original = new(Graph(false), resolver,
            soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound revised = new(Graph(true), resolver,
            soundResolver: resolver, enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        SoundState state = original.CreateState();
        SoundState comparison = original.CreateState();
        state.SetNoiseSeed(0x7601UL);
        comparison.SetNoiseSeed(0x7601UL);
        original.Render(context, state, 0, 192, new float[192]);
        original.Render(context, comparison, 0, 192, new float[192]);

        original.TryPrepareRunningState(revised, state).Should().BeFalse();
        original.RequestMultiExpansionPreparation(revised, state).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(state, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(state);
        original.TryPrepareRunningState(revised, state).Should().BeTrue();
        original.CommitPreparedRunningState(revised, state);
        float[] actual = new float[160];
        float[] expected = new float[160];
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, state, 192, actual.Length, actual);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        allocated.Should().Be(0L,
            "the Sample's integrated position is transferred without PCM replay");
        original.Render(context, comparison, 192, expected.Length, expected);
        actual.Should().Equal(expected,
            "identical onset skips must retain every interpolated Sample frame");
        worker.PreparedMultiConstituentCount.Should().Be(2);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void DirectSampleMultiLiveRephaseShiftsPlayheadButKeepsFirstFrame()
    {
        ObjectId id = (ObjectId)7602U;
        float[] pcm = Enumerable.Range(0, 8192)
            .Select(i => (float)Math.Sin(2 * Math.PI * i / 107.0)).ToArray();
        SampleSound sample = new(new SampleDefinition(id, "Long PCM",
            new ExternalAssetReference("live-rephase.wav")),
            new MemorySampleData(48000, 1, pcm));
        SourceResolver resolver = new(id, sample);
        FmSynthGraph Graph(bool random) => new([
            new FmSoundSourceNode(1, id, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 3, detune: 0,
                randomizePhase: random, cycleLength: 1700,
                gainCompensation: true),
        ], 2);
        FmSynthSound original = new(Graph(false), resolver,
            soundResolver: resolver, reserveMultiHeadroom: true);
        FmSynthSound revised = new(Graph(true), resolver,
            soundResolver: resolver, reserveMultiHeadroom: true);
        RenderContext context = Mono();
        SoundState state = original.CreateState();
        SoundState baseline = original.CreateState();
        state.SetNoiseSeed(0x7602UL);
        baseline.SetNoiseSeed(0x7602UL);
        original.Render(context, state, 0, 112, new float[112]);
        original.Render(context, baseline, 0, 112, new float[112]);
        original.RequestMultiExpansionPreparation(revised, state).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(state, worker);
        Assert.That(SpinWait.SpinUntil(
            () => worker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(state);
        original.TryPrepareRunningState(revised, state).Should().BeTrue();
        original.CommitPreparedRunningState(revised, state);
        float[] actual = new float[192], expected = new float[192];
        revised.Render(context, state, 112, actual.Length, actual);
        original.Render(context, baseline, 112, expected.Length, expected);
        actual[0].Should().Be(expected[0],
            "the first publication frame uses exactly the outgoing playhead");
        actual.Should().OnlyContain(x => float.IsFinite(x));
        Enumerable.Range(65, 127).Max(i => Math.Abs(actual[i] - expected[i]))
            .Should().BeGreaterThan(0.05f,
                "the prepared Sample playheads must adopt the new phase offsets");
        worker.PreparedMultiConstituentCount.Should().Be(3);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void LiveScalarEditIsNotBlockedByNonconstantSampleOnsetHistory()
    {
        // A past pitch change invalidates the constant-rate shortcut.
        // It must not starve an independent, immediately compatible
        // authored Constant edit in the same parent Synth.
        ObjectId id = (ObjectId)7603U;
        float[] pcm = Enumerable.Range(0, 8192)
            .Select(i => (float)Math.Sin(2 * Math.PI * i / 91.0)).ToArray();
        SampleSound sample = new(new SampleDefinition(id, "Retuned PCM",
            new ExternalAssetReference("live-retuned.wav")),
            new MemorySampleData(48000, 1, pcm));
        SourceResolver resolver = new(id, sample);
        FmSynthGraph Graph(bool random, double value) => new([
            new FmSoundSourceNode(1, id, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 2, detune: 0,
                randomizePhase: random, cycleLength: 830,
                gainCompensation: true),
            new FmConstantNode(3, value),
            new FmOperatorNode(4, FmOperatorKind.Add, [2, 3]),
        ], 4);
        FmSynthSound original = new(Graph(false, 0.25), resolver,
            soundResolver: resolver, enableLiveMultiExpansion: true);
        FmSynthSound revised = new(Graph(true, 0.5), resolver,
            soundResolver: resolver, enableLiveMultiExpansion: true);
        RenderContext context = Mono();
        SoundState state = original.CreateState();
        SoundState baseline = original.CreateState();
        original.Render(context, state, 0, 80, new float[80]);
        original.Render(context, baseline, 0, 80, new float[80]);
        state.PitchTrajectory.SetMultiplier(80, 1.3);
        baseline.PitchTrajectory.SetMultiplier(80, 1.3);
        original.Render(context, state, 80, 48, new float[48]);
        original.Render(context, baseline, 80, 48, new float[48]);

        // A finite replay history has not been prepared. Publication
        // must retain the old onset and still accept the scalar change.
        original.TryPrepareRunningState(revised, state).Should().BeTrue(
            "a nonconstant Sample trajectory must not block unrelated live controls");
        original.CommitPreparedRunningState(revised, state);
        float[] actual = new float[128], expected = new float[128];
        revised.Render(context, state, 128, actual.Length, actual);
        original.Render(context, baseline, 128, expected.Length, expected);
        for (int i = 0; i < actual.Length; i++)
            (actual[i] - expected[i]).Should().BeApproximately(0.25f, 1e-6f);
    }

    [Test]
    public void SeededMultiOnsetCycleCalculationIsInvariantAcrossRenderSplits()
    {
        // The initial offset calculation is shared with upcoming prepared
        // re-phase candidates. Split rendering and cycle overrides must
        // keep every constituent's seeded phase and onset fade identical.
        foreach (double? cycleLength in new double?[] { null, 320.0 })
        foreach (bool randomized in new[] { false, true })
        {
            FmSynthGraph graph = new([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sine, 185),
                new FmMultiNode(2, 1, voiceCount: 3, detune: 0.12,
                    randomizePhase: randomized, cycleLength: cycleLength,
                    gainCompensation: true),
            ], 2);
            FmSynthSound sound = new(graph, new EmptyEnvelopes());
            SoundState continuous = sound.CreateState();
            SoundState split = sound.CreateState();
            continuous.SetNoiseSeed(0x1234567UL);
            split.SetNoiseSeed(0x1234567UL);
            continuous.PitchMultiplier = split.PitchMultiplier = 1.25;
            continuous.PlaybackSpeedMultiplier =
                split.PlaybackSpeedMultiplier = 0.75;
            RenderContext context = Mono();
            float[] expected = new float[512];
            float[] actual = new float[512];

            sound.Render(context, continuous, 0, expected.Length, expected);
            int offset = 0;
            foreach (int count in new[] { 1, 7, 40, 4, 93, 367 })
            {
                sound.Render(context, split, offset, count,
                    actual.AsSpan(offset, count));
                offset += count;
            }
            offset.Should().Be(expected.Length);
            actual.Should().Equal(expected,
                "Multi randomized onset and cycle override must not depend " +
                "on how the audio producer partitions the render blocks");
            actual.Should().OnlyContain(x => float.IsFinite(x));
        }
    }

    [Test]
    public void RapidSecondMultiEditStartsFromInterpolatedMixInsteadOfJumping()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        RenderContext context = Mono();
        FmSynthSound old = new(Graph(2), new EmptyEnvelopes(), reserveMultiHeadroom: true);
        SoundState voice = old.CreateState();
        old.Render(context, voice, 0, 80, new float[80]);
        FmSynthSound expanded = new(Graph(4), new EmptyEnvelopes());
        old.TryPrepareRunningState(expanded, voice).Should().BeTrue();
        old.CommitPreparedRunningState(expanded, voice);
        float[] halfFade = new float[32];
        expanded.Render(context, voice, 80, halfFade.Length, halfFade);

        FmSynthSound reduced = new(Graph(1), new EmptyEnvelopes());
        expanded.TryPrepareRunningState(reduced, voice).Should().BeTrue();
        expanded.CommitPreparedRunningState(reduced, voice);
        float[] second = new float[96];
        reduced.Render(context, voice, 112, second.Length, second);
        // At the edit boundary all four constituents start their new
        // transitions from their currently interpolated gains.
        // Frame 111 was just before the second edit, frame 112 is the
        // next sample. At 32/64 the old blend is exactly halfway.
        second[0].Should().BeApproximately(0.75f, 1e-6f,
            "the second edit must begin from the interpolated current mix");
        Math.Abs(second[0] - halfFade[31]).Should().BeLessThan(0.02f,
            "a rapid edit must not cause an audible gain discontinuity");
        second[80].Should().BeApproximately(0.25f, 1e-6f);
    }

    [Test]
    public void NestedMultiCanRetireAndReviveConstituentsWithoutRestartingParent()
    {
        FmSynthGraph Graph(int innerCount) => new([
            new FmConstantNode(1, 0.1),
            new FmMultiNode(2, 1, voiceCount: innerCount, detune: 0,
                gainCompensation: false, randomizePhase: false),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                gainCompensation: false, randomizePhase: false),
        ], 3);
        FmSynthSound old = new(Graph(3), new EmptyEnvelopes());
        SoundState state = old.CreateState();
        RenderContext context = Mono();
        float[] initial = new float[80];
        old.Render(context, state, 0, 80, initial);
        initial.Should().OnlyContain(x => Math.Abs(x - 0.6f) < 1e-6f);

        FmSynthSound reduced = new(Graph(1), new EmptyEnvelopes());
        old.TryPrepareRunningState(reduced, state).Should().BeTrue();
        old.CommitPreparedRunningState(reduced, state);
        float[] after = new float[100];
        reduced.Render(context, state, 80, after.Length, after);
        after[0].Should().BeApproximately(0.6f, 1e-6f);
        after[90].Should().BeApproximately(0.2f, 1e-6f,
            "both parent constituents must migrate their inner Multi state");

        FmSynthSound revived = new(Graph(3), new EmptyEnvelopes());
        reduced.TryPrepareRunningState(revived, state).Should().BeTrue();
        reduced.CommitPreparedRunningState(revived, state);
        float[] comeback = new float[110];
        revived.Render(context, state, 180, comeback.Length, comeback);
        comeback[0].Should().BeApproximately(0.2f, 1e-6f);
        comeback[90].Should().BeApproximately(0.6f, 1e-6f);
    }

    [Test]
    public void OffThreadMultiExpansionAddsMissingChildrenWithoutRewindingHeldState()
    {
        FmSynthGraph Graph(int count, double value) => new([
            new FmConstantNode(1, value),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        RenderContext context = Mono();
        FmSynthSound original = new(Graph(2, 0.25), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState held = original.CreateState();
        original.Render(context, held, 0, 101, new float[101]);
        FmSynthSound revised = new(Graph(9, 0.40), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);

        original.CanPrepareMultiExpansion(revised, held).Should().BeTrue();
        original.CanAdoptRunningState(revised, held).Should().BeFalse(
            "the new child invocations have not yet been created off-thread");
        original.TryPrepareRunningState(revised, held).Should().BeFalse();
        original.RequestMultiExpansionPreparation(revised, held).Should().BeTrue();
        float[] unchanged = new float[18];
        original.Render(context, held, 101, unchanged.Length, unchanged);
        unchanged.Should().OnlyContain(x => Math.Abs(x - 0.5f) < 1e-6f,
            "neither changed constant nor new children may publish prematurely");

        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        Assert.That(SpinWait.SpinUntil(() =>
            worker.CompletedPreparationCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        original.AdoptPreparedFirControls(held);
        original.CanAdoptRunningState(revised, held).Should().BeTrue();
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);
        float[] result = new float[130];
        revised.Render(context, held, 119, result.Length, result);
        result[0].Should().BeApproximately(0.8f, 1e-6f,
            "the two existing children immediately adopt the authored value");
        result[32].Should().BeInRange(0.8f, 3.6f);
        result[100].Should().BeApproximately(3.6f, 1e-5f,
            "seven additional constituents fade in, including three prepared by the worker");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void OffThreadMultiExpansionPublishesOnlyTheLatestPreparedRevision()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.125),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        FmSynthSound old = new(Graph(2), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState state = old.CreateState();
        old.Render(Mono(), state, 0, 100, new float[100]);
        FmSynthSound superseded = new(Graph(9), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        FmSynthSound current = new(Graph(11), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        old.RequestMultiExpansionPreparation(superseded, state).Should().BeTrue();
        old.RequestMultiExpansionPreparation(current, state).Should().BeTrue();

        using FirPreparationScheduler worker = new();
        old.QueuePendingFirControls(state, worker);
        Assert.That(SpinWait.SpinUntil(() =>
            worker.CompletedPreparationCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        old.AdoptPreparedFirControls(state);
        old.CanAdoptRunningState(superseded, state).Should().BeFalse(
            "superseded worker plans may not publish stale constituent counts");
        old.CanAdoptRunningState(current, state).Should().BeTrue();
        old.TryPrepareRunningState(current, state).Should().BeTrue();
        old.CommitPreparedRunningState(current, state);
        float[] rendered = new float[120];
        current.Render(Mono(), state, 100, 120, rendered);
        rendered[0].Should().BeApproximately(0.25f, 1e-6f);
        rendered[100].Should().BeApproximately(1.375f, 1e-6f);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void TwoIndependentMultiNodesExpandFromOneAtomicPreparedWorkerBatch()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
            new FmMultiNode(3, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
            new FmOperatorNode(4, FmOperatorKind.Add, [2, 3]),
        ], 4);
        FmSynthSound original = new(Graph(2), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState held = original.CreateState();
        RenderContext context = Mono();
        original.Render(context, held, 0, 100, new float[100]);
        FmSynthSound revision = new(Graph(9), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        original.CanPrepareMultiExpansion(revision, held).Should().BeTrue();
        original.CanAdoptRunningState(revision, held).Should().BeFalse();
        original.RequestMultiExpansionPreparation(revision, held).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        original.QueuePendingFirControls(held, worker);
        Assert.That(SpinWait.SpinUntil(() =>
            worker.CompletedPreparationCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True,
            "one invocation-level worker plan must prepare both enlarged arrays");
        original.AdoptPreparedFirControls(held);
        original.TryPrepareRunningState(revision, held).Should().BeTrue();
        original.CommitPreparedRunningState(revision, held);
        float[] after = new float[120];
        revision.Render(context, held, 100, 120, after);
        after[0].Should().BeApproximately(1.0f, 1e-6f);
        after[100].Should().BeApproximately(4.5f, 1e-5f,
            "both expanded Multi nodes must commit at the same audio boundary");
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void OffThreadMultiExpansionRecursesIntoNestedConstituentStates()
    {
        FmSynthGraph Graph(int innerCount) => new([
            new FmConstantNode(1, 0.1),
            new FmMultiNode(2, 1, voiceCount: innerCount, detune: 0,
                gainCompensation: false, randomizePhase: false),
            new FmMultiNode(3, 2, voiceCount: 2, detune: 0,
                gainCompensation: false, randomizePhase: false),
        ], 3);
        RenderContext context = Mono();
        FmSynthSound old = new(Graph(3), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState state = old.CreateState();
        old.Render(context, state, 0, 90, new float[90]);
        FmSynthSound expanded = new(Graph(9), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        old.CanPrepareMultiExpansion(expanded, state).Should().BeTrue();
        old.CanAdoptRunningState(expanded, state).Should().BeFalse();
        old.RequestMultiExpansionPreparation(expanded, state).Should().BeTrue();
        using FirPreparationScheduler worker = new();
        old.QueuePendingFirControls(state, worker);
        Assert.That(SpinWait.SpinUntil(() =>
            worker.CompletedPreparationCount >= 2,
            TimeSpan.FromSeconds(5)), Is.True,
            "nested Multi graphs have only two real outer voices, each with a separate inner state");
        old.AdoptPreparedFirControls(state);
        old.TryPrepareRunningState(expanded, state).Should().BeTrue();
        old.CommitPreparedRunningState(expanded, state);
        float[] result = new float[120];
        expanded.Render(context, state, 90, 120, result);
        result[0].Should().BeApproximately(0.6f, 1e-6f);
        result[100].Should().BeApproximately(1.8f, 1e-5f);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void CancellationInsideOneNestedMultiChildStopsBeforePreparingSiblingStates()
    {
        // The outer Multi's second child would itself allocate 60 inner
        // constituents. The old worker checked cancellation only after
        // completing all 60; the child-construction path must now check
        // between each nested invocation as well.
        FmSynthGraph Graph(int outerCount) => new([
            new FmConstantNode(1, 0.01),
            new FmMultiNode(2, 1, voiceCount: 60, detune: 0,
                randomizePhase: false, gainCompensation: false),
            new FmMultiNode(3, 2, voiceCount: outerCount, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 3);
        FmSynthSound old = new(Graph(1), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        FmSynthSound expanded = new(Graph(2), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState voice = old.CreateState();
        old.Render(Mono(), voice, 0, 40, new float[40]);
        old.RequestMultiExpansionPreparation(expanded, voice).Should().BeTrue();

        using ManualResetEventSlim innerConstructed = new(false);
        using ManualResetEventSlim resume = new(false);
        using FirPreparationScheduler worker = new();
        int nestedChildren = 0;
        worker.AfterNestedMultiChildStateCreated = () =>
        {
            if (Interlocked.Increment(ref nestedChildren) == 1)
            {
                innerConstructed.Set();
                resume.Wait(TimeSpan.FromSeconds(10));
            }
        };
        try
        {
            old.QueuePendingFirControls(voice, worker);
            Assert.That(innerConstructed.Wait(TimeSpan.FromSeconds(5)),
                Is.True, "the worker must enter nested state construction");
            old.CancelPendingMultiExpansion(voice);
        }
        finally
        {
            resume.Set();
        }
        Assert.That(SpinWait.SpinUntil(() =>
            worker.CancelledMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        Volatile.Read(ref nestedChildren).Should().Be(1,
            "cancellation must propagate inside an expensive nested child constructor");
        worker.PreparedMultiConstituentCount.Should().Be(0);
        old.CanAdoptRunningState(expanded, voice).Should().BeFalse();
        float[] pcm = new float[80];
        old.Render(Mono(), voice, 40, 80, pcm);
        pcm.Should().OnlyContain(x => Math.Abs(x - 0.6f) < 1e-5f);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void CancelledInFlightMultiExpansionStopsAtNextChildBoundary()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.25),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        FmSynthSound sound = new(Graph(2), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        FmSynthSound expanded = new(Graph(120), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState state = sound.CreateState();
        RenderContext context = Mono();
        sound.Render(context, state, 0, 60, new float[60]);
        sound.RequestMultiExpansionPreparation(expanded, state).Should().BeTrue();

        using ManualResetEventSlim firstChild = new(false);
        using ManualResetEventSlim release = new(false);
        using FirPreparationScheduler worker = new();
        int built = 0;
        worker.AfterMultiConstituentPrepared = () =>
        {
            if (Interlocked.Increment(ref built) == 1)
            {
                firstChild.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        try
        {
            sound.QueuePendingFirControls(state, worker);
            Assert.That(firstChild.Wait(TimeSpan.FromSeconds(5)), Is.True);
            sound.CancelPendingMultiExpansion(state);
        }
        finally
        {
            release.Set();
        }
        Assert.That(SpinWait.SpinUntil(() =>
            worker.CancelledMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        Volatile.Read(ref built).Should().Be(1,
            "a canceled job must not keep building more child states");
        worker.PreparedMultiConstituentCount.Should().Be(0);
        worker.AbandonedMultiConstituentCount.Should().Be(1);
        worker.AbandonedMultiPreparationAllocatedBytes.Should().BeGreaterThan(0);
        sound.CanAdoptRunningState(expanded, state).Should().BeFalse();
        float[] oldPcm = new float[100];
        sound.Render(context, state, 60, oldPcm.Length, oldPcm);
        oldPcm.Should().OnlyContain(x => Math.Abs(x - 0.5f) < 1e-6f);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void SupersededInFlightMultiExpansionPreparesOnlyLatestRevision()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.125),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        FmSynthSound sound = new(Graph(2), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState state = sound.CreateState();
        FmSynthSound stale = new(Graph(120), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        FmSynthSound latest = new(Graph(9), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        sound.RequestMultiExpansionPreparation(stale, state).Should().BeTrue();

        using ManualResetEventSlim firstChild = new(false);
        using ManualResetEventSlim release = new(false);
        using FirPreparationScheduler worker = new();
        int built = 0;
        worker.AfterMultiConstituentPrepared = () =>
        {
            if (Interlocked.Increment(ref built) == 1)
            {
                firstChild.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        try
        {
            sound.QueuePendingFirControls(state, worker);
            Assert.That(firstChild.Wait(TimeSpan.FromSeconds(5)), Is.True);
            sound.RequestMultiExpansionPreparation(latest, state).Should().BeTrue();
        }
        finally
        {
            release.Set();
        }
        Assert.That(SpinWait.SpinUntil(() =>
            worker.CompletedMultiExpansionCount >= 1
            && worker.CancelledMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)), Is.True);
        worker.AbandonedMultiConstituentCount.Should().Be(1);
        worker.PreparedMultiConstituentCount.Should().Be(3,
            "only three missing children are needed for the latest nine-voice revision");
        sound.AdoptPreparedFirControls(state);
        sound.CanAdoptRunningState(stale, state).Should().BeFalse();
        sound.TryPrepareRunningState(latest, state).Should().BeTrue();
        sound.CommitPreparedRunningState(latest, state);
        float[] result = new float[100];
        latest.Render(Mono(), state, 0, result.Length, result);
        result[99].Should().BeApproximately(1.125f, 1e-5f);
        worker.Fault.Should().BeNull();
    }

    [Test]
    public void MultiExpansionWorkerQueueIsBoundedAndRetriesSurvivingVoices()
    {
        FmSynthGraph Graph(int count) => new([
            new FmConstantNode(1, 0.0625),
            new FmMultiNode(2, 1, voiceCount: count, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        FmSynthSound sound = new(Graph(2), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        FmSynthSound expanded = new(Graph(48), new EmptyEnvelopes(),
            reserveMultiHeadroom: true);
        SoundState[] states = Enumerable.Range(0, 96)
            .Select(i =>
            {
                SoundState state = sound.CreateState();
                state.SetNoiseSeed((ulong)(i + 1));
                return state;
            }).ToArray();

        using ManualResetEventSlim workerEntered = new(false);
        using ManualResetEventSlim release = new(false);
        using FirPreparationScheduler worker = new();
        int gateCount = 0;
        worker.BeforePrepareWorkItem = () =>
        {
            if (Interlocked.Increment(ref gateCount) == 1)
            {
                workerEntered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        long started = Stopwatch.GetTimestamp();
        try
        {
            foreach (SoundState state in states)
                sound.RequestMultiExpansionPreparation(expanded, state)
                    .Should().BeTrue();
            sound.QueuePendingFirControls(states[0], worker);
            Assert.That(workerEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            for (int i = 1; i < states.Length; i++)
                sound.QueuePendingFirControls(states[i], worker);
            worker.PeakQueuedWorkItems.Should()
                .BeLessThanOrEqualTo(FirPreparationScheduler.MaximumQueuedMultiExpansions,
                    "the PCM producer cannot enqueue an unbounded expansion backlog");
            // Retire 84 of the voices before the worker is allowed to run.
            // Their queued slots must become cheap cancellations. Refused
            // admissions must not silently discard the surviving requests.
            for (int i = 12; i < states.Length; i++)
                sound.CancelPendingMultiExpansion(states[i]);
        }
        finally
        {
            release.Set();
        }
        Assert.That(SpinWait.SpinUntil(() =>
        {
            for (int i = 0; i < 12; i++)
                sound.QueuePendingFirControls(states[i], worker);
            return worker.CompletedMultiExpansionCount >= 12;
        }, TimeSpan.FromSeconds(10)), Is.True,
            "all twelve live voices must eventually prepare despite initial queue saturation");

        for (int i = 0; i < 12; i++)
        {
            sound.AdoptPreparedFirControls(states[i]);
            sound.TryPrepareRunningState(expanded, states[i]).Should().BeTrue();
            sound.CommitPreparedRunningState(expanded, states[i]);
        }
        worker.CancelledMultiExpansionCount.Should().BeGreaterThan(0,
            "retired queued voices must be discarded without new child allocation");
        worker.PreparedMultiConstituentCount.Should().Be(12 * (48 - 6));
        worker.MultiPreparationAllocatedBytes.Should().BeGreaterThan(0);
        worker.PeakMultiPreparationAllocatedBytes.Should().BeGreaterThan(0);
        worker.Fault.Should().BeNull();

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        TestContext.Progress.WriteLine(
            $"Multi stress: 96 requests, 84 retired, " +
            $"completed={worker.CompletedMultiExpansionCount}, " +
            $"cancelled={worker.CancelledMultiExpansionCount}, " +
            $"peak queue={worker.PeakQueuedWorkItems}, " +
            $"prepared children={worker.PreparedMultiConstituentCount}, " +
            $"prep bytes={worker.MultiPreparationAllocatedBytes}, " +
            $"peak/job={worker.PeakMultiPreparationAllocatedBytes}, " +
            $"worker prep ms={worker.MultiPreparationElapsedTime.TotalMilliseconds:F2}, " +
            $"test elapsed ms={elapsed.TotalMilliseconds:F2}, " +
            $"process CPU ms={cpu.TotalMilliseconds:F2}");
    }

    [Test]
    public void UnpreparedMultiGrowthCannotPartiallyModifyTheExistingVoice()
    {
        FmSynthGraph Graph(int count, double gain) => new([
            new FmConstantNode(1, gain),
            new FmMultiNode(2, 1, detune: 0, voiceCount: count,
                randomizePhase: false, gainCompensation: false),
        ], 2);
        FmSynthSound old = new(Graph(2, 0.25), new EmptyEnvelopes(), reserveMultiHeadroom: true);
        SoundState voice = old.CreateState();
        RenderContext context = Mono();
        old.Render(context, voice, 0, 80, new float[80]);
        FmSynthSound tooLarge = new(Graph(7, 0.8), new EmptyEnvelopes());
        old.CanAdoptRunningState(tooLarge, voice).Should().BeFalse(
            "two existing and four dormant constituents cannot grow to seven");
        old.TryPrepareRunningState(tooLarge, voice).Should().BeFalse();
        float[] unchanged = new float[80];
        old.Render(context, voice, 80, unchanged.Length, unchanged);
        unchanged.Should().OnlyContain(x => Math.Abs(x - 0.5f) < 1e-6f);
    }

    private static float[] Render(FmSynthGraph graph, int count, ulong seed = 1)
    {
        FmSynthSound sound = new(graph, new EmptyEnvelopes());
        SoundState state = sound.CreateState();
        state.SetNoiseSeed(seed);
        float[] samples = new float[count];
        sound.Render(Mono(), state, 0, count, samples);
        return samples;
    }

    private static RenderContext Mono() => new(new RenderConfiguration(48000,
    [
        new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
    ]));

    private sealed class TenFrameRelease : IEnvelopeCurveResolver, IEnvelopeCurve
    {
        private readonly ObjectId _id;
        public TenFrameRelease(ObjectId id) => _id = id;
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = id == _id ? this : null;
            return curve is not null;
        }
        public double GetValue(long activeFrame, int sampleRate,
            long? noteOffActiveFrame)
            => noteOffActiveFrame.HasValue
                && activeFrame >= noteOffActiveFrame.Value + 10 ? 0 : 1;
        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long noteOffActiveFrame, int sampleRate) => noteOffActiveFrame + 10;
    }

    // Mimics the production frozen-song provider: distinct adapters can
    // advertise identical immutable SamplePcmData across revisions.
    private sealed class FrozenPcmData(SamplePcmData pcm)
        : ISampleData, IImmutableSamplePcmIdentity
    {
        public SamplePcmData ImmutablePcm { get; } = pcm;
        public int SampleRate => ImmutablePcm.SampleRate;
        public int ChannelCount => ImmutablePcm.ChannelCount;
        public long FrameCount => ImmutablePcm.FrameCount;
        public float GetSample(long frame, int channel)
            => ImmutablePcm.GetSample(frame, channel);
    }

    private sealed class SourceResolver : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly ObjectId _id;
        private readonly ISound _sound;
        public SourceResolver(ObjectId id, ISound sound)
        {
            _id = id; _sound = sound;
        }
        public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
        {
            sound = sourceId == _id ? _sound : null;
            return sound is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }

    private sealed class EmptyEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}

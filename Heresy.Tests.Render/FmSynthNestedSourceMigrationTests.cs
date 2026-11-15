using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
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
public sealed class FmSynthNestedSourceMigrationTests
{
    private static readonly ObjectId ChildId = (ObjectId)99810U;
    private static readonly ObjectId ParentId = (ObjectId)99811U;
    private static readonly ObjectId ReferenceSampleId = (ObjectId)99812U;
    private static readonly ObjectId OtherChildId = (ObjectId)99814U;

    [Test]
    public void CompatibleNestedConstantEditInsideMultiPreservesParentFilterHistory()
    {
        const int start = 128, fadeFrames = 4800;
        Registry resolver = new();
        FilterInstance filter = OnePole();
        resolver.Set(ChildId, ConstantChild(0.20));
        FmSynthSound original = MultiFilteredParent(ChildId, filter, resolver);
        resolver.Set(ChildId, ConstantChild(0.60));
        FmSynthSound revised = MultiFilteredParent(ChildId, filter, resolver);

        RenderContext context = Mono();
        SoundState held = original.CreateState();
        original.Render(context, held, 0, start, new float[start]);
        original.CanAdoptRunningState(revised, held).Should().BeTrue();
        original.TryPrepareRunningState(revised, held).Should().BeTrue();
        original.CommitPreparedRunningState(revised, held);

        // Independent reference: the changed child contributes a 100 ms
        // smoothstep, while three equal Multi constituents normalize to one
        // continuous input for the SAME unrestarted parent OnePole.
        float[] baked = new float[start + fadeFrames + 96];
        for (int frame = 0; frame < baked.Length; frame++)
        {
            double t = Math.Clamp((double)(frame - start) / fadeFrames, 0, 1);
            double w = t * t * (3 - 2 * t);
            baked[frame] = frame < start ? 0.20f
                : (float)(0.20 * (1 - w) + 0.60 * w);
        }
        resolver.Set(ReferenceSampleId, new SampleSound(
            new SampleDefinition(ReferenceSampleId, "Nested Synth oracle",
                new ExternalAssetReference("nested-synth-reference.wav")),
            new MemorySampleData(48000, 1, baked)));
        FmSynthSound reference = MultiFilteredParent(
            ReferenceSampleId, filter, resolver);
        float[] expected = new float[baked.Length];
        reference.Render(context, reference.CreateState(), 0,
            expected.Length, expected);

        float[] actual = new float[baked.Length - start];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        revised.Render(context, held, start, actual.Length, actual);
        (GC.GetAllocatedBytesForCurrentThread() - allocated).Should().Be(0,
            "recursive Source/Multi/Filter adoption must allocate no PCM memory");
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[start + i], 5e-5f,
                $"every nested child must migrate without restarting the parent filter at {start + i}");
    }

    [Test]
    public void IncompatibleNestedChildRetainsSoundAndMarksOnlyHeldParentOlder()
    {
        Registry resolver = new();
        FilterInstance filter = OnePole();
        resolver.Set(ChildId, ConstantChild(0.20));
        FmSynthSound original = FilteredParent(ChildId, filter, resolver);

        resolver.Set(ChildId, StructuralChild(0.80));
        FmSynthSound revised = FilteredParent(ChildId, filter, resolver);
        resolver.Set(ParentId, original);
        PlaybackSession session = new(Mono(),
            new NoteScheduleBuilder().Freeze(), resolver);
        PlaybackSession uninterrupted = new(Mono(),
            new NoteScheduleBuilder().Freeze(), resolver);
        session.ApplyLiveEvent(ChannelTarget.Virtual(1),
            [new StartNoteCommand(ParentId)]);
        uninterrupted.ApplyLiveEvent(ChannelTarget.Virtual(1),
            [new StartNoteCommand(ParentId)]);
        session.Render(0, 128, new float[128]);
        uninterrupted.Render(0, 128, new float[128]);

        session.AdoptLiveSynthParameters(ParentId, original, revised);
        session.CountOlderAudioVoices().Should().Be(1,
            "an incompatible referenced child is still old audio even when the parent keeps its own Filter state");

        float[] expected = new float[128];
        float[] actual = new float[128];
        uninterrupted.Render(128, expected.Length, expected);
        session.Render(128, actual.Length, actual);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[i], 5e-5f,
                "the retained child must keep sounding with its old definition");

        resolver.Set(ParentId, revised);
        session.ApplyLiveEvent(ChannelTarget.Virtual(2),
            [new StartNoteCommand(ParentId)]);
        session.Render(256, 256, new float[256]);
        session.CountOlderAudioVoices().Should().Be(1,
            "a new note uses the current child, while the old note stays marked");

        session.ApplyLiveEvent(ChannelTarget.Virtual(1),
            [new NoteCutCommand()]);
        session.CountOlderAudioVoices().Should().Be(0,
            "only retirement of the incompatible note clears the warning");
    }


    [Test]
    public void SelectingCompatibleSynthSourceIdKeepsIndependentMultiAndFilterHistories()
    {
        const int firstAge = 211, secondAge = 67, continuation = 4928;
        Registry resolver = new();
        FilterInstance filter = OnePole();
        FmSynthSound firstChild = OscillatorChild(193);
        FmSynthSound selectedChild = OscillatorChild(431);
        resolver.Set(ChildId, firstChild);
        FmSynthSound original = MultiFilteredParent(ChildId, filter, resolver);
        resolver.Set(OtherChildId, selectedChild);
        FmSynthSound selected = MultiFilteredParent(OtherChildId, filter, resolver);

        // Oracle: a compatible edit of the same child definition, without
        // changing the parent's SourceId, must produce identical PCM.
        resolver.Set(ChildId, selectedChild);
        FmSynthSound sameIdEdit = MultiFilteredParent(ChildId, filter, resolver);
        RenderContext context = Mono();
        SoundState older = original.CreateState();
        SoundState younger = original.CreateState();
        SoundState olderReference = original.CreateState();
        SoundState youngerReference = original.CreateState();
        SoundState uninterrupted = original.CreateState();

        original.Render(context, older, 0, firstAge, new float[firstAge]);
        original.Render(context, olderReference, 0, firstAge, new float[firstAge]);
        original.Render(context, uninterrupted, 0, firstAge, new float[firstAge]);
        original.Render(context, younger, 0, secondAge, new float[secondAge]);
        original.Render(context, youngerReference, 0, secondAge, new float[secondAge]);

        foreach (SoundState held in new[] { older, younger })
        {
            original.CanAdoptRunningState(selected, held).Should().BeTrue(
                "a new SourceId for a compatible Synth may reuse that voice's invocation");
            original.TryPrepareRunningState(selected, held).Should().BeTrue();
            original.CommitPreparedRunningState(selected, held);
        }
        foreach (SoundState held in new[] { olderReference, youngerReference })
        {
            original.TryPrepareRunningState(sameIdEdit, held).Should().BeTrue();
            original.CommitPreparedRunningState(sameIdEdit, held);
        }

        float[] actualOlder = new float[continuation];
        float[] actualYounger = new float[continuation];
        float[] expectedOlder = new float[continuation];
        float[] expectedYounger = new float[continuation];
        float[] oldGeneration = new float[continuation];
        sameIdEdit.Render(context, olderReference, firstAge, continuation, expectedOlder);
        sameIdEdit.Render(context, youngerReference, secondAge, continuation, expectedYounger);
        original.Render(context, uninterrupted, firstAge, continuation, oldGeneration);

        long bytes = GC.GetAllocatedBytesForCurrentThread();
        selected.Render(context, older, firstAge, continuation, actualOlder);
        selected.Render(context, younger, secondAge, continuation, actualYounger);
        (GC.GetAllocatedBytesForCurrentThread() - bytes).Should().Be(0,
            "neither compatible SourceId selection may rebuild an invocation on PCM");
        for (int i = 0; i < continuation; i++)
        {
            actualOlder[i].Should().BeApproximately(expectedOlder[i], 5e-5f,
                $"the older note's nested oscillator/Multi/filter state at {i}");
            actualYounger[i].Should().BeApproximately(expectedYounger[i], 5e-5f,
                $"the younger note's independent nested state at {i}");
        }
        actualOlder.Zip(actualYounger, (a, b) => Math.Abs(a - b))
            .Max().Should().BeGreaterThan(0.001f,
                "different note ages must not share a nested oscillator phase or filter history");
        actualOlder.Zip(oldGeneration, (a, b) => Math.Abs(a - b))
            .Skip(4800).Max().Should().BeGreaterThan(0.005f,
                "SourceId selection must actually retune the child rather than retain old audio");
    }

    [Test]
    public void CompatibleSynthSourceIdSelectionKeepsTwoHeldSiblingVoicesCurrent()
    {
        Registry sounds = new();
        Registry referenceSounds = new();
        FilterInstance filter = OnePole();
        FmSynthSound firstChild = OscillatorChild(193);
        FmSynthSound selectedChild = OscillatorChild(431);
        sounds.Set(ChildId, firstChild);
        referenceSounds.Set(ChildId, firstChild);
        FmSynthSound original = MultiFilteredParent(ChildId, filter, sounds);
        FmSynthSound referenceOriginal = MultiFilteredParent(
            ChildId, filter, referenceSounds);
        sounds.Set(OtherChildId, selectedChild);
        referenceSounds.Set(ChildId, selectedChild);
        FmSynthSound selected = MultiFilteredParent(OtherChildId, filter, sounds);
        FmSynthSound equivalent = MultiFilteredParent(
            ChildId, filter, referenceSounds);
        sounds.Set(ParentId, original);
        referenceSounds.Set(ParentId, referenceOriginal);
        PlaybackSession session = new(Mono(),
            new NoteScheduleBuilder().Freeze(), sounds);
        PlaybackSession reference = new(Mono(),
            new NoteScheduleBuilder().Freeze(), referenceSounds);

        foreach (PlaybackSession playback in new[] { session, reference })
            playback.ApplyLiveEvent(ChannelTarget.Virtual(1),
                [new StartNoteCommand(ParentId)]);
        float[] prelude = new float[211];
        session.Render(0, 211, prelude);
        reference.Render(0, 211, new float[211]);
        foreach (PlaybackSession playback in new[] { session, reference })
            playback.ApplyLiveEvent(ChannelTarget.Virtual(2),
                [new StartNoteCommand(ParentId)]);
        session.Render(211, 79, new float[79]);
        reference.Render(211, 79, new float[79]);

        session.AdoptLiveSynthParameters(ParentId, original, selected);
        reference.AdoptLiveSynthParameters(ParentId, referenceOriginal, equivalent);
        session.CountOlderAudioVoices().Should().Be(0,
            "both held siblings must successfully adopt a compatible child Synth selection");
        reference.CountOlderAudioVoices().Should().Be(0);

        sounds.Set(ParentId, selected);
        referenceSounds.Set(ParentId, equivalent);
        session.ApplyLiveEvent(ChannelTarget.Virtual(3),
            [new StartNoteCommand(ParentId)]);
        reference.ApplyLiveEvent(ChannelTarget.Virtual(3),
            [new StartNoteCommand(ParentId)]);

        float[] actual = new float[5024];
        float[] expected = new float[actual.Length];
        session.Render(290, actual.Length, actual);
        reference.Render(290, expected.Length, expected);
        for (int i = 0; i < actual.Length; i++)
            actual[i].Should().BeApproximately(expected[i], 7e-5f,
                $"SourceId selection must not mix the three independent note histories at {i}");
        session.CountOlderAudioVoices().Should().Be(0);

        session.ApplyLiveEvent(ChannelTarget.Virtual(1), [new NoteCutCommand()]);
        reference.ApplyLiveEvent(ChannelTarget.Virtual(1), [new NoteCutCommand()]);
        float[] remaining = new float[256];
        float[] remainingReference = new float[256];
        session.Render(5314, 256, remaining);
        reference.Render(5314, 256, remainingReference);
        for (int i = 0; i < remaining.Length; i++)
            remaining[i].Should().BeApproximately(remainingReference[i], 7e-5f,
                "retiring one sibling must not change another child's selected Synth");
        session.CountOlderAudioVoices().Should().Be(0);
    }


    [TestCase(false)]
    [TestCase(true)]
    public void SupersededNestedMultiWorkerCannotApplyObsoleteParentRevision(
        bool firstWorkerWasReady)
    {
        const int firstFrames = 128, waitingFrames = 64, outputFrames = 5000;
        Registry sounds = new();
        FilterInstance originalFilter = OnePole();
        sounds.Set(ChildId, MultiConstantChild(1, 0.10));
        FmSynthSound original = FilteredParent(ChildId, originalFilter, sounds);
        sounds.Set(ChildId, MultiConstantChild(4, 0.14));
        FmSynthSound obsolete = FilteredParent(ChildId, OnePole(1700), sounds);
        sounds.Set(ChildId, MultiConstantChild(3, 0.18));
        FmSynthSound latest = FilteredParent(ChildId, OnePole(2600), sounds);
        sounds.Set(ParentId, original);

        // Two independently running notes will reach the same selected
        // revision: the primary sees an obsolete preparation first; its
        // oracle sees ONLY the latest accepted candidate at the same frame.
        PlaybackSession actualSession = new(Mono(),
            new NoteScheduleBuilder().Freeze(), sounds);
        PlaybackSession referenceSession = new(Mono(),
            new NoteScheduleBuilder().Freeze(), sounds);
        foreach (PlaybackSession session in new[] { actualSession, referenceSession })
            session.ApplyLiveEvent(ChannelTarget.Virtual(1),
                [new StartNoteCommand(ParentId)]);
        actualSession.Render(0, firstFrames, new float[firstFrames]);
        referenceSession.Render(0, firstFrames, new float[firstFrames]);

        using ManualResetEventSlim firstStarted = new(false);
        using ManualResetEventSlim allowFirstFinish = new(false);
        using FirPreparationScheduler worker = new();
        using FirPreparationScheduler referenceWorker = new();
        int gateCount = 0;
        worker.AfterMultiConstituentPrepared = () =>
        {
            if (!firstWorkerWasReady && Interlocked.Exchange(ref gateCount, 1) == 0)
            {
                firstStarted.Set();
                // Never strand the scheduler if an assertion fails.
                allowFirstFinish.Wait(TimeSpan.FromSeconds(10));
            }
        };

        actualSession.AdoptLiveSynthParameters(ParentId, original, obsolete);
        actualSession.CountOlderAudioVoices().Should().Be(0,
            "worker preparation is not a definite refusal");
        actualSession.QueuePendingFirControls(worker);
        if (firstWorkerWasReady)
            SpinWait.SpinUntil(() => worker.CompletedMultiExpansionCount >= 1,
                TimeSpan.FromSeconds(5)).Should().BeTrue(
                "the obsolete candidate should be ready but not yet published");
        else
            firstStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
                "the obsolete nested worker must be in-flight before supersession");

        float[] waitingActual = new float[waitingFrames];
        float[] waitingReference = new float[waitingFrames];
        actualSession.Render(firstFrames, waitingFrames, waitingActual);
        referenceSession.Render(firstFrames, waitingFrames, waitingReference);
        waitingActual.Should().Equal(waitingReference,
            "a pending revision must not change the sounding parent Filter or child");

        actualSession.AdoptLiveSynthParameters(ParentId, obsolete, latest);
        referenceSession.AdoptLiveSynthParameters(ParentId, original, latest);
        actualSession.CountOlderAudioVoices().Should().Be(0,
            "superseding a pending worker must not mark the still-migratable voice old");
        referenceSession.CountOlderAudioVoices().Should().Be(0);
        actualSession.QueuePendingFirControls(worker);
        referenceSession.QueuePendingFirControls(referenceWorker);
        allowFirstFinish.Set();

        int expectedCompleted = firstWorkerWasReady ? 2 : 1;
        SpinWait.SpinUntil(() =>
            worker.CompletedMultiExpansionCount >= expectedCompleted
            && referenceWorker.CompletedMultiExpansionCount >= 1,
            TimeSpan.FromSeconds(5)).Should().BeTrue(
                "only the latest nested Multi expansion should be prepared for adoption");
        actualSession.AdoptPreparedFirControls();
        referenceSession.AdoptPreparedFirControls();
        actualSession.CountOlderAudioVoices().Should().Be(0);
        referenceSession.CountOlderAudioVoices().Should().Be(0);

        // New notes use the latest frozen parent, while the held notes keep
        // their own accumulated child/Filter histories after publication.
        sounds.Set(ParentId, latest);
        foreach (PlaybackSession session in new[] { actualSession, referenceSession })
            session.ApplyLiveEvent(ChannelTarget.Virtual(2),
                [new StartNoteCommand(ParentId)]);
        float[] actual = new float[outputFrames];
        float[] expected = new float[outputFrames];
        actualSession.Render(firstFrames + waitingFrames, outputFrames, actual);
        referenceSession.Render(firstFrames + waitingFrames, outputFrames, expected);
        for (int i = 0; i < outputFrames; i++)
            actual[i].Should().BeApproximately(expected[i], 7e-5f,
                $"no obsolete child, interrupted Filter history, or partial parent adoption at {i}");

        actual[^1].Should().BeGreaterThan(0.30f,
            "the 3-voice latest child, not the obsolete 4-voice proposal, must sound");
        actualSession.CountOlderAudioVoices().Should().Be(0);
        referenceSession.CountOlderAudioVoices().Should().Be(0);
        worker.Fault.Should().BeNull();
        referenceWorker.Fault.Should().BeNull();
        if (!firstWorkerWasReady)
            worker.CancelledMultiExpansionCount.Should().BeGreaterThan(0,
                "the in-flight obsolete child preparation must be discarded");
    }

    private static FmSynthSound MultiConstantChild(int voices, double value)
        => new(new FmSynthGraph([
            new FmConstantNode(1, value),
            new FmMultiNode(2, 1, voiceCount: voices, detune: 0,
                randomizePhase: false, gainCompensation: false),
        ], 2), new NoEnvelopes(), enableLiveMultiExpansion: true);

    private static FmSynthSound OscillatorChild(double frequency)
        => new(new FmSynthGraph([
            new FmOscillatorNode(1, FmOscillatorWaveform.Sine,
                frequency, minimum: -0.08, maximum: 0.08),
        ], 1), new NoEnvelopes(), enableLiveMultiExpansion: true);

    private static FmSynthSound ConstantChild(double value)
        => new(new FmSynthGraph([new FmConstantNode(1, value)], 1),
            new NoEnvelopes(), enableLiveMultiExpansion: true);

    private static FmSynthSound StructuralChild(double value)
        => new(new FmSynthGraph([
            new FmConstantNode(1, value),
            new FmConstantNode(2, 0),
            new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
        ], 3), new NoEnvelopes(), enableLiveMultiExpansion: true);

    private static FmSynthSound MultiFilteredParent(
        ObjectId sourceId, FilterInstance filter, Registry resolver)
        => new(new FmSynthGraph([
            new FmSoundSourceNode(1, sourceId, FmMultiNode.NeutralFrequencyHz),
            new FmMultiNode(2, 1, voiceCount: 3, detune: 0,
                randomizePhase: false, gainCompensation: true),
            new FmFilterNode(3, 2, filter, tailSeconds: 0.003),
        ], 3), new NoEnvelopes(), soundResolver: resolver,
            enableLiveMultiExpansion: true);

    private static FmSynthSound FilteredParent(
        ObjectId sourceId, FilterInstance filter, Registry resolver)
        => new(new FmSynthGraph([
            new FmSoundSourceNode(1, sourceId, FmMultiNode.NeutralFrequencyHz),
            new FmFilterNode(2, 1, filter, tailSeconds: 0.003),
        ], 2), new NoEnvelopes(), soundResolver: resolver,
            enableLiveMultiExpansion: true);

    private static FilterInstance OnePole(double cutoffHz = 1200)
    {
        FilterInstance filter = new(new FilterDefinition(
            (ObjectId)99813U, "Nested Synth parent pole"));
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(cutoffHz));
        return filter;
    }

    private static RenderContext Mono() => new(
        new RenderConfiguration(48000, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));

    private sealed class Registry : ISoundResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        public void Set(ObjectId id, ISound sound) => _sounds[id] = sound;
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            if (!mixdown)
                return _sounds.TryGetValue(id, out sound);
            sound = null;
            return false;
        }
    }

    private sealed class NoEnvelopes : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = null;
            return false;
        }
    }
}

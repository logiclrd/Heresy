using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AwesomeAssertions;
using Heresy.Core.Assets;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
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
        SoundState uninterrupted = original.CreateState();

        resolver.Set(ChildId, StructuralChild(0.80));
        FmSynthSound revised = FilteredParent(ChildId, filter, resolver);
        resolver.Set(ParentId, original);
        PlaybackSession session = new(Mono(),
            new NoteScheduleBuilder().Freeze(), resolver);
        session.ApplyLiveEvent(ChannelTarget.Virtual(1),
            [new StartNoteCommand(ParentId)]);
        session.Render(0, 128, new float[128]);
        original.Render(Mono(), uninterrupted, 0, 128, new float[128]);

        session.AdoptLiveSynthParameters(ParentId, original, revised);
        session.CountOlderAudioVoices().Should().Be(1,
            "an incompatible referenced child is still old audio even when the parent keeps its own Filter state");

        float[] expected = new float[128];
        float[] actual = new float[128];
        original.Render(Mono(), uninterrupted, 128, expected.Length, expected);
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

    private static FilterInstance OnePole()
    {
        FilterInstance filter = new(new FilterDefinition(
            (ObjectId)99813U, "Nested Synth parent pole"));
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(1200));
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

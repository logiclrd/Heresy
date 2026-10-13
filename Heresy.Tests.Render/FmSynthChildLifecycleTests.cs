using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthChildLifecycleTests
{
    private static readonly double C4 = 440 * Math.Pow(2, -9.0 / 12.0);
    private static readonly ObjectId SampleId = (ObjectId)50U;
    private static readonly ObjectId InstrumentId = (ObjectId)51U;
    private static readonly ObjectId EnvelopeId = (ObjectId)52U;

    [Test]
    public void ModulatedSampleNaturalEndBecomesFiniteOnlyAfterSourcePositionReachesEof()
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([1, 2, 3, 4], 8));
        FmSynthSound synth = MakeSynth(registry, [
            new FmSoundSourceNode(0, SampleId, C4),
        ]);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(8);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.Null,
            "the streaming pitch trajectory is not projectable before PCM reaches EOF");
        float[] pcm = new float[8];
        synth.Render(context, state, 0, 8, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 2, 3, 4, 0, 0, 0, 0 }));
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.EqualTo(4));
        Assert.That(registry.SoundLookups, Is.EqualTo(1));
    }

    [Test]
    public void BareLoopingSampleCutsAtNoteOffAndReportsFiniteEnd()
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([1, 1, 1, 1], 8, loop: true));
        FmSynthSound synth = MakeSynth(registry, [
            new FmSoundSourceNode(0, SampleId, C4),
        ]);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(8);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.Null);
        state.NoteOffTime = TimeSpan.FromSeconds(2.0 / 8);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.EqualTo(2));
        float[] pcm = new float[6];
        synth.Render(context, state, 0, 6, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 1, 0, 0, 0, 0 }));
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.EqualTo(2));
    }

    [Test]
    public void InstrumentSampleSurvivesOffUntilItsVolumeEnvelopeFinishes()
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([1, 1, 1, 1], 8, loop: true));
        registry.AddEnvelope(EnvelopeId, new ThreeFrameRelease());
        registry.Add(InstrumentId, new InstrumentSound(
            _ => new ToneSpecification { SourceId = SampleId,
                VolumeEnvelopeId = EnvelopeId }, registry, registry));

        FmSynthSound synth = MakeSynth(registry, [
            new FmSoundSourceNode(0, InstrumentId, C4),
        ]);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(8);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.Null);
        state.NoteOffTime = TimeSpan.FromSeconds(2.0 / 8);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.EqualTo(5));
        float[] pcm = new float[7];
        synth.Render(context, state, 0, pcm.Length, pcm);
        Assert.Multiple(() =>
        {
            Assert.That(pcm[0], Is.EqualTo(1).Within(1e-6));
            Assert.That(pcm[1], Is.EqualTo(1).Within(1e-6));
            Assert.That(pcm[2], Is.EqualTo(1).Within(1e-6),
                "Note Off begins release, it must not cut SampleSound");
            Assert.That(pcm[3], Is.EqualTo(2f / 3).Within(1e-6));
            Assert.That(pcm[4], Is.EqualTo(1f / 3).Within(1e-6));
            Assert.That(pcm[5], Is.Zero);
            Assert.That(pcm[6], Is.Zero);
        });
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.EqualTo(5));
    }

    [Test]
    public void ToneFilterDrainsExactlyItsPreparedTailAfterSampleNaturalEnd()
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([1, 1, 1, 1], 1000));
        FilterDefinition definition = new((ObjectId)99U, "Lowpass");
        FilterInstance filter = new(definition);
        filter.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(100));
        registry.Add(InstrumentId, new InstrumentSound(
            _ => new ToneSpecification
            {
                SourceId = SampleId, FilterConfiguration = filter,
            }, registry, registry));

        FmSynthSound synth = MakeSynth(registry, [
            new FmSoundSourceNode(0, InstrumentId, C4),
        ]);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(1000);
        int tail = new StreamingFilterChain(new FilterChain([filter]),
            1000, 1).PreparedTailFrames;
        float[] output = new float[4 + tail + 5];
        synth.Render(context, state, 0, output.Length, output);
        Assert.Multiple(() =>
        {
            Assert.That(tail, Is.GreaterThan(0));
            Assert.That(output[3], Is.GreaterThan(0));
            Assert.That(output[4], Is.GreaterThan(0),
                "filter must drain zero-input history after sample EOF");
            Assert.That(output[4 + tail], Is.Zero);
            Assert.That(output[^1], Is.Zero);
        });
        Assert.That(synth.GetEndFrameExclusive(context, state),
            Is.EqualTo(4 + tail));
    }

    [Test]
    public void AdditiveSourcePathsMustAllFinishBeforeParentGetsFiniteEnd()
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([1, 1, 1], 8));
        registry.Add(InstrumentId, MakeSample([2, 2, 2, 2, 2], 8));
        FmSynthSound synth = MakeSynth(registry, [
            new FmSoundSourceNode(0, SampleId, C4),
            new FmSoundSourceNode(1, InstrumentId, C4),
            new FmOperatorNode(2, FmOperatorKind.Add, [0, 1]),
        ], 2);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(8);
        float[] pcm = new float[6];
        synth.Render(context, state, 0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 3, 3, 3, 2, 2, 0 }));
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.EqualTo(5));
    }

    [Test]
    public void NestedSynthInheritsReleaseAndTerminatesRatherThanLoopingForever()
    {
        Registry registry = new();
        ObjectId childId = (ObjectId)66U;
        FmSynthSound child = MakeSynth(registry, [
            new FmConstantNode(0, 0.5),
        ]);
        registry.Add(childId, child);
        FmSynthSound parent = MakeSynth(registry, [
            new FmSoundSourceNode(0, childId, C4),
        ]);
        SoundState state = parent.CreateState();
        RenderContext context = Mono(8);
        state.NoteOffTime = TimeSpan.FromSeconds(2.0 / 8);
        Assert.That(parent.GetEndFrameExclusive(context, state), Is.EqualTo(3));
        float[] pcm = new float[6];
        parent.Render(context, state, 0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 0.5f, 0.5f, 0.5f, 0, 0, 0 }));
    }

    [Test]
    public void CascadedAndParallelGraphFiltersUseMaximumAuthoredTailAllowance()
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([1, 1, 1], 1000));
        FilterDefinition definition = new((ObjectId)95U, "Graph filter");
        FilterInstance use = new(definition);
        use.SetStageParameter(1, "CutoffHz",
            FilterParameterValue.FromNumber(100));

        FmSynthSound synth = MakeSynth(registry, [
            new FmSoundSourceNode(0, SampleId, C4),
            new FmFilterNode(1, 0, use, tailSeconds: 0.002),
            new FmFilterNode(2, 1, use, tailSeconds: 0.003),
            new FmFilterNode(3, 0, use, tailSeconds: 0.004),
            new FmOperatorNode(4, FmOperatorKind.Add, [2, 3]),
        ], 4);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(1000);
        Assert.That(synth, Is.InstanceOf<IStreamingFiniteSound>());
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.Null);
        float[] audio = new float[12];
        synth.Render(context, state, 0, audio.Length, audio);
        // Source stops at 3, then the FM Filter authoring contract adds
        // max(2,3,4) = 4 frames, NOT the sum of serial stages. Their
        // physical Filter histories still process during this budget.
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.EqualTo(7));
        Assert.That(audio[3], Is.GreaterThan(0));
    }

    [TestCase(FmOperatorKind.Add, false)]
    [TestCase(FmOperatorKind.Minimum, false)]
    [TestCase(FmOperatorKind.Maximum, false)]
    [TestCase(FmOperatorKind.Multiply, true)]
    public void MixedFiniteSourceAndIndefiniteConstantAreConservative(
        FmOperatorKind operation, bool finitelyBounded)
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([0.25f, 0.5f, 0.75f], 8));
        FmSynthSound synth = MakeSynth(registry, [
            new FmSoundSourceNode(0, SampleId, C4),
            new FmConstantNode(1, 0.5),
            new FmOperatorNode(2, operation, [0, 1]),
        ], 2);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(8);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.Null);
        float[] pcm = new float[6];
        synth.Render(context, state, 0, pcm.Length, pcm);
        Assert.That(synth.GetEndFrameExclusive(context, state),
            finitelyBounded ? Is.EqualTo(3) : Is.Null);
        if (operation == FmOperatorKind.Multiply)
        {
            Assert.That(pcm, Is.EqualTo(new float[]
                { 0.125f, 0.25f, 0.375f, 0, 0, 0 }));
        }
    }

    [Test]
    public void UnreleasedEnvelopeDoesNotInheritHypotheticalNoteOffZero()
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([1, 1, 1], 8));
        registry.AddEnvelope(EnvelopeId, new ThreeFrameRelease());
        FmSynthSound synth = MakeSynth(registry, [
            new FmSoundSourceNode(0, SampleId, C4),
            new FmEnvelopeNode(1, EnvelopeId),
            new FmOperatorNode(2, FmOperatorKind.Add, [0, 1]),
        ], 2);
        SoundState state = synth.CreateState();
        RenderContext context = Mono(8);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.Null,
            "without Note Off the envelope has no release deadline");
        float[] audio = new float[4];
        synth.Render(context, state, 0, 4, audio);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.Null,
            "a finite sample does not end an unreleased envelope branch");
        Assert.That(audio[3], Is.GreaterThan(0));
        state.NoteOffTime = TimeSpan.FromSeconds(2.0 / 8);
        Assert.That(synth.GetEndFrameExclusive(context, state), Is.EqualTo(5),
            "the actual Note Off, not a hypothetical zero, bounds the envelope");
    }

    [Test]
    public void NestedStreamingSourceReportsFiniteEndWithinFirstPcmBlock()
    {
        Registry registry = new();
        registry.Add(SampleId, MakeSample([1, 2, 3], 8));
        ObjectId nestedId = (ObjectId)88U;
        FmSynthSound nested = MakeSynth(registry, [
            new FmSoundSourceNode(0, SampleId, C4),
        ]);
        registry.Add(nestedId, nested);
        FmSynthSound parent = MakeSynth(registry, [
            new FmSoundSourceNode(0, nestedId, C4),
        ]);
        SoundState state = parent.CreateState();
        RenderContext context = Mono(8);
        Assert.That(parent.GetEndFrameExclusive(context, state), Is.Null);
        float[] pcm = new float[10];
        parent.Render(context, state, 0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 2, 3, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.That(parent.GetEndFrameExclusive(context, state), Is.EqualTo(3));
    }

    [Test]
    public void NestedIndefiniteSourceEndQueriesStayAllocationFreeDuringStreaming()
    {
        // A parent's child-end observer polls the nested Synth every
        // rendered PCM frame. Its graph-bound and tail analysis must not
        // allocate a fresh scratch array or HashSet for those queries.
        Registry registry = new();
        registry.Add(SampleId, MakeSample([0.5f, 0.5f, 0.5f, 0.5f],
            48000, loop: true));
        ObjectId nestedId = (ObjectId)89U;
        FmSynthSound child = MakeSynth(registry, [
            new FmSoundSourceNode(0, SampleId, C4),
        ]);
        registry.Add(nestedId, child);
        FmSynthSound parent = MakeSynth(registry, [
            new FmSoundSourceNode(0, nestedId, C4),
        ]);
        SoundState state = parent.CreateState();
        RenderContext context = Mono(48000);
        float[] pcm = new float[128];
        for (int block = 0; block < 4; block++)
            parent.Render(context, state, block * pcm.Length,
                pcm.Length, pcm);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int block = 4; block < 20; block++)
            parent.Render(context, state, block * pcm.Length,
                pcm.Length, pcm);
        long managedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(managedBytes, Is.LessThanOrEqualTo(8192),
            "prepared nested source lifecycle queries must not allocate per PCM frame");
        Assert.That(parent.GetEndFrameExclusive(context, state), Is.Null);
    }

    private static FmSynthSound MakeSynth(Registry registry,
        FmSynthNode[] nodes, int output = 0)
        => new(new FmSynthGraph(nodes, output),
            registry, soundResolver: registry);

    private static SampleSound MakeSample(float[] pcm, int rate, bool loop = false)
    {
        SampleDefinition definition = new(SampleId, "PCM",
            new ExternalAssetReference("sample.wav"));
        if (loop)
            definition.Loop = new SampleLoop(SampleLoopMode.Forward, 0,
                pcm.Length);
        return new SampleSound(definition,
            new MemorySampleData(rate, 1, pcm));
    }

    private static RenderContext Mono(int rate) => new(
        new RenderConfiguration(rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));

    private sealed class ThreeFrameRelease : IEnvelopeCurve
    {
        public double GetValue(long activeFrame, int rate, long? off) =>
            !off.HasValue ? 1 : Math.Max(0,
                1 - (activeFrame - off.Value) / 3.0);

        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long off, int rate) => checked(off + 3);
    }

    private sealed class Registry : ISoundResolver, IEnvelopeCurveResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        private readonly Dictionary<ObjectId, IEnvelopeCurve> _envelopes = [];
        public int SoundLookups { get; private set; }
        public void Add(ObjectId id, ISound sound) => _sounds.Add(id, sound);
        public void AddEnvelope(ObjectId id, IEnvelopeCurve curve) =>
            _envelopes.Add(id, curve);
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
        {
            SoundLookups++;
            return _sounds.TryGetValue(id, out sound);
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? envelope) =>
            _envelopes.TryGetValue(id, out envelope);
    }
}

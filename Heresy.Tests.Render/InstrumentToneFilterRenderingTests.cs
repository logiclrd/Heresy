using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class InstrumentToneFilterRenderingTests
{
    private const int Rate = 1000;
    private static readonly ObjectId InnerId = (ObjectId)11U;
    private static readonly ObjectId OuterId = (ObjectId)12U;
    private static readonly ObjectId PulseId = (ObjectId)13U;

    [Test]
    public void NestedTonesComposeInnerBeforeOuterWithFiniteTailAndTerminalIdentity()
    {
        ProbeSource pulse = new();
        TestResolver resolver = new();
        resolver.Add(PulseId, pulse);
        FilterInstance inner = Comb(101, 0.001);
        FilterInstance outer = Comb(102, 0.002);
        InstrumentSound instrument = Nested(resolver, inner, outer);

        SoundInvocation invocation = instrument.CreateInvocation(1, 1)!;
        Assert.That(invocation.Sound, Is.SameAs(pulse),
            "binding must retain the actual source's seek and release capabilities");
        Assert.That(invocation.State, Is.TypeOf<ProbeState>());
        Assert.That(invocation.ToneFilters!.Layers.Select(l => l.DefinitionId),
            Is.EqualTo(new[] { inner.DefinitionId, outer.DefinitionId }));

        // Equivalent per-layer DSP baseline; two distinct delays yield the
        // order-preserving convolution [1,1,1,1] at frames 0..3.
        StreamingFilterChain reference = new(
            new FilterChain([inner, outer]), Rate, 1);
        float[] expected = new float[9];
        expected[0] = 1;
        reference.ProcessInterleaved(expected);

        PlaybackSession full = Session(Schedule(Event(0, 0,
            new StartNoteCommand(OuterId))), resolver);
        float[] pcm = new float[9];
        full.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(expected));
        Assert.That(pcm.Take(4), Is.EqualTo(new float[] { 1, 1, 1, 1 }));
        Assert.That(full.GetChannelState(0).CurrentVoice, Is.Null,
            "one-frame source must drain the three-frame composed Filter tail");

        PlaybackSession sliced = Session(Schedule(Event(0, 0,
            new StartNoteCommand(OuterId))), resolver);
        float[] split = new float[pcm.Length];
        sliced.Render(0, 1, split.AsSpan(0, 1));
        sliced.Render(1, 2, split.AsSpan(1, 2));
        sliced.Render(3, 6, split.AsSpan(3));
        Assert.That(split, Is.EqualTo(pcm));

        // The direct InstrumentSound compatibility entry point must also
        // process its invocation chain, not only the PlaybackSession path.
        SoundState direct = instrument.CreateState();
        float[] directPcm = new float[9];
        instrument.Render(Context(), direct, 0, directPcm.Length, directPcm);
        Assert.That(directPcm, Is.EqualTo(pcm));
        Assert.That(instrument.GetEndFrameExclusive(Context(), direct), Is.EqualTo(4));
    }

    [Test]
    public void MultipleVoicesAndDisplacedNnaKeepIndependentFilterHistories()
    {
        TestResolver resolver = new();
        resolver.Add(PulseId, new ProbeSource());
        InstrumentSound instrument = Nested(resolver, Comb(111, 0.001), null);
        PlaybackSession session = Session(Schedule(
            Event(0, 0, new StartNoteCommand(OuterId)),
            Event(0, 1, new StartNoteCommand(OuterId)),
            Event(1, 0, new StartNoteCommand(OuterId))), resolver);
        float[] pcm = new float[5];
        session.Render(0, pcm.Length, pcm);
        // Each pulse has a one-frame echo. Previous voice on channel 0
        // migrates via Continue; its history must remain with that voice.
        Assert.That(pcm, Is.EqualTo(new float[] { 2, 3, 1, 0, 0 }));
    }

    [Test]
    public void FlattenedVirtualDescendantOwnsItsFilterAndRespectsScopeCancellation()
    {
        TestResolver resolver = new();
        resolver.Add(PulseId, new ProbeSource());
        Nested(resolver, Comb(121, 0.002), null);
        PlaybackSession session = Session(Schedule(), resolver);

        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
            [new BeginFlattenedSourceVolumeCommand(1, 0.5)]);
        StartNoteCommand child = new(OuterId)
        {
            ParentSourceScopes = new long[] { 1 },
        };
        session.ApplyScopedEvent(0, ChannelTarget.Virtual(7), [child]);
        // This unscoped host is a sibling rather than a descendant.
        session.ApplyScopedEvent(0, ChannelTarget.Physical(1),
            [new StartNoteCommand(OuterId)]);
        float[] first = new float[1];
        session.Render(0, 1, first);
        Assert.That(first[0], Is.EqualTo(1.5f));

        session.ApplyFlattenedScopeAction(1, NoteDisplacementAction.Cut);
        float[] after = new float[4];
        session.Render(1, 4, after);
        // The cut deliberately contributes an anti-click residue from the
        // descendant's last sample. At 1 kHz it decays rapidly; its own
        // two-frame comb echo must *not* continue after cancellation.
        Assert.That(after[0], Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(after[1], Is.GreaterThan(1f).And.LessThan(1.1f),
            "unscoped sibling retains its exact echo plus short cut residue");
        Assert.That(Math.Abs(after[2]), Is.LessThan(0.01f));
        Assert.That(Math.Abs(after[3]), Is.LessThan(0.01f));
    }

    [Test]
    public void StreamingFiniteSourceDiscoveredMidBlockKeepsToneEchoBeforeRetirement()
    {
        TestResolver resolver = new();
        resolver.Add(PulseId, new ProbeSource(discoverEndOnRender: true));
        Nested(resolver, Comb(131, 0.001), null);
        PlaybackSession session = Session(Schedule(Event(0, 0,
            new StartNoteCommand(OuterId))), resolver);
        float[] pcm = new float[5];
        session.Render(0, 5, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 1, 0, 0, 0 }));
        Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null);
    }

    [Test]
    public void ScriptSelectorAndFrozenInvocationAreIndependentOfLaterToneEdits()
    {
        TestResolver resolver = new();
        ProbeSource pulse = new();
        resolver.Add(PulseId, pulse);
        FilterInstance use = Comb(141, 0.001);
        ToneSpecification tone = new()
        {
            SourceId = PulseId, FilterConfiguration = use.Clone(),
        };
        InstrumentSound scripted = new((Func<double, ToneSpecification?>)(_ => tone),
            resolver, new EmptyEnvelopes());
        resolver.Add(OuterId, scripted);
        SoundInvocation old = scripted.CreateInvocation(1, 1)!;
        use.SetStageParameter(1, "Mix", FilterParameterValue.FromNumber(0));
        Assert.That(old.ToneFilters!.Layers[0]
            .GetStageParameters(1).GetParameter("Mix").NumericValue, Is.EqualTo(1));
        Assert.That(old.Sound, Is.SameAs(pulse));
        PlaybackSession session = Session(Schedule(Event(0, 0,
            new StartNoteCommand(OuterId))), resolver);
        float[] pcm = new float[3];
        session.Render(0, 3, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 1, 0 }));
    }

    private static FilterInstance Comb(uint id, double seconds)
    {
        FilterDefinition definition = new((ObjectId)id, "Comb", [
            new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
        ]);
        FilterInstance instance = new(definition);
        instance.SetStageParameters(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(seconds)),
            new("Feedforward", FilterParameterValue.FromNumber(1)),
            new("Mix", FilterParameterValue.FromNumber(1)),
        ]);
        return instance;
    }

    private static InstrumentSound Nested(TestResolver resolver,
        FilterInstance inner, FilterInstance? outer)
    {
        InstrumentDefinition innerDefinition = Instrument(InnerId, PulseId, inner);
        InstrumentSound innerSound = new(innerDefinition, resolver, new EmptyEnvelopes());
        resolver.Add(InnerId, innerSound);
        InstrumentDefinition outerDefinition = Instrument(OuterId, InnerId, outer);
        InstrumentSound outerSound = new(outerDefinition, resolver, new EmptyEnvelopes());
        resolver.Add(OuterId, outerSound);
        return outerSound;
    }

    private static InstrumentDefinition Instrument(ObjectId id, ObjectId source,
        FilterInstance? filter)
    {
        InstrumentDefinition definition = new(id, "Tone");
        definition.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = source, FilterConfiguration = filter,
        });
        definition.ToneTable.Add(0);
        return definition;
    }

    private static RenderContext Context() => new(new RenderConfiguration(
        Rate, [new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0)]));

    private static PlaybackSession Session(NoteSchedule schedule, ISoundResolver resolver)
        => new(Context(), schedule, resolver);

    private static NoteSchedule Schedule(params NoteEvent[] events)
    {
        NoteScheduleBuilder b = new();
        foreach (NoteEvent e in events) b.Append(e);
        return b.Freeze();
    }

    private static NoteEvent Event(int millisecond, int channel,
        params NoteCommand[] commands)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(millisecond), 0),
            ChannelTarget.Physical(channel), commands);

    private sealed class ProbeState : SoundState
    {
        public bool EndKnown { get; set; }
    }

    private sealed class ProbeSource : IStreamingFiniteSound
    {
        private readonly bool _discoverEnd;
        public ProbeSource(bool discoverEndOnRender = false)
            => _discoverEnd = discoverEndOnRender;
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => new(NewNotePolicy.Continue);
        public SoundState CreateState() => new ProbeState();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state)
            => !_discoverEnd || ((ProbeState)state).EndKnown ? 1 : null;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
        {
            if (startFrame == 0 && frameCount > 0)
                destination[0] += 1;
            if (_discoverEnd) ((ProbeState)state).EndKnown = true;
        }
    }

    private sealed class TestResolver : ISoundResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        public void Add(ObjectId id, ISound sound) => _sounds[id] = sound;
        public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
            => _sounds.TryGetValue(sourceId, out sound);
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

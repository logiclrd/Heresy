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
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class NoteEventFilterRenderingTests
{
    private const int Rate = 1000;
    private static readonly ObjectId Source = (ObjectId)100U;
    private static readonly ObjectId InstrumentId = (ObjectId)101U;

    [Test]
    public void EventLayerComposesAfterNestedToneFiltersAndHasSeparateState()
    {
        FilterInstance inner = Echo(301, 0.001);
        FilterInstance outer = Echo(302, 0.002);
        Resolver resolver = new();
        ProbeSound source = new();
        resolver.Add(Source, source);
        InstrumentDefinition instrument = new(InstrumentId, "Tone");
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = Source,
            FilterConfiguration = inner,
        });
        instrument.ToneTable.Add(0);
        resolver.Add(InstrumentId,
            new InstrumentSound(instrument, resolver, new NoEnvelopes()));
        StartNoteCommand start = new(InstrumentId)
        {
            EventFilterConfiguration = outer,
        };
        PlaybackSession session = Session(resolver,
            Schedule(Event(0, 0, start)));
        float[] pcm = new float[7];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm.Take(4), Is.EqualTo(new float[] { 1, 1, 1, 1 }));
        Assert.That(pcm.Skip(4), Is.All.Zero);
        Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null,
            "the two-stage composed finite tail must retire the note");
    }

    [Test]
    public void UnfilteredReplacementDoesNotInheritDisplacedNoteEventFilter()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        FilterInstance filter = Echo(311, 0.002);
        PlaybackSession session = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source)
            {
                EventFilterConfiguration = filter,
            }),
            Event(0, 1, new StartNoteCommand(Source))));
        float[] pcm = new float[4];
        session.Render(0, 4, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 2, 0, 1, 0 }));
        Assert.That(session.GetChannelState(1).CurrentVoice, Is.Null);
    }

    [Test]
    public void SameFrameRepeatedStartUsesTheLaterStartWithoutLeakingEventLayer()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession session = Session(resolver, Schedule(
            Event(0, 0,
                new StartNoteCommand(Source)
                {
                    EventFilterConfiguration = Echo(321, 0.002),
                },
                new StartNoteCommand(Source))));
        float[] pcm = new float[4];
        session.Render(0, 4, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 0, 0, 0 }),
            "same-frame second start replaces the unplayed first note");
    }

    [Test]
    public void LiveVirtualNoteAndScopedDescendantKeepIndependentEventFilters()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
            [new BeginFlattenedSourceVolumeCommand(1, 0.5)]);
        session.ApplyScopedEvent(0, ChannelTarget.Virtual(7),
            [new StartNoteCommand(Source)
            {
                ParentSourceScopes = new long[] { 1 },
                EventFilterConfiguration = Echo(331, 0.001),
            }]);
        session.ApplyScopedEvent(0, ChannelTarget.Physical(1),
            [new StartNoteCommand(Source)
            {
                EventFilterConfiguration = Echo(332, 0.002),
            }]);
        float[] pcm = new float[5];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1.5f, 0.5f, 1, 0, 0 }));
    }

    [Test]
    public void EventFilterRetainsClonedOverridesAcrossExternalAuthoringMutation()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        FilterInstance authored = Echo(341, 0.001);
        PlaybackSession session = Session(resolver,
            Schedule(Event(0, 0, new StartNoteCommand(Source)
            {
                EventFilterConfiguration = authored,
            })));
        // The event's template is still an authoring object, not the runtime.
        // Once the voice is created the prepared DSP is invocation private.
        float[] first = new float[1];
        session.Render(0, 1, first);
        authored.SetStageParameter(1, "Mix",
            FilterParameterValue.FromNumber(0));
        float[] second = new float[2];
        session.Render(1, 2, second);
        Assert.That(first[0], Is.EqualTo(1));
        Assert.That(second, Is.EqualTo(new float[] { 1, 0 }));
    }

    private static FilterInstance Echo(uint id, double delay)
    {
        FilterDefinition definition = new((ObjectId)id, "Echo", [
            new FilterStageDefinition(1, "delay", FilterKind.CombFeedforward),
        ]);
        FilterInstance result = new(definition);
        result.SetStageParameters(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(delay)),
            new("Feedforward", FilterParameterValue.FromNumber(1)),
            new("Mix", FilterParameterValue.FromNumber(1)),
        ]);
        return result;
    }

    private static PlaybackSession Session(Resolver resolver, NoteSchedule schedule)
        => new(new RenderContext(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero,
                positionalImportance: 0),
        ])), schedule, resolver);

    private static NoteSchedule Schedule(params NoteEvent[] events)
    {
        NoteScheduleBuilder result = new();
        foreach (NoteEvent e in events) result.Append(e);
        return result.Freeze();
    }

    private static NoteEvent Event(int milliseconds, int channel,
        params NoteCommand[] commands)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(milliseconds), 0),
            ChannelTarget.Physical(channel), commands);

    private sealed class ProbeState : SoundState;
    private sealed class ProbeSound : ISound
    {
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => new(NewNotePolicy.Cut);
        public SoundState CreateState() => new ProbeState();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state)
            => 1;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
        {
            if (startFrame == 0 && frameCount > 0)
                destination[0] += 1;
        }
    }

    private sealed class Resolver : ISoundResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];
        public void Add(ObjectId id, ISound sound) => _sounds.Add(id, sound);
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? sound)
            => _sounds.TryGetValue(id, out sound);
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

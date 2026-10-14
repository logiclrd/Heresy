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

    [Test]
    public void SameFrameSelectAndClearAffectOnlyCurrentNoteAndPreserveTone()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        FilterInstance tone = Echo(401, 0.002);
        FilterInstance selection = Echo(402, 0.001);
        InstrumentDefinition instrument = new(InstrumentId, "tone");
        instrument.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = Source, FilterConfiguration = tone,
        });
        instrument.ToneTable.Add(0);
        resolver.Add(InstrumentId,
            new InstrumentSound(instrument, resolver, new NoEnvelopes()));
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new StartNoteCommand(InstrumentId),
            new SetNoteFilterCommand { Configuration = selection },
            new SetNoteFilterCommand(), // clears only event layer
        ]);
        float[] pcm = new float[5];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 0, 1, 0, 0 }),
            "clearing the event Filter cannot remove an inherited tone Filter");
    }

    [Test]
    public void TypedNonstructuralChangesPreserveCurrentImpulseHistory()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new StartNoteCommand(Source),
            new SetNoteFilterCommand { Configuration = Echo(411, 0.002) },
        ]);
        float[] first = new float[1];
        session.Render(0, 1, first);
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.5)),
            ]),
        ]);
        float[] rest = new float[4];
        session.Render(1, 4, rest);
        Assert.That(first, Is.EqualTo(new float[] { 1 }));
        Assert.That(rest, Is.EqualTo(new float[] { 0, 0.96875f, 0, 0 }),
            "compatible reconfiguration preserves the delayed sample while " +
            "smoothly ramping feedforward gain across 32 PCM frames");
    }

    [Test]
    public void StructuralRetuneResetsHistoryInsteadOfPublishingStaleDelay()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession session = Session(resolver, Schedule());
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new StartNoteCommand(Source),
            new SetNoteFilterCommand { Configuration = Echo(421, 0.002) },
        ]);
        session.Render(0, 1, new float[1]);
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new SetNoteFilterParametersCommand(1, [
                new("DelaySeconds", FilterParameterValue.FromNumber(0.001)),
            ]),
        ]);
        float[] remaining = new float[4];
        session.Render(1, 4, remaining);
        Assert.That(remaining, Is.All.Zero,
            "structural delay topology must discard the old impulse history");
    }

    [Test]
    public void InvalidBatchIsAtomicAndRepeatedNoNoteCommandsAreDiagnosed()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession session = Session(resolver, Schedule());
        List<string> errors = [];
        session.NoteFilterCommandRejected = errors.Add;
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new SetNoteFilterCommand { Configuration = Echo(430, 0.001) },
            new SetNoteFilterParametersCommand(1, [
                new("Mix", FilterParameterValue.FromNumber(0.2)),
            ]),
        ]);
        Assert.That(errors, Has.Count.EqualTo(2));
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new StartNoteCommand(Source),
            new SetNoteFilterCommand { Configuration = Echo(431, 0.002) },
            new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.25)),
                new("NoSuchParameter", FilterParameterValue.FromNumber(2)),
            ]),
        ]);
        float[] pcm = new float[5];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 0, 1, 0, 0 }),
            "invalid typed batch must not publish even its first valid field");
        Assert.That(errors, Has.Count.EqualTo(3));
    }

    [Test]
    public void UnavailableSelectionDoesNotErasePreviouslySelectedEventLayer()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession session = Session(resolver, Schedule());
        List<string> errors = [];
        session.NoteFilterCommandRejected = errors.Add;
        session.ApplyScopedEvent(0, ChannelTarget.Virtual(7),
        [
            new StartNoteCommand(Source),
            new SetNoteFilterCommand { Configuration = Echo(441, 0.001) },
            new SetNoteFilterCommand((ObjectId)44444U),
        ]);
        float[] pcm = new float[4];
        session.Render(0, 4, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 1, 0, 0 }));
        Assert.That(errors, Has.Count.EqualTo(1));
    }

    [Test]
    public void ChangingCurrentNoteNeverReconfiguresItsDisplacedNnaVoice()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound(continueOnDisplacement: true));
        PlaybackSession session = Session(resolver, Schedule(
            Event(0, 0,
                new StartNoteCommand(Source)
                {
                    EventFilterConfiguration = Echo(451, 0.002),
                }),
            Event(1, 0,
                new StartNoteCommand(Source),
                new SetNoteFilterCommand { Configuration = Echo(452, 0.001) })));
        float[] pcm = new float[5];
        session.Render(0, pcm.Length, pcm);
        Assert.That(pcm, Is.EqualTo(new float[] { 1, 1, 2, 0, 0 }),
            "the old voice's delay-two history and replacement's delay-one echo coexist");
    }

    [Test]
    public void FlattenedCurrentNoteUpdatesDescendantsAndNotHostSiblings()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession session = Session(resolver, Schedule());
        FilterInstance initial = Echo(461, 0.002);
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new BeginFlattenedSourceVolumeCommand(1, 1)
            {
                EventFilterConfiguration = initial,
            },
        ]);
        session.ApplyScopedEvent(0, ChannelTarget.Virtual(7),
        [
            new StartNoteCommand(Source)
            {
                ParentSourceScopes = new long[] { 1 },
            },
        ]);
        session.ApplyScopedEvent(0, ChannelTarget.Physical(1),
        [
            new StartNoteCommand(Source)
            {
                EventFilterConfiguration = Echo(462, 0.002),
            },
        ]);
        float[] before = new float[1];
        session.Render(0, 1, before);
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.5)),
            ]),
        ]);
        float[] after = new float[4];
        session.Render(1, 4, after);
        Assert.That(before, Is.EqualTo(new float[] { 2 }));
        Assert.That(after, Is.EqualTo(new float[] { 0, 1.96875f, 0, 0 }),
            "the controlled descendant ramps toward 0.5, while the " +
            "unrelated sibling retains its full 1.0 echo");
    }

    [Test]
    public void InvalidFlattenedScopeUpdateIsAtomicAcrossExistingDescendantVoices()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession session = Session(resolver, Schedule());
        List<string> errors = [];
        session.NoteFilterCommandRejected = errors.Add;
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new BeginFlattenedSourceVolumeCommand(1, 1)
            {
                EventFilterConfiguration = Echo(471, 0.002),
            },
        ]);
        foreach (uint virtualId in new uint[] { 7, 8 })
            session.ApplyScopedEvent(0, ChannelTarget.Virtual(virtualId),
            [
                new StartNoteCommand(Source)
                {
                    ParentSourceScopes = new long[] { 1 },
                },
            ]);
        session.Render(0, 1, new float[1]);
        session.ApplyScopedEvent(0, ChannelTarget.Physical(0),
        [
            new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.25)),
                new("DelaySeconds", FilterParameterValue.FromNumber(-1)),
            ]),
        ]);
        float[] remainder = new float[4];
        session.Render(1, 4, remainder);
        Assert.That(remainder, Is.EqualTo(new float[] { 0, 2, 0, 0 }));
        Assert.That(errors, Has.Count.EqualTo(1));
    }

    [Test]
    public void TextNoteAssignmentsMatchTypedCommandWithInvalidSibling()
    {
        Resolver resolver = new();
        resolver.Add(Source, new ProbeSound());
        PlaybackSession Make()
        {
            PlaybackSession session = Session(resolver, Schedule());
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                new StartNoteCommand(Source)
                {
                    EventFilterConfiguration = Echo(551, 0.002),
                },
            ]);
            return session;
        }

        PlaybackSession text = Make(), typed = Make(), rejected = Make();
        text.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterAssignmentsCommand("""
                # delay: a smoothed amplitude change
                delay.Feedforward = 0.5
                """),
        ]);
        typed.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.5)),
            ]),
        ]);
        List<string> diagnostics = [];
        rejected.NoteFilterCommandRejected = diagnostics.Add;
        rejected.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterAssignmentsCommand("""
                delay.Feedforward = 0.5
                delay.Invalid = 9
                """),
        ]);
        float[] viaText = new float[8], viaTyped = new float[8], invalid = new float[8];
        text.Render(0, 8, viaText);
        typed.Render(0, 8, viaTyped);
        rejected.Render(0, 8, invalid);
        Assert.That(viaText, Is.EqualTo(viaTyped).Within(1e-6f));
        Assert.Multiple(() =>
        {
            Assert.That(invalid, Is.EqualTo(viaTyped).Within(1e-6f),
                "valid note text must match typed commands despite invalid siblings");
            Assert.That(diagnostics, Is.Empty,
                "runtime must ignore unsupported property names silently");
            Assert.That(invalid[2], Is.EqualTo(0.5f).Within(1e-6f));
        });
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

    private sealed class ProbeState : SoundState { }
    private sealed class ProbeSound : ISound
    {
        private readonly bool _continue;
        public ProbeSound(bool continueOnDisplacement = false)
            => _continue = continueOnDisplacement;
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => new(_continue ? NewNotePolicy.Continue : NewNotePolicy.Cut);
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class NoteFilterParameterEnvelopeTests
{
    private const int Rate = 1000;
    private static readonly ObjectId Source = (ObjectId)12001U;
    private static readonly ObjectId Envelope = (ObjectId)12002U;

    [Test]
    public void AttachmentAndDetachmentLeaveAuthoredBaseUntouched()
    {
        FilterInstance use = Echo();
        var resolver = new TestResolver(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession envelope = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = use,
            }, new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope)),
            Event(4, 0, new SetNoteFilterParameterEnvelopeCommand(1,
                "Feedforward"))));
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = use,
            }, new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.5)),
            ])),
            Event(4, 0, new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(1.0)),
            ]))));

        float[] actual = new float[10], reference = new float[10];
        envelope.Render(0, 10, actual);
        typed.Render(0, 10, reference);
        Assert.That(actual, Is.EqualTo(reference).Within(1e-6f),
            "detaching the envelope reveals the unchanged base and retains history");
        Assert.That(envelope.GetChannelState(0).CurrentVoice!
            .GetEventFilterConfiguration()!.GetStageParameters(1)
            .GetParameter("Feedforward").NumericValue, Is.EqualTo(1.0));
    }

    [Test]
    public void MovingEnvelopeClockMatchesPerFrameTypedUpdatesAndChunkPartitions()
    {
        TestResolver resolver = new(new ConstantSound(),
            new RampCurve());
        FilterInstance use = Echo();
        PlaybackSession automated = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = use,
            }, new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope))));
        PlaybackSession partitioned = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = use,
            }, new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope))));
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = use,
            }, SetAt(0)),
            Event(1, 0, SetAt(1)),
            Event(2, 0, SetAt(2)),
            Event(3, 0, SetAt(3)),
            Event(4, 0, SetAt(4)),
            Event(5, 0, SetAt(5))));

        float[] actual = new float[6], chunks = new float[6], reference = new float[6];
        automated.Render(0, 6, actual);
        partitioned.Render(0, 1, chunks.AsSpan(0, 1));
        partitioned.Render(1, 2, chunks.AsSpan(1, 2));
        partitioned.Render(3, 3, chunks.AsSpan(3, 3));
        typed.Render(0, 6, reference);
        Assert.That(chunks, Is.EqualTo(actual).Within(1e-6f));
        Assert.That(actual, Is.EqualTo(reference).Within(1e-6f));

        static NoteCommand SetAt(int frame)
            => new SetNoteFilterParametersCommand(1, [
                new("Feedforward",
                    FilterParameterValue.FromNumber(0.2 + frame * 0.1)),
            ]);
    }

    [Test]
    public void InvalidStructuralBindingAndNonfiniteValuesDoNotChangeDsp()
    {
        TestResolver resolver = new(new ConstantSound(),
            new FixedCurve(double.NaN));
        FilterInstance use = Echo();
        PlaybackSession faulty = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = use,
            }, new SetNoteFilterParameterEnvelopeCommand(1, "DelaySeconds", Envelope),
               new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope))));
        PlaybackSession clean = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = use,
            })));
        List<string> errors = [];
        faulty.NoteFilterCommandRejected = errors.Add;
        float[] actual = new float[6], reference = new float[6];
        faulty.Render(0, 6, actual);
        clean.Render(0, 6, reference);
        Assert.That(actual, Is.EqualTo(reference).Within(1e-6f));
        Assert.That(errors, Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(errors.Any(x => x.Contains("structural")), Is.True);
    }

    [Test]
    public void MissingCurveOrCurrentNoteIsDiagnosedWithoutThrowing()
    {
        TestResolver resolver = new(new ConstantSound(), null);
        PlaybackSession session = Session(resolver, Schedule(
            Event(0, 0, new SetNoteFilterParameterEnvelopeCommand(1,
                "Feedforward", Envelope)),
            Event(1, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope))));
        List<string> messages = [];
        session.NoteFilterCommandRejected = messages.Add;
        session.Render(0, 4, new float[4]);
        Assert.That(messages, Has.Count.EqualTo(2));
        Assert.That(messages[0], Does.Contain("No current note"));
        Assert.That(messages[1], Does.Contain("unavailable"));
    }

    private static FilterInstance Echo()
    {
        FilterDefinition definition = new((ObjectId)12003U, "Echo", [
            new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
        ]);
        FilterInstance instance = new(definition);
        instance.SetStageParameters(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(0.002)),
            new("Feedforward", FilterParameterValue.FromNumber(1)),
            new("Mix", FilterParameterValue.FromNumber(1)),
        ]);
        return instance;
    }

    private static PlaybackSession Session(TestResolver resolver, NoteSchedule schedule)
        => new(new RenderContext(new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ])), schedule, resolver) { NoteFilterEnvelopeResolver = resolver };

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

    private sealed class State : SoundState { }
    private sealed class ConstantSound : ISound
    {
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => new(NewNotePolicy.Continue);
        public SoundState CreateState() => new State();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state)
            => 10;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
            => destination.Fill(1);
    }

    private sealed class FixedCurve(double value) : IEnvelopeCurve
    {
        public double GetValue(long activeFrame, int sampleRate, long? off)
            => value;
    }
    private sealed class RampCurve : IEnvelopeCurve
    {
        public double GetValue(long activeFrame, int sampleRate, long? off)
            => 0.2 + activeFrame * 0.1;
    }
    private sealed class TestResolver(ISound sound, IEnvelopeCurve? curve)
        : ISoundResolver, IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? resolved)
        {
            resolved = id == Source ? sound : null;
            return resolved is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? resolved)
        {
            resolved = id == Envelope ? curve : null;
            return resolved is not null;
        }
    }
}

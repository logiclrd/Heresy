using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Filters;
using Heresy.Core.Envelopes;
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


    [Test]
    public void FlattenedEnvelopeUpdatesAllDescendantsAndDetachesToBaseAtomically()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession automated = Session(resolver, Schedule());
        PlaybackSession typed = Session(resolver, Schedule());
        foreach (PlaybackSession session in new[] { automated, typed })
        {
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                new BeginFlattenedSourceVolumeCommand(1, 1) {
                    EventFilterConfiguration = Echo(),
                },
            ]);
            foreach (uint id in new uint[] { 7, 8 })
                session.ApplyScopedEvent(0, ChannelTarget.Virtual(id), [
                    new StartNoteCommand(Source) {
                        ParentSourceScopes = new long[] { 1 },
                    },
                ]);
        }

        float[] actual = new float[7], expected = new float[7];
        automated.Render(0, 1, actual.AsSpan(0, 1));
        typed.Render(0, 1, expected.AsSpan(0, 1));

        automated.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope),
        ]);
        typed.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.5)),
            ]),
        ]);
        automated.Render(1, 3, actual.AsSpan(1, 3));
        typed.Render(1, 3, expected.AsSpan(1, 3));

        automated.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward"),
        ]);
        typed.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(1.0)),
            ]),
        ]);
        automated.Render(4, 3, actual.AsSpan(4, 3));
        typed.Render(4, 3, expected.AsSpan(4, 3));
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }

    [Test]
    public void LaterFlattenedDescendantsUseIndependentClocksWithOriginalAttachmentFrame()
    {
        TestResolver resolver = new(new ConstantSound(), new RampCurve());
        PlaybackSession automated = Session(resolver, Schedule());
        PlaybackSession typed = Session(resolver, Schedule());
        foreach (PlaybackSession session in new[] { automated, typed })
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                new BeginFlattenedSourceVolumeCommand(1, 1) {
                    EventFilterConfiguration = Echo(),
                },
            ]);
        automated.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope),
        ]);

        float[] actual = new float[7], expected = new float[7];
        for (int frame = 0; frame < actual.Length; frame++)
        {
            if (frame == 2)
                foreach (PlaybackSession session in new[] { automated, typed })
                    session.ApplyScopedEvent(0, ChannelTarget.Virtual(8), [
                        new StartNoteCommand(Source) {
                            ParentSourceScopes = new long[] { 1 },
                        },
                    ]);
            // Prior to the late descendant's first note there is no
            // matching running DSP to retune in the typed reference.
            if (frame >= 2)
                typed.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                    new SetNoteFilterParametersCommand(1, [
                        new("Feedforward", FilterParameterValue.FromNumber(
                            0.2 + frame * 0.1)),
                    ]),
                ]);
            automated.Render(frame, 1, actual.AsSpan(frame, 1));
            typed.Render(frame, 1, expected.AsSpan(frame, 1));
        }
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }

    [Test]
    public void InvalidFlattenedBindingsCannotPartiallyModifyDescendants()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession rejected = Session(resolver, Schedule());
        PlaybackSession reference = Session(resolver, Schedule());
        foreach (PlaybackSession session in new[] { rejected, reference })
        {
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                new BeginFlattenedSourceVolumeCommand(1, 1) {
                    EventFilterConfiguration = Echo(),
                },
            ]);
            foreach (uint id in new uint[] { 7, 8 })
                session.ApplyScopedEvent(0, ChannelTarget.Virtual(id), [
                    new StartNoteCommand(Source) {
                        ParentSourceScopes = new long[] { 1 },
                    },
                ]);
        }
        List<string> errors = [];
        rejected.NoteFilterCommandRejected = errors.Add;
        rejected.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParameterEnvelopeCommand(1, "DelaySeconds", Envelope),
            new SetNoteFilterParameterEnvelopeCommand(999, "Feedforward", Envelope),
            new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward",
                (ObjectId)99999U),
        ]);
        float[] actual = new float[6], expected = new float[6];
        rejected.Render(0, 6, actual);
        reference.Render(0, 6, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
        Assert.That(errors, Has.Count.EqualTo(3));
    }


    [Test]
    public void FlattenedNoteOffReleasesOnlyItsOwnParameterEnvelopeClock()
    {
        TestResolver resolver = new(new ConstantSound(), new ReleaseCurve());
        PlaybackSession released = Session(resolver, Schedule());
        PlaybackSession running = Session(resolver, Schedule());
        foreach (PlaybackSession session in new[] { released, running })
        {
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                new BeginFlattenedSourceVolumeCommand(1, 1) {
                    EventFilterConfiguration = Echo(),
                },
                new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope),
            ]);
            session.ApplyScopedEvent(0, ChannelTarget.Virtual(7), [
                new StartNoteCommand(Source) {
                    ParentSourceScopes = new long[] { 1 },
                },
            ]);
        }

        float[] afterOff = new float[8], stillHeld = new float[8];
        released.Render(0, 3, afterOff.AsSpan(0, 3));
        running.Render(0, 3, stillHeld.AsSpan(0, 3));
        released.ApplyFlattenedScopeAction(1, NoteDisplacementAction.Off);
        released.Render(3, 5, afterOff.AsSpan(3, 5));
        running.Render(3, 5, stillHeld.AsSpan(3, 5));
        Assert.That(afterOff[2], Is.EqualTo(stillHeld[2]).Within(1e-6f));
        Assert.That(afterOff[5], Is.LessThan(stillHeld[5]),
            "the instigator's Note Off releases each descendant's scope envelope");
    }

    [Test]
    public void NonfiniteInitialFlattenedBindingRollsBackAcrossAllDescendants()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(double.NaN));
        PlaybackSession faulty = Session(resolver, Schedule());
        PlaybackSession clean = Session(resolver, Schedule());
        foreach (PlaybackSession session in new[] { faulty, clean })
        {
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                new BeginFlattenedSourceVolumeCommand(1, 1) {
                    EventFilterConfiguration = Echo(),
                },
            ]);
            foreach (uint id in new uint[] { 7, 8 })
                session.ApplyScopedEvent(0, ChannelTarget.Virtual(id), [
                    new StartNoteCommand(Source) {
                        ParentSourceScopes = new long[] { 1 },
                    },
                ]);
        }
        List<string> diagnostics = [];
        faulty.NoteFilterCommandRejected = diagnostics.Add;
        faulty.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope),
        ]);
        float[] actual = new float[6], reference = new float[6];
        faulty.Render(0, 6, actual);
        clean.Render(0, 6, reference);
        Assert.That(actual, Is.EqualTo(reference).Within(1e-6f));
        Assert.That(diagnostics, Has.Count.EqualTo(1));
    }


    [Test]
    public void SessionFIRControlBoundaryPublishesOnlyMatchingPreparedEnvelopeTarget()
    {
        FilterDefinition definition = new((ObjectId)24001U, "Fir", [
            new FilterStageDefinition(1, "fir", FilterKind.FirLowPass),
        ]);
        FilterInstance selected = new(definition);
        selected.SetStageParameters(1, [
            new("CutoffHz", FilterParameterValue.FromNumber(150)),
            new("TapCount", FilterParameterValue.FromNumber(127)),
            new("TransitionWidthHz", FilterParameterValue.FromNumber(50)),
        ]);
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(225));
        PlaybackSession automated = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = selected,
            }, new SetNoteFilterParameterEnvelopeCommand(1, "CutoffHz", Envelope))));
        PlaybackSession reference = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = selected,
            }),
            Event(1, 0, new SetNoteFilterParametersCommand(1, [
                new("CutoffHz", FilterParameterValue.FromNumber(225)),
            ]))));

        float[] actual = new float[10], expected = new float[10];
        automated.Render(0, 1, actual.AsSpan(0, 1));
        reference.Render(0, 1, expected.AsSpan(0, 1));
        Assert.That(automated.PreparePendingNoteFilterEnvelopeControls(),
            Is.EqualTo(1), "kernel design is explicitly off the PCM render loop");
        automated.Render(1, 9, actual.AsSpan(1, 9));
        reference.Render(1, 9, expected.AsSpan(1, 9));
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
        Assert.That(automated.PreparePendingNoteFilterEnvelopeControls(),
            Is.EqualTo(0));
    }


    [Test]
    public void AuthoredNoteEnvelopeReferenceMatchesTypedParameterEnvelopeAndNumericEdits()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        var names = Names();
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterAssignmentsCommand(
                "echo.Mix = 0.6\necho.Feedforward = @\"Sweep\""))));
        authored.FilterAssignmentNames = names;
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterParametersCommand(1, [
                new("Mix", FilterParameterValue.FromNumber(0.6)),
            ]), new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope))));

        float[] actual = new float[10], expected = new float[10];
        authored.Render(0, 10, actual);
        typed.Render(0, 10, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
        Assert.That(authored.GetChannelState(0).CurrentVoice!
            .GetEventFilterConfiguration()!.GetStageParameters(1)
            .GetParameter("Mix").NumericValue, Is.EqualTo(0.6));
    }

    [Test]
    public void AuthoredChannelEnvelopeMatchesTypedCommandOnPhysicalHost()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new SetChannelFilterCommand { Configuration = Echo() },
                new StartNoteCommand(Source),
                new SetChannelFilterAssignmentsCommand(
                    "echo.Mix = 0.75\necho.Feedforward = @\"Sweep\""))));
        authored.FilterAssignmentNames = Names();
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new SetChannelFilterCommand { Configuration = Echo() },
                new StartNoteCommand(Source),
                new SetChannelFilterParametersCommand(1, [
                    new("Mix", FilterParameterValue.FromNumber(0.75)),
                ]), new SetChannelFilterParameterEnvelopeCommand(
                    1, "Feedforward", Envelope))));

        float[] actual = new float[10], expected = new float[10];
        authored.Render(0, 10, actual);
        typed.Render(0, 10, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }

    [Test]
    public void FrozenNameResolutionSurvivesAuthoringRenameAndSkipsInvalidSiblings()
    {
        SongDocument song = new();
        NodeEnvelopeDefinition original = new(Envelope, "Sweep");
        song.Add(original);
        var frozen = FilterAssignmentEnvelopeNames.Capture(song);
        original.Name = "Later";
        song.Add(new ScriptEnvelopeDefinition((ObjectId)12099U, "Sweep"));

        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterAssignmentsCommand(
                "unknown.CutoffHz = @\"Sweep\"\n"
                + "echo.Feedforward = @\"Sweep\"\n"
                + "echo.DelaySeconds = @\"Sweep\"\n"
                + "echo.Mix = @\"Missing\"\n"
                + "echo.Mix = 0.8"))));
        authored.FilterAssignmentNames = frozen;
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope),
                new SetNoteFilterParametersCommand(1, [
                    new("Mix", FilterParameterValue.FromNumber(0.8)),
                ]))));

        float[] actual = new float[10], expected = new float[10];
        authored.Render(0, 10, actual);
        typed.Render(0, 10, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }

    [Test]
    public void LaterLiteralOverridesEarlierEnvelopeWithinOneAssignmentText()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = @\"Sweep\"\necho.Feedforward = 0.8"))));
        authored.FilterAssignmentNames = Names();
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.8)),
            ]))));
        float[] actual = new float[7], expected = new float[7];
        authored.Render(0, 7, actual);
        typed.Render(0, 7, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }

    [Test]
    public void AuthoredFlattenedEnvelopeControlsOnlyItsOwnSourceDescendants()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession authored = Session(resolver, Schedule());
        PlaybackSession typed = Session(resolver, Schedule());
        authored.FilterAssignmentNames = Names();
        foreach (PlaybackSession session in new[] { authored, typed })
        {
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                new BeginFlattenedSourceVolumeCommand(1, 1) {
                    EventFilterConfiguration = Echo(),
                },
            ]);
            foreach (uint id in new uint[] { 7, 8 })
                session.ApplyScopedEvent(0, ChannelTarget.Virtual(id), [
                    new StartNoteCommand(Source) {
                        ParentSourceScopes = new long[] { 1 },
                    },
                ]);
        }
        authored.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = @\"Sweep\""),
        ]);
        typed.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope),
        ]);
        float[] actual = new float[8], expected = new float[8];
        authored.Render(0, 8, actual);
        typed.Render(0, 8, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }


    [Test]
    public void SubsequentNumericTextDetachesOnlyPreviouslyBoundNoteParameter()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = @\"Sweep\"")),
            Event(4, 0, new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = 0.8"))));
        authored.FilterAssignmentNames = Names();
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }, new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope)),
            Event(4, 0, new SetNoteFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.8)),
            ]), new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward"))));

        float[] actual = new float[10], expected = new float[10];
        authored.Render(0, 10, actual);
        typed.Render(0, 10, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
        Assert.That(actual[3], Is.Not.EqualTo(actual[5]),
            "after detachment the newly authored numeric base takes effect");
    }

    [Test]
    public void SubsequentNumericTextDetachesOnlyPreviouslyBoundHostParameter()
    {
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new SetChannelFilterCommand { Configuration = Echo() },
                new StartNoteCommand(Source),
                new SetChannelFilterAssignmentsCommand(
                    "echo.Feedforward = @\"Sweep\"")),
            Event(4, 0, new SetChannelFilterAssignmentsCommand(
                "echo.Feedforward = 0.8"))));
        authored.FilterAssignmentNames = Names();
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new SetChannelFilterCommand { Configuration = Echo() },
                new StartNoteCommand(Source),
                new SetChannelFilterParameterEnvelopeCommand(
                    1, "Feedforward", Envelope)),
            Event(4, 0, new SetChannelFilterParametersCommand(1, [
                new("Feedforward", FilterParameterValue.FromNumber(0.8)),
            ]), new SetChannelFilterParameterEnvelopeCommand(1, "Feedforward"))));
        float[] actual = new float[10], expected = new float[10];
        authored.Render(0, 10, actual);
        typed.Render(0, 10, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
    }

    [Test]
    public void ReplacingEnvelopeByAnotherNameUsesNewCurveOnTheExactHost()
    {
        // A second identity bound by text replaces the previously attached
        // name, with no resurrection of the first reference at future rows.
        SongDocument namesSong = new();
        namesSong.Add(new NodeEnvelopeDefinition(Envelope, "Sweep"));
        namesSong.Add(new ScriptEnvelopeDefinition((ObjectId)12099U, "Other"));
        var frozen = FilterAssignmentEnvelopeNames.Capture(namesSong);
        // The resolver intentionally only implements the first curve;
        // the second binding is rejected without erasing the first.
        TestResolver resolver = new(new ConstantSound(), new FixedCurve(0.5));
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new SetChannelFilterCommand { Configuration = Echo() },
                new StartNoteCommand(Source),
                new SetChannelFilterAssignmentsCommand(
                    "echo.Feedforward = @\"Sweep\"")),
            Event(4, 0, new SetChannelFilterAssignmentsCommand(
                "echo.Feedforward = @\"Other\""))));
        authored.FilterAssignmentNames = frozen;
        List<string> diagnostics = [];
        authored.ChannelFilterCommandRejected = diagnostics.Add;
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new SetChannelFilterCommand { Configuration = Echo() },
                new StartNoteCommand(Source),
                new SetChannelFilterParameterEnvelopeCommand(1, "Feedforward", Envelope))));
        float[] actual = new float[10], expected = new float[10];
        authored.Render(0, 10, actual);
        typed.Render(0, 10, expected);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6f),
            "rejected replacement must preserve the earlier host binding");
        Assert.That(diagnostics, Has.Count.EqualTo(1));
    }


    [Test]
    public void LateSongNoteBindingStartsAtAssignmentFrameNotNoteStartOrSongZero()
    {
        TestResolver resolver = new(new ConstantSound(), new RampCurve());
        FilterInstance selected = Echo();
        PlaybackSession authored = Session(resolver, Schedule(
            Event(1, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = selected,
            }),
            Event(5, 0, new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = @\"Sweep\""))));
        authored.FilterAssignmentNames = Names();

        PlaybackSession typed = Session(resolver, Schedule(
            Event(1, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = selected,
            }),
            Event(5, 0, new SetNoteFilterParameterEnvelopeCommand(
                1, "Feedforward", Envelope))));

        // The expected control surface is indexed relative to frame FIVE,
        // not the note's start at frame one. Each target is sent on precisely
        // the same frame as the corresponding live envelope sample.
        NoteScheduleBuilder referenceBuilder = new();
        referenceBuilder.Append(Event(1, 0, new StartNoteCommand(Source) {
            EventFilterConfiguration = selected,
        }));
        for (int frame = 5; frame < 11; frame++)
            referenceBuilder.Append(Event(frame, 0, new SetNoteFilterParametersCommand(
                1, [new("Feedforward", FilterParameterValue.FromNumber(
                    0.2 + (frame - 5) * 0.1))])));
        PlaybackSession reference = Session(resolver, referenceBuilder.Freeze());

        float[] output = new float[11], typedOutput = new float[11],
            perFrame = new float[11], partitioned = new float[11];
        authored.Render(0, output.Length, output);
        typed.Render(0, typedOutput.Length, typedOutput);
        reference.Render(0, perFrame.Length, perFrame);
        Assert.That(output, Is.EqualTo(typedOutput).Within(1e-6f),
            "authored text must select the identical absolute-frame origin as typed attachment");
        Assert.That(output, Is.EqualTo(perFrame).Within(1e-6f),
            "the resulting curve must start at t=0 on frame five");
        Assert.That(output[0], Is.Zero);

        PlaybackSession split = Session(resolver, Schedule(
            Event(1, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = selected,
            }),
            Event(5, 0, new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = @\"Sweep\""))));
        split.FilterAssignmentNames = Names();
        split.Render(0, 3, partitioned.AsSpan(0, 3));
        split.Render(3, 3, partitioned.AsSpan(3, 3));
        split.Render(6, 5, partitioned.AsSpan(6, 5));
        Assert.That(partitioned, Is.EqualTo(output).Within(1e-6f),
            "device and offline block partitions must be sample identical");
    }

    [Test]
    public void LateChannelBindingPersistsThroughReplacementNotesAndHasHostClock()
    {
        TestResolver resolver = new(new ConstantSound(), new RampCurve());
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new SetChannelFilterCommand { Configuration = Echo() },
                new StartNoteCommand(Source)),
            Event(4, 0, new SetChannelFilterAssignmentsCommand(
                "echo.Feedforward = @\"Sweep\"")),
            Event(6, 0, new StartNoteCommand(Source))));
        authored.FilterAssignmentNames = Names();
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new SetChannelFilterCommand { Configuration = Echo() },
                new StartNoteCommand(Source)),
            Event(4, 0, new SetChannelFilterParameterEnvelopeCommand(
                1, "Feedforward", Envelope)),
            Event(6, 0, new StartNoteCommand(Source))));

        float[] actual = new float[14], reference = new float[14];
        authored.Render(0, actual.Length, actual);
        typed.Render(0, reference.Length, reference);
        Assert.That(actual, Is.EqualTo(reference).Within(1e-6f),
            "channel modulation must neither reset at a later note nor migrate to note time");
        Assert.That(actual[7], Is.GreaterThan(0));
    }

    [Test]
    public void LateNoteAttachmentReleasesAtOffWithoutRetargetingReplacementNote()
    {
        TestResolver resolver = new(new ConstantSound(), new ReleaseCurve());
        PlaybackSession authored = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }),
            Event(2, 0, new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = @\"Sweep\"")),
            Event(4, 0, new NoteOffCommand()),
            Event(5, 0, new StartNoteCommand(Source))));
        authored.FilterAssignmentNames = Names();
        PlaybackSession typed = Session(resolver, Schedule(
            Event(0, 0, new StartNoteCommand(Source) {
                EventFilterConfiguration = Echo(),
            }),
            Event(2, 0, new SetNoteFilterParameterEnvelopeCommand(
                1, "Feedforward", Envelope)),
            Event(4, 0, new NoteOffCommand()),
            Event(5, 0, new StartNoteCommand(Source))));

        float[] actual = new float[12], reference = new float[12];
        authored.Render(0, actual.Length, actual);
        typed.Render(0, reference.Length, reference);
        Assert.That(actual, Is.EqualTo(reference).Within(1e-6f));
    }

    [Test]
    public void LateFlattenedReferenceTracksControllerAndLaterDescendantAtOriginalFrame()
    {
        TestResolver resolver = new(new ConstantSound(), new RampCurve());
        PlaybackSession authored = Session(resolver, Schedule());
        PlaybackSession typed = Session(resolver, Schedule());
        authored.FilterAssignmentNames = Names();
        foreach (PlaybackSession session in new[] { authored, typed })
        {
            session.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
                new BeginFlattenedSourceVolumeCommand(20, 1) {
                    EventFilterConfiguration = Echo(),
                },
            ]);
            session.ApplyScopedEvent(0, ChannelTarget.Virtual(7), [
                new StartNoteCommand(Source) {
                    ParentSourceScopes = new long[] { 20 },
                },
            ]);
        }

        float[] actual = new float[9], reference = new float[9];
        authored.Render(0, 3, actual.AsSpan(0, 3));
        typed.Render(0, 3, reference.AsSpan(0, 3));
        authored.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterAssignmentsCommand(
                "echo.Feedforward = @\"Sweep\""),
        ]);
        typed.ApplyScopedEvent(0, ChannelTarget.Physical(0), [
            new SetNoteFilterParameterEnvelopeCommand(1, "Feedforward", Envelope),
        ]);
        authored.Render(3, 3, actual.AsSpan(3, 3));
        typed.Render(3, 3, reference.AsSpan(3, 3));
        foreach (PlaybackSession session in new[] { authored, typed })
            session.ApplyScopedEvent(0, ChannelTarget.Virtual(8), [
                new StartNoteCommand(Source) {
                    ParentSourceScopes = new long[] { 20 },
                },
            ]);
        authored.Render(6, 3, actual.AsSpan(6, 3));
        typed.Render(6, 3, reference.AsSpan(6, 3));
        Assert.That(actual, Is.EqualTo(reference).Within(1e-6f),
            "later descendants must use the controller's earlier attachment frame");
    }

    private static FilterAssignmentEnvelopeNames Names()
    {
        SongDocument song = new();
        song.Add(new NodeEnvelopeDefinition(Envelope, "Sweep"));
        return FilterAssignmentEnvelopeNames.Capture(song);
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
    private sealed class ReleaseCurve : IEnvelopeCurve
    {
        public double GetValue(long activeFrame, int sampleRate, long? off)
            => off.HasValue ? 0.1 : 0.5;
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

using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

/// <summary>
/// Real Pattern-grid S7x, SFx/Zxx and Vxx commands must use the same
/// incremental effect-memory and event mapping as eager tracker processing.
/// The incremental producer is consumed only as due rows begin.
/// </summary>
[TestFixture]
public sealed class IncrementalAdvancedTrackerEffectsTests
{
    [TestCase(TrackerEnvelopeControlTarget.Volume)]
    [TestCase(TrackerEnvelopeControlTarget.Panning)]
    [TestCase(TrackerEnvelopeControlTarget.PitchOrFilter)]
    public void S7xAppliedToSameCellNoteMatchesEagerTrackerEvents(
        TrackerEnvelopeControlTarget target)
    {
        DataPatternDefinition pattern = Pattern(2, 1);
        PatternCell first = pattern.Grid.GetOrCreateCell(0, 0);
        first.Note = new StartPatternNote((ObjectId)17U);
        first.Effects.Add(new TrackerEnvelopeControlPatternEffect(target, false));
        pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
            new TrackerEnvelopeControlPatternEffect(target, true));

        Compare(pattern);
    }

    [Test]
    public void SfAndZMacrosResolveAtOwnRowAndRetainIsolatedChannelMemory()
    {
        DataPatternDefinition pattern = Pattern(3, 2);
        pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new TrackerMidiMacroSelectPatternEffect(1));
        pattern.Grid.GetOrCreateCell(0, 1).Effects.Add(
            new TrackerMidiMacroPatternEffect(0x20));
        pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
            new TrackerMidiMacroPatternEffect(0x40));
        pattern.Grid.GetOrCreateCell(2, 1).Effects.Add(
            new TrackerMidiMacroPatternEffect(0x60));

        TrackerMidiMacroConfiguration macros =
            TrackerMidiMacroConfiguration.CreateImpulseTrackerDefault();
        macros.SetParameterizedMacro(1, "F0 F0 01 z");
        SequencingContext eager = new(trackerMidiMacros: macros);
        SequencingContext live = new(trackerMidiMacros: macros);
        Compare(pattern, eager, live);
        Assert.Multiple(() =>
        {
            Assert.That(live.GetPhysicalChannelState(0).MidiMacroIndex,
                Is.EqualTo(1));
            Assert.That(live.GetPhysicalChannelState(1).MidiMacroIndex,
                Is.Zero);
        });
    }

    [Test]
    public void PhysicalChannelVxxAndWxxAreResolvedOnlyOnTheirRows()
    {
        DataPatternDefinition pattern = Pattern(3, 1);
        pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new TrackerGlobalVolumePatternEffect(64));
        pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
            new TrackerGlobalVolumeSlidePatternEffect(0x20));
        pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
            new TrackerGlobalVolumeSlidePatternEffect(0x00));
        Compare(pattern);
    }

    private static void Compare(DataPatternDefinition pattern,
        SequencingContext? eagerContext = null,
        SequencingContext? liveContext = null)
    {
        eagerContext ??= new SequencingContext();
        liveContext ??= new SequencingContext();
        NoteScheduleBuilder eagerSchedule = new();
        PatternNoteProcessor.GenerateNotes(pattern, eagerContext, eagerSchedule,
            out TimeSpan duration);
        NoteEvent[] expected = eagerSchedule.Freeze().ToArray();

        using IncrementalPatternTimeline timeline = new(liveContext);
        timeline.Add(pattern, pattern.RowCount, liveContext);
        List<NoteEvent> actual = [];
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit emitted)
                actual.Add(emitted.Note);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Has.Count.EqualTo(expected.Length));
            Assert.That(timeline.Elapsed.TotalSeconds,
                Is.EqualTo(duration.TotalSeconds).Within(1e-6));
        });
        for (int i = 0; i < Math.Min(actual.Count, expected.Length); i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual[i].Target, Is.EqualTo(expected[i].Target));
                Assert.That(actual[i].Commands, Is.EqualTo(expected[i].Commands),
                    $"Resolved commands and effect memory at event {i}");
                Assert.That(actual[i].Offset.TimeOffset.TotalSeconds,
                    Is.EqualTo(expected[i].Offset.TimeOffset.TotalSeconds)
                        .Within(1e-6));
            });
        }
    }

    private static DataPatternDefinition Pattern(int rows, int channels)
        => new((ObjectId)1U, "Effects")
        {
            RowCount = rows,
            ChannelCount = channels,
        };
}

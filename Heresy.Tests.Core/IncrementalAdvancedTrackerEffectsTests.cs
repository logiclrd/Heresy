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

    [TestCase("Arpeggio")]
    [TestCase("Tremolo")]
    [TestCase("Tremor")]
    [TestCase("Panbrello")]
    public void RowScopedModulationsMatchEagerEffectsAndCleanup(string kind)
    {
        DataPatternDefinition pattern = Pattern(2, 1);
        PatternCell first = pattern.Grid.GetOrCreateCell(0, 0);
        first.Note = new StartPatternNote((ObjectId)10U);
        first.Effects.Add(kind switch
        {
            "Arpeggio" => new ArpeggioPatternEffect(0x37),
            "Tremolo" => new TremoloPatternEffect(0x48),
            "Tremor" => new TremorPatternEffect(0x34),
            _ => new PanbrelloPatternEffect(0x48),
        });
        if (kind == "Tremolo")
            first.Effects.Insert(0, new TrackerTremoloWaveformPatternEffect(2));
        if (kind == "Panbrello")
            first.Effects.Insert(0, new TrackerPanbrelloWaveformPatternEffect(1));
        PatternCell second = pattern.Grid.GetOrCreateCell(1, 0);
        second.Effects.Add(kind switch
        {
            "Arpeggio" => new ArpeggioPatternEffect(0),
            "Tremolo" => new TremoloPatternEffect(0),
            "Tremor" => new TremorPatternEffect(0),
            _ => new PanbrelloPatternEffect(0),
        });

        Compare(pattern);
    }

    [TestCase("Tremor")]
    [TestCase("Panbrello")]
    public void SEyRepeatsCapturedModulationMemoryAtOwnRowTicks(string kind)
    {
        DataPatternDefinition pattern = Pattern(1, 1);
        PatternCell first = pattern.Grid.GetOrCreateCell(0, 0);
        first.Note = new StartPatternNote((ObjectId)12U);
        first.Effects.Add(new TrackerPatternDelayPatternEffect(2));
        first.Effects.Add(kind == "Tremor"
            ? new TremorPatternEffect(0x34)
            : new PanbrelloPatternEffect(0x48));
        Compare(pattern);
    }

    [Test]
    public void SEyRepeatsTonePortamentoWithoutReinstatingTargetNote()
    {
        DataPatternDefinition pattern = Pattern(1, 1);
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.Effects.Add(new TonePortamentoPatternEffect(4));
        cell.Effects.Add(new TrackerPatternDelayPatternEffect(2));
        Compare(pattern);
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void S1xGlissandoAffectsLaterTonePortamentoAtItsOwnRow(byte enabled)
    {
        DataPatternDefinition pattern = Pattern(3, 1);
        pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new TrackerGlissandoControlPatternEffect(enabled));
        pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
            new TonePortamentoPatternEffect(4));
        pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
            new TrackerGlissandoControlPatternEffect(0));

        SequencingContext eager = new();
        SequencingContext live = new();
        Compare(pattern, eager, live);
        Assert.Multiple(() =>
        {
            Assert.That(live.GetPhysicalChannelState(0).GlissandoEnabled, Is.False);
            Assert.That(eager.GetPhysicalChannelState(0).GlissandoEnabled, Is.False);
        });
    }

    [Test]
    public void SAxPersistsThroughOxxAndExplicitSA0ClearsHighNibble()
    {
        DataPatternDefinition pattern = Pattern(4, 1);
        pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new SampleOffsetHighPatternEffect(3));
        PatternCell offset = pattern.Grid.GetOrCreateCell(1, 0);
        offset.Note = new StartPatternNote((ObjectId)10U);
        offset.Effects.Add(new SampleOffsetPatternEffect(0x04));
        pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
            new SampleOffsetHighPatternEffect(0));
        PatternCell reset = pattern.Grid.GetOrCreateCell(3, 0);
        reset.Note = new StartPatternNote((ObjectId)10U);
        reset.Effects.Add(new SampleOffsetPatternEffect(0x00));

        SequencingContext eager = new();
        SequencingContext live = new();
        Compare(pattern, eager, live);
        Assert.Multiple(() =>
        {
            Assert.That(live.GetPhysicalChannelState(0).SampleOffsetHigh, Is.Zero);
            Assert.That(live.GetPhysicalChannelState(0).TryGetEffectParameter(
                EffectMemorySlot.SampleOffset, out byte remembered), Is.True);
            Assert.That(remembered, Is.EqualTo(0x04));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EffectColumnPanOverridesVolumeColumnRegardlessOfStoredOrder(
        bool effectFirst)
    {
        DataPatternDefinition pattern = Pattern(2, 1);
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        PatternEffect effectPan = new TrackerPanning8BitPatternEffect(0);
        PatternEffect volumePan = new TrackerVolumeColumnPanningPatternEffect(64);
        if (effectFirst)
        {
            cell.Effects.Add(effectPan);
            cell.Effects.Add(volumePan);
        }
        else
        {
            cell.Effects.Add(volumePan);
            cell.Effects.Add(effectPan);
        }
        pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
            new TrackerPanningPatternEffect(15));
        Compare(pattern);
    }

    [Test]
    public void SurroundSuppressesVolumeColumnPanInSameCell()
    {
        DataPatternDefinition pattern = Pattern(1, 1);
        PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
        cell.Effects.Add(new TrackerVolumeColumnPanningPatternEffect(0));
        cell.Effects.Add(new TrackerSurroundPatternEffect());
        Compare(pattern);
    }

    [Test]
    public void S8xXxxAndVolumePanRunAtFutureRowNotOnCurrentRow()
    {
        DataPatternDefinition pattern = Pattern(3, 1);
        pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
            new TrackerPanning8BitPatternEffect(0));
        pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
            new TrackerVolumeColumnPanningPatternEffect(64));
        pattern.Grid.GetOrCreateCell(2, 0).Effects.Add(
            new TrackerPanningPatternEffect(15));
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

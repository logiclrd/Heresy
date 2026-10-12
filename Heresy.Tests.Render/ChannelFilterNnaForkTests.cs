using System;
using System.Collections.Generic;
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

/// <summary>Verify the dynamic NNA output host is a real independent fork
/// of the persistent physical/virtual host and not an alias or reset DSP.</summary>
[TestFixture]
public sealed class ChannelFilterNnaForkTests
{
    private const int Rate = 1000;
    private static readonly ObjectId SoundId = (ObjectId)34001U;
    private static readonly ObjectId CurveId = (ObjectId)34002U;

    [Test]
    public void StereoConvolutionHistoriesAreExactAtTheFirstForkFrameAndDivergeAfterStructuralEdit()
    {
        ChannelOutputFilterState original = new(Echo(34010, 0.002), Rate, 2);
        float[] prior = [1, -1];
        original.ProcessInterleaved(prior, 0, null);
        ChannelOutputFilterState displaced = original.Fork();
        Assert.That(displaced, Is.Not.SameAs(original));
        Assert.That(displaced.SnapshotConfiguration().DefinitionId,
            Is.EqualTo(original.SnapshotConfiguration().DefinitionId));

        float[] originFrame = [0.75f, 0.25f];
        float[] virtualFrame = (float[])originFrame.Clone();
        original.ProcessInterleaved(originFrame, 1, null);
        displaced.ProcessInterleaved(virtualFrame, 1, null);
        Assert.That(virtualFrame, Is.EqualTo(originFrame),
            "the very first virtual frame carries both stereo DSP histories");

        ChannelOutputFilterState edited = original.PrepareParameterUpdate(
            1, [new("DelaySeconds", FilterParameterValue.FromNumber(0.001))], 2);
        float[] physicallyEdited = new float[8];
        float[] independentlyContinued = new float[8];
        edited.ProcessInterleaved(physicallyEdited, 2, null);
        displaced.ProcessInterleaved(independentlyContinued, 2, null);
        Assert.That(physicallyEdited, Is.Not.EqualTo(independentlyContinued),
            "editing the original host's structural delay does not resize the NNA ring");
        Assert.That(independentlyContinued[0], Is.EqualTo(1f));
        Assert.That(independentlyContinued[1], Is.EqualTo(-1f));
    }

    [Test]
    public void ActiveEnvelopeAndInProgressIirInterpolationForkAtTheExactFrame()
    {
        FilterInstance filter = Pole(34020, 65);
        ChannelOutputFilterState original = new(filter, Rate, 1);
        original = original.PrepareParameterEnvelopeUpdate(
            1, "CutoffHz", new MovingCurve(), 0);
        float[] warmup = new float[9];
        for (int i = 0; i < warmup.Length; i++)
            warmup[i] = i == 0 ? 1 : 0.1f;
        original.ProcessInterleaved(warmup, 0, null);

        ChannelOutputFilterState displaced = original.Fork();
        float[] firstPhysical = [0.7f];
        float[] firstVirtual = [0.7f];
        original.ProcessInterleaved(firstPhysical, 9, null);
        displaced.ProcessInterleaved(firstVirtual, 9, null);
        Assert.That(firstVirtual, Is.EqualTo(firstPhysical),
            "a fork must carry accepted numeric targets, smoothing progress and histories");

        original = original.PrepareParameterEnvelopeUpdate(
            1, "CutoffHz", null, 10);
        float[] continuedPhysical = new float[32];
        float[] continuedVirtual = new float[32];
        Array.Fill(continuedPhysical, 0.4f);
        Array.Fill(continuedVirtual, 0.4f);
        original.ProcessInterleaved(continuedPhysical, 10, null);
        displaced.ProcessInterleaved(continuedVirtual, 10, null);
        Assert.That(continuedVirtual, Is.Not.EqualTo(continuedPhysical),
            "the displaced channel keeps modulating after the original detaches");
        Assert.That(displaced.SnapshotConfiguration()
            .GetStageParameters(1).GetParameter("CutoffHz").NumericValue,
            Is.EqualTo(65), "the independent bound override never rewrites the authored base");
    }

    [Test]
    public void NnaDisplacementCopiesOldDelayExactlyBeforeSameFramePhysicalReplacement()
    {
        FilterInstance oldFilter = Echo(34030, 0.001);
        FilterInstance newFilter = Echo(34031, 0.002);
        PlaybackSession session = CreateSession(new LongPulse(), Schedule(
            Event(0, new SetChannelFilterCommand { Configuration = oldFilter },
                new StartNoteCommand(SoundId)
                {
                    EventFilterConfiguration = Pole(34032, 110),
                }),
            Event(1, new StartNoteCommand(SoundId),
                new SetChannelFilterCommand { Configuration = newFilter })));

        float[] first = new float[1];
        session.Render(0, 1, first);
        PlaybackVoice oldVoice = session.GetChannelState(0).CurrentVoice!;
        var oldEventDsp = oldVoice.EventFilterState;
        Assert.That(oldEventDsp, Is.Not.Null);
        float[] after = new float[5];
        session.Render(1, after.Length, after);

        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice displaced = session.VirtualVoices[0];
        Assert.That(displaced, Is.SameAs(oldVoice),
            "the original voice, and not a freshly constructed note, migrates");
        Assert.That(displaced.EventFilterState, Is.SameAs(oldEventDsp),
            "note-local event DSP stays with the displaced voice");
        Assert.That(session.GetChannelState(0).CurrentVoice!.EventFilterState,
            Is.Null, "new notes do not inherit the previous note's event Filter");
        Assert.That(displaced.DisplacedOutputFilterState, Is.Not.Null);
        Assert.That(displaced.DisplacedOutputFilterState!.SnapshotConfiguration()
            .GetStageParameters(1).GetParameter("DelaySeconds").NumericValue,
            Is.EqualTo(0.001));
        Assert.That(first[0], Is.GreaterThan(0));
        Assert.That(after[0], Is.GreaterThan(0),
            "the first NNA PCM frame includes the old host's echo history");
        Assert.That(after[2], Is.GreaterThan(0),
            "the continuing physical host independently emits its new delay-two tail");
    }

    [Test]
    public void FirstDisplacedVirtualFrameIncludesCopiedOldHistoryWithExactPcm()
    {
        // The prior host has a one-frame delay history from the first note.
        // At frame one the old voice continues on a fork and the physical
        // host is re-selected with a new two-frame delay. The old note has
        // no fresh excitation, so its *first virtual frame* outputs exactly
        // the copied original host's pending echo.
        PlaybackSession session = CreateSession(new LongPulse(), Schedule(
            Event(0, new SetChannelFilterCommand {
                    Configuration = Echo(34050, 0.001),
                }, new StartNoteCommand(SoundId)),
            Event(1, new StartNoteCommand(SoundId),
                new SetChannelFilterCommand {
                    Configuration = Echo(34051, 0.002),
                })));
        float[] entire = new float[6];
        session.Render(0, entire.Length, entire);
        Assert.That(entire, Is.EqualTo(new float[] { 1, 2, 0, 1, 0, 0 }),
            "the fork's first output sample retains its prior ring history; "
            + "the physical host's next delay is independent");

        PlaybackSession partitioned = CreateSession(new LongPulse(), Schedule(
            Event(0, new SetChannelFilterCommand {
                    Configuration = Echo(34052, 0.001),
                }, new StartNoteCommand(SoundId)),
            Event(1, new StartNoteCommand(SoundId),
                new SetChannelFilterCommand {
                    Configuration = Echo(34053, 0.002),
                })));
        float[] split = new float[6];
        partitioned.Render(0, 1, split.AsSpan(0, 1));
        partitioned.Render(1, 2, split.AsSpan(1, 2));
        partitioned.Render(3, 3, split.AsSpan(3, 3));
        Assert.That(split, Is.EqualTo(entire));
    }

    [TestCase(NoteDisplacementAction.Off)]
    [TestCase(NoteDisplacementAction.Fade)]
    public void ReleasedOrFadingNnaRetainsIndependentHostHistory(
        NoteDisplacementAction action)
    {
        PlaybackSession session = CreateSession(new LongPulse(), Schedule(
            Event(0, new SetChannelFilterCommand {
                    Configuration = Echo(34060, 0.001),
                }, new StartNoteCommand(SoundId),
                new SetCurrentVoiceDisplacementActionCommand(action)),
            Event(1, new StartNoteCommand(SoundId))));
        float[] actual = new float[4];
        session.Render(0, actual.Length, actual);
        Assert.That(actual, Is.EqualTo(new float[] { 1, 3, 1, 0 }),
            $"NNA {action} must preserve the previous echo in both the " +
            "new virtual host and the continuing physical host");
        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        Assert.That(session.VirtualVoices[0].DisplacedOutputFilterState,
            Is.Not.Null);
    }

    [Test]
    public void DisplacedNnaOutputEnvelopeRemainsIndependentAfterPhysicalDetach()
    {
        PlaybackSession session = CreateSession(new LongPulse(), Schedule(
            Event(0, new SetChannelFilterCommand {
                    Configuration = Pole(34040, 55),
                },
                new SetChannelFilterParameterEnvelopeCommand(
                    1, "CutoffHz", CurveId),
                new StartNoteCommand(SoundId)),
            Event(2, new StartNoteCommand(SoundId),
                new SetChannelFilterParameterEnvelopeCommand(1, "CutoffHz"))));
        float[] first = new float[2];
        session.Render(0, 2, first);
        float[] next = new float[1];
        session.Render(2, 1, next);

        Assert.That(session.VirtualVoices, Has.Count.EqualTo(1));
        PlaybackVoice displaced = session.VirtualVoices[0];
        Assert.That(displaced.DisplacedOutputFilterState, Is.Not.Null);
        ChannelOutputFilterState dynamicHost =
            displaced.DisplacedOutputFilterState!;
        // A snapshot fork from the same actual NNA host must render the same
        // following sample; independent physical detach cannot reach it.
        ChannelOutputFilterState probe = dynamicHost.Fork();
        float[] displacedFeed = [0.15f], copiedFeed = [0.15f];
        dynamicHost.ProcessInterleaved(displacedFeed, 3, null);
        probe.ProcessInterleaved(copiedFeed, 3, null);
        Assert.That(displacedFeed, Is.EqualTo(copiedFeed));
    }

    private static PlaybackSession CreateSession(ISound sound, NoteSchedule schedule)
    {
        Resolver resolver = new(sound);
        return new(new RenderContext(new RenderConfiguration(
                Rate, [new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)])), schedule, resolver)
        {
            NoteFilterEnvelopeResolver = resolver,
        };
    }

    private static NoteSchedule Schedule(params NoteEvent[] events)
    {
        NoteScheduleBuilder result = new();
        foreach (NoteEvent item in events)
            result.Append(item);
        return result.Freeze();
    }

    private static NoteEvent Event(int ms, params NoteCommand[] commands)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(ms), 0),
            ChannelTarget.Physical(0), commands);

    private static FilterInstance Echo(uint id, double seconds)
    {
        FilterDefinition definition = new((ObjectId)id, "Echo", [
            new FilterStageDefinition(1, "echo", FilterKind.CombFeedforward),
        ]);
        FilterInstance filter = new(definition);
        filter.SetStageParameters(1, [
            new("DelaySeconds", FilterParameterValue.FromNumber(seconds)),
            new("Feedforward", FilterParameterValue.FromNumber(1)),
            new("Mix", FilterParameterValue.FromNumber(1)),
        ]);
        return filter;
    }

    private static FilterInstance Pole(uint id, double hz)
    {
        FilterDefinition definition = new((ObjectId)id, "Pole", [
            new FilterStageDefinition(1, "pole", FilterKind.OnePole),
        ]);
        FilterInstance filter = new(definition);
        filter.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(hz));
        return filter;
    }

    private sealed class MovingCurve : IEnvelopeCurve
    {
        public double GetValue(long frame, int sampleRate, long? off)
            => 140 + Math.Min(frame, 120) * 2.0;
    }

    private sealed class LongPulse : ISound
    {
        public NoteConfigurationSnapshot SnapshotNoteConfiguration()
            => new(NewNotePolicy.Continue);
        public SoundState CreateState() => new SoundStateProbe();
        public long? GetEndFrameExclusive(RenderContext context, SoundState state) => 12;
        public void Render(RenderContext context, SoundState state,
            long startFrame, int frameCount, Span<float> destination)
        {
            if (startFrame == 0 && frameCount > 0)
                destination[0] += 1;
        }
    }

    private sealed class SoundStateProbe : SoundState { }

    private sealed class Resolver(ISound sound) : ISoundResolver, IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId id, bool mixdown, out ISound? result)
        {
            result = id == SoundId ? sound : null;
            return result is not null;
        }
        public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
        {
            curve = id == CurveId ? new MovingCurve() : null;
            return curve is not null;
        }
    }
}

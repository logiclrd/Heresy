using System;
using System.Collections.Generic;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.Instruments;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class PlaybackDelayedEnvelopeLifecycleTests
{
    [Test]
    public void DelayedTrackerEnvelopePauseAndResumeKeepsInstrumentSamplePlayingThroughRelease()
    {
        ObjectId instrumentId = (ObjectId)1U;
        ObjectId sampleId = (ObjectId)2U;
        ObjectId envelopeId = (ObjectId)3U;

        // Same looping PCM must be able to sustain an Instrument release
        // without changing the direct SampleSound Note Off contract.
        SampleDefinition sampleDefinition = new(
            sampleId, "Looping PCM", new ExternalAssetReference("pcm.raw"))
        {
            Loop = new SampleLoop(SampleLoopMode.Forward, 0, 4),
        };
        SampleSound sample = new(
            sampleDefinition,
            new MemorySampleData(1000, 1, [1f, 1f, 1f, 1f]));
        SoundResolver sounds = new();
        sounds.Add(sampleId, sample);
        AdsrEnvelopeCurve volume = new(
            new AdsrEnvelopeDefinition(envelopeId, "Release envelope")
            {
                SustainLevel = 1.0,
                Release = TimeSpan.FromMilliseconds(40),
            });
        InstrumentDefinition definition = new(instrumentId, "Tone")
        {
            Divisions = 12.0,
        };
        definition.ToneSpecifications.Add(new ToneSpecification
        {
            SourceId = sampleId,
            VolumeEnvelopeId = envelopeId,
        });
        definition.ToneTable.Add(0);
        sounds.Add(instrumentId,
            new InstrumentSound(definition, sounds,
                new EnvelopeResolver(envelopeId, volume)));

        SequencingContext context = new();
        using IncrementalPatternTimeline timeline = new(context);
        timeline.Add(new RawSource(
            At(0, new StartNoteCommand(instrumentId)),
            At(10, new NoteOffCommand()),
            At(20, new ApplyTrackerEnvelopeControlCommand(
                TrackerEnvelopeControlTarget.Volume, false)),
            At(50, new ApplyTrackerEnvelopeControlCommand(
                TrackerEnvelopeControlTarget.Volume, true))),
            rowCount: 1, context: context);
        NoteScheduleBuilder builder = new();
        while (timeline.TryStep(out IncrementalPatternTimelineStep? step))
            if (step is IncrementalPatternTimelineStep.Emit emitted)
                builder.Append(emitted.Note);
        PlaybackSession session = new(
            new RenderContext(new RenderConfiguration(1000,
                [new OutputChannelConfiguration(Vector3.Zero,
                    positionalImportance: 0)])),
            builder.Freeze(), sounds);

        float[] output = new float[95];
        session.Render(0, 25, output.AsSpan(0, 25));
        Assert.That(session.GetChannelState(0).CurrentVoice,
            Is.Not.Null, "The disabled envelope holds a releasing voice alive.");
        session.Render(25, 25, output.AsSpan(25, 25));
        Assert.That(session.GetChannelState(0).CurrentVoice,
            Is.Not.Null, "The held release should not expire at frame 50.");
        session.Render(50, 45, output.AsSpan(50, 45));
        Assert.Multiple(() =>
        {
            Assert.That(output[0], Is.EqualTo(1f).Within(1e-6f));
            Assert.That(output[10], Is.EqualTo(1f).Within(1e-6f));
            Assert.That(output[19], Is.EqualTo(0.775f).Within(1e-6f));
            Assert.That(output[20], Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(output[49], Is.EqualTo(0.75f).Within(1e-6f),
                "The disabled ADSR release must hold its current value.");
            Assert.That(output[50], Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(output[51], Is.EqualTo(0.725f).Within(1e-6f));
            Assert.That(output[79], Is.EqualTo(0.025f).Within(1e-6f));
            Assert.That(output[80], Is.Zero);
            Assert.That(output[94], Is.Zero);
            Assert.That(session.GetChannelState(0).CurrentVoice, Is.Null,
                "After resumption, only the remaining release duration plays.");
        });
    }

    private static NoteEvent At(int wallMilliseconds, params NoteCommand[] commands)
        => new(new MusicalTime(TimeSpan.FromMilliseconds(wallMilliseconds), 0),
            ChannelTarget.Physical(0), commands);

    private sealed class RawSource(params NoteEvent[] notes)
        : IIncrementalRawPatternNoteGenerator
    {
        public IEnumerable<RawPatternStep> EnumerateRawSteps(SequencingContext context)
        {
            foreach (NoteEvent note in notes)
                yield return new RawPatternStep.Emit(note);
        }
    }

    private sealed class SoundResolver : ISoundResolver
    {
        private readonly Dictionary<ObjectId, ISound> _sounds = [];

        public void Add(ObjectId id, ISound sound)
            => _sounds.Add(id, sound);

        public bool TryResolve(ObjectId sourceId, bool mixdown, out ISound? sound)
        {
            if (mixdown)
            {
                sound = null;
                return false;
            }
            return _sounds.TryGetValue(sourceId, out sound);
        }
    }

    private sealed class EnvelopeResolver(ObjectId id, IEnvelopeCurve curve)
        : IEnvelopeCurveResolver
    {
        public bool TryResolve(ObjectId envelopeId, out IEnvelopeCurve? found)
        {
            found = envelopeId == id ? curve : null;
            return found is not null;
        }
    }
}

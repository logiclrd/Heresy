using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Assets;
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
public sealed class FmSynthChildInheritedControlsTests
{
    private const int Rate = 16;
    private static readonly double C4 = 440 * Math.Pow(2.0, -9.0 / 12.0);
    private static readonly ObjectId SampleId = (ObjectId)201U;
    private static readonly ObjectId InstrumentId = (ObjectId)202U;
    private static readonly ObjectId PitchId = (ObjectId)203U;
    private static readonly ObjectId PanningId = (ObjectId)204U;
    private static readonly ObjectId FilterId = (ObjectId)205U;
    private static readonly ObjectId VolumeId = (ObjectId)206U;

    [Test]
    public void InstrumentPitchEnvelopeUsesOctavesAndDoesNotReselectChildTone()
    {
        Registry resolver = new();
        resolver.Add(SampleId, Sample(Enumerable.Range(0, 32)
            .Select(i => (float)i).ToArray(), 1));
        resolver.AddEnvelope(PitchId, new ConstantCurve(1.0));
        resolver.Add(InstrumentId, new InstrumentSound(
            _ => new ToneSpecification { SourceId = SampleId,
                PitchEnvelopeId = PitchId }, resolver, resolver));
        FmSynthSound synth = Synth(resolver);
        SoundInvocation voice = synth.CreateInvocation(1, 1);
        float[] output = new float[4];
        synth.Render(Mono(), voice.State, 0, output.Length, output);
        Assert.Multiple(() =>
        {
            Assert.That(output, Is.EqualTo(new float[] { 0, 2, 4, 6 }));
            Assert.That(resolver.SoundLookups, Is.EqualTo(2),
                "one Synth binding and one initial Instrument selection");
        });
        synth.Render(Mono(), voice.State, 4, 2, new float[2]);
        Assert.That(resolver.SoundLookups, Is.EqualTo(2),
            "Pitch automation must not reselect a tone during PCM");
    }

    [Test]
    public void ChildPitchEnvelopeReleasesOnTheParentNoteOffFrame()
    {
        Registry resolver = new();
        resolver.Add(SampleId, Sample(Enumerable.Range(0, 32)
            .Select(i => (float)i).ToArray(), 1));
        resolver.AddEnvelope(PitchId, new ReleasePitchCurve());
        // The selected Sample's Volume envelope owns release so its native
        // Note Off must not cut the PCM before the Pitch envelope responds.
        resolver.AddEnvelope(VolumeId, new ConstantCurve(1.0));
        resolver.Add(InstrumentId, new InstrumentSound(
            _ => new ToneSpecification { SourceId = SampleId,
                PitchEnvelopeId = PitchId, VolumeEnvelopeId = VolumeId },
            resolver, resolver));
        FmSynthSound synth = Synth(resolver);
        SoundState state = synth.CreateState();
        state.NoteOffTime = TimeSpan.FromSeconds(2.0 / Rate);
        float[] actual = new float[4];
        synth.Render(Mono(), state, 0, actual.Length, actual);
        Assert.That(actual, Is.EqualTo(new float[] { 0, 1, 2, 4 }));
    }

    [Test]
    public void NestedMovingPitchAndLoopedInstrumentReleaseMatchLiveAndPredeclaredTransport()
    {
        Registry resolver = new();
        resolver.Add(SampleId, LoopedSample([0, 1, 2, 3]));
        resolver.AddEnvelope(PitchId, new MovingOctaveCurve());
        resolver.AddEnvelope(VolumeId, new ThreeFrameRelease());
        resolver.Add(InstrumentId, new InstrumentSound(
            _ => new ToneSpecification
            {
                SourceId = SampleId, PitchEnvelopeId = PitchId,
                VolumeEnvelopeId = VolumeId,
            }, resolver, resolver));
        ObjectId innerId = (ObjectId)207U;
        FmSynthSound inner = Synth(resolver);
        resolver.Add(innerId, inner);
        FmSynthSound outer = new(new FmSynthGraph([
            new FmSoundSourceNode(0, innerId, C4),
        ], 0), resolver, soundResolver: resolver);

        RenderContext context = Mono();
        SoundState planned = outer.CreateState();
        planned.PitchTrajectory.SetMultiplier(3, 2);
        planned.PitchTrajectory.SetMultiplier(5, 0.5);
        planned.NoteOffTime = TimeSpan.FromSeconds(6.0 / Rate);
        Assert.That(outer.GetEndFrameExclusive(context, planned), Is.EqualTo(9),
            "the Instrument Volume Envelope owns the release of a looped Sample");
        int resolutionCount = resolver.SoundLookups;
        float[] continuous = new float[12];
        outer.Render(context, planned, 0, continuous.Length, continuous);

        SoundState live = outer.CreateState();
        int liveResolutionCount = resolver.SoundLookups;
        Assert.That(outer.GetEndFrameExclusive(context, live), Is.Null);
        float[] partitioned = new float[12];
        outer.Render(context, live, 0, 3, partitioned.AsSpan(0, 3));
        live.PitchTrajectory.SetMultiplier(3, 2);
        outer.Render(context, live, 3, 2, partitioned.AsSpan(3, 2));
        live.PitchTrajectory.SetMultiplier(5, 0.5);
        outer.Render(context, live, 5, 1, partitioned.AsSpan(5, 1));
        Assert.That(outer.GetEndFrameExclusive(context, live), Is.Null,
            "a sustaining loop is indefinite until an actual Note Off");
        live.NoteOffTime = TimeSpan.FromSeconds(6.0 / Rate);
        Assert.That(outer.GetEndFrameExclusive(context, live), Is.EqualTo(9));
        outer.Render(context, live, 6, 2, partitioned.AsSpan(6, 2));
        outer.Render(context, live, 8, 4, partitioned.AsSpan(8, 4));

        // The pitch Envelope moves 0 -> +1 -> -1 octaves, then starts
        // its release (+1, 0, -1). Combined with transport 1 -> 2 ->
        // 0.5, source positions integrate as:
        // 0,1,2,4,8,9,9.25,10.25,10.75 (then silence at frame 9).
        // The four-frame loop maps positions modulo four, while
        // Volume release weights frames 6..8 by 1, 2/3 and 1/3.
        float[] expected =
            [0, 1, 2, 0, 0, 1, 1.25f, 1.5f, 11f / 12f, 0, 0, 0];
        for (int frame = 0; frame < expected.Length; frame++)
        {
            Assert.That(continuous[frame],
                Is.EqualTo(expected[frame]).Within(1e-5f),
                $"nested integrated PCM frame {frame}; actual [{string.Join(", ", continuous)}], split [{string.Join(", ", partitioned)}]");
            Assert.That(partitioned[frame],
                Is.EqualTo(continuous[frame]).Within(1e-5f),
                $"live transport/chunk parity frame {frame}");
        }
        Assert.Multiple(() =>
        {
            Assert.That(outer.GetEndFrameExclusive(context, planned), Is.EqualTo(9));
            Assert.That(outer.GetEndFrameExclusive(context, live), Is.EqualTo(9));
            Assert.That(resolutionCount, Is.EqualTo(3),
                "Synth references resolve during graph preparation; Instrument selects one Sample per voice");
            Assert.That(liveResolutionCount, Is.EqualTo(4));
            Assert.That(resolver.SoundLookups, Is.EqualTo(liveResolutionCount),
                "audio-rate Envelope and transport changes cannot reselect sources");
        });

        // Replay the same nested pitch/Envelope/loop trajectory from a
        // mid-note control boundary after EOF, and from zero after release.
        int beforeReplay = resolver.SoundLookups;
        outer.PrepareReplay(context, planned, targetFrame: 4);
        Assert.That(resolver.SoundLookups, Is.EqualTo(beforeReplay + 1),
            "the Instrument reselects its Sample at the control boundary");
        int boundLookups = resolver.SoundLookups;
        float[] midReplay = new float[8];
        outer.Render(context, planned, 4, midReplay.Length, midReplay);
        Assert.That(midReplay,
            Is.EqualTo(continuous.AsSpan(4).ToArray()).Within(1e-5f));
        Assert.That(resolver.SoundLookups, Is.EqualTo(boundLookups),
            "warm-up and PCM rendering never resolve new graph references");

        outer.PrepareReplay(context, live, targetFrame: 0);
        int liveLookups = resolver.SoundLookups;
        float[] fullReplay = new float[12];
        outer.Render(context, live, 0, fullReplay.Length, fullReplay);
        Assert.Multiple(() =>
        {
            Assert.That(fullReplay, Is.EqualTo(continuous).Within(1e-5f),
                "late-created parent transport segments survive replay");
            Assert.That(outer.GetEndFrameExclusive(context, live), Is.EqualTo(9));
            Assert.That(resolver.SoundLookups, Is.EqualTo(liveLookups));
        });
    }

    [Test]
    public void PanningEnvelopeProducesMonoGraphSignalThenOnlyParentSpatialization()
    {
        Registry resolver = new();
        // A stereo Sample with signal only in its left native channel
        // reveals a moving left/right pan during the scalar fold.
        resolver.Add(SampleId, Sample([1, 0, 1, 0, 1, 0, 1, 0], 2));
        resolver.AddEnvelope(PanningId, new StepPanCurve());
        resolver.Add(InstrumentId, new InstrumentSound(
            _ => new ToneSpecification { SourceId = SampleId,
                PanningEnvelopeId = PanningId }, resolver, resolver));
        FmSynthSound synth = Synth(resolver);
        float[] mono = new float[3];
        synth.Render(Mono(), synth.CreateState(), 0, 3, mono);
        Assert.That(mono.All(float.IsFinite), Is.True);
        Assert.That(mono[0], Is.Not.EqualTo(mono[1]).Within(1e-4f),
            "a panned stereo source must not silently discard its envelope");
        RenderContext stereoContext = new(RenderConfiguration.Stereo(Rate));
        float[] stereo = new float[6];
        synth.Render(stereoContext, synth.CreateState(), 0, 3, stereo);
        for (int i = 0; i < 3; i++)
        {
            Assert.That(stereo[2 * i], Is.EqualTo(mono[i] / Math.Sqrt(2)).Within(1e-5));
            Assert.That(stereo[2 * i + 1], Is.EqualTo(mono[i] / Math.Sqrt(2)).Within(1e-5));
        }
    }

    [Test]
    public void FilterEnvelopeUsesLegacyCutoffAndPreservesIndependentFilterHistory()
    {
        Registry resolver = new();
        resolver.Add(SampleId, Sample(Enumerable.Repeat(1f, 16).ToArray(), 1));
        resolver.AddEnvelope(FilterId, new ConstantCurve(0.1));
        resolver.Add(InstrumentId, new InstrumentSound(
            _ => new ToneSpecification { SourceId = SampleId,
                FilterEnvelopeId = FilterId }, resolver, resolver));
        FmSynthSound synth = Synth(resolver);
        float[] whole = new float[8], split = new float[8];
        SoundState full = synth.CreateState();
        SoundState chunked = synth.CreateState();
        synth.Render(Mono(), full, 0, 8, whole);
        synth.Render(Mono(), chunked, 0, 3, split.AsSpan(0, 3));
        synth.Render(Mono(), chunked, 3, 5, split.AsSpan(3, 5));
        Assert.Multiple(() =>
        {
            Assert.That(split, Is.EqualTo(whole));
            Assert.That(whole[0], Is.GreaterThan(0).And.LessThan(1));
            Assert.That(whole[7], Is.GreaterThan(0));
        });
        float[] other = new float[2];
        synth.Render(Mono(), synth.CreateState(), 0, 2, other);
        Assert.That(other, Is.EqualTo(whole.AsSpan(0, 2).ToArray()),
            "a second voice must not inherit the first voice's Filter history");
    }

    [Test]
    public void NestedPreparedFirsPassThroughSourceInvocationAtControlBoundaries()
    {
        Registry resolver = new();
        FirAwareSound child = new();
        resolver.Add(SampleId, child);
        FmSynthSound parent = new(new FmSynthGraph([
            new FmSoundSourceNode(0, SampleId, C4),
        ], 0), resolver, soundResolver: resolver);
        SoundState state = parent.CreateState();
        using FirPreparationScheduler scheduler = new();
        parent.QueuePendingFirControls(state, scheduler);
        parent.AdoptPreparedFirControls(state);
        Assert.Multiple(() =>
        {
            Assert.That(child.Queued, Is.EqualTo(1));
            Assert.That(child.Adopted, Is.EqualTo(1));
        });
    }

    private static FmSynthSound Synth(Registry resolver) =>
        new(new FmSynthGraph([new FmSoundSourceNode(0, InstrumentId, C4)], 0),
            resolver, soundResolver: resolver);

    private static SampleSound Sample(float[] values, int channels)
    {
        SampleDefinition definition = new(SampleId, "PCM",
            new ExternalAssetReference("pcm.wav"));
        return new SampleSound(definition, new MemorySampleData(Rate, channels, values));
    }

    private static RenderContext Mono() => new(
        new RenderConfiguration(Rate, [
            new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
        ]));

    private static SampleSound LoopedSample(float[] values)
    {
        SampleDefinition definition = new(SampleId, "Loop",
            new ExternalAssetReference("loop.wav"))
        {
            Loop = new SampleLoop(SampleLoopMode.Forward, 0, values.Length),
        };
        return new SampleSound(definition, new MemorySampleData(Rate, 1, values));
    }

    private sealed class MovingOctaveCurve : IEnvelopeCurve
    {
        public double GetValue(long frame, int sampleRate, long? noteOffFrame)
        {
            if (noteOffFrame is long off)
                return (frame - off) switch
                {
                    0 => 1,
                    1 => 0,
                    _ => -1,
                };
            return frame < 2 ? 0 : frame < 4 ? 1 : -1;
        }

        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long off, int sampleRate) => off + 3;
    }

    private sealed class ThreeFrameRelease : IEnvelopeCurve
    {
        public double GetValue(long frame, int sampleRate, long? noteOffFrame)
            => noteOffFrame is long off
                ? Math.Max(0, 1 - (frame - off) / 3.0)
                : 1;

        public long? GetEndActiveFrameExclusiveAfterNoteOff(
            long off, int sampleRate) => off + 3;
    }

    private sealed class ConstantCurve(double value) : IEnvelopeCurve
    {
        public double GetValue(long frame, int rate, long? off) => value;
    }

    private sealed class ReleasePitchCurve : IEnvelopeCurve
    {
        public double GetValue(long frame, int rate, long? off) => off.HasValue ? 1 : 0;
    }

    private sealed class StepPanCurve : IEnvelopeCurve
    {
        public double GetValue(long frame, int rate, long? off) =>
            frame == 0 ? -1 : 1;
    }

    private sealed class FirAwareSound : ISound, IFirPreparationSound
    {
        private sealed class State : SoundState { }
        public int Queued { get; private set; }
        public int Adopted { get; private set; }
        public SoundState CreateState() => new State();
        public NoteConfigurationSnapshot SnapshotNoteConfiguration() =>
            NoteConfigurationSnapshot.Default;
        public long? GetEndFrameExclusive(RenderContext context, SoundState state) => null;
        public void Render(RenderContext context, SoundState state, long startFrame,
            int frameCount, Span<float> destination) { }
        public void QueuePendingFirControls(SoundState state,
            FirPreparationScheduler scheduler) => Queued++;
        public void AdoptPreparedFirControls(SoundState state) => Adopted++;
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

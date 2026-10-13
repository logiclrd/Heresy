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

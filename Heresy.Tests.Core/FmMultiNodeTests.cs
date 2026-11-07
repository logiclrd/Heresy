using System;
using System.Linq;
using System.Text.Json.Nodes;

using AwesomeAssertions;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FmMultiNodeTests
{
    [Test]
    public void SpreadIsSymmetricAndContainsTheCenterOnlyForOddCounts()
    {
        FmMultiNode odd = new(1);
        double[] seven = odd.GetFrequencyRatios();
        seven.Should().HaveCount(7);
        seven[3].Should().BeApproximately(1, 1e-12);
        (seven[0] * seven[6]).Should().BeApproximately(1, 1e-12);
        seven[0].Should().BeApproximately(Math.Pow(2, -6.0 / 1200), 1e-12);
        double[] even = new FmMultiNode(1, voiceCount: 2).GetFrequencyRatios();
        even.Should().HaveCount(2);
        even[0].Should().BeLessThan(1);
        even[1].Should().BeGreaterThan(1);
        (even[0] * even[1]).Should().BeApproximately(1, 1e-12);
        new FmMultiNode(1, voiceCount: 1).GetFrequencyRatios()
            .Should().Equal(1.0);
    }

    [Test]
    public void BothComponentNotationsShareFractionalParserAndPreserveText()
    {
        FmMultiNode harmonic = new(1,
            distribution: FmMultiDistribution.Harmonic,
            harmonicComponents: "1/2 1 2 3");
        harmonic.GetFrequencyRatios().Should().Equal(0.5, 1, 2, 3);
        harmonic.Components.Should().Be("1/2 1 2 3");

        FmMultiNode chord = new(1,
            distribution: FmMultiDistribution.Chord,
            chordComponents: "0 4 12 7/2 -1.5");
        double[] actual = chord.GetFrequencyRatios();
        actual[0].Should().Be(1);
        actual[1].Should().BeApproximately(Math.Pow(2, 4.0 / 12), 1e-12);
        actual[2].Should().Be(2);
        actual[3].Should().BeApproximately(Math.Pow(2, 3.5 / 12), 1e-12);
        actual[4].Should().BeApproximately(Math.Pow(2, -1.5 / 12), 1e-12);
        Assert.That(() => new FmMultiNode(1, harmonicComponents: "1/0"),
            Throws.ArgumentException);
        Assert.That(() => new FmMultiNode(1, harmonicComponents: "0 2"),
            Throws.ArgumentException);
        Assert.That(() => new FmMultiNode(1, chordComponents: "NaN"),
            Throws.ArgumentException);
        Assert.That(() => new FmMultiNode(1, chordComponents: "1//2"),
            Throws.ArgumentException);
    }

    [Test]
    public void PresetsAlwaysResetGainAndPhaseEvenWithoutDistributionChange()
    {
        FmMultiNode first = new(4, gainCompensation: false,
            randomizePhase: false, cycleLength: 100.5);
        FmMultiNode super = first.WithPreset("Super saw/super square");
        super.Distribution.Should().Be(FmMultiDistribution.Spread);
        super.Detune.Should().Be(12);
        super.VoiceCount.Should().Be(7);
        super.GainCompensation.Should().BeTrue();
        super.RandomizePhase.Should().BeTrue();
        super.CycleLength.Should().Be(100.5);

        FmMultiNode power = super.WithPreset("Power Chord");
        power.GainCompensation.Should().BeFalse();
        power.RandomizePhase.Should().BeTrue();
        power.HarmonicComponents.Should().Be("1 3/2 2");

        FmMultiNode userOverride = power.With(power.InputNodeId, power.Distribution,
            power.Detune, power.VoiceCount, power.HarmonicComponents,
            power.ChordComponents, false, true, power.CycleLength);
        FmMultiNode changed = userOverride.WithDistribution(FmMultiDistribution.Chord);
        changed.GainCompensation.Should().BeFalse();
        changed.RandomizePhase.Should().BeFalse();
        changed.WithPreset("Major Triad").RandomizePhase.Should().BeTrue();
        FmMultiNode.Presets.Should().HaveCount(10);
    }

    [Test]
    public void GraphAndDocumentRoundTripMultiAndOptionalCycle()
    {
        SongDocument document = new();
        ObjectId id = document.AllocateObjectId();
        FmMultiNode multi = new(2, inputNodeId: 1,
            distribution: FmMultiDistribution.Harmonic,
            harmonicComponents: "1/2 1 3/2", randomizePhase: false,
            gainCompensation: true, cycleLength: 183.75);
        document.Add(new FmSynthDefinition(id, "Multi",
            new FmSynthGraph([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sawtooth, 100),
                multi], 2)));
        const string path = "/tmp/heresy-multi-persistence/song.hm.json";
        string json = SongDocumentJson.Serialize(document, path);
        FmMultiNode copy = ((FmSynthDefinition)SongDocumentJson.Deserialize(json, path)
            .Objects[id]).Graph.Nodes.OfType<FmMultiNode>().Single();
        copy.InputNodeId.Should().Be(1);
        copy.HarmonicComponents.Should().Be("1/2 1 3/2");
        copy.CycleLength.Should().Be(183.75);
        copy.RandomizePhase.Should().BeFalse();
        copy.GainCompensation.Should().BeTrue();
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        JsonNode multiJson = root["objects"]![id.Value.ToString()]!["graph"]!["nodes"]!
            .AsArray().Single(n => n!["type"]!.GetValue<string>() == "multi")!;
        multiJson["cycleLength"]!.GetValue<double>().Should().Be(183.75);
        // Without the override, the field must be absent rather than
        // saving a computed, output-rate-dependent inference.
        FmMultiNode noOverride = multi.With(1, multi.Distribution, multi.Detune,
            multi.VoiceCount, multi.HarmonicComponents, multi.ChordComponents,
            multi.RandomizePhase, multi.GainCompensation, null);
        document.Objects[id].Should().BeOfType<FmSynthDefinition>()
            .Which.Graph = new FmSynthGraph([
                new FmOscillatorNode(1, FmOscillatorWaveform.Sawtooth, 100),
                noOverride], 2);
        JsonNode updated = JsonNode.Parse(SongDocumentJson.Serialize(document, path))!;
        updated["objects"]![id.Value.ToString()]!["graph"]!["nodes"]!
            .AsArray().Single(n => n!["type"]!.GetValue<string>() == "multi")!
            .AsObject().ContainsKey("cycleLength").Should().BeFalse();
    }
}

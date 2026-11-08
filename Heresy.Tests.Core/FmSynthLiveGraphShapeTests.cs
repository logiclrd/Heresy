using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Filters;
using Heresy.Core.Objects;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FmSynthLiveGraphShapeTests
{
    private static FmSynthGraph StartingGraph() => new(
    [
        new FmConstantNode(1, 2),
        new FmOscillatorNode(2, FmOscillatorWaveform.Sine, 440,
            multiplierNodeId: 1),
        new FmSoundSourceNode(3, ObjectId.None, 330,
            multiplierNodeId: 1),
        new FmEnvelopeNode(4, ObjectId.None),
        new FmFilterNode(5, audioInputNodeId: 2, tailSeconds: 1),
        new FmOperatorNode(6, FmOperatorKind.Add, [2, 5]),
        new FmMultiNode(7, inputNodeId: 6),
        new FmNoiseNode(8),
    ], outputNodeId: 7);

    private static FmSynthGraph Replace(FmSynthGraph original,
        FmSynthNode replacement, int? outputNodeId = null)
        => new(original.Nodes.Select(n => n.Id == replacement.Id
            ? replacement : n), outputNodeId ?? original.OutputNodeId);

    [Test]
    public void ReplacingNonstructuralPropertiesDoesNotInventGraphWiringChanges()
    {
        FmSynthGraph before = StartingGraph();
        FmSynthNode[] replacements =
        [
            new FmConstantNode(1, -0.25),
            new FmOscillatorNode(2, FmOscillatorWaveform.Sawtooth, 261.625,
                minimum: -0.75, maximum: 0.8, multiplierNodeId: 1,
                exponentialMultiplier: true, constant: true, curvature: -3.5),
            new FmSoundSourceNode(3, new ObjectId(999), 960,
                multiplierNodeId: 1, exponentialMultiplier: true, constant: true),
            new FmEnvelopeNode(4, new ObjectId(998)),
            new FmFilterNode(5, audioInputNodeId: 2, tailSeconds: 9.5),
            new FmOperatorNode(6, FmOperatorKind.Multiply, [2, 5]),
            new FmMultiNode(7, inputNodeId: 6,
                distribution: FmMultiDistribution.Chord,
                chordComponents: "0 4 7 11",
                voiceCount: 3, randomizePhase: false,
                gainCompensation: true, cycleLength: 200),
        ];

        foreach (FmSynthNode replacement in replacements)
        {
            FmSynthGraph after = Replace(before, replacement);
            FmSynthLiveGraphShape.HasSameWiring(before, after)
                .Should().BeTrue($"editing parameters on {replacement.GetType().Name}"
                    + " must not be mistaken for a rewired graph");
            FmSynthLiveGraphShape.HasSameWiring(after, before)
                .Should().BeTrue();
        }
    }

    [Test]
    public void NodePresentationOrderIsNotWiringButOutputSelectionIs()
    {
        FmSynthGraph before = StartingGraph();
        FmSynthGraph reordered = new(before.Nodes.Reverse(),
            before.OutputNodeId);
        FmSynthLiveGraphShape.HasSameWiring(before, reordered).Should().BeTrue();

        FmSynthGraph changedOutput = new(before.Nodes, outputNodeId: 6);
        FmSynthLiveGraphShape.HasSameWiring(before, changedOutput)
            .Should().BeFalse();
    }

    [Test]
    public void AdditionRemovalOrReplacementOfNodeTypeChangesWiring()
    {
        FmSynthGraph before = StartingGraph();
        FmSynthGraph added = new(
            before.Nodes.Concat([new FmConstantNode(10, 1)]), 7);
        FmSynthGraph removed = new(
            before.Nodes.Where(n => n.Id != 8), 7);
        FmSynthGraph changedType = Replace(before,
            new FmConstantNode(4, 3));

        foreach (FmSynthGraph changed in new[] { added, removed, changedType })
        {
            FmSynthLiveGraphShape.HasSameWiring(before, changed)
                .Should().BeFalse();
            FmSynthLiveGraphShape.HasSameWiring(changed, before)
                .Should().BeFalse();
        }
    }

    [Test]
    public void ConnectedPortChangesAndOperatorInputOrderAreStructural()
    {
        FmSynthGraph before = StartingGraph();
        FmSynthGraph[] changed =
        [
            Replace(before, new FmOscillatorNode(2,
                FmOscillatorWaveform.Sine, 440, multiplierNodeId: 8)),
            Replace(before, new FmSoundSourceNode(3, ObjectId.None, 330,
                multiplierNodeId: 8)),
            Replace(before, new FmFilterNode(5,
                audioInputNodeId: 1, tailSeconds: 1)),
            Replace(before, new FmOperatorNode(6,
                FmOperatorKind.Add, [5, 2])),
            Replace(before, new FmMultiNode(7, inputNodeId: 8)),
        ];

        foreach (FmSynthGraph after in changed)
            FmSynthLiveGraphShape.HasSameWiring(before, after)
                .Should().BeFalse();
    }

    [Test]
    public void FilterWireIdentityMattersEvenWhenTheEndpointsAreIdentical()
    {
        FilterDefinition definition = new(
            new ObjectId(200), "Parametric",
            [new FilterStageDefinition(1, "Peak", FilterKind.BiquadPeaking)]);
        FilterInstance initial = new(definition);
        FmFilterNode filter = new(5, audioInputNodeId: 2,
            configuration: initial);
        FmFilterInputPort cutoff = filter.GetPorts()
            .Single(p => p.ParameterName == "CutoffHz");
        FmFilterInputPort gain = filter.GetPorts()
            .Single(p => p.ParameterName == "GainDb");

        FmFilterNode cutoffWired = filter.WithParameterConnection(cutoff, 1);
        FmFilterNode gainWired = filter.WithParameterConnection(gain, 1);
        cutoffWired.InputNodeIds.Should().Equal(gainWired.InputNodeIds);

        FmSynthGraph before = Replace(StartingGraph(), cutoffWired);
        FmSynthGraph rewired = Replace(before, gainWired);
        FmSynthLiveGraphShape.HasSameWiring(before, rewired)
            .Should().BeFalse("a source must not be silently moved from cutoff to gain");

        FilterInstance changedParameters = initial.Clone();
        changedParameters.SetStageParameter(
            1, "CutoffHz", FilterParameterValue.FromNumber(1200));
        FmSynthGraph parameterOnly = Replace(before,
            cutoffWired.WithConfiguration(changedParameters));
        FmSynthLiveGraphShape.HasSameWiring(before, parameterOnly)
            .Should().BeTrue("only a filter parameter value changed");
    }

    [Test]
    public void NullArgumentsCannotBeCompared()
    {
        FmSynthGraph before = StartingGraph();
        Action missingPrevious = () => FmSynthLiveGraphShape.HasSameWiring(null!, before);
        Action missingNext = () => FmSynthLiveGraphShape.HasSameWiring(before, null!);
        missingPrevious.Should().Throw<ArgumentNullException>();
        missingNext.Should().Throw<ArgumentNullException>();
    }
}

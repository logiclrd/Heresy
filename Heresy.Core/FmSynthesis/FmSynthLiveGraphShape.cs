using System;
using System.Collections.Generic;
using System.Linq;

namespace Heresy.Core.FmSynthesis;

/// <summary>
/// The graph-wiring prerequisite for applying new authored Synth parameters
/// to an existing preview voice without changing its routing.
///
/// This is deliberately **not** a claim that compiled DSP state can be reused:
/// changes to child sources, filter stages, envelope binding, or the number
/// of Multi constituents may require prepared resources and state migration
/// despite identical graph connections. Callers must validate that separately
/// before publishing changes to an active audio session.
///
/// Compare identities, node types, output and connected input *ports*, not
/// graph object identity, authored parameter values, document AudioRevision
/// or node presentation order. Unconnected Filter ports do not represent
/// existing graph wires; active modulation port identity matters.
/// </summary>
public static class FmSynthLiveGraphShape
{
    public static bool HasSameWiring(FmSynthGraph previous, FmSynthGraph next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        if (previous.OutputNodeId != next.OutputNodeId
            || previous.Nodes.Count != next.Nodes.Count)
            return false;

        Dictionary<int, FmSynthNode> nextById =
            next.Nodes.ToDictionary(node => node.Id);

        foreach (FmSynthNode before in previous.Nodes)
        {
            if (!nextById.TryGetValue(before.Id, out FmSynthNode? after)
                || before.GetType() != after.GetType()
                || !before.InputNodeIds.SequenceEqual(after.InputNodeIds))
                return false;

            if (before is FmFilterNode originalFilter
                && after is FmFilterNode replacementFilter
                && !HaveSameConnectedFilterPorts(originalFilter, replacementFilter))
                return false;
        }

        return true;
    }

    private static bool HaveSameConnectedFilterPorts(
        FmFilterNode before, FmFilterNode after)
    {
        FmFilterInputPort[] oldWires =
            before.GetPorts().Where(port => port.SourceNodeId.HasValue).ToArray();
        FmFilterInputPort[] newWires =
            after.GetPorts().Where(port => port.SourceNodeId.HasValue).ToArray();
        if (oldWires.Length != newWires.Length)
            return false;

        for (int index = 0; index < oldWires.Length; index++)
        {
            FmFilterInputPort oldPort = oldWires[index];
            FmFilterInputPort newPort = newWires[index];
            if (oldPort.IsAudio != newPort.IsAudio
                || oldPort.StageId != newPort.StageId
                || oldPort.Kind != newPort.Kind
                || oldPort.Generation != newPort.Generation
                || !string.Equals(oldPort.ParameterName, newPort.ParameterName,
                    StringComparison.OrdinalIgnoreCase)
                || oldPort.SourceNodeId != newPort.SourceNodeId)
                return false;
        }

        return true;
    }
}

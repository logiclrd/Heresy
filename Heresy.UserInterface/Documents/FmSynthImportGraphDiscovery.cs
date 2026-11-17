using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

/// <summary>An object belongs to one loaded import document, not merely to
/// its numeric ObjectId. Two files can legitimately allocate the same IDs.</summary>
public readonly record struct FmImportObjectKey(
	string SourceDocumentIdentity, ObjectId SourceObjectId);

/// <summary>A graph-local node ID belongs to its owning source Synth.</summary>
public readonly record struct FmImportNodeKey(
	FmImportObjectKey SourceSynth, int NodeId);

/// <summary>A particular object reference in a graph node or Instrument tone.
/// The semantic Slot is stable even when ports are reordered visually.</summary>
public readonly record struct FmImportReferenceKey(
	FmImportObjectKey Owner, int? NodeId, int? ToneIndex, string Slot);

public enum FmImportReferenceKind
{
	SoundSource,
	Envelope,
	Filter,
}

public enum FmImportDiscoveryIssueKind
{
	MissingObject,
	IncompatibleObject,
	DependencyCycle,
}

/// <summary>A definition use is separate from the referenced definition:
/// multiple rows may refer to one shared external object, and later a single
/// use can be manually remapped without cloning all other uses.</summary>
public sealed record FmImportObjectReference(
	FmImportReferenceKey Use,
	FmImportReferenceKind Kind,
	FmImportObjectKey? Target);

/// <summary>One graph connection with the real semantic input port identity.
/// Inactive/stale Filter wires are retained for plan diagnostics, but never
/// counted as active uses in root or dependency computation.</summary>
public sealed record FmImportGraphConnection(
	FmImportNodeKey Source,
	FmImportNodeKey Target,
	int TargetInputIndex,
	string PortIdentity,
	string PortLabel,
	bool IsActive);

public sealed record FmImportDiscoveredObject(
	FmImportObjectKey Key, string Name, SongObjectKind Kind, string RuntimeType);

/// <summary>All existing graph nodes are retained, including disconnected
/// authoring fragments. The output node is always the first presentation root,
/// followed by nodes with no active consumers in node-ID order.</summary>
public sealed record FmImportDiscoveredSynthGraph(
	FmImportObjectKey Synth,
	FmImportNodeKey OutputNode,
	IReadOnlyList<FmImportNodeKey> Nodes,
	IReadOnlyList<FmImportNodeKey> PresentationRoots);

public sealed record FmImportDiscoveryIssue(
	FmImportDiscoveryIssueKind Kind,
	FmImportReferenceKey Use,
	FmImportObjectKey Target,
	string Message);

public sealed record FmImportDiscoveredGraph(
	string SourceDocumentIdentity,
	IReadOnlyList<FmImportObjectKey> SelectedSynthRoots,
	IReadOnlyList<FmImportDiscoveredObject> Objects,
	IReadOnlyList<FmImportDiscoveredSynthGraph> SynthGraphs,
	IReadOnlyList<FmImportGraphConnection> Connections,
	IReadOnlyList<FmImportObjectReference> ObjectReferences,
	IReadOnlyList<FmImportDiscoveryIssue> Issues)
{
	public bool IsInternallyDiscoverable => Issues.Count == 0;
}

/// <summary>
/// First, read-only stage of the approved graph-aware import plan.
/// Discovers every authored graph node (even disconnected nodes), exact
/// input-port identities, all external uses and their shared definitions.
/// No destination IDs are allocated and no song or asset is ever mutated.
/// This is NOT the effective editable plan, match engine or commit path.
/// </summary>
public static class FmSynthImportGraphDiscovery
{
	public static FmImportDiscoveredGraph Discover(
		SongFmSynthImportSource source, IEnumerable<ObjectId> selectedSynthIds)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(selectedSynthIds);
		string sourceIdentity = Path.GetFullPath(source.Path);
		SongDocument document = source.Document;
		ObjectId[] selectedIds = selectedSynthIds.Distinct().ToArray();

		FmImportObjectKey Key(ObjectId id) => new(sourceIdentity, id);
		FmImportNodeKey NodeKey(ObjectId owner, int id)
			=> new(Key(owner), id);

		foreach (ObjectId id in selectedIds)
		{
			if (!document.TryGet(id, out SongObject? stored)
				|| stored is not FmSynthDefinition
				|| !source.Synths.Any(synth =>
					synth.Id == id && ReferenceEquals(synth, stored)))
			{
				throw new InvalidOperationException(
					$"Selected Synth #{id.Value} is not in this import source.");
			}
		}

		List<FmImportDiscoveredObject> objects = [];
		List<FmImportDiscoveredSynthGraph> graphs = [];
		List<FmImportGraphConnection> connections = [];
		List<FmImportObjectReference> references = [];
		List<FmImportDiscoveryIssue> issues = [];
		HashSet<ObjectId> visited = [];
		HashSet<ObjectId> visiting = [];

		void Reference(ObjectId owner, int? node, int? tone,
			string slot, FmImportReferenceKind kind, ObjectId? targetId)
		{
			FmImportReferenceKey use = new(Key(owner), node, tone, slot);
			ObjectId? assigned = targetId is ObjectId id && !id.IsNone
				? id : null;
			references.Add(new FmImportObjectReference(
				use, kind, assigned is ObjectId referenceId
					? Key(referenceId) : null));
			if (assigned is not ObjectId target)
				return;

			if (!document.TryGet(target, out SongObject? candidate)
				|| candidate is null)
			{
				issues.Add(new FmImportDiscoveryIssue(
					FmImportDiscoveryIssueKind.MissingObject, use, Key(target),
					$"Import reference {slot} requires missing object #{target.Value}."));
				return;
			}

			bool compatible = kind switch
			{
				FmImportReferenceKind.SoundSource => candidate is
					SampleDefinition or FmSynthDefinition or InstrumentDefinition
					or ScriptInstrumentDefinition,
				FmImportReferenceKind.Envelope => candidate is
					NodeEnvelopeDefinition or ScriptEnvelopeDefinition,
				FmImportReferenceKind.Filter => candidate is FilterDefinition,
				_ => false,
			};
			if (!compatible)
			{
				issues.Add(new FmImportDiscoveryIssue(
					FmImportDiscoveryIssueKind.IncompatibleObject,
					use, Key(target),
					$"Import reference {slot} cannot use object #{target.Value}"
						+ $" of type {candidate.GetType().Name}."));
				return;
			}

			if (visiting.Contains(target))
			{
				issues.Add(new FmImportDiscoveryIssue(
					FmImportDiscoveryIssueKind.DependencyCycle,
					use, Key(target),
					$"Synth import source dependency cycle includes object #{target.Value}."));
				return;
			}
			Visit(target);
		}

		void Tone(ObjectId owner, int index, ToneSpecification tone)
		{
			Reference(owner, null, index, "Source",
				FmImportReferenceKind.SoundSource, tone.SourceId);
			Reference(owner, null, index, "VolumeEnvelope",
				FmImportReferenceKind.Envelope, tone.VolumeEnvelopeId);
			Reference(owner, null, index, "PitchEnvelope",
				FmImportReferenceKind.Envelope, tone.PitchEnvelopeId);
			Reference(owner, null, index, "PanningEnvelope",
				FmImportReferenceKind.Envelope, tone.PanningEnvelopeId);
			Reference(owner, null, index, "FilterEnvelope",
				FmImportReferenceKind.Envelope, tone.FilterEnvelopeId);
			Reference(owner, null, index, "Filter",
				FmImportReferenceKind.Filter,
				tone.FilterConfiguration?.DefinitionId);
		}

		void Synth(FmSynthDefinition synth)
		{
			ObjectId owner = synth.Id;
			foreach (FmSynthNode node in synth.Graph.Nodes)
			{
				FmImportNodeKey target = NodeKey(owner, node.Id);
				void AddInput(int sourceNodeId, int ordinal,
					string identity, string label, bool active = true)
					=> connections.Add(new FmImportGraphConnection(
						NodeKey(owner, sourceNodeId), target,
						ordinal, identity, label, active));

				switch (node)
				{
					case FmConstantNode or FmNoiseNode:
						break;
					case FmEnvelopeNode envelope:
						Reference(owner, node.Id, null, "Envelope",
							FmImportReferenceKind.Envelope, envelope.EnvelopeId);
						break;
					case FmOscillatorNode oscillator:
						if (oscillator.MultiplierNodeId is int oscillatorInput)
							AddInput(oscillatorInput, 0,
								"frequency", "Frequency modulation");
						break;
					case FmSoundSourceNode sound:
						if (sound.MultiplierNodeId is int sourceInput)
							AddInput(sourceInput, 0,
								"frequency", "Frequency modulation");
						Reference(owner, node.Id, null, "Source",
							FmImportReferenceKind.SoundSource, sound.SourceId);
						break;
					case FmMultiNode multi:
						if (multi.InputNodeId is int multiInput)
							AddInput(multiInput, 0, "audio", "Audio");
						break;
					case FmOperatorNode operation:
						for (int input = 0; input < operation.InputNodeIds.Count; input++)
							AddInput(operation.InputNodeIds[input], input,
								$"operand:{input}", $"Operand {input + 1}");
						break;
					case FmFilterNode filter:
						foreach (FmFilterInputPort port in filter.GetPorts())
						{
							if (port.SourceNodeId is not int sourceNodeId)
								continue;
							string identity = port.IsAudio ? "audio"
								: $"filter:{port.StageId}:{(int)port.Kind}"
									+ $":{port.Generation}:{port.ParameterName}";
							AddInput(sourceNodeId, port.Index, identity,
								port.IsAudio ? "Audio"
									: $"Stage {port.StageId}: {port.ParameterName}");
						}
						foreach (FmFilterParameterConnection stale in
							filter.StaleConnections)
						{
							// An invalidated wire is still authored metadata,
							// but it is NOT a graph dependency and cannot own
							// a canonical live occurrence.
							AddInput(stale.SourceNodeId, -1,
								$"filter:{stale.StageId}:{(int)stale.Kind}"
									+ $":{stale.Generation}:{stale.ParameterName}",
								$"Inactive stage {stale.StageId}: {stale.ParameterName}",
								active: false);
						}
						Reference(owner, node.Id, null, "Filter",
							FmImportReferenceKind.Filter,
							filter.Configuration?.DefinitionId);
						break;
					default:
						throw new NotSupportedException(
							$"Graph discovery does not recognize FM node type "
								+ node.GetType().Name + ".");
				}
			}

			HashSet<int> activeConsumers = connections
				.Where(edge => edge.IsActive && edge.Target.SourceSynth.SourceObjectId == owner)
				.Select(edge => edge.Source.NodeId)
				.ToHashSet();
			int output = synth.Graph.OutputNodeId;
			FmImportNodeKey[] graphNodes =
				synth.Graph.Nodes.Select(node => NodeKey(owner, node.Id)).ToArray();
			FmImportNodeKey[] roots =
				[new FmImportNodeKey(Key(owner), output),
					.. graphNodes.Where(node => node.NodeId != output
						&& !activeConsumers.Contains(node.NodeId))
						.OrderBy(node => node.NodeId)];
			graphs.Add(new FmImportDiscoveredSynthGraph(
				Key(owner), NodeKey(owner, output),
				Array.AsReadOnly(graphNodes), Array.AsReadOnly(roots)));
		}

		void Visit(ObjectId id)
		{
			if (visited.Contains(id))
				return;
			if (!document.TryGet(id, out SongObject? obj) || obj is null)
				throw new InvalidOperationException(
					$"Discovered object #{id.Value} is not in the source document.");
			if (!visiting.Add(id))
				throw new InvalidOperationException(
					$"Object #{id.Value} was visited without cycle preflight.");
			objects.Add(new FmImportDiscoveredObject(
				Key(id), obj.Name, obj.Kind, obj.GetType().Name));
			switch (obj)
			{
				case FmSynthDefinition synth:
					Synth(synth);
					break;
				case InstrumentDefinition instrument:
					for (int index = 0; index < instrument.ToneSpecifications.Count; index++)
						Tone(id, index, instrument.ToneSpecifications[index]);
					break;
				case ScriptInstrumentDefinition scripted:
					for (int index = 0; index < scripted.ToneSpecifications.Count; index++)
						Tone(id, index, scripted.ToneSpecifications[index]);
					break;
				case SampleDefinition or EnvelopeDefinition or FilterDefinition:
					break;
				default:
					throw new NotSupportedException(
						$"Import discovery cannot traverse object type {obj.GetType().Name}.");
			}
			visiting.Remove(id);
			visited.Add(id);
		}

		foreach (ObjectId selected in selectedIds)
			Visit(selected);

		return new FmImportDiscoveredGraph(
			sourceIdentity,
			Array.AsReadOnly(selectedIds.Select(Key).ToArray()),
			Array.AsReadOnly(objects.ToArray()),
			Array.AsReadOnly(graphs.ToArray()),
			Array.AsReadOnly(connections.ToArray()),
			Array.AsReadOnly(references.ToArray()),
			Array.AsReadOnly(issues.ToArray()));
	}
}

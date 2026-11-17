using System;
using System.IO;
using System.Linq;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthImportGraphDiscoveryTests
{
	private const double C4 = FmMultiNode.NeutralFrequencyHz;

	[Test]
	public void SourceMultiFilterAndInstrumentDependenciesAreDiscoveredWithoutImportingAnything()
	{
		SongDocument original = new();
		SampleDefinition sample = new(original.AllocateObjectId(), "PCM",
			new ExternalAssetReference(Path.GetFullPath("not-yet-readable.wav")));
		original.Add(sample);
		NodeEnvelopeDefinition envelope =
			new(original.AllocateObjectId(), "Amp");
		original.Add(envelope);
		FilterDefinition filter =
			new(original.AllocateObjectId(), "One stage");
		original.Add(filter);
		InstrumentDefinition instrument =
			new(original.AllocateObjectId(), "Keys");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = sample.Id,
			VolumeEnvelopeId = envelope.Id,
			FilterConfiguration = new FilterInstance(filter),
		});
		original.Add(instrument);
		FmSynthDefinition child = new(original.AllocateObjectId(), "Child",
			new FmSynthGraph(
				[new FmSoundSourceNode(0, instrument.Id, C4)],
				0));
		original.Add(child);

		FmFilterNode filtered = new(4, 3, new FilterInstance(filter),
			[new FmFilterParameterConnection(1, FilterKind.OnePole, 0,
				"CutoffHz", 1)]);
		Assert.That(filtered.GetPorts().Any(p => p.ParameterName == "CutoffHz"
			&& p.SourceNodeId == 1), Is.True,
			"the filter stage's modulation port must be an active wire");
		FmSynthDefinition root = new(original.AllocateObjectId(), "Parent",
			new FmSynthGraph(
			[
				new FmSoundSourceNode(0, child.Id, C4),
				new FmOscillatorNode(1, FmOscillatorWaveform.Sine, C4),
				new FmOperatorNode(2, FmOperatorKind.Add, [0, 1, 0]),
				new FmMultiNode(3, 2),
				filtered,
				new FmEnvelopeNode(5, envelope.Id),
				new FmConstantNode(6, .5),
				new FmNoiseNode(7),
			], 4));
		original.Add(root);
		root.NodePositions.Add(new FmSynthNodePosition(3, 420, -45));
		root.ConnectionRoutingHints.Add(new FmSynthConnectionRoutingHint(
			2, 3, 0, [new FmSynthRoutePoint(333, 88)]));
		DocumentWorkspace destination = new();
		uint sourceDocumentRevision = original.DocumentRevision;
		uint destDocumentRevision = destination.Document.DocumentRevision;
		uint destAudioRevision = destination.Document.AudioRevision;

		FmImportDiscoveredGraph graph = FmSynthImportGraphDiscovery.Discover(
			new SongFmSynthImportSource("source.hm", original, [root, child]),
			[root.Id]);

		Assert.That(graph.IsInternallyDiscoverable, Is.True,
			string.Join(" | ", graph.Issues.Select(issue => issue.Message)));
		Assert.That(graph.SelectedSynthRoots.Select(k => k.SourceObjectId),
			Is.EqualTo(new[] { root.Id }),
			"nested Synth is discovered, not a second explicitly selected root");
		Assert.That(graph.Objects.Select(o => o.Key.SourceObjectId),
			Is.EquivalentTo(new[]
				{ root.Id, child.Id, instrument.Id, sample.Id, envelope.Id, filter.Id }));
		Assert.That(graph.Objects.Count, Is.EqualTo(6),
			"shared dependencies are recorded once as definitions");
		Assert.That(graph.SynthGraphs.Count, Is.EqualTo(2));
		FmImportDiscoveredSynthGraph rootGraph = graph.SynthGraphs
			.Single(g => g.Synth.SourceObjectId == root.Id);
		Assert.That(rootGraph.Nodes.Count, Is.EqualTo(8),
			"disconnected authoring nodes must not vanish");
		Assert.That(rootGraph.PresentationRoots.Select(n => n.NodeId),
			Is.EqualTo(new[] { 4, 5, 6, 7 }),
			"designated output first, then disconnected roots in ID order");
		Assert.That(graph.Connections.Where(c => c.Target.SourceSynth.SourceObjectId == root.Id
			&& c.Target.NodeId == 2).Select(c => (c.Source.NodeId,
				c.TargetInputIndex, c.PortLabel)),
			Is.EqualTo(new[] {
				(0, 0, "Operand 1"),
				(1, 1, "Operand 2"),
				(0, 2, "Operand 3"),
			}), "repeated Operator operands have distinct stable use slots");
		Assert.That(graph.Connections.Single(c => c.Target.NodeId == 3)
			.PortLabel, Is.EqualTo("Audio"));
		Assert.That(graph.Connections.Single(c => c.Target.NodeId == 4
			&& c.TargetInputIndex == 0).PortIdentity, Is.EqualTo("audio"));
		FmImportGraphConnection modulation = graph.Connections.Single(
			c => c.Target.NodeId == 4 && c.TargetInputIndex != 0);
		Assert.That(modulation.PortIdentity,
			Does.Contain("filter:1:").And.Contain(":CutoffHz"));
		Assert.That(modulation.PortLabel, Is.EqualTo("Stage 1: CutoffHz"));

		var sampleReferences = graph.ObjectReferences.Where(r =>
			r.Target?.SourceObjectId == sample.Id).ToArray();
		Assert.That(sampleReferences, Has.Length.EqualTo(1));
		Assert.That(sampleReferences[0].Use.Owner.SourceObjectId,
			Is.EqualTo(instrument.Id));
		Assert.That(sampleReferences[0].Use.ToneIndex, Is.EqualTo(0));
		Assert.That(graph.ObjectReferences.Count(r =>
			r.Target?.SourceObjectId == envelope.Id), Is.EqualTo(2),
			"the Instrument tone and disconnected graph node share one envelope");
		Assert.That(graph.ObjectReferences.Count(r =>
			r.Target?.SourceObjectId == filter.Id), Is.EqualTo(2),
			"Filter instances in Instrument and Synth share one definition");
		Assert.That(root.NodePositions.Single().Y, Is.EqualTo(-45));
		Assert.That(root.ConnectionRoutingHints.Single().RoutePoints.Single().X,
			Is.EqualTo(333));
		Assert.That(original.DocumentRevision, Is.EqualTo(sourceDocumentRevision));
		Assert.That(destination.Document.Objects, Is.Empty,
			"discovery may not allocate destination objects or assets");
		Assert.That(destination.Document.DocumentRevision,
			Is.EqualTo(destDocumentRevision));
		Assert.That(destination.Document.AudioRevision,
			Is.EqualTo(destAudioRevision));
	}

	[Test]
	public void RepeatedSharedDependencyUsesRemainDistinctAcrossSelectedSynths()
	{
		SongDocument song = new();
		ScriptEnvelopeDefinition envelope =
			new(song.AllocateObjectId(), "Shared");
		song.Add(envelope);
		FmSynthDefinition first = Add(song, "First", new FmSynthGraph([
			new FmEnvelopeNode(1, envelope.Id),
			new FmOperatorNode(2, FmOperatorKind.Add, [1, 1]),
		], 2));
		FmSynthDefinition second = Add(song, "Second", new FmSynthGraph([
			new FmEnvelopeNode(4, envelope.Id),
		], 4));
		var snapshot = FmSynthImportGraphDiscovery.Discover(
			new SongFmSynthImportSource("reuse.hm", song, [first, second]),
			[first.Id, first.Id, second.Id]);

		Assert.That(snapshot.SelectedSynthRoots.Count, Is.EqualTo(2));
		Assert.That(snapshot.Objects.Count, Is.EqualTo(3));
		Assert.That(snapshot.ObjectReferences.Where(r =>
			r.Target?.SourceObjectId == envelope.Id).Select(r => r.Use.NodeId),
			Is.EqualTo(new int?[] { 1, 4 }));
		Assert.That(snapshot.Connections.Where(c => c.Target.NodeId == 2)
			.Select(c => c.TargetInputIndex), Is.EqualTo(new[] { 0, 1 }));
		Assert.That(snapshot.Issues, Is.Empty);
	}

	[Test]
	public void DocumentIdentityIsNotJustAReusedNumericObjectId()
	{
		SongDocument first = new();
		FmSynthDefinition a = Add(first, "A", new FmSynthGraph(
			[new FmConstantNode(0, .1)], 0));
		SongDocument second = new();
		FmSynthDefinition b = Add(second, "B", new FmSynthGraph(
			[new FmConstantNode(0, .2)], 0));
		Assert.That(a.Id, Is.EqualTo(b.Id));
		var left = FmSynthImportGraphDiscovery.Discover(
			new SongFmSynthImportSource("one.hm", first, [a]), [a.Id]);
		var right = FmSynthImportGraphDiscovery.Discover(
			new SongFmSynthImportSource("two.hm", second, [b]), [b.Id]);
		Assert.That(left.SelectedSynthRoots[0],
			Is.Not.EqualTo(right.SelectedSynthRoots[0]));
		Assert.That(left.SynthGraphs[0].OutputNode,
			Is.Not.EqualTo(right.SynthGraphs[0].OutputNode));
	}

	[Test]
	public void BrokenAndCyclicReferencesAreDiagnosedWithoutMutatingSource()
	{
		SongDocument song = new();
		ObjectId ownerId = song.AllocateObjectId();
		ObjectId instrumentId = song.AllocateObjectId();
		InstrumentDefinition instrument = new(instrumentId, "Cycle");
		instrument.ToneSpecifications.Add(new ToneSpecification
		{
			SourceId = ownerId,
			PitchEnvelopeId = (ObjectId)999U,
		});
		song.Add(instrument);
		FmSynthDefinition root = new(ownerId, "Owner", new FmSynthGraph([
			new FmSoundSourceNode(1, instrumentId, C4),
			new FmEnvelopeNode(2, (ObjectId)777U),
		], 1));
		song.Add(root);
		uint revision = song.DocumentRevision;
		var discovery = FmSynthImportGraphDiscovery.Discover(
			new SongFmSynthImportSource("broken.hm", song, [root]), [ownerId]);
		Assert.That(discovery.IsInternallyDiscoverable, Is.False);
		Assert.That(discovery.Issues.Select(x => x.Kind),
			Is.EquivalentTo(new[]
			{
				FmImportDiscoveryIssueKind.DependencyCycle,
				FmImportDiscoveryIssueKind.MissingObject,
				FmImportDiscoveryIssueKind.MissingObject,
			}));
		Assert.That(discovery.Issues.Select(x => x.Target.SourceObjectId),
			Does.Contain((ObjectId)999U));
		Assert.That(discovery.Issues.Select(x => x.Target.SourceObjectId),
			Does.Contain((ObjectId)777U));
		Assert.That(song.DocumentRevision, Is.EqualTo(revision));
	}

	[Test]
	public void IncompatibleSourceObjectsAreReportedRatherThanDiscardingTheirUse()
	{
		SongDocument song = new();
		FilterDefinition filter = new(song.AllocateObjectId(), "Not a sound");
		song.Add(filter);
		FmSynthDefinition root = Add(song, "Bad", new FmSynthGraph([
			new FmSoundSourceNode(0, filter.Id, C4),
		], 0));
		var graph = FmSynthImportGraphDiscovery.Discover(
			new SongFmSynthImportSource("types.hm", song, [root]), [root.Id]);
		Assert.That(graph.Issues.Single().Kind,
			Is.EqualTo(FmImportDiscoveryIssueKind.IncompatibleObject));
		Assert.That(graph.ObjectReferences.Single().Target?.SourceObjectId,
			Is.EqualTo(filter.Id));
		Assert.That(graph.Objects.Count, Is.EqualTo(1),
			"an incompatible target cannot be included in the copied closure");
	}

	private static FmSynthDefinition Add(
		SongDocument song, string name, FmSynthGraph graph)
	{
		FmSynthDefinition synth = new(song.AllocateObjectId(), name, graph);
		song.Add(synth);
		return synth;
	}
}

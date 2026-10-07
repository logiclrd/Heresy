using System.Linq;
using System.Text.Json.Nodes;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class FmSynthPersistenceTests
{
	private const string JsonContextPath = "/tmp/heresy-fm-persistence-tests/song.hm.json";

	[Test]
	public void FmSynthRoundTripsSemanticGraphAndEditorOnlyLayoutMetadata()
	{
		SongDocument document = new();
		ObjectId envelopeId = document.AllocateObjectId();
		document.Add(new AdsrEnvelopeDefinition(envelopeId, "Amp"));

		ObjectId synthId = document.AllocateObjectId();
		FmSynthDefinition synth =
			new(
				synthId,
				"Bell",
				CreateGraph(envelopeId));
		synth.NodePositions.Add(
			new FmSynthNodePosition(
				nodeId: 1,
				x: 120.5,
				y: 80.25));
		synth.ConnectionRoutingHints.Add(
			new FmSynthConnectionRoutingHint(
				sourceNodeId: 1,
				targetNodeId: 3,
				targetInputIndex: 0,
				routePoints:
				[
					new FmSynthRoutePoint(180.0, 80.25),
					new FmSynthRoutePoint(180.0, 160.0),
				]));
		document.Add(synth);

		string json =
			SongDocumentJson.Serialize(
				document,
				JsonContextPath);

		JsonObject persisted =
			JsonNode.Parse(json)!["objects"]![synthId.Value.ToString()]!.AsObject();
		persisted["type"]!.GetValue<string>().Should().Be("fmSynth");
		persisted["graph"]!["outputNodeId"]!.GetValue<int>().Should().Be(3);
		persisted["editorLayout"]!["nodePositions"]!.AsArray().Should().HaveCount(1);
		persisted["editorLayout"]!["connectionRoutingHints"]!.AsArray().Should().HaveCount(1);

		SongDocument restored =
			SongDocumentJson.Deserialize(
				json,
				JsonContextPath);

		FmSynthDefinition copy =
			restored.Objects[synthId]
				.Should().BeOfType<FmSynthDefinition>().Subject;
		copy.Name.Should().Be("Bell");
		copy.Kind.Should().Be(SongObjectKind.FmSynth);
		copy.Graph.OutputNodeId.Should().Be(3);
		copy.Graph.Nodes.Should().HaveCount(4);

		FmOscillatorNode oscillator =
			copy.Graph.Nodes
				.Single(node => node.Id == 1)
				.Should().BeOfType<FmOscillatorNode>().Subject;
		oscillator.Waveform.Should().Be(FmOscillatorWaveform.Sine);
		oscillator.FrequencyHz.Should().Be(440.0);
		oscillator.Minimum.Should().Be(-0.75);
		oscillator.Maximum.Should().Be(0.75);
		oscillator.MultiplierNodeId.Should().Be(0);
		oscillator.ExponentialMultiplier.Should().BeTrue();

		FmEnvelopeNode envelope =
			copy.Graph.Nodes
				.Single(node => node.Id == 2)
				.Should().BeOfType<FmEnvelopeNode>().Subject;
		envelope.EnvelopeId.Should().Be(envelopeId);

		FmOperatorNode output =
			copy.Graph.Nodes
				.Single(node => node.Id == 3)
				.Should().BeOfType<FmOperatorNode>().Subject;
		output.Operation.Should().Be(FmOperatorKind.Multiply);
		output.InputNodeIds.Should().Equal(1, 2);

		copy.NodePositions.Should().Equal(
			new FmSynthNodePosition(1, 120.5, 80.25));
		copy.ConnectionRoutingHints.Should().ContainSingle();
		FmSynthConnectionRoutingHint route =
			copy.ConnectionRoutingHints.Single();
		route.SourceNodeId.Should().Be(1);
		route.TargetNodeId.Should().Be(3);
		route.TargetInputIndex.Should().Be(0);
		route.RoutePoints.Should().Equal(
			new FmSynthRoutePoint(180.0, 80.25),
			new FmSynthRoutePoint(180.0, 160.0));
	}

	[Test]
	public void AddPlacesFmSynthInSamplesSection()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(
			new FmSynthDefinition(
				id,
				"Synth",
				new FmSynthGraph(
					[new FmConstantNode(0, 0.0)],
					0)));

		SongTreeFolder samples =
			document.GetSectionRoot(
				SongTreeSection.Samples);
		samples.Children
			.Cast<SongTreeObject>()
			.Select(node => node.ObjectId)
			.Should().Equal(id);
	}

	[Test]
	public void SnapshotDeepClonesFmSynthGraphAndEditorMetadata()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		FmSynthDefinition synth =
			new(
				id,
				"Synth",
				new FmSynthGraph(
					[new FmConstantNode(0, 0.5)],
					0));
		synth.NodePositions.Add(
			new FmSynthNodePosition(0, 10.0, 20.0));
		document.Add(synth);

		SongDocumentSnapshot snapshot =
			SongDocumentSnapshot.Create(document);

		FmSynthDefinition copy =
			snapshot.Document.Objects[id]
				.Should().BeOfType<FmSynthDefinition>().Subject;
		copy.Should().NotBeSameAs(synth);
		copy.Graph.Should().NotBeSameAs(synth.Graph);
		copy.Graph.Nodes.Should().NotBeSameAs(synth.Graph.Nodes);
		copy.NodePositions.Should().NotBeSameAs(synth.NodePositions);
		copy.NodePositions.Should().Equal(
			new FmSynthNodePosition(0, 10.0, 20.0));

		synth.Graph =
			new FmSynthGraph(
				[new FmConstantNode(1, -0.5)],
				1);
		synth.NodePositions[0] =
			new FmSynthNodePosition(0, 30.0, 40.0);

		copy.Graph.OutputNodeId.Should().Be(0);
		copy.NodePositions.Should().Equal(
			new FmSynthNodePosition(0, 10.0, 20.0));
	}

	[Test]
	public void ReferenceAnalysisReportsFmEnvelopeEdges()
	{
		SongDocument document = new();
		ObjectId envelopeId = document.AllocateObjectId();
		document.Add(new AdsrEnvelopeDefinition(envelopeId, "Envelope"));

		ObjectId synthId = document.AllocateObjectId();
		document.Add(
			new FmSynthDefinition(
				synthId,
				"Synth",
				new FmSynthGraph(
					[new FmEnvelopeNode(0, envelopeId)],
					0)));

		SongReferenceAnalysis analysis =
			SongReferenceAnalyzer.Analyze(document);

		analysis.References.Should().Contain(
			new SongReference(
				envelopeId,
				synthId,
				SongReferenceKind.FmSynthEnvelope));
	}

	[Test]
	public void FmEnvelopeReferenceKeepsDeletedEnvelopeTombstoneAlive()
	{
		SongDocument document = new();
		ObjectId envelopeId = document.AllocateObjectId();
		document.Add(new AdsrEnvelopeDefinition(envelopeId, "Envelope"));
		document.Remove(envelopeId);

		ObjectId synthId = document.AllocateObjectId();
		document.Add(
			new FmSynthDefinition(
				synthId,
				"Synth",
				new FmSynthGraph(
					[new FmEnvelopeNode(0, envelopeId)],
					0)));

		string json =
			SongDocumentJson.Serialize(
				document,
				JsonContextPath);
		JsonObject tombstones =
			JsonNode.Parse(json)!["tombstones"]!.AsObject();

		tombstones.ContainsKey(
			envelopeId.Value.ToString())
			.Should().BeTrue();
	}

	[Test]
	public void FmSynthTombstoneKindRoundTrips()
	{
		SongDocument document = new();
		ObjectId synthId = document.AllocateObjectId();
		document.Add(
			new FmSynthDefinition(
				synthId,
				"Deleted Synth",
				new FmSynthGraph(
					[new FmConstantNode(0, 0.0)],
					0)));
		document.Remove(synthId);

		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Broken")
			{
				RowCount = 1,
				ChannelCount = 1,
			};
		pattern.Grid.GetOrCreateCell(0, 0).SourceId = synthId;
		document.Add(pattern);

		string json =
			SongDocumentJson.Serialize(
				document,
				JsonContextPath);
		JsonObject tombstone =
			JsonNode.Parse(json)!["tombstones"]![synthId.Value.ToString()]!.AsObject();
		tombstone["kind"]!.GetValue<string>().Should().Be("fmSynth");

		SongDocument restored =
			SongDocumentJson.Deserialize(
				json,
				JsonContextPath);
		restored.Tombstones[synthId].Kind.Should().Be(SongObjectKind.FmSynth);
	}

	private static FmSynthGraph CreateGraph(
		ObjectId envelopeId)
		=> new(
			[
				new FmConstantNode(
					id: 0,
					value: 12.0),
				new FmOscillatorNode(
					id: 1,
					waveform: FmOscillatorWaveform.Sine,
					frequencyHz: 440.0,
					minimum: -0.75,
					maximum: 0.75,
					multiplierNodeId: 0,
					exponentialMultiplier: true),
				new FmEnvelopeNode(
					id: 2,
					envelopeId),
				new FmOperatorNode(
					id: 3,
					operation: FmOperatorKind.Multiply,
					inputNodeIds: [1, 2]),
			],
			outputNodeId: 3);
}

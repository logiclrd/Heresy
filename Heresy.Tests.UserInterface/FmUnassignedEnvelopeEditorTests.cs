using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmUnassignedEnvelopeEditorTests
{
	[Test]
	public void UnassignedEnvelopeNodeCanBeAddedWithoutAnyEnvelopeObjects()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "Empty FM");

		uint before = workspace.Document.AudioRevision;
		int id = FmSynthDocumentEditor.AddEnvelopeNode(
			workspace, synth, ObjectId.None, x: 190, y: 120);

		FmEnvelopeNode node = synth.Graph.Nodes
			.Single(n => n.Id == id).Should().BeOfType<FmEnvelopeNode>().Subject;
		node.EnvelopeId.Should().Be(ObjectId.None);
		workspace.Document.Objects.Values.OfType<EnvelopeDefinition>()
			.Should().BeEmpty();
		workspace.Document.AudioRevision.Should().Be(before + 1);
	}

	[Test]
	public void AssignCreateAndClearUsesSameLiveEnvelopeObject()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int id = FmSynthDocumentEditor.AddEnvelopeNode(
			workspace, synth, ObjectId.None, 220, 180);

		AdsrEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateAdsrEnvelope(workspace, "New FM Envelope");
		FmSynthDocumentEditor.UpdateNode(workspace, synth,
			new FmEnvelopeNode(id, envelope.Id));
		((FmEnvelopeNode)synth.Graph.Nodes.Single(n => n.Id == id))
			.EnvelopeId.Should().Be(envelope.Id);

		EnvelopeDocumentEditor.UpdateAdsrEnvelope(workspace, envelope,
			TimeSpan.FromMilliseconds(150),
			TimeSpan.FromMilliseconds(250),
			sustainLevel: 0.4,
			TimeSpan.FromMilliseconds(350));
		workspace.Document.Objects[envelope.Id].Should().BeSameAs(envelope);
		((AdsrEnvelopeDefinition)workspace.Document.Objects[envelope.Id])
			.SustainLevel.Should().Be(0.4);

		FmSynthDocumentEditor.UpdateNode(workspace, synth,
			new FmEnvelopeNode(id, ObjectId.None));
		((FmEnvelopeNode)synth.Graph.Nodes.Single(n => n.Id == id))
			.EnvelopeId.Should().Be(ObjectId.None);
		workspace.Document.Objects.ContainsKey(envelope.Id).Should().BeTrue();
	}

	[Test]
	public void UnassignedNodeSurvivesJsonRoundTripAndImportWithoutGhostEnvelope()
	{
		DocumentWorkspace source = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(source, "Unassigned");
		int id = FmSynthDocumentEditor.AddEnvelopeNode(
			source, synth, ObjectId.None, 210, 120);

		string json = SongDocumentJson.Serialize(
			source.Document, "/tmp/heresy-unassigned-fm.hm.json");
		SongDocument restored = SongDocumentJson.Deserialize(
			json, "/tmp/heresy-unassigned-fm.hm.json");
		FmSynthDefinition restoredSynth =
			(FmSynthDefinition)restored.Objects[synth.Id];
		((FmEnvelopeNode)restoredSynth.Graph.Nodes.Single(n => n.Id == id))
			.EnvelopeId.Should().Be(ObjectId.None);

		DocumentWorkspace destination = new();
		// Source import should not allocate any envelope ID for None.
		var imported = FmSynthDocumentEditor.ImportFromSong(
			destination,
			new SongFmSynthImportSource(restored, new[] { restoredSynth }),
			new[] { restoredSynth.Id });
		imported.Should().ContainSingle();
		((FmEnvelopeNode)imported[0].Graph.Nodes.Single(n => n.Id == id))
			.EnvelopeId.Should().Be(ObjectId.None);
		destination.Document.Objects.Values.OfType<EnvelopeDefinition>()
			.Should().BeEmpty();
	}

	[Test]
	public void InvalidNonzeroEnvelopeReferencesStillAreRejected()
	{
		DocumentWorkspace workspace = new();
		FmSynthDefinition synth =
			FmSynthDocumentEditor.CreateFmSynth(workspace, "FM");
		int id = FmSynthDocumentEditor.AddEnvelopeNode(
			workspace, synth, ObjectId.None, 100, 100);
		Action invalid = () => FmSynthDocumentEditor.UpdateNode(
			workspace, synth, new FmEnvelopeNode(id, (ObjectId)123456U));
		invalid.Should().Throw<InvalidOperationException>();
	}
}

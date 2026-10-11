using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NodeEnvelopeDocumentEditorTests
{
	[Test]
	public void CreationValidatesBeforeAllocatingAndPlacesSharedEnvelope()
	{
		DocumentWorkspace workspace = new();
		uint revision = workspace.Document.AudioRevision;
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			EnvelopeDocumentEditor.CreateNodeEnvelope(
				workspace, "Invalid",
				[new EnvelopeNode(TimeSpan.Zero, 0.0)],
				sustainStartIndex: 0, sustainEndIndex: 1));
		Assert.That(workspace.Document.Objects.Count, Is.EqualTo(0));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));

		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "  Shape  ");
		Assert.That(envelope.Name, Is.EqualTo("Shape"));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
		Assert.That(workspace.Document.GetSectionRoot(SongTreeSection.Envelopes)
			.Children.Cast<SongTreeObject>().Single().ObjectId,
			Is.EqualTo(envelope.Id));
	}

	[Test]
	public void EditsAreAtomicAndIncrementAudioRevisionOnlyForChanges()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Shape");
		uint revision = workspace.Document.AudioRevision;
		EnvelopeNode[] updated =
		[
			new(TimeSpan.Zero, 0.0),
			new(TimeSpan.FromMilliseconds(50), -0.25),
			new(TimeSpan.FromMilliseconds(90), 0.3),
		];
		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, updated, 1, 2,
			EnvelopeSustainLoopMode.PingPong);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
		Assert.That(envelope.Nodes, Is.EqualTo(updated));
		Assert.That(envelope.SustainStartIndex, Is.EqualTo(1));
		Assert.That(envelope.SustainEndIndex, Is.EqualTo(2));
		EnvelopeDocumentEditor.UpdateNodeEnvelope(
			workspace, envelope, updated.ToArray(), 1, 2,
			EnvelopeSustainLoopMode.PingPong);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
		Assert.Throws<ArgumentException>(() =>
			EnvelopeDocumentEditor.UpdateNodeEnvelope(
				workspace, envelope,
				[new(TimeSpan.Zero, 0), new(TimeSpan.Zero, 1)],
				0, 1, EnvelopeSustainLoopMode.Forward));
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision + 1));
		Assert.That(envelope.Nodes, Is.EqualTo(updated));
	}

	[Test]
	public void ForeignEnvelopeEditsAreRejected()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition foreign = new((ObjectId)99U, "Foreign");
		Assert.Throws<InvalidOperationException>(() =>
			EnvelopeDocumentEditor.UpdateNodeEnvelope(
				workspace, foreign, foreign.Nodes,
				foreign.SustainStartIndex, foreign.SustainEndIndex,
				foreign.SustainLoopMode));
	}
}

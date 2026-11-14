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
	public void OnlyAudibleNodeEnvelopeEditsNotifySharedDependents()
	{
		DocumentWorkspace workspace = new();
		NodeEnvelopeDefinition envelope =
			EnvelopeDocumentEditor.CreateNodeEnvelope(workspace, "Shared");
		var notifications = new System.Collections.Generic.List<SharedAudioObjectCommittedEventArgs>();
		workspace.SharedAudioObjectCommitted += (_, e) => notifications.Add(e);
		uint revision = workspace.Document.AudioRevision;

		EnvelopeDocumentEditor.UpdateNodeEnvelopeDisplayRange(workspace, envelope,
			envelope.Vmin - 0.5, envelope.Vmax + 0.5);
		Assert.That(workspace.Document.AudioRevision, Is.EqualTo(revision));
		Assert.That(notifications, Is.Empty);

		EnvelopeDocumentEditor.UpdateNodeEnvelope(workspace, envelope,
			envelope.Nodes, envelope.SustainStartIndex, envelope.SustainEndIndex,
			envelope.SustainLoopMode);
		Assert.That(notifications, Is.Empty);
		EnvelopeNode[] edited = [new(TimeSpan.Zero, 0.42)];
		EnvelopeDocumentEditor.UpdateNodeEnvelope(workspace, envelope,
			edited, 0, 0, EnvelopeSustainLoopMode.Forward);
		Assert.That(notifications.Count, Is.EqualTo(1));
		Assert.That(notifications[0].Document, Is.SameAs(workspace.Document));
		Assert.That(notifications[0].ObjectId, Is.EqualTo(envelope.Id));
		Assert.That(notifications[0].AudioRevision,
			Is.EqualTo(workspace.Document.AudioRevision));

		EnvelopeDocumentEditor.UpdateNodeEnvelope(workspace, envelope,
			edited, 0, 0, EnvelopeSustainLoopMode.Forward);
		Assert.Throws<ArgumentException>(() =>
			EnvelopeDocumentEditor.UpdateNodeEnvelope(workspace, envelope,
				[new EnvelopeNode(TimeSpan.Zero, 0.1),
				 new EnvelopeNode(TimeSpan.Zero, 0.2)],
				0, 0, EnvelopeSustainLoopMode.Forward));
		Assert.That(notifications.Count, Is.EqualTo(1));
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

using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class NodeEnvelopeDefinitionTests
{
	[Test]
	public void DefaultShapeBeginsAtZeroWithAValidSingleNodeSustain()
	{
		NodeEnvelopeDefinition envelope = new((ObjectId)1U, "Curve");
		Assert.That(envelope.Nodes.Count, Is.EqualTo(4));
		Assert.That(envelope.Nodes[0], Is.EqualTo(
			new EnvelopeNode(TimeSpan.Zero, 0.0)));
		Assert.That(envelope.SustainStartIndex, Is.EqualTo(2));
		Assert.That(envelope.SustainEndIndex, Is.EqualTo(2));
		Assert.That(envelope.SustainLoopMode,
			Is.EqualTo(EnvelopeSustainLoopMode.Forward));
	}

	[Test]
	public void AllowsPingPongAndSignedUnboundedFiniteLevels()
	{
		NodeEnvelopeDefinition envelope = new((ObjectId)1U, "Curve");
		envelope.ReplaceCurve(
			[
				new(TimeSpan.Zero, -3.5),
				new(TimeSpan.FromTicks(1), 7.25),
				new(TimeSpan.FromSeconds(2), -0.5),
			],
			0, 2, EnvelopeSustainLoopMode.PingPong);
		Assert.That(envelope.Nodes.Select(n => n.Value),
			Is.EqualTo(new[] { -3.5, 7.25, -0.5 }));
		Assert.That(envelope.SustainStartIndex, Is.EqualTo(0));
		Assert.That(envelope.SustainEndIndex, Is.EqualTo(2));
	}

	[Test]
	public void ValidationFailureLeavesCompleteCurveUnchanged()
	{
		NodeEnvelopeDefinition envelope = new((ObjectId)1U, "Curve");
		EnvelopeNode[] oldNodes = envelope.Nodes.ToArray();
		int oldStart = envelope.SustainStartIndex;
		int oldEnd = envelope.SustainEndIndex;

		void Rejected(EnvelopeNode[] proposed, int start = 0, int end = 1,
			EnvelopeSustainLoopMode mode = EnvelopeSustainLoopMode.Forward)
		{
			Assert.That(
				() => envelope.ReplaceCurve(proposed, start, end, mode),
				Throws.InstanceOf<ArgumentException>());
			Assert.That(envelope.Nodes, Is.EqualTo(oldNodes));
			Assert.That(envelope.SustainStartIndex, Is.EqualTo(oldStart));
			Assert.That(envelope.SustainEndIndex, Is.EqualTo(oldEnd));
		}

		Rejected([]);
		Rejected([new(TimeSpan.FromMilliseconds(1), 0), new(TimeSpan.FromSeconds(1), 1)]);
		Rejected([new(TimeSpan.Zero, 0), new(TimeSpan.Zero, 1)]);
		Rejected([new(TimeSpan.Zero, 0), new(TimeSpan.FromTicks(-1), 1)]);
		Rejected([new(TimeSpan.Zero, 0), new(TimeSpan.FromSeconds(1), double.NaN)]);
		Rejected([new(TimeSpan.Zero, 0), new(TimeSpan.FromSeconds(1), double.PositiveInfinity)]);
		Rejected([new(TimeSpan.Zero, 0), new(TimeSpan.FromSeconds(1), 1)], 1, 0);
		Rejected([new(TimeSpan.Zero, 0), new(TimeSpan.FromSeconds(1), 1)], 0, 2);
		Rejected([new(TimeSpan.Zero, 0), new(TimeSpan.FromSeconds(1), 1)],
			0, 1, (EnvelopeSustainLoopMode)99);
	}

	[Test]
	public void DisplayRangeIsIndependentFromNodesAndRejectsInvalidBounds()
	{
		NodeEnvelopeDefinition envelope = new((ObjectId)1U, "Range");
		EnvelopeNode[] points = envelope.Nodes.ToArray();
		envelope.SetDisplayRange(-200, 300);
		Assert.That(envelope.Vmin, Is.EqualTo(-200));
		Assert.That(envelope.Vmax, Is.EqualTo(300));
		Assert.That(envelope.Nodes, Is.EqualTo(points));
		foreach ((double min, double max) in new[]
		{
			(1.0, 1.0), (2.0, 1.0),
			(double.NaN, 1.0),
			(0.0, double.PositiveInfinity),
			(-double.MaxValue, double.MaxValue),
		})
		{
			Assert.Throws<ArgumentException>(
				() => envelope.SetDisplayRange(min, max));
			Assert.That(envelope.Vmin, Is.EqualTo(-200));
			Assert.That(envelope.Vmax, Is.EqualTo(300));
		}
	}

	[Test]
	public void SingleNodeAndCoincidentSustainIndicesAreLegal()
	{
		NodeEnvelopeDefinition envelope = new((ObjectId)1U, "Curve");
		envelope.ReplaceCurve([new(TimeSpan.Zero, 0.2)], 0, 0,
			EnvelopeSustainLoopMode.PingPong);
		Assert.That(envelope.Nodes.Count, Is.EqualTo(1));
		Assert.That(envelope.SustainEndIndex, Is.EqualTo(0));
	}

	[Test]
	public void SerializationAndSnapshotRoundTripWithoutAdsrProperties()
	{
		SongDocument doc = new();
		ObjectId id = doc.AllocateObjectId();
		NodeEnvelopeDefinition envelope = new(id, "Moving sustain");
		envelope.ReplaceCurve(
			[
				new(TimeSpan.Zero, 0.0),
				new(TimeSpan.FromMilliseconds(25), 2.0),
				new(TimeSpan.FromMilliseconds(90), 0.1),
				new(TimeSpan.FromMilliseconds(140), -0.5),
			],
			1, 2, EnvelopeSustainLoopMode.PingPong);
		envelope.SetDisplayRange(-3.0, 5.0);
		doc.Add(envelope);
		const string path = "/tmp/node-envelope.hm.json";
		string json = SongDocumentJson.Serialize(doc, path);
		Assert.That(json, Does.Contain("\"nodeEnvelope\""));
		Assert.That(json, Does.Not.Contain("\"sustainLevel\""));
		SongDocument loaded = SongDocumentJson.Deserialize(json, path);
		NodeEnvelopeDefinition restored =
			(NodeEnvelopeDefinition)loaded.Objects[id];
		Assert.That(restored.Nodes, Is.EqualTo(envelope.Nodes));
		Assert.That(restored.SustainStartIndex, Is.EqualTo(1));
		Assert.That(restored.SustainEndIndex, Is.EqualTo(2));
		Assert.That(restored.SustainLoopMode,
			Is.EqualTo(EnvelopeSustainLoopMode.PingPong));
		Assert.That(restored.Vmin, Is.EqualTo(-3.0));
		Assert.That(restored.Vmax, Is.EqualTo(5.0));

		SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(doc);
		NodeEnvelopeDefinition frozen =
			(NodeEnvelopeDefinition)snapshot.Document.Objects[id];
		envelope.ReplaceCurve([new(TimeSpan.Zero, 0.0)], 0, 0,
			EnvelopeSustainLoopMode.Forward);
		Assert.That(frozen.Nodes, Is.EqualTo(restored.Nodes));
		Assert.That(frozen.SustainEndIndex, Is.EqualTo(2));
		Assert.That(frozen.Vmin, Is.EqualTo(-3.0));
		Assert.That(frozen.Vmax, Is.EqualTo(5.0));
	}
}

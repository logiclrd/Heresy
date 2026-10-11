using System;
using System.Linq;

using Heresy.Core.Envelopes;
using Heresy.UserInterface.EnvelopeEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NodeEnvelopeGraphGeometryTests
{
	private static EnvelopeNode[] Nodes() =>
	[
		new(TimeSpan.Zero, 0),
		new(TimeSpan.FromSeconds(1), 1),
		new(TimeSpan.FromSeconds(2), 0.5),
		new(TimeSpan.FromSeconds(3), 0),
	];

	private static NodeEnvelopeGraphGeometry Layout(EnvelopeNode[]? nodes = null)
		=> new(nodes ?? Nodes(), 440, 250);

	[Test]
	public void SelectionUsesClosestNodeAndExactlyFourPixelCircle()
	{
		NodeEnvelopeGraphGeometry geometry = Layout();
		(double x, double y) = geometry.Point(1);
		Assert.That(geometry.HitNode(x, y), Is.EqualTo(1));
		Assert.That(geometry.HitNode(x + 4, y), Is.EqualTo(1));
		Assert.That(geometry.HitNode(x + 4.01, y), Is.EqualTo(-1));
		Assert.That(geometry.HitNode(x + 3, y + 3), Is.EqualTo(-1));
		Assert.That(geometry.HitNode(double.NaN, y), Is.EqualTo(-1));
		Assert.That(geometry.HitNode(x, double.PositiveInfinity),
			Is.EqualTo(-1));
	}

	[Test]
	public void DoubleClickOnLineInsertsAtProjectedPointNotMouseX()
	{
		EnvelopeNode[] nodes = Nodes();
		NodeEnvelopeGraphGeometry g = Layout(nodes);
		(double ax, double ay) = g.Point(1);
		(double bx, double by) = g.Point(2);
		double xm = (ax + bx) / 2;
		double ym = (ay + by) / 2;
		int segment = g.HitSegment(xm, ym + 3, out EnvelopeNode? projected);
		Assert.That(segment, Is.EqualTo(1));
		Assert.That(projected, Is.Not.Null);
		// Projection is perpendicular in pixel space, not just time
		// at pointer X; the nonhorizontal slope alters the time.
		Assert.That(projected!.Time,
			Is.GreaterThan(nodes[1].Time)
				.And.LessThan(nodes[2].Time));
		Assert.That(projected.Time.Ticks,
			Is.EqualTo((long)Math.Round(g.TimeTicks(
				g.X(projected.Time.Ticks)))).Within(1));
		NodeEnvelopeGraphEdit inserted = NodeEnvelopeGraphGeometry.Insert(
			nodes, segment, projected, 2, 3);
		Assert.That(inserted.Nodes.Count, Is.EqualTo(5));
		Assert.That(inserted.SelectedNodeIndex, Is.EqualTo(2));
		Assert.That(inserted.SustainStartIndex, Is.EqualTo(3));
		Assert.That(inserted.SustainEndIndex, Is.EqualTo(4));
		Assert.That(inserted.Nodes[2], Is.EqualTo(projected));
	}

	[Test]
	public void SegmentDetectionAvoidsEmptySpaceAndDuplicateTimeInsertion()
	{
		EnvelopeNode[] nodes = Nodes();
		NodeEnvelopeGraphGeometry g = Layout(nodes);
		Assert.That(g.HitSegment(350, 20, out _), Is.EqualTo(-1));
		Assert.Throws<ArgumentException>(() => NodeEnvelopeGraphGeometry.Insert(
			nodes, 0, new EnvelopeNode(nodes[0].Time, 0.1), 1, 2));
		Assert.Throws<ArgumentException>(() => NodeEnvelopeGraphGeometry.Insert(
			nodes, 1, new EnvelopeNode(nodes[2].Time, 0.1), 1, 2));

		EnvelopeNode[] tiny =
		[
			new(TimeSpan.Zero, 0),
			new(TimeSpan.FromTicks(1), 1),
		];
		NodeEnvelopeGraphGeometry tinyLayout = Layout(tiny);
		(double x0, double y0) = tinyLayout.Point(0);
		(double x1, double y1) = tinyLayout.Point(1);
		Assert.That(tinyLayout.HitSegment(
			(x0 + x1) / 2, (y0 + y1) / 2, out _), Is.EqualTo(-1),
			"there is no valid tick strictly between adjacent ticks");
	}

	[Test]
	public void DeleteFirstNodeRebasesTimesAndSustainReferences()
	{
		NodeEnvelopeGraphEdit? result = NodeEnvelopeGraphGeometry.Delete(
			Nodes(), 0, 1, 3);
		Assert.That(result, Is.Not.Null);
		Assert.That(result!.Nodes.Select(x => x.Time),
			Is.EqualTo(new[]
			{
				TimeSpan.Zero, TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(2),
			}));
		Assert.That(result.SustainStartIndex, Is.EqualTo(0));
		Assert.That(result.SustainEndIndex, Is.EqualTo(2));
		Assert.That(result.SelectedNodeIndex, Is.EqualTo(-1));
	}

	[Test]
	public void DeleteSustainEndpointRetargetsAndKeepsOrderedIndices()
	{
		NodeEnvelopeGraphEdit? start = NodeEnvelopeGraphGeometry.Delete(
			Nodes(), 1, 1, 2);
		Assert.That(start!.SustainStartIndex, Is.EqualTo(1));
		Assert.That(start.SustainEndIndex, Is.EqualTo(1));

		NodeEnvelopeGraphEdit? end = NodeEnvelopeGraphGeometry.Delete(
			Nodes(), 3, 2, 3);
		Assert.That(end!.SustainStartIndex, Is.EqualTo(2));
		Assert.That(end.SustainEndIndex, Is.EqualTo(2));

		NodeEnvelopeGraphEdit? one = NodeEnvelopeGraphGeometry.Delete(
			[new EnvelopeNode(TimeSpan.Zero, 3)], 0, 0, 0);
		Assert.That(one, Is.Null);
	}

	[Test]
	public void DragClampsNeighbourCrossingButPreservesValuesOutsideZeroToOne()
	{
		EnvelopeNode[] nodes = Nodes();
		NodeEnvelopeGraphGeometry g = Layout(nodes);
		EnvelopeNode farRight = NodeEnvelopeGraphGeometry.Move(
			nodes, 1, g, g.Right + 1000, g.Y(3.25));
		Assert.That(farRight.Time.Ticks,
			Is.EqualTo(nodes[2].Time.Ticks - 1));
		Assert.That(farRight.Value, Is.EqualTo(3.25).Within(1e-10));
		EnvelopeNode farLeft = NodeEnvelopeGraphGeometry.Move(
			nodes, 1, g, -1000, g.Y(-2.5));
		Assert.That(farLeft.Time.Ticks,
			Is.EqualTo(nodes[0].Time.Ticks + 1));
		Assert.That(farLeft.Value, Is.EqualTo(-2.5).Within(1e-10));

		EnvelopeNode first = NodeEnvelopeGraphGeometry.Move(
			nodes, 0, g, g.Right, g.Y(7));
		Assert.That(first.Time, Is.EqualTo(TimeSpan.Zero));
		Assert.That(first.Value, Is.EqualTo(7).Within(1e-10));
	}

	[Test]
	public void ValidLayoutPreservesUnboundedValueCoordinates()
	{
		EnvelopeNode[] nodes =
		[
			new(TimeSpan.Zero, -20.0),
			new(TimeSpan.FromMilliseconds(100), 12.0),
			new(TimeSpan.FromMilliseconds(200), 0.0),
		];
		NodeEnvelopeGraphGeometry g = Layout(nodes);
		for (int i = 0; i < nodes.Length; i++)
		{
			(double x, double y) = g.Point(i);
			Assert.That(g.Value(y), Is.EqualTo(nodes[i].Value).Within(1e-10));
			Assert.That(g.TimeTicks(x),
				Is.EqualTo(nodes[i].Time.Ticks).Within(1));
		}
	}
}

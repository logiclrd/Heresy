using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthWaypointGeometryTests
{
	private static readonly FmSynthConnectionCandidate Connection =
		new(1, 5, 2);

	[Test]
	public void LineHitHasThreePhysicalPixelToleranceAtEveryZoom()
	{
		FmSynthRenderedConnection connection =
			new(
				Connection,
				[
					new FmSynthRoutePoint(10, 10),
					new FmSynthRoutePoint(110, 10),
					new FmSynthRoutePoint(110, 90),
				],
				[]);

		FmSynthWaypointGeometry.HitConnection(
				[connection],
				new FmSynthRoutePoint(45, 12.9),
				renderScaling: 1)
			.Should().NotBeNull();
		FmSynthWaypointGeometry.HitConnection(
				[connection],
				new FmSynthRoutePoint(45, 13.1),
				renderScaling: 1)
			.Should().BeNull();
		FmSynthWaypointGeometry.HitConnection(
				[connection],
				new FmSynthRoutePoint(107.2, 40),
				renderScaling: 1)
			.Should().NotBeNull();
		FmSynthWaypointGeometry.HitConnection(
				[connection],
				new FmSynthRoutePoint(45, 11.4),
				renderScaling: 2)
			.Should().NotBeNull();
		FmSynthWaypointGeometry.HitConnection(
				[connection],
				new FmSynthRoutePoint(45, 11.6),
				renderScaling: 2)
			.Should().BeNull();
	}

	[Test]
	public void InsertionOrderFollowsClickedRenderedSegmentNotAppendOrder()
	{
		FmSynthRoutePoint first = new(150, 90);
		FmSynthRoutePoint second = new(310, 170);
		FmSynthRenderedConnection connection =
			new(
				Connection,
				[
					new FmSynthRoutePoint(20, 20),
					new FmSynthRoutePoint(150, 20),
					first,
					new FmSynthRoutePoint(310, 90),
					second,
					new FmSynthRoutePoint(420, 170),
					new FmSynthRoutePoint(420, 90),
				],
				[first, second]);

		var beforeFirst = FmSynthWaypointGeometry.HitConnection(
			[connection], new FmSynthRoutePoint(75, 21), 1);
		beforeFirst.Should().NotBeNull();
		beforeFirst!.Value.InsertIndex.Should().Be(0);

		var between = FmSynthWaypointGeometry.HitConnection(
			[connection], new FmSynthRoutePoint(220, 90), 1);
		between!.Value.InsertIndex.Should().Be(1);
		between.Value.SegmentIndex.Should().Be(3);

		var afterLast = FmSynthWaypointGeometry.HitConnection(
			[connection], new FmSynthRoutePoint(375, 170), 1);
		afterLast!.Value.InsertIndex.Should().Be(2);

		FmSynthRoutePoint inserted = new(220, 120);
		FmSynthWaypointGeometry.Insert(
				connection.Waypoints,
				between.Value.InsertIndex,
				inserted)
			.Should().Equal(first, inserted, second);
	}

	[Test]
	public void ClosestConnectionWinsAtCrossingAndZeroLengthSegmentsAreIgnored()
	{
		FmSynthRenderedConnection horizontal =
			new(new FmSynthConnectionCandidate(0, 1, 0),
				[new FmSynthRoutePoint(0, 30), new FmSynthRoutePoint(100, 30)],
				[]);
		FmSynthRenderedConnection vertical =
			new(new FmSynthConnectionCandidate(2, 3, 0),
				[
					new FmSynthRoutePoint(50, 0),
					new FmSynthRoutePoint(50, 0),
					new FmSynthRoutePoint(50, 100),
				],
				[]);

		FmSynthConnectionLineHit? chosen = FmSynthWaypointGeometry.HitConnection(
			[vertical, horizontal],
			new FmSynthRoutePoint(53, 32),
			renderScaling: 1);
		chosen.Should().NotBeNull();
		chosen!.Value.Connection.Should().Be(horizontal.Connection);

		FmSynthConnectionLineHit? nonzero = FmSynthWaypointGeometry.HitConnection(
			[vertical],
			new FmSynthRoutePoint(50, 0),
			renderScaling: 1);
		nonzero.Should().NotBeNull();
		nonzero!.Value.SegmentIndex.Should().Be(1);
	}

	[Test]
	public void ExistingWaypointCanMoveAndDeleteWithoutChangingOthers()
	{
		FmSynthRoutePoint[] original =
			[new(10, 20), new(50, 60), new(90, 100)];
		FmSynthRenderedConnection connection =
			new(Connection, [new FmSynthRoutePoint(0, 0)], original);
		var hit = FmSynthWaypointGeometry.HitWaypoint(
			[connection], new FmSynthRoutePoint(53, 64), 1);
		hit.Should().NotBeNull();
		hit!.Value.Connection.Should().Be(Connection);
		hit.Value.WaypointIndex.Should().Be(1);
		FmSynthRoutePoint replacement = new(55, 63);
		FmSynthRoutePoint[] moved =
			FmSynthWaypointGeometry.Replace(original, 1, replacement);
		moved.Should().Equal(original[0], replacement, original[2]);

		FmSynthWaypointGeometry.Remove(moved, 1)
			.Should().Equal(original[0], original[2]);
		original.Should().Equal(
			new FmSynthRoutePoint(10, 20),
			new FmSynthRoutePoint(50, 60),
			new FmSynthRoutePoint(90, 100));
	}

	[Test]
	public void OutsideHitsAndInvalidScalingHaveNoSideEffects()
	{
		FmSynthRenderedConnection empty =
			new(Connection, [new FmSynthRoutePoint(0, 0)], []);
		FmSynthWaypointGeometry.HitConnection(
			[empty], new FmSynthRoutePoint(0, 0), 1).Should().BeNull();
		FmSynthWaypointGeometry.HitWaypoint(
			[empty], new FmSynthRoutePoint(0, 0), 1).Should().BeNull();

		Action invalid = () =>
			FmSynthWaypointGeometry.HitConnection(
				[empty], new FmSynthRoutePoint(0, 0), 0);
		invalid.Should().Throw<ArgumentOutOfRangeException>();
	}
}

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
	public void WireHitIncludesFourDipBufferBeyondVisibleStrokeAtEveryScale()
	{
		FmSynthRenderedConnection connection = new(
			Connection,
			[
				new FmSynthRoutePoint(10, 10),
				new FmSynthRoutePoint(110, 10),
				new FmSynthRoutePoint(110, 90),
			],
			[]);

		FmSynthWaypointGeometry.WireBufferDips.Should().Be(4.0);
		FmSynthWaypointGeometry.WireStrokeWidthDips.Should().Be(2.0);
		// Unselected line is 2 DIPs wide, so 1 + 4 = 5 DIP
		// centerline-to-hit-border at EVERY render scaling. The extra
		// four DIPs are four physical pixels at 100%, six at 150%.
		foreach (double scaling in new[] { 0.75, 1.0, 1.5, 2.0 })
		{
			Hit(45, 14.99, scaling).Should().NotBeNull();
			Hit(45, 15.01, scaling).Should().BeNull();
			Hit(45, 5.01, scaling).Should().NotBeNull();
			Hit(45, 4.99, scaling).Should().BeNull();
			Hit(105.01, 40, scaling).Should().NotBeNull();
			Hit(104.99, 40, scaling).Should().BeNull();
		}

		// Selection renders a 3-DIP stroke. Its corresponding
		// centerline-to-hit-border distance is 1.5 + 4 = 5.5 DIPs.
		foreach (double scaling in new[] { 1.0, 1.5, 2.0 })
		{
			Hit(45, 15.49, scaling, Connection).Should().NotBeNull();
			Hit(45, 15.51, scaling, Connection).Should().BeNull();
			Hit(45, 4.51, scaling, Connection).Should().NotBeNull();
			Hit(45, 4.49, scaling, Connection).Should().BeNull();
		}

		FmSynthConnectionLineHit? Hit(double x, double y, double scaling,
			FmSynthConnectionCandidate? selected = null)
			=> FmSynthWaypointGeometry.HitConnection([connection],
				new FmSynthRoutePoint(x, y), scaling, selected);
	}

	[Test]
	public void WireHitIsDistanceToFiniteSegmentNotItsInfiniteLine()
	{
		FmSynthRenderedConnection segment = new(
			Connection,
			[new FmSynthRoutePoint(10, 10),
			 new FmSynthRoutePoint(100, 10)],
			[]);
		FmSynthConnectionLineHit? hit =
			FmSynthWaypointGeometry.HitConnection([segment],
				new FmSynthRoutePoint(6, 8), 1);
		hit.Should().NotBeNull(); // 4 by 2 from start => 4.47, under 5.
		FmSynthWaypointGeometry.HitConnection([segment],
			new FmSynthRoutePoint(6, 6), 1).Should().BeNull();
		FmSynthWaypointGeometry.HitConnection([segment],
			new FmSynthRoutePoint(50, 25), 1).Should().BeNull();
	}

	[Test]
	public void WaypointHitExtendsThreeDipsOutsideRenderedOutlineAtEveryScale()
	{
		FmSynthRenderedConnection horizontal = new(
			Connection,
			[new FmSynthRoutePoint(10, 60),
			 new FmSynthRoutePoint(120, 60)],
			[new FmSynthRoutePoint(50, 60)]);

		FmSynthWaypointGeometry.WaypointRadiusDips.Should().Be(4.5);
		FmSynthWaypointGeometry.WaypointOutlineWidthDips.Should().Be(1.5);
		FmSynthWaypointGeometry.WaypointBufferDips.Should().Be(3.0);
		FmSynthWaypointGeometry.WaypointHitRadiusDips.Should().Be(8.25);

		// DrawEllipse's 1.5-DIP outline straddles the 4.5-DIP radius,
		// so the visible edge is 5.25 DIPs. The hit radius is 8.25 DIPs.
		// At 150% scaling this is 12.375 physical pixels from center,
		// including 4.5 physical pixels beyond the rendered edge.
		foreach (double scaling in new[] { 0.75, 1.0, 1.5, 2.0 })
		{
			Hit(58.25, 60, scaling).Should().NotBeNull();
			Hit(58.26, 60, scaling).Should().BeNull();
			Hit(41.75, 60, scaling).Should().NotBeNull();
			Hit(41.74, 60, scaling).Should().BeNull();
			Hit(50, 68.25, scaling).Should().NotBeNull();
			Hit(50, 68.26, scaling).Should().BeNull();
			Hit(50, 51.75, scaling).Should().NotBeNull();
			Hit(50, 51.74, scaling).Should().BeNull();

			// Euclidean circle, not an axis-aligned rectangular region.
			Hit(55, 66.5, scaling).Should().NotBeNull();
			Hit(55.1, 66.6, scaling).Should().BeNull();
		}
		// 8.2 DIPs is beyond the previous 8-physical-pixel radius at
		// 100%, but is now correctly selectable regardless of DPI.
		Hit(58.2, 60, 1).Should().NotBeNull();
		Hit(55, 60, 1)!.Value.Connection.Should().Be(Connection);

		FmSynthWaypointHit? Hit(double x, double y, double scaling)
			=> FmSynthWaypointGeometry.HitWaypoint([horizontal],
				new FmSynthRoutePoint(x, y), scaling);
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
		between.Value.SegmentIndex.Should().Be(2);

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

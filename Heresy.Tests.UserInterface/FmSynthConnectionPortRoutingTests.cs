using System.Linq;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthConnectionPortRoutingTests
{
	[Test]
	public void OperatorConnectionsEndAtTheirDistinctInputHandles()
	{
		FmSynthLayoutRect source = new(20, 80, 170, 72);
		FmSynthLayoutRect target = new(620, 80, 170, 72);
		FmSynthConnectionPort sourcePort =
			FmSynthConnectionPorts.GetPorts(new FmConstantNode(0, 1), source)[0];
		FmSynthConnectionPort[] targetPorts =
			FmSynthConnectionPorts.GetPorts(
				new FmOperatorNode(1, FmOperatorKind.Add, [0, 0]),
				target);
		foreach (FmSynthConnectionPort sink in targetPorts.Skip(1))
		{
			FmSynthRoutePoint[] route =
				FmSynthConnectionRouter.Route(
					source,
					target,
					[],
					null,
					sourcePort.Center,
					sink.Center);

			route[0].Should().Be(sourcePort.Center);
			route[^1].Should().Be(sink.Center);
			route[^2].Y.Should().Be(sink.Center.Y);
			route[^2].X.Should().BeLessThan(sink.Center.X);
		}
	}

	[Test]
	public void BackwardConnectionUsesExteriorLaneToReachFixedPortSides()
	{
		FmSynthLayoutRect source = new(620, 80, 170, 72);
		FmSynthLayoutRect target = new(20, 80, 170, 72);
		FmSynthLayoutRect blocker = new(300, 50, 170, 120);
		FmSynthRoutePoint start = new(source.Right, source.CenterY);
		FmSynthRoutePoint end = new(target.Left, target.CenterY);

		FmSynthRoutePoint[] route =
			FmSynthConnectionRouter.Route(
				source, target, [blocker], null, start, end);

		route[0].Should().Be(start);
		route[^1].Should().Be(end);
		route.Length.Should().BeGreaterThan(3);
		route[^2].Y.Should().Be(end.Y);
		route[^2].X.Should().BeLessThan(end.X);
		FmSynthConnectionRouter.CrossesInterior(route, source).Should().BeFalse();
		FmSynthConnectionRouter.CrossesInterior(route, target).Should().BeFalse();
		FmSynthConnectionRouter.CrossesInterior(route, blocker).Should().BeFalse();
	}

	[Test]
	public void HintedAnchoredConnectionsRetainWaypointsAndReachInputHorizontally()
	{
		FmSynthLayoutRect source = new(20, 80, 170, 72);
		FmSynthLayoutRect target = new(620, 80, 170, 72);
		FmSynthRoutePoint start = new(source.Right, source.CenterY);
		FmSynthRoutePoint end = new(target.Left, target.Top + 18);

		FmSynthRoutePoint[] route =
			FmSynthConnectionRouter.Route(
				source,
				target,
				[],
				[new FmSynthRoutePoint(400, 270)],
				start,
				end);

		route.Should().Contain(new FmSynthRoutePoint(400, 270));
		route[0].Should().Be(start);
		route[^1].Should().Be(end);
		route[^2].Y.Should().Be(end.Y);
		route[^2].X.Should().BeLessThan(end.X);
	}
}

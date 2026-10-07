using System.Linq;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthConnectionRouterTests
{
	[Test]
	public void AutomaticRouteIsOrthogonalAndAvoidsBlockingNode()
	{
		FmSynthLayoutRect source =
			new(20.0, 80.0, 160.0, 68.0);
		FmSynthLayoutRect target =
			new(620.0, 80.0, 160.0, 68.0);
		FmSynthLayoutRect blocker =
			new(300.0, 50.0, 160.0, 140.0);

		FmSynthRoutePoint[] route =
			FmSynthConnectionRouter.Route(
				source,
				target,
				[blocker],
				routingHints: null);

		route.Length.Should().BeGreaterThan(2);
		for (int index = 1; index < route.Length; index++)
		{
			FmSynthRoutePoint a = route[index - 1];
			FmSynthRoutePoint b = route[index];
			(a.X == b.X || a.Y == b.Y).Should().BeTrue();
		}

		FmSynthConnectionRouter.CrossesInterior(
				route,
				blocker)
			.Should().BeFalse();
	}

	[Test]
	public void UserRoutingHintsBecomeOrthogonalWaypoints()
	{
		FmSynthLayoutRect source =
			new(20.0, 80.0, 160.0, 68.0);
		FmSynthLayoutRect target =
			new(620.0, 80.0, 160.0, 68.0);

		FmSynthRoutePoint[] route =
			FmSynthConnectionRouter.Route(
				source,
				target,
				[],
				[
					new FmSynthRoutePoint(250.0, 250.0),
					new FmSynthRoutePoint(550.0, 250.0),
				]);

		route.Should().Contain(
			point => point.X == 250.0 && point.Y == 250.0);
		route.Should().Contain(
			point => point.X == 550.0 && point.Y == 250.0);
		for (int index = 1; index < route.Length; index++)
		{
			FmSynthRoutePoint a = route[index - 1];
			FmSynthRoutePoint b = route[index];
			(a.X == b.X || a.Y == b.Y).Should().BeTrue();
		}
	}
}

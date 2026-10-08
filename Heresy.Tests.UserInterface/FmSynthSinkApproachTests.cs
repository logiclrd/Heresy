using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthSinkApproachTests
{
	[TestCase(false)]
	[TestCase(true)]
	public void HintedRoutesFinishByPointingIntoTargetSide(
		bool reverse)
	{
		FmSynthLayoutRect source =
			reverse
				? new FmSynthLayoutRect(620.0, 80.0, 160.0, 68.0)
				: new FmSynthLayoutRect(20.0, 80.0, 160.0, 68.0);
		FmSynthLayoutRect target =
			reverse
				? new FmSynthLayoutRect(20.0, 80.0, 160.0, 68.0)
				: new FmSynthLayoutRect(620.0, 80.0, 160.0, 68.0);

		FmSynthRoutePoint[] route =
			FmSynthConnectionRouter.Route(
				source,
				target,
				[],
				[new FmSynthRoutePoint(400.0, 250.0)]);

		FmSynthRoutePoint penultimate = route[^2];
		FmSynthRoutePoint sink = route[^1];
		sink.X.Should().Be(
			reverse ? target.Right : target.Left);
		sink.Y.Should().Be(target.CenterY);
		penultimate.Y.Should().Be(sink.Y);
		if (reverse)
			penultimate.X.Should().BeGreaterThan(sink.X);
		else
			penultimate.X.Should().BeLessThan(sink.X);
	}
}

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.FmEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class FmSynthConnectionArrowheadTests
{
	[Test]
	public void ForwardConnectionPointsIntoTargetOnly()
	{
		FmSynthConnectionArrowhead? arrow =
			FmSynthConnectionArrowheadGeometry.FromRoute(
				[
					new FmSynthRoutePoint(0.0, 20.0),
					new FmSynthRoutePoint(100.0, 20.0),
				]);

		arrow.Should().NotBeNull();
		arrow!.Value.Tip.Should().Be(
			new FmSynthRoutePoint(100.0, 20.0));
		arrow.Value.FirstWing.Should().Be(
			new FmSynthRoutePoint(92.0, 24.0));
		arrow.Value.SecondWing.Should().Be(
			new FmSynthRoutePoint(92.0, 16.0));
	}

	[Test]
	public void ReverseConnectionFacesLeft()
	{
		FmSynthConnectionArrowhead? arrow =
			FmSynthConnectionArrowheadGeometry.FromRoute(
				[
					new FmSynthRoutePoint(100.0, 20.0),
					new FmSynthRoutePoint(0.0, 20.0),
				]);

		arrow.Should().NotBeNull();
		arrow!.Value.Tip.Should().Be(
			new FmSynthRoutePoint(0.0, 20.0));
		arrow.Value.FirstWing.Should().Be(
			new FmSynthRoutePoint(8.0, 16.0));
		arrow.Value.SecondWing.Should().Be(
			new FmSynthRoutePoint(8.0, 24.0));
	}

	[Test]
	public void LastSegmentDeterminesDirectionThroughBendsAndDuplicates()
	{
		FmSynthConnectionArrowhead? arrow =
			FmSynthConnectionArrowheadGeometry.FromRoute(
				[
					new FmSynthRoutePoint(0.0, 20.0),
					new FmSynthRoutePoint(100.0, 20.0),
					new FmSynthRoutePoint(100.0, 60.0),
					new FmSynthRoutePoint(100.0, 60.0),
				]);

		arrow.Should().NotBeNull();
		arrow!.Value.Tip.Should().Be(
			new FmSynthRoutePoint(100.0, 60.0));
		arrow.Value.FirstWing.Should().Be(
			new FmSynthRoutePoint(96.0, 52.0));
		arrow.Value.SecondWing.Should().Be(
			new FmSynthRoutePoint(104.0, 52.0));
	}

	[Test]
	public void ShortLastSegmentScalesArrowWithinAvailableLength()
	{
		FmSynthConnectionArrowhead? arrow =
			FmSynthConnectionArrowheadGeometry.FromRoute(
				[
					new FmSynthRoutePoint(0.0, 0.0),
					new FmSynthRoutePoint(3.0, 0.0),
				]);

		arrow.Should().NotBeNull();
		arrow!.Value.FirstWing.Should().Be(
			new FmSynthRoutePoint(0.0, 1.5));
		arrow.Value.SecondWing.Should().Be(
			new FmSynthRoutePoint(0.0, -1.5));
	}

	[Test]
	public void ZeroLengthRoutesHaveNoArrow()
	{
		FmSynthConnectionArrowheadGeometry.FromRoute([])
			.Should().BeNull();
		FmSynthConnectionArrowheadGeometry.FromRoute(
				[new FmSynthRoutePoint(5.0, 10.0)])
			.Should().BeNull();
		FmSynthConnectionArrowheadGeometry.FromRoute(
				[
					new FmSynthRoutePoint(5.0, 10.0),
					new FmSynthRoutePoint(5.0, 10.0),
				])
			.Should().BeNull();
	}
}

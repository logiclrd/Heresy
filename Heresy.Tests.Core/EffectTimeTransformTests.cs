using System;

using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class TrackerRowTimingTests
{
	[Test]
	public void ConstantTempoMapsWholeWallDurationOntoCapturedSpeedDomain()
	{
		TrackerRowTiming row = new(
			speed: 6.0,
			startingTempo: 125.0,
			endingTempo: 125.0);

		Assert.That(
			row.RowDurationSeconds,
			Is.EqualTo(6.0 * 2.5 / 125.0).Within(1e-12));
		Assert.That(
			row.GetRowTime(0.06),
			Is.EqualTo(3.0).Within(1e-12));
		Assert.That(
			row.GetWallTimeSeconds(3.0),
			Is.EqualTo(0.06).Within(1e-12));
	}

	[Test]
	public void TempoRampKeepsSameRowDomainButAdvancesThroughItNonlinearly()
	{
		TrackerRowTiming row = new(
			speed: 6.0,
			startingTempo: 125.0,
			endingTempo: 135.0);

		double halfwayWall =
			row.GetWallTimeSeconds(3.0);

		Assert.That(
			halfwayWall,
			Is.EqualTo(
				1.5 * Math.Log(130.0 / 125.0))
				.Within(1e-12));
		Assert.That(
			row.GetRowTime(halfwayWall),
			Is.EqualTo(3.0).Within(1e-12));
		Assert.That(
			row.GetTempo(0.0),
			Is.EqualTo(125.0));
		Assert.That(
			row.GetTempo(3.0),
			Is.EqualTo(130.0));
		Assert.That(
			row.GetTempo(6.0),
			Is.EqualTo(135.0));
	}

	[Test]
	public void TempoRampDerivativeStartsAndEndsAtBoundaryTempos()
	{
		TrackerRowTiming row = new(
			speed: 6.0,
			startingTempo: 125.0,
			endingTempo: 135.0);

		Assert.That(
			row.GetRowTimeRate(0.0),
			Is.EqualTo(125.0 / 2.5).Within(1e-12));
		Assert.That(
			row.GetRowTimeRate(row.RowDurationSeconds),
			Is.EqualTo(135.0 / 2.5).Within(1e-12));
	}

	[Test]
	public void NewRowStartsFreshAtCommittedTempo()
	{
		TrackerRowTiming first = new(
			speed: 6.0,
			startingTempo: 125.0,
			endingTempo: 135.0);
		TrackerRowTiming second = new(
			speed: 6.0,
			startingTempo: 135.0,
			endingTempo: 135.0);

		Assert.That(first.GetTempo(6.0), Is.EqualTo(135.0));
		Assert.That(second.GetTempo(0.0), Is.EqualTo(135.0));
		Assert.That(
			second.GetRowTimeRate(0.0),
			Is.EqualTo(135.0 / 2.5).Within(1e-12));
	}
}

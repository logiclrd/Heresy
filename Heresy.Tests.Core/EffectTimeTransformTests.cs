using System;

using Heresy.Core.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class EffectTimeTransformTests
{
	[TestCase(125.0)]
	[TestCase(250.0)]
	public void ConstantTempoMakesEffectTimeEqualWallTime(
		double tempo)
	{
		TrackerTimeMap map = new(tempo);
		EffectTimeTransform effect =
			new(map, startTimeSeconds: 0.0);

		Assert.That(
			effect.GetTimeSeconds(0.5),
			Is.EqualTo(0.5).Within(1e-12));
		Assert.That(
			effect.GetRate(0.5),
			Is.EqualTo(1.0).Within(1e-12));
	}

	[Test]
	public void SuddenTempoChangeKeepsValueContinuousButChangesDerivative()
	{
		TrackerTimeMap map = new(125.0);
		map.AppendConstantTime(0.25);
		map.SetTempo(250.0);

		EffectTimeTransform effect =
			new(map, startTimeSeconds: 0.0);

		Assert.That(
			effect.GetTimeSeconds(0.25),
			Is.EqualTo(0.25).Within(1e-12));
		Assert.That(
			effect.GetRate(0.25),
			Is.EqualTo(2.0).Within(1e-12));
		Assert.That(
			effect.GetTimeSeconds(0.375),
			Is.EqualTo(0.5).Within(1e-12));
	}

	[Test]
	public void TempoRampChangesEffectTimeRateSmoothly()
	{
		TrackerTimeMap map = new(125.0);
		map.AppendTempoRamp(
			endingTempo: 250.0,
			trackerTicks: 1.0);

		EffectTimeTransform effect =
			new(map, startTimeSeconds: 0.0);

		double halfwayTime =
			map.GetTimeAtTick(0.5);

		Assert.That(
			effect.GetRate(0.0),
			Is.EqualTo(1.0).Within(1e-12));
		Assert.That(
			effect.GetRate(halfwayTime),
			Is.EqualTo(1.5).Within(1e-12));
		Assert.That(
			effect.GetRate(map.CurrentTimeSeconds),
			Is.EqualTo(2.0).Within(1e-12));

		Assert.That(
			effect.GetTimeSeconds(map.CurrentTimeSeconds),
			Is.EqualTo(2.5 / 125.0).Within(1e-12));
	}

	[Test]
	public void FreshEffectAfterRampStartsAtUnitRateAgain()
	{
		TrackerTimeMap map = new(125.0);
		map.AppendTempoRamp(
			endingTempo: 250.0,
			trackerTicks: 1.0);

		double start = map.CurrentTimeSeconds;
		EffectTimeTransform effect =
			new(map, start);

		Assert.That(
			effect.ReferenceTempo,
			Is.EqualTo(250.0).Within(1e-12));
		Assert.That(
			effect.GetRate(start),
			Is.EqualTo(1.0).Within(1e-12));
		Assert.That(
			effect.GetTimeSeconds(start + 0.5),
			Is.EqualTo(0.5).Within(1e-12));
	}

	[Test]
	public void LinearTrackerTempoRampHasLogarithmicWallDuration()
	{
		TrackerTimeMap map = new(125.0);
		map.AppendTempoRamp(
			endingTempo: 250.0,
			trackerTicks: 1.0);

		double expected =
			2.5 / (250.0 - 125.0)
				* Math.Log(250.0 / 125.0);

		Assert.That(
			map.CurrentTimeSeconds,
			Is.EqualTo(expected).Within(1e-12));
		Assert.That(
			map.GetTickAtTime(expected),
			Is.EqualTo(1.0).Within(1e-12));
	}

	[Test]
	public void TempoAndTrackerPositionRemainContinuousAcrossRampAndSet()
	{
		TrackerTimeMap map = new(125.0);
		map.AppendTempoRamp(150.0, trackerTicks: 1.0);

		double boundaryTime = map.CurrentTimeSeconds;
		double boundaryTick = map.CurrentTick;

		map.SetTempo(200.0);

		Assert.That(
			map.GetTickAtTime(boundaryTime),
			Is.EqualTo(boundaryTick).Within(1e-12));
		Assert.That(
			map.GetTimeAtTick(boundaryTick),
			Is.EqualTo(boundaryTime).Within(1e-12));
		Assert.That(
			map.GetTempoAtTime(boundaryTime),
			Is.EqualTo(200.0).Within(1e-12));
	}
}

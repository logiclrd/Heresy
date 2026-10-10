using System;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.UserInterface.EnvelopeEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class AdsrGraphGeometryTests
{
	[Test]
	public void GraphUsesOneAtTopAndZeroAtBottom()
	{
		AdsrGraphLayout graph = AdsrGraphLayout.Create(
			new(1, 2, 0.25, 3), 600, 240);
		Assert.That(graph.YToValue(graph.Top), Is.EqualTo(1).Within(1e-12));
		Assert.That(graph.YToValue(graph.Top + graph.Height),
			Is.EqualTo(0).Within(1e-12));
		Assert.That(graph.SustainY,
			Is.EqualTo(graph.Top + graph.Height * 0.75).Within(1e-12));
		Assert.That(graph.AttackX, Is.LessThan(graph.DecayX));
		Assert.That(graph.DecayX, Is.LessThan(graph.ReleaseStartX));
		Assert.That(graph.ReleaseStartX, Is.LessThan(graph.ReleaseEndX));
	}

	[TestCase(-0.5, 0.0)]
	[TestCase(1.5, 1.0)]
	public void OutOfRangeSustainClipsGraphOnlyAndNeverChangesSource(
		double sustain, double referenceValue)
	{
		AdsrEnvelopeDefinition envelope = new((ObjectId)1U, "Sustain")
		{
			SustainLevel = sustain,
		};
		AdsrGraphValues values = AdsrGraphValues.FromEnvelope(envelope);
		AdsrGraphLayout graph = AdsrGraphLayout.Create(values, 640, 200);
		Assert.That(values.SustainLevel, Is.EqualTo(sustain));
		Assert.That(graph.YToValue(graph.SustainY),
			Is.EqualTo(referenceValue).Within(1e-12));
		Assert.That(envelope.SustainLevel, Is.EqualTo(sustain));
	}

	[TestCase(AdsrGraphHandle.Attack)]
	[TestCase(AdsrGraphHandle.Decay)]
	[TestCase(AdsrGraphHandle.Release)]
	public void ZeroDurationEndBoundaryCanBeDraggedPositive(
		AdsrGraphHandle handle)
	{
		AdsrGraphValues values = new(0, 0, 0.5, 0);
		AdsrGraphLayout graph = AdsrGraphLayout.Create(values, 620, 220);
		double x = handle switch
		{
			AdsrGraphHandle.Attack => graph.AttackX,
			AdsrGraphHandle.Decay => graph.DecayX,
			_ => graph.ReleaseEndX,
		};
		double capY = handle switch
		{
			AdsrGraphHandle.Attack => graph.Top + 8,
			AdsrGraphHandle.Decay => graph.Top + 23,
			_ => graph.Top + 38,
		};
		Assert.That(graph.HitHandle(x, capY), Is.EqualTo(handle));
		AdsrGraphValues moved = AdsrGraphValues.MoveHandle(values, handle,
			graph.PixelsPerSecond * 0.25, capY, graph);
		double duration = handle switch
		{
			AdsrGraphHandle.Attack => moved.AttackSeconds,
			AdsrGraphHandle.Decay => moved.DecaySeconds,
			_ => moved.ReleaseSeconds,
		};
		Assert.That(duration, Is.EqualTo(0.25).Within(1e-9));
	}

	[Test]
	public void OverlappingDecaySecondBoundaryWinsOverFirstOutsideCapLanes()
	{
		AdsrGraphLayout graph = AdsrGraphLayout.Create(
			new(1, 0, 0.5, 1), 700, 240);
		Assert.That(graph.AttackX, Is.EqualTo(graph.DecayX));
		Assert.That(graph.HitHandle(graph.DecayX, graph.Top + 90),
			Is.EqualTo(AdsrGraphHandle.Decay));
	}

	[Test]
	public void SustainLineCanBeDraggedToNegativeOrAboveUnity()
	{
		AdsrGraphValues initial = new(1, 1, 0.5, 1);
		AdsrGraphLayout graph = AdsrGraphLayout.Create(initial, 700, 260);
		Assert.That(graph.HitHandle(
			(graph.DecayX + graph.ReleaseStartX) / 2, graph.SustainY),
			Is.EqualTo(AdsrGraphHandle.Sustain));
		Assert.That(AdsrGraphValues.MoveHandle(initial, AdsrGraphHandle.Sustain,
			0, graph.Top - graph.Height / 2, graph).SustainLevel,
			Is.EqualTo(1.5).Within(1e-12));
		Assert.That(AdsrGraphValues.MoveHandle(initial, AdsrGraphHandle.Sustain,
			0, graph.Top + graph.Height * 1.25, graph).SustainLevel,
			Is.EqualTo(-0.25).Within(1e-12));
	}

	[Test]
	public void DurationMovementClampsAtZeroWithoutChangingOtherFields()
	{
		AdsrGraphValues original = new(1, 2, 0.6, 3);
		AdsrGraphLayout graph = AdsrGraphLayout.Create(original, 640, 220);
		AdsrGraphValues edited = AdsrGraphValues.MoveHandle(
			original, AdsrGraphHandle.Decay,
			-100 * graph.PixelsPerSecond, graph.SustainY, graph);
		Assert.That(edited.DecaySeconds, Is.Zero);
		Assert.That(edited.AttackSeconds, Is.EqualTo(1));
		Assert.That(edited.ReleaseSeconds, Is.EqualTo(3));
		Assert.That(edited.SustainLevel, Is.EqualTo(0.6));
	}
}

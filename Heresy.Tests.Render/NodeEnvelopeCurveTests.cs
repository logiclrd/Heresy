using System;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.Render.Envelopes;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class NodeEnvelopeCurveTests
{
	private static NodeEnvelopeCurve Curve(
		EnvelopeSustainLoopMode mode = EnvelopeSustainLoopMode.Forward,
		int start = 1, int end = 2)
	{
		NodeEnvelopeDefinition definition = new((ObjectId)1U, "Curve");
		definition.ReplaceCurve(
		[
			new(TimeSpan.Zero, 0.0),
			new(TimeSpan.FromSeconds(1), 1.0),
			new(TimeSpan.FromSeconds(2), 0.25),
			new(TimeSpan.FromSeconds(3), -0.5),
			new(TimeSpan.FromSeconds(4), 0.0),
		], start, end, mode);
		return new NodeEnvelopeCurve(definition);
	}

	private static void Near(
		NodeEnvelopeCurve curve, int frame, double value, int? off = null)
		=> Assert.That(curve.GetValue(frame, 10, off),
			Is.EqualTo(value).Within(1e-10), $"Frame {frame}, off {off}");

	[Test]
	public void ForwardWrapCanJumpAtEndAndReleaseKeepsCurrentPosition()
	{
		NodeEnvelopeCurve curve = Curve();
		Near(curve, 0, 0.0);
		Near(curve, 5, 0.5);
		Near(curve, 10, 1.0);
		Near(curve, 15, 0.625);
		Near(curve, 19, 0.325);
		Near(curve, 20, 1.0); // Sustain end jumps to start.
		Near(curve, 25, 0.625);
		Near(curve, 30, 1.0);
		Near(curve, 35, -0.125, off: 25);
		Near(curve, 30, 0.25, off: 25); // No wrap at sustain end.
		Near(curve, 40, -0.5, off: 25);
		Near(curve, 50, 0.0, off: 25);
		Near(curve, 51, 0.0, off: 25);
		Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(25, 10),
			Is.EqualTo(51));
	}

	[Test]
	public void PingPongReflectsContinuouslyAndReleasesWhileTravellingBackwards()
	{
		NodeEnvelopeCurve curve = Curve(EnvelopeSustainLoopMode.PingPong);
		Near(curve, 19, 0.325);
		Near(curve, 20, 0.25);
		Near(curve, 25, 0.625);
		Near(curve, 30, 1.0);
		Near(curve, 35, 0.625);
		Near(curve, 40, 0.25);
		// Off halfway down the backward leg: continue back to sustain
		// start, reflect there, but do NOT reflect at the sustain end.
		Near(curve, 25, 0.625, off: 25);
		Near(curve, 30, 1.0, off: 25);
		Near(curve, 35, 0.625, off: 25);
		Near(curve, 40, 0.25, off: 25);
		Near(curve, 50, -0.5, off: 25);
		Near(curve, 60, 0.0, off: 25);
		Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(25, 10),
			Is.EqualTo(61));
	}

	[Test]
	public void ReleaseAtPingPongReflectionPointContinuesForward()
	{
		NodeEnvelopeCurve curve = Curve(EnvelopeSustainLoopMode.PingPong);
		Near(curve, 30, 1.0, off: 30);
		Near(curve, 35, 0.625, off: 30);
		Near(curve, 40, 0.25, off: 30);
		Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(30, 10),
			Is.EqualTo(61));
	}

	[Test]
	public void OffBeforeSustainPassesStraightThroughSustainEnd()
	{
		foreach (EnvelopeSustainLoopMode mode
			in Enum.GetValues<EnvelopeSustainLoopMode>())
		{
			NodeEnvelopeCurve curve = Curve(mode);
			Near(curve, 5, 0.5, off: 5);
			Near(curve, 15, 0.625, off: 5);
			Near(curve, 20, 0.25, off: 5);
			Near(curve, 30, -0.5, off: 5);
			Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(5, 10),
				Is.EqualTo(41));
		}
	}

	[Test]
	public void OneNodeSustainHoldsUntilReleaseThenResumesForward()
	{
		foreach (EnvelopeSustainLoopMode mode
			in new[] { EnvelopeSustainLoopMode.Forward, EnvelopeSustainLoopMode.PingPong })
		{
			NodeEnvelopeCurve curve = Curve(mode, 1, 1);
			Near(curve, 10, 1.0);
			Near(curve, 100, 1.0);
			Near(curve, 50, 1.0, off: 50);
			Near(curve, 55, 0.625, off: 50);
			Near(curve, 60, 0.25, off: 50);
			Near(curve, 70, -0.5, off: 50);
			Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(50, 10),
				Is.EqualTo(81));
		}
	}

	[Test]
	public void NoneModeIgnoresSustainAndProceedsToEndWithOrWithoutNoteOff()
	{
		NodeEnvelopeCurve curve = Curve(EnvelopeSustainLoopMode.None, 1, 2);
		Near(curve, 10, 1.0);
		Near(curve, 20, 0.25);
		Near(curve, 30, -0.5);
		Near(curve, 40, 0.0);
		Near(curve, 41, 0.0);
		Near(curve, 20, 0.25, off: 15);
		Near(curve, 30, -0.5, off: 15);
		Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(15, 10),
			Is.EqualTo(41));
	}

	[Test]
	public void LastNodeMayBeNonZeroAndRetiresAfterItsLastSample()
	{
		NodeEnvelopeDefinition definition = new((ObjectId)1U, "One node");
		definition.ReplaceCurve([new(TimeSpan.Zero, -1.25)], 0, 0,
			EnvelopeSustainLoopMode.Forward);
		NodeEnvelopeCurve curve = new(definition);
		Assert.That(curve.GetValue(999, 10, null), Is.EqualTo(-1.25));
		Assert.That(curve.GetValue(9, 10, 9), Is.EqualTo(-1.25));
		Assert.That(curve.GetValue(10, 10, 9), Is.Zero);
		Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(9, 10),
			Is.EqualTo(10));
	}

	[Test]
	public void ExactFinalNodeMayBeAboveUnityBeforeSilence()
	{
		NodeEnvelopeDefinition definition = new((ObjectId)1U, "Positive tail");
		definition.ReplaceCurve(
			[new(TimeSpan.Zero, -0.5),
			 new(TimeSpan.FromSeconds(1), 2.5)],
			0, 0, EnvelopeSustainLoopMode.PingPong);
		NodeEnvelopeCurve curve = new(definition);
		Assert.That(curve.GetValue(20, 10, 10), Is.EqualTo(2.5));
		Assert.That(curve.GetValue(21, 10, 10), Is.Zero);
		Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(10, 10),
			Is.EqualTo(21));
	}

	[Test]
	public void CurveClonesNodesAndDoesNotTrackSubsequentEdits()
	{
		NodeEnvelopeDefinition definition = new((ObjectId)1U, "Copy");
		definition.ReplaceCurve(
			[new(TimeSpan.Zero, 0.0),
			 new(TimeSpan.FromSeconds(1), 1.0)], 1, 1,
			EnvelopeSustainLoopMode.Forward);
		NodeEnvelopeCurve frozen = new(definition);
		definition.ReplaceCurve([new(TimeSpan.Zero, 7.0)], 0, 0,
			EnvelopeSustainLoopMode.Forward);
		Assert.That(frozen.GetValue(10, 10, null), Is.EqualTo(1.0));
	}

	[Test]
	public void PlaybackStatePauseAndHistoricalValueQueriesStayDeterministic()
	{
		EnvelopePlaybackState voice = new(
			Curve(EnvelopeSustainLoopMode.PingPong),
			startFrame: 10, sampleRate: 10);
		Near(Curve(EnvelopeSustainLoopMode.PingPong), 25, 0.625);
		Assert.That(voice.GetValue(35), Is.EqualTo(0.625).Within(1e-10));
		voice.SetEnabled(35, false);
		Assert.That(voice.GetValue(48), Is.EqualTo(0.625).Within(1e-10));
		voice.SetEnabled(48, true);
		voice.NoteOff(48); // Active frame is still 25.
		Assert.That(voice.GetValue(53), Is.EqualTo(1.0).Within(1e-10));
		Assert.That(voice.GetValue(58), Is.EqualTo(0.625).Within(1e-10));
		Assert.That(voice.GetValue(15), Is.EqualTo(0.5).Within(1e-10));
		Assert.That(voice.GetEndFrameExclusiveAfterNoteOff(58),
			Is.EqualTo(84));
	}

	[Test]
	public void InvalidArgumentsAreRejected()
	{
		NodeEnvelopeCurve curve = Curve();
		Assert.Throws<ArgumentOutOfRangeException>(
			() => curve.GetValue(-1, 10, null));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => curve.GetValue(1, 0, null));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => curve.GetValue(1, 10, 2));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => curve.GetEndActiveFrameExclusiveAfterNoteOff(-1, 10));
	}
}

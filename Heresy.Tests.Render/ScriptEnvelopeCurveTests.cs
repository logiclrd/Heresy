using System;

using Heresy.Render.Envelopes;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class ScriptEnvelopeCurveTests
{
	[Test]
	public void ScriptCurveEvaluatesActiveSecondsAndExplicitReleaseBound()
	{
		ScriptEnvelopeCurve curve = new(
			(t, release) => release < 0 ? t / 4.0 : 1.0 - release / 0.5,
			TimeSpan.FromMilliseconds(500));
		Assert.Multiple(() =>
		{
			Assert.That(curve.GetValue(100, 100, null), Is.EqualTo(0.25));
			Assert.That(curve.GetValue(250, 100, 200), Is.EqualTo(0.0));
			Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(200, 100),
				Is.EqualTo(250));
			Assert.That(curve.GetValue(251, 100, 200), Is.Zero);
		});
	}

	[Test]
	public void FiniteReleaseEnforcesSilenceEvenWhenExpressionWouldContinue()
	{
		int calls = 0;
		ScriptEnvelopeCurve curve = new(
			(_, _) => { calls++; return -2.0; },
			TimeSpan.FromMilliseconds(150));
		Assert.That(curve.GetValue(1, 100, null), Is.EqualTo(-2.0));
		Assert.That(curve.GetValue(16, 100, 1), Is.Zero);
		Assert.That(calls, Is.EqualTo(1),
			"An explicit bound must bypass script evaluation after release.");
		Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(1, 100),
			Is.EqualTo(16), "150 ms at 100 Hz is fifteen active frames.");
	}

	[Test]
	public void IndefiniteReleaseIsNeverClaimedAsFinite()
	{
		ScriptEnvelopeCurve curve = new((_, _) => 0.5, null);
		Assert.Multiple(() =>
		{
			Assert.That(curve.GetEndActiveFrameExclusiveAfterNoteOff(10, 100),
				Is.Null);
			Assert.That(curve.GetValue(10000, 100, 10), Is.EqualTo(0.5));
		});
	}

	[Test]
	public void PausedTimelineDoesNotConsumeReleaseDuration()
	{
		EnvelopePlaybackState state = new(
			new ScriptEnvelopeCurve((t, r) => r < 0 ? t : 1.0 - r,
				TimeSpan.FromSeconds(1)),
			startFrame: 0, sampleRate: 10);
		state.NoteOff(2);
		state.SetEnabled(4, false);
		Assert.That(state.GetValue(20), Is.EqualTo(0.8));
		Assert.That(state.GetEndFrameExclusiveAfterNoteOff(20), Is.Null);
		state.SetEnabled(20, true);
		Assert.That(state.GetValue(25), Is.EqualTo(0.3).Within(1e-12));
		Assert.That(state.GetEndFrameExclusiveAfterNoteOff(25), Is.EqualTo(30));
	}

	[TestCase(-0.01)]
	[TestCase(1.01)]
	public void ScriptCurveDoesNotClipFiniteValues(double value)
	{
		ScriptEnvelopeCurve curve = new((_, _) => value, null);
		Assert.That(curve.GetValue(0, 100, null), Is.EqualTo(value));
	}

	[Test]
	public void NonFiniteOutputsAreRejected()
	{
		ScriptEnvelopeCurve curve = new((_, _) => double.NaN, null);
		Assert.Throws<InvalidOperationException>(() => curve.GetValue(0, 100, null));
	}

	[Test]
	public void NegativeReleaseBoundIsInvalid()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			new ScriptEnvelopeCurve((_, _) => 1, TimeSpan.FromTicks(-1)));
	}
}

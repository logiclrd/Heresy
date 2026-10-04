using System;

using Heresy.Render.Playback;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class AntiClickTailTests
{
	[Test]
	public void ReferenceDecayIsExactlyPointNineThreeAt44100Hz()
	{
		Assert.That(
			AntiClickTail.CalculateDecay(44100),
			Is.EqualTo(0.93).Within(1e-15));
	}

	[Test]
	public void HalfSampleRateSquaresReferenceDecay()
	{
		Assert.That(
			AntiClickTail.CalculateDecay(22050),
			Is.EqualTo(0.93 * 0.93).Within(1e-15));
	}

	[Test]
	public void FirstTailFrameContinuesLastObservedSlope()
	{
		AntiClickTail tail = new(1, 44100);
		tail.AddCut(
			new float[] { 1.0f },
			new float[] { 2.0f },
			havePrevious: true);
		float[] output = new float[1];

		tail.RenderFrame(output);

		Assert.That(output[0], Is.EqualTo(3.0f).Within(1e-6f));
	}
}

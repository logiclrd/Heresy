using System;
using System.Numerics;

using Heresy.Render.Configuration;
using Heresy.Render.Spatial;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class SpatializerTests
{
	[Test]
	public void CenterSourceHasInverseSqrtTwoGainToStereoSpeakersAtUnitDistance()
	{
		OutputChannelConfiguration left = new(
			new Vector3(-1.0f, 0.0f, 0.0f),
			positionalImportance: 1.0);

		double gain = Spatializer.CalculateGain(Vector3.Zero, left);

		Assert.That(gain, Is.EqualTo(1.0 / Math.Sqrt(2.0)).Within(1e-12));
	}

	[Test]
	public void ZeroPositionalImportanceGivesUnityGainAtAnyDistance()
	{
		OutputChannelConfiguration output = new(
			new Vector3(100.0f, -200.0f, 50.0f),
			positionalImportance: 0.0);

		double gain = Spatializer.CalculateGain(
			new Vector3(-20.0f, 4.0f, 12.0f),
			output);

		Assert.That(gain, Is.EqualTo(1.0));
	}
}

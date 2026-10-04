using System;

using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class ResonantFilterParametersTests
{
	[Test]
	public void ParametersMustBeNormalized()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new ResonantFilterParameters(-0.001, 0.0));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new ResonantFilterParameters(1.001, 0.0));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new ResonantFilterParameters(0.5, -0.001));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new ResonantFilterParameters(0.5, 1.001));
	}

	[Test]
	public void MaximumCutoffAndZeroResonanceBypassesFilter()
	{
		ResonantFilterParameters parameters =
			new(1.0, 0.0);

		Assert.That(parameters.IsDisabled, Is.True);
		Assert.That(
			parameters.CalculateCoefficients(48000),
			Is.EqualTo(ResonantFilterCoefficients.Bypass));
	}

	[Test]
	public void FullNormalizedCutoffUsesCompleteClassicItRange()
	{
		ResonantFilterParameters parameters =
			new(1.0, 0.5);

		Assert.That(
			parameters.GetCutoffFrequency(48000),
			Is.EqualTo(5123.899203604501).Within(1e-9));
	}

	[Test]
	public void CoefficientsMatchClassicItFormula()
	{
		ResonantFilterParameters parameters =
			new(0.5, 0.25);

		ResonantFilterCoefficients coefficients =
			parameters.CalculateCoefficients(48000);

		Assert.That(coefficients.A, Is.EqualTo(0.010258259468866299).Within(1e-15));
		Assert.That(coefficients.B, Is.EqualTo(1.8829354502892335).Within(1e-15));
		Assert.That(coefficients.C, Is.EqualTo(-0.8931937097580998).Within(1e-15));
	}
}

using System;

using Heresy.Core.FmSynthesis;
using Heresy.Render.FmSynthesis;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmOscillatorCurvatureShaperTests
{
	[TestCase(-32.0)]
	[TestCase(-8.0)]
	[TestCase(-2.0)]
	[TestCase(-0.01)]
	[TestCase(0.0)]
	[TestCase(0.01)]
	[TestCase(2.0)]
	[TestCase(8.0)]
	[TestCase(32.0)]
	public void ApproximationMatchesExactExponentialAndPreservesBounds(double curvature)
	{
		FmOscillatorCurvatureShaper shaper = new(curvature);
		double previous = -1.0;
		for (int i = 0; i <= 10000; i++)
		{
			double input = (2.0 * i / 10000.0) - 1.0;
			double actual = shaper.Transform(input);
			double expected = curvature == 0.0 ? input
				: (2.0 * (Math.Exp(curvature * (input + 1.0)) - 1.0)
					/ (Math.Exp(2.0 * curvature) - 1.0)) - 1.0;

			Assert.That(actual, Is.InRange(-1.0, 1.0),
				$"k={curvature}, input={input}");
			Assert.That(actual, Is.GreaterThanOrEqualTo(previous),
				$"Nonmonotonic at k={curvature}, input={input}");
			Assert.That(Math.Abs(actual - expected), Is.LessThan(0.00007),
				$"k={curvature}, input={input}");
			previous = actual;
		}
		Assert.That(shaper.Transform(-1.0), Is.EqualTo(-1.0));
		Assert.That(shaper.Transform(1.0), Is.EqualTo(1.0));
	}

	[TestCase(-1e-9)]
	[TestCase(-1e-12)]
	[TestCase(1e-12)]
	[TestCase(1e-9)]
	public void VerySmallCurvatureRemainsStableAndApproachesIdentity(double curvature)
	{
		FmOscillatorCurvatureShaper shaper = new(curvature);
		Assert.That(shaper.Transform(-1.0), Is.EqualTo(-1.0));
		Assert.That(shaper.Transform(1.0), Is.EqualTo(1.0));
		// First-order expansion at the midpoint: f_k(0) = -k/2 + O(k^3).
		Assert.That(shaper.Transform(0.0),
			Is.EqualTo(-curvature / 2.0).Within(1e-12));
	}

	[Test]
	public void IdentityCurvatureIsExactWithoutClampingOrTableApproximation()
	{
		FmOscillatorCurvatureShaper shaper = new(0.0);
		foreach (double value in new[] { -1.0, -0.75, -0.1, 0.0,
			0.001, 0.25, 0.5, 1.0 })
			Assert.That(shaper.Transform(value), Is.EqualTo(value));
	}

	[TestCase(double.NaN)]
	[TestCase(double.PositiveInfinity)]
	[TestCase(double.NegativeInfinity)]
	[TestCase(32.001)]
	[TestCase(-32.001)]
	public void RejectsCurvatureOutsideCoreContract(double curvature)
	{
		Assert.That(() => new FmOscillatorCurvatureShaper(curvature),
			Throws.TypeOf<ArgumentOutOfRangeException>());
	}

	[Test]
	public void PerSampleLookupDoesNotAllocateProportionallyToSampleCount()
	{
		FmOscillatorCurvatureShaper shaper = new(2.0);
		double total = 0.0;
		for (int i = 0; i < 4096; i++)
			total += shaper.Transform((2.0 * i / 4096.0) - 1.0);

		// A tiny fixed JIT/runtime bookkeeping allocation is acceptable;
		// an allocation per sample would grow with this much larger count.
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 65536; i++)
			total += shaper.Transform((2.0 * (i % 4096) / 4096.0) - 1.0);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		GC.KeepAlive(total);
		Assert.That(allocated, Is.LessThan(256L),
			"Lookup cost must not allocate memory in proportion to PCM frames.");
	}
}

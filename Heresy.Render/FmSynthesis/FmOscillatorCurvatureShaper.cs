using System;

using Heresy.Core.FmSynthesis;

namespace Heresy.Render.FmSynthesis;

/// <summary>
/// Immutable, preparation-time exponential transfer function for normalized
/// oscillator samples. The audio thread performs only table interpolation,
/// never exponentiation or allocation. Values are clamped to [-1, 1] and
/// both endpoints are exact.
/// </summary>
public sealed class FmOscillatorCurvatureShaper
{
	private const int Segments = 4096;
	private readonly double[]? _values;

	public FmOscillatorCurvatureShaper(double curvature)
	{
		if (!double.IsFinite(curvature)
			|| Math.Abs(curvature) > FmOscillatorNode.MaximumCurvatureMagnitude)
		{
			throw new ArgumentOutOfRangeException(nameof(curvature));
		}

		// The renderer does not construct this object at all for k == 0.
		// The identity case is still supported for editor preview consumers.
		if (curvature == 0.0)
			return;

		_values = new double[Segments + 1];
		double denominator = ExpMinusOne(2.0 * curvature);
		for (int i = 1; i < Segments; i++)
		{
			double normalized = ((2.0 * i) / Segments) - 1.0;
			_values[i] = (2.0
				* ExpMinusOne(curvature * (normalized + 1.0))
				/ denominator) - 1.0;
		}
		_values[0] = -1.0;
		_values[Segments] = 1.0;
	}

	/// <summary>Transform a normalized oscillator sample, retaining exact
	/// extrema. For nonzero k this is an allocation-free table lookup.</summary>
	public double Transform(double normalized)
	{
		if (_values is null)
			return normalized;
		if (normalized <= -1.0)
			return -1.0;
		if (normalized >= 1.0)
			return 1.0;

		double position = (normalized + 1.0) * (Segments / 2.0);
		int index = (int)position;
		double fraction = position - index;
		return _values[index]
			+ ((_values[index + 1] - _values[index]) * fraction);
	}

	// exp(x)-1 loses significant digits for very small |x|. A short
	// Taylor polynomial is enough there; all calls are preparation-only.
	private static double ExpMinusOne(double x)
	{
		if (Math.Abs(x) < 0.0001)
		{
			return x * (1.0 + x * (0.5 + x *
				(1.0 / 6.0 + x * (1.0 / 24.0 + x *
				(1.0 / 120.0 + x / 720.0)))));
		}
		return Math.Exp(x) - 1.0;
	}
}

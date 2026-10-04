namespace Heresy.Render.Filters;

/// <summary>
/// Coefficients for y[n] = A*x[n] + B*y[n-1] + C*y[n-2].
/// </summary>
public readonly record struct ResonantFilterCoefficients(
	double A,
	double B,
	double C)
{
	public static ResonantFilterCoefficients Bypass { get; } =
		new(1.0, 0.0, 0.0);
}

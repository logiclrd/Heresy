using System;

namespace Heresy.Render.Filters;

/// <summary>
/// Normalized Impulse-Tracker-style resonant low-pass filter parameters.
/// Cutoff and Resonance are both in the range [0, 1].
/// </summary>
public readonly record struct ResonantFilterParameters
{
	public ResonantFilterParameters(double cutoff, double resonance)
	{
		if (double.IsNaN(cutoff)
			|| double.IsInfinity(cutoff)
			|| cutoff < 0.0
			|| cutoff > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(cutoff));
		}

		if (double.IsNaN(resonance)
			|| double.IsInfinity(resonance)
			|| resonance < 0.0
			|| resonance > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(resonance));
		}

		Cutoff = cutoff;
		Resonance = resonance;
	}

	public double Cutoff { get; }

	public double Resonance { get; }

	/// <summary>
	/// IT treats maximum cutoff with zero resonance as filter bypass.
	/// The 0.996 threshold preserves the traditional near-maximum tolerance.
	/// </summary>
	public bool IsDisabled =>
		Resonance == 0.0
		&& Cutoff >= 0.996;

	public static ResonantFilterParameters Disabled { get; } =
		new(1.0, 0.0);

	public ResonantFilterCoefficients CalculateCoefficients(int sampleRate)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		if (IsDisabled)
			return ResonantFilterCoefficients.Bypass;

		// Classic IT domain: cutoff/resonance are 7-bit quantities. The
		// normalized public representation maps the complete 0..127 range.
		double cutoffValue = Cutoff * 127.0;
		double resonanceValue = Resonance * 127.0;

		double frequency =
			110.0
			* Math.Pow(2.0, 0.25 + cutoffValue / 24.0);
		frequency = Math.Min(frequency, sampleRate * 0.5);

		double r =
			sampleRate
			/ (frequency * 2.0 * Math.PI);

		double p = Math.Pow(
			10.0,
			(-resonanceValue * 24.0)
				/ (128.0 * 20.0));

		double d = 2.0 * p * (r + 1.0) - 1.0;
		double e = r * r;
		double denominator = 1.0 + d + e;

		return new ResonantFilterCoefficients(
			1.0 / denominator,
			(d + 2.0 * e) / denominator,
			-e / denominator);
	}

	public double GetCutoffFrequency(int sampleRate)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		if (IsDisabled)
			return sampleRate * 0.5;

		double cutoffValue = Cutoff * 127.0;
		return Math.Min(
			110.0 * Math.Pow(2.0, 0.25 + cutoffValue / 24.0),
			sampleRate * 0.5);
	}
}

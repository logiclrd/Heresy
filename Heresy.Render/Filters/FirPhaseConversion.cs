using System;
using System.Numerics;

namespace Heresy.Render.Filters;

/// <summary>
/// Bounded, configuration-time real-cepstral spectral factorization.
/// Approximate the magnitude of a symmetric FIR by a causal minimum-phase
/// sequence of the same authored length. Maximum phase is its time reversal.
/// Neither the FFT nor coefficient conversion runs inside PCM processing.
/// </summary>
internal static class FirPhaseConversion
{
	// Zero padding reduces periodic-cepstrum wraparound, particularly for
	// high-order narrow-transition designs. At the maximum 2047 tap limit
	// the transform is exactly 65536 complex samples, no unbounded FFT.
	private const int MinimumTransformLength = 4096;
	private const int OversamplingFactor = 32;
	private const double MagnitudeFloor = 1e-12;

	public static double[] Convert(
		double[] linearCoefficients, string phaseResponse,
		double referenceFrequency, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(linearCoefficients);
		if (phaseResponse is not ("Minimum" or "Maximum"))
			throw new ArgumentException(
				"Phase conversion requires Minimum or Maximum.",
				nameof(phaseResponse));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		int count = linearCoefficients.Length;
		int length = MinimumTransformLength;
		int targetLength = checked(count * OversamplingFactor);
		while (length < targetLength)
			length *= 2;
		Complex[] spectrum = new Complex[length];
		for (int i = 0; i < count; i++)
			spectrum[i] = new Complex(linearCoefficients[i], 0);

		Transform(spectrum, inverse: false);

		// The logarithm of magnitude is real and even. Strictly positive
		// flooring prevents log(0) at exact spectral notches, and converts
		// unit-circle zeros into a deterministic nearby minimum-phase
		// solution rather than nonfinite roots.
		double greatestMagnitude = 0;
		for (int i = 0; i < length; i++)
			greatestMagnitude = Math.Max(greatestMagnitude,
				spectrum[i].Magnitude);
		if (!double.IsFinite(greatestMagnitude) || greatestMagnitude <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(linearCoefficients), "FIR spectrum is not finite.");

		double floor = Math.Max(MagnitudeFloor,
			greatestMagnitude * MagnitudeFloor);
		for (int i = 0; i < length; i++)
			spectrum[i] = new Complex(
				Math.Log(Math.Max(spectrum[i].Magnitude, floor)), 0);
		Transform(spectrum, inverse: true);

		// Real cepstrum -> causal complex cepstrum: keep DC and Nyquist,
		// double positive-time terms, zero negative-time terms.
		for (int i = 1; i < length / 2; i++)
			spectrum[i] *= 2;
		for (int i = length / 2 + 1; i < length; i++)
			spectrum[i] = Complex.Zero;

		Transform(spectrum, inverse: false);
		for (int i = 0; i < length; i++)
		{
			spectrum[i] = Complex.Exp(spectrum[i]);
			if (!double.IsFinite(spectrum[i].Real)
				|| !double.IsFinite(spectrum[i].Imaginary))
				throw new ArgumentOutOfRangeException(
					nameof(linearCoefficients),
					"FIR spectral factorization produced nonfinite coefficients.");
		}
		Transform(spectrum, inverse: true);

		double[] converted = new double[count];
		for (int i = 0; i < count; i++)
		{
			double value = spectrum[i].Real;
			if (!double.IsFinite(value))
				throw new ArgumentOutOfRangeException(
					nameof(linearCoefficients),
					"FIR phase conversion produced nonfinite coefficients.");
			converted[i] = value;
		}

		// The finite truncation of the reconstructed causal sequence can
		// slightly alter magnitude. Normalize *at the same authored
		// passband reference frequency* as the linear-phase design.
		// Never silently amplify an unresolved/near-zero response.
		double originalGain = FrequencyMagnitude(
			linearCoefficients, referenceFrequency, sampleRate);
		double convertedGain = FrequencyMagnitude(
			converted, referenceFrequency, sampleRate);
		if (!double.IsFinite(convertedGain) || convertedGain <= 1e-12)
			throw new ArgumentOutOfRangeException(
				nameof(linearCoefficients),
				"FIR minimum-phase passband is unresolved.");
		double scale = originalGain / convertedGain;
		for (int i = 0; i < count; i++)
			converted[i] *= scale;

		// Time reversal of the causal minimum-phase FIR is maximum-phase,
		// has exactly the same finite-length magnitude, and moves its
		// energy toward the end of the authored convolution window.
		if (phaseResponse == "Maximum")
			Array.Reverse(converted);
		return converted;
	}

	private static double FrequencyMagnitude(
		double[] coefficients, double frequency, int sampleRate)
	{
		double real = 0;
		double imaginary = 0;
		for (int i = 0; i < coefficients.Length; i++)
		{
			double angle = 2 * Math.PI * frequency * i / sampleRate;
			real += coefficients[i] * Math.Cos(angle);
			imaginary -= coefficients[i] * Math.Sin(angle);
		}
		return Math.Sqrt(real * real + imaginary * imaginary);
	}

	/// <summary>In-place radix-2 FFT, with conventional 1/N inverse scale.
	/// Prepared lengths are powers of two, between 4096 and 65536.</summary>
	private static void Transform(Complex[] values, bool inverse)
	{
		int length = values.Length;
		for (int i = 1, j = 0; i < length; i++)
		{
			int bit = length >> 1;
			for (; (j & bit) != 0; bit >>= 1)
				j ^= bit;
			j ^= bit;
			// Each bit-reversed pair must be swapped only once.
			// Using i != j would undo the swap when the index is
			// visited again in the reverse direction.
			if (i < j)
				(values[i], values[j]) = (values[j], values[i]);
		}

		for (int step = 2; step <= length; step <<= 1)
		{
			int half = step >> 1;
			double angle = (inverse ? 2 : -2) * Math.PI / step;
			Complex stride = new(Math.Cos(angle), Math.Sin(angle));
			for (int offset = 0; offset < length; offset += step)
			{
				Complex multiplier = Complex.One;
				for (int k = 0; k < half; k++)
				{
					Complex even = values[offset + k];
					Complex odd = values[offset + half + k] * multiplier;
					values[offset + k] = even + odd;
					values[offset + half + k] = even - odd;
					multiplier *= stride;
				}
			}
		}
		if (inverse)
			for (int i = 0; i < length; i++)
				values[i] /= length;
	}
}

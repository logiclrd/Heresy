using System;

namespace Heresy.Render.Filters;

/// <summary>
/// Stateful two-pole resonant low-pass filter. Histories are independent for
/// each output stream and are deliberately preserved when parameters change.
/// </summary>
public sealed class ResonantFilterState
{
	private readonly double[] _previous1;
	private readonly double[] _previous2;
	private ResonantFilterParameters _parameters;
	private ResonantFilterCoefficients _coefficients;

	public ResonantFilterState(
		int outputChannelCount,
		int sampleRate,
		ResonantFilterParameters? parameters = null)
	{
		if (outputChannelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(outputChannelCount));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		SampleRate = sampleRate;
		_previous1 = new double[outputChannelCount];
		_previous2 = new double[outputChannelCount];

		SetParameters(parameters ?? ResonantFilterParameters.Disabled);
	}

	public int OutputChannelCount => _previous1.Length;

	public int SampleRate { get; }

	public ResonantFilterParameters Parameters => _parameters;

	public ResonantFilterCoefficients Coefficients => _coefficients;

	public bool IsEnabled => !_parameters.IsDisabled;

	public void SetParameters(ResonantFilterParameters parameters)
	{
		_parameters = parameters;
		_coefficients = parameters.CalculateCoefficients(SampleRate);
	}

	/// <summary>
	/// Applies the filter in place to one already-spatialized output frame.
	/// Bypass leaves both samples and stored histories untouched.
	/// </summary>
	public void ProcessFrame(Span<float> frame)
	{
		if (frame.Length != OutputChannelCount)
		{
			throw new ArgumentException(
				"Frame width does not match filter output-channel count.",
				nameof(frame));
		}

		if (!IsEnabled)
			return;

		for (int channel = 0; channel < frame.Length; channel++)
		{
			double output =
				_coefficients.A * frame[channel]
				+ _coefficients.B * _previous1[channel]
				+ _coefficients.C * _previous2[channel];

			_previous2[channel] = _previous1[channel];
			_previous1[channel] = output;
			frame[channel] = (float)output;
		}
	}

	public void ClearHistory()
	{
		Array.Clear(_previous1);
		Array.Clear(_previous2);
	}
}

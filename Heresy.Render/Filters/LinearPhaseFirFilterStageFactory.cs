using System;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Windowed-sinc finite impulse response designs for low-pass, high-pass,
/// band-pass, and band-stop (notch). Odd tap counts yield Type-I symmetry
/// and known group delay in Linear mode. Minimum/Maximum mode applies
/// bounded offline spectral factorization or its time reversal.
/// </summary>
public sealed class LinearPhaseFirFilterStageFactory : IStreamingFilterStageFactory
{
	public static LinearPhaseFirFilterStageFactory Instance { get; } = new();

	public const int MaximumTaps = 2047;
	public const int CoefficientTransitionFrames = 32;

	private LinearPhaseFirFilterStageFactory() { }

	public static bool IsStandardFir(FilterKind kind)
		=> kind is FilterKind.FirLowPass or FilterKind.FirHighPass
			or FilterKind.FirBandPass or FilterKind.FirNotch;

	/// <summary>The latency of an odd-length symmetric FIR is (N-1)/2,
	/// independently of its window and response. The current streaming
	/// engine reports this delay; future song hosts must account for it
	/// when aligning parallel dry and filtered signals.</summary>
	public static int GroupDelayFrames(int tapCount)
	{
		if (tapCount < 3 || tapCount > MaximumTaps || (tapCount & 1) == 0)
			throw new ArgumentOutOfRangeException(nameof(tapCount));
		return (tapCount - 1) / 2;
	}

	public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (!IsStandardFir(stage.Kind))
			throw new ArgumentException(
				"A standard FIR factory requires one of the four FIR kinds.",
				nameof(stage));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		string phaseResponse = stage.Parameters["PhaseResponse"].ChoiceValue
			?? throw new ArgumentException("FIR PhaseResponse is missing.", nameof(stage));
		if (phaseResponse is not ("Linear" or "Minimum" or "Maximum"))
			throw new ArgumentException(
				"FIR PhaseResponse must be Linear, Minimum or Maximum.",
				nameof(stage));

		double tapsValue = stage.Parameters["TapCount"].NumericValue;
		if (!double.IsFinite(tapsValue) || tapsValue < 3
			|| tapsValue > MaximumTaps || tapsValue != Math.Truncate(tapsValue)
			|| ((int)tapsValue & 1) == 0)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"FIR TapCount must be an odd integer in [3, 2047].");
		int taps = (int)tapsValue;
		double width = stage.Parameters["TransitionWidthHz"].NumericValue;
		if (!double.IsFinite(width) || width < 1 || width > 5000)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"FIR transition width must be between 1 and 5000 Hz.");

		string window = stage.Parameters["Window"].ChoiceValue
			?? throw new ArgumentException("FIR window is missing.", nameof(stage));
		if (window is not ("Hann" or "Hamming" or "Blackman"))
			throw new ArgumentException(
				"FIR window must be Hann, Hamming or Blackman.", nameof(stage));

		double nyquist = sampleRate * 0.5;
		// Cutoffs are boundaries of the nominal pass/reject band.
		// The transition midpoint is displaced by width/2 *away* from
		// that band. This gives TransitionWidthHz an audible, specified
		// design role without pretending 3 taps can realize a 1 Hz
		// transition. Window/tap count control actual attainable width.
		double halfWidth = width * 0.5;
		double low = 0;
		double high = 0;
		if (stage.Kind is FilterKind.FirLowPass or FilterKind.FirHighPass)
		{
			double cutoff = stage.Parameters["CutoffHz"].NumericValue;
			if (!double.IsFinite(cutoff) || cutoff <= 0 || cutoff >= nyquist)
				throw new ArgumentOutOfRangeException(nameof(stage),
					"FIR cutoff must lie strictly below Nyquist.");
			low = stage.Kind == FilterKind.FirLowPass
				? cutoff + halfWidth : cutoff - halfWidth;
			if (low <= 0 || low >= nyquist)
				throw new ArgumentOutOfRangeException(nameof(stage),
					"FIR transition extends outside (0, Nyquist).");
		}
		else
		{
			double lowerEdge = stage.Parameters["LowCutoffHz"].NumericValue;
			double upperEdge = stage.Parameters["HighCutoffHz"].NumericValue;
			if (!double.IsFinite(lowerEdge) || !double.IsFinite(upperEdge)
				|| lowerEdge <= 0 || lowerEdge >= upperEdge
				|| upperEdge >= nyquist)
				throw new ArgumentOutOfRangeException(nameof(stage),
					"FIR band edges must be strictly ordered within Nyquist.");
			if (stage.Kind == FilterKind.FirBandPass)
			{
				low = lowerEdge - halfWidth;
				high = upperEdge + halfWidth;
			}
			else
			{
				low = lowerEdge + halfWidth;
				high = upperEdge - halfWidth;
			}
			if (low <= 0 || high >= nyquist || low >= high)
				throw new ArgumentOutOfRangeException(nameof(stage),
					"FIR band transitions overlap or extend outside Nyquist.");
		}

		int center = GroupDelayFrames(taps);
		double[] coefficients = new double[taps];
		for (int i = 0; i < taps; i++)
		{
			int offset = i - center;
			double value = stage.Kind switch
			{
				FilterKind.FirLowPass => SincLowPass(low, sampleRate, offset),
				FilterKind.FirHighPass => (offset == 0 ? 1 : 0)
					- SincLowPass(low, sampleRate, offset),
				FilterKind.FirBandPass => SincLowPass(high, sampleRate, offset)
					- SincLowPass(low, sampleRate, offset),
				FilterKind.FirNotch => (offset == 0 ? 1 : 0)
					- SincLowPass(high, sampleRate, offset)
					+ SincLowPass(low, sampleRate, offset),
				_ => throw new ArgumentOutOfRangeException(nameof(stage)),
			};
			double phase = 2 * Math.PI * i / (taps - 1);
			double taper = window switch
			{
				"Hann" => 0.5 - 0.5 * Math.Cos(phase),
				"Hamming" => 0.54 - 0.46 * Math.Cos(phase),
				_ => 0.42 - 0.5 * Math.Cos(phase)
					+ 0.08 * Math.Cos(2 * phase),
			};
			coefficients[i] = value * taper;
		}

		// Normalize a representative passband frequency. Center-relative
		// cosine projection removes the known linear group-delay phase.
		double referenceFrequency = stage.Kind switch
		{
			FilterKind.FirLowPass or FilterKind.FirNotch => 0,
			FilterKind.FirHighPass => nyquist,
			_ => (low + high) * 0.5,
		};
		double response = 0;
		for (int i = 0; i < taps; i++)
			response += coefficients[i] * Math.Cos(
				2 * Math.PI * referenceFrequency * (i - center) / sampleRate);
		// A very narrow unresolved band can have almost no usable
		// passband amplitude. Refuse to amplify near-zero coefficients.
		if (!double.IsFinite(response) || response <= 0.01)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"FIR tap/window/transition choice cannot resolve a usable "
				+ "passband; increase TapCount or widen the band.");
		for (int i = 0; i < taps; i++)
		{
			coefficients[i] /= response;
			if (!double.IsFinite(coefficients[i]))
				throw new ArgumentOutOfRangeException(nameof(stage),
					"FIR design produced nonfinite coefficients.");
		}
		// The symmetric windowed-sinc magnitude design is the common
		// reference for all phase choices. Minimum/Maximum conversion is a
		// bounded FFT-based preparation step with no PCM-time allocations.
		// Only Linear has an exact constant group delay; the other modes
		// have frequency-dependent group delay and report no fixed latency.
		if (phaseResponse != "Linear")
			coefficients = FirPhaseConversion.Convert(
				coefficients, phaseResponse, referenceFrequency, sampleRate);
		return new FirPlan(coefficients,
			phaseResponse == "Linear" ? center : 0);
	}

	private static double SincLowPass(double cutoff, int sampleRate, int offset)
		=> offset == 0
			? 2 * cutoff / sampleRate
			: Math.Sin(2 * Math.PI * cutoff * offset / sampleRate)
				/ (Math.PI * offset);

	private sealed class FirPlan(double[] coefficients, int groupDelayFrames)
		: IStreamingFilterStagePlan, ILatencyAwareFilterStagePlan
	{
		// Coefficients are privately allocated and never published mutably.
		// Plans can safely be shared by all output channels.
		public double[] Coefficients { get; } = coefficients;
		public int LatencyFrames { get; } = groupDelayFrames;
		public int TailFrames => Coefficients.Length - 1;
		public IStreamingFilterStageState CreateState() => new FirState(this);
	}

	private sealed class FirState : IStreamingFilterStageState
	{
		private FirPlan _plan;
		private readonly double[] _history;
		private double[]? _rampFrom;
		private int _position;
		private int _remaining;

		public FirState(FirPlan plan)
		{
			_plan = plan;
			_history = new double[plan.Coefficients.Length];
		}

		private FirState(FirState original)
		{
			_plan = original._plan;
			_history = (double[])original._history.Clone();
			_rampFrom = original._rampFrom is null
				? null : (double[])original._rampFrom.Clone();
			_position = original._position;
			_remaining = original._remaining;
		}

		public float Process(float sample)
		{
			double excitation = float.IsFinite(sample) ? sample : 0;
			_history[_position] = Math.Abs(excitation) < 1e-20
				? 0 : excitation;

			int length = _history.Length;
			int location = _position;
			double[] target = _plan.Coefficients;
			double output = 0;
			if (_remaining > 0)
			{
				double progress = (double)(
					CoefficientTransitionFrames - _remaining + 1)
					/ CoefficientTransitionFrames;
				double[] prior = _rampFrom!;
				for (int tap = 0; tap < length; tap++)
				{
					double coefficient = prior[tap]
						+ (target[tap] - prior[tap]) * progress;
					output += coefficient * _history[location];
					if (--location < 0)
						location = length - 1;
				}
				if (--_remaining == 0)
					_rampFrom = null;
			}
			else
			{
				for (int tap = 0; tap < length; tap++)
				{
					output += target[tap] * _history[location];
					if (--location < 0)
						location = length - 1;
				}
			}
			if (++_position == length)
				_position = 0;
			if (!double.IsFinite(output) || Math.Abs(output) < 1e-20)
				return 0;
			return (float)Math.Clamp(output, -float.MaxValue, float.MaxValue);
		}

		public IStreamingFilterStageState Clone() => new FirState(this);

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not FirPlan next
				|| next.Coefficients.Length != _history.Length)
				throw new ArgumentException(
					"Compatible FIR state requires the same tap count.",
					nameof(plan));
			// Materialize the coefficient vector at the *current*
			// interpolation position, outside the real-time PCM loop.
			// This retains both historical PCM and uninterrupted ramps.
			double[] previous = _plan.Coefficients;
			if (_remaining > 0)
			{
				double progress = (double)(
					CoefficientTransitionFrames - _remaining)
					/ CoefficientTransitionFrames;
				double[] starting = _rampFrom!;
				previous = new double[starting.Length];
				for (int i = 0; i < previous.Length; i++)
					previous[i] = starting[i]
						+ (_plan.Coefficients[i] - starting[i]) * progress;
			}
			_rampFrom = previous;
			_plan = next;
			_remaining = CoefficientTransitionFrames;
		}
	}
}

using System;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Shared topology-preserving state-variable filter (TPT SVF) for four
/// selectable responses. A pair of integrators simultaneously computes
/// low, high and band outputs; selection never changes the DSP topology.
/// </summary>
public sealed class SvfFilterStageFactory : IStreamingFilterStageFactory
{
	public static SvfFilterStageFactory Instance { get; } = new();

	/// <summary>
	/// Rapid cutoff/Q updates interpolate the strictly positive TPT g and k
	/// variables for a bounded number of samples. The integrator coefficients
	/// are rebuilt only during those transitions, not during steady-state PCM.
	/// </summary>
	public const int ParameterTransitionFrames = 32;

	private SvfFilterStageFactory() { }

	public static bool IsSvf(FilterKind kind)
		=> kind is FilterKind.SvfLowPass or FilterKind.SvfHighPass
			or FilterKind.SvfBandPass or FilterKind.SvfNotch;

	public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (!IsSvf(stage.Kind))
			throw new ArgumentException("An SVF factory requires an SVF stage.",
				nameof(stage));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		double cutoff = stage.Parameters["CutoffHz"].NumericValue;
		if (!double.IsFinite(cutoff) || cutoff <= 0 || cutoff >= sampleRate * 0.5)
			throw new ArgumentOutOfRangeException(nameof(stage),
				$"SVF cutoff {cutoff} Hz must be strictly below Nyquist "
				+ $"({sampleRate * 0.5} Hz).");
		double q = stage.Parameters["Q"].NumericValue;
		if (!double.IsFinite(q) || q <= 0)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"SVF Q must be positive and finite.");

		double g = Math.Tan(Math.PI * cutoff / sampleRate);
		double k = 1.0 / q;
		if (!double.IsFinite(g) || g <= 0 || !double.IsFinite(k) || k <= 0)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"SVF preparation produced invalid integrator coefficients.");

		// Bilinear transform of s^2 + k * omega * s + omega^2.
		// Both roots lie strictly within the unit circle for g,k > 0.
		double denominator = 1 + g * (g + k);
		double a1 = 2 * (g * g - 1) / denominator;
		double a2 = (1 + g * (g - k)) / denominator;
		double discriminant = a1 * a1 - 4 * a2;
		double poleRadius = discriminant < 0
			? Math.Sqrt(a2)
			: Math.Max(Math.Abs((-a1 + Math.Sqrt(discriminant)) / 2),
				Math.Abs((-a1 - Math.Sqrt(discriminant)) / 2));
		if (!double.IsFinite(poleRadius) || poleRadius >= 1)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"SVF preparation produced unstable poles.");

		// This is a conservative finite work budget, not a mathematical
		// endpoint for a second-order IIR. The invocation has an additional
		// configurable global cap; unit-input decay target is -400 dB.
		double frames = poleRadius == 0
			? 2
			: Math.Ceiling(Math.Log(1e-20) / Math.Log(poleRadius)) + 2;
		int tail = !double.IsFinite(frames) || frames >= int.MaxValue
			? int.MaxValue
			: Math.Max(2, (int)frames);

		return new SvfPlan(stage.Kind, g, k, tail);
	}

	private readonly record struct IntegratorCoefficients(
		double A1, double A2, double A3)
	{
		public static IntegratorCoefficients From(double g, double k)
		{
			double a1 = 1.0 / (1.0 + g * (g + k));
			double a2 = g * a1;
			return new(a1, a2, g * a2);
		}
	}

	private sealed class SvfPlan(FilterKind response, double g, double k,
		int tailFrames) : IStreamingFilterStagePlan
	{
		public FilterKind Response { get; } = response;
		public double G { get; } = g;
		public double K { get; } = k;
		public IntegratorCoefficients Coefficients { get; }
			= IntegratorCoefficients.From(g, k);
		public int TailFrames { get; } = tailFrames;
		public IStreamingFilterStageState CreateState() => new SvfState(this);
	}

	private sealed class SvfState : IStreamingFilterStageState, IRealtimeTptFilterState
	{
		private SvfPlan _plan;
		private IntegratorCoefficients _coefficients;
		private double _currentG;
		private double _currentK;
		private double _targetG;
		private double _targetK;
		private double _startG;
		private double _startK;
		private int _remaining;
		private double _ic1;
		private double _ic2;

		public SvfState(SvfPlan plan)
		{
			_plan = plan;
			_currentG = plan.G;
			_currentK = plan.K;
			_targetG = plan.G;
			_targetK = plan.K;
			_startG = _currentG;
			_startK = _currentK;
			_coefficients = plan.Coefficients;
		}

		private SvfState(SvfState source)
		{
			_plan = source._plan;
			_coefficients = source._coefficients;
			_currentG = source._currentG;
			_currentK = source._currentK;
			_targetG = source._targetG;
			_targetK = source._targetK;
			_startG = source._startG;
			_startK = source._startK;
			_remaining = source._remaining;
			_ic1 = source._ic1;
			_ic2 = source._ic2;
		}

		public float Process(float sample)
		{
			if (_remaining != 0)
			{
				double t = (double)(ParameterTransitionFrames - _remaining + 1)
					/ ParameterTransitionFrames;
				_currentG = _startG + (_targetG - _startG) * t;
				_currentK = _startK + (_targetK - _startK) * t;
				_coefficients = IntegratorCoefficients.From(_currentG, _currentK);
				_remaining--;
			}

			double input = float.IsFinite(sample) ? sample : 0;
			double v3 = input - _ic2;
			double bandState = _coefficients.A1 * _ic1 + _coefficients.A2 * v3;
			double low = _ic2 + _coefficients.A2 * _ic1
				+ _coefficients.A3 * v3;
			double high = input - _currentK * bandState - low;
			// The band-pass is normalized to unit gain at its centre (Q
			// controls bandwidth), and notch = low + high = input - band.
			double band = _currentK * bandState;
			double output = _plan.Response switch
			{
				FilterKind.SvfLowPass => low,
				FilterKind.SvfHighPass => high,
				FilterKind.SvfBandPass => band,
				FilterKind.SvfNotch => input - band,
				_ => 0,
			};

			double next1 = 2 * bandState - _ic1;
			double next2 = 2 * low - _ic2;
			// Flush state before float denormals and defend against
			// poisoned or excessively large source frames.
			_ic1 = double.IsFinite(next1) && Math.Abs(next1) >= 1e-20
				? next1 : 0;
			_ic2 = double.IsFinite(next2) && Math.Abs(next2) >= 1e-20
				? next2 : 0;
			if (!double.IsFinite(output) || Math.Abs(output) < 1e-20)
				return 0;
			return (float)Math.Clamp(output, -float.MaxValue, float.MaxValue);
		}

		public IStreamingFilterStageState Clone() => new SvfState(this);

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not SvfPlan next || next.Response != _plan.Response)
				throw new ArgumentException(
					"SVF state requires a plan with the same response kind.",
					nameof(plan));
			if (_currentG == next.G && _currentK == next.K)
			{
				_plan = next;
				_targetG = next.G;
				_targetK = next.K;
				_coefficients = next.Coefficients;
				_startG = next.G;
				_startK = next.K;
				_remaining = 0;
				return;
			}
			// No reset: the old integrator histories are intentionally
			// retained, even if another change interrupts interpolation.
			_startG = _currentG;
			_startK = _currentK;
			_plan = next;
			_targetG = next.G;
			_targetK = next.K;
			_remaining = ParameterTransitionFrames;
		}

		public bool TryPrepareRealtime(
			double cutoffHz, double q, int sampleRate,
			out RealtimeTptCoefficients coefficients)
		{
			coefficients = default;
			if (!double.IsFinite(cutoffHz)
				|| cutoffHz < 1 || cutoffHz > 20000
				|| cutoffHz >= sampleRate * 0.5
				|| !double.IsFinite(q) || q < 0.1 || q > 20)
				return false;
			double g = Math.Tan(Math.PI * cutoffHz / sampleRate);
			double k = 1.0 / q;
			if (!double.IsFinite(g) || g <= 0 || !double.IsFinite(k) || k <= 0)
				return false;
			double denominator = 1 + g * (g + k);
			double a1 = 2 * (g * g - 1) / denominator;
			double a2 = (1 + g * (g - k)) / denominator;
			double discriminant = a1 * a1 - 4 * a2;
			double radius = discriminant < 0
				? Math.Sqrt(a2)
				: Math.Max(Math.Abs((-a1 + Math.Sqrt(discriminant)) / 2),
					Math.Abs((-a1 - Math.Sqrt(discriminant)) / 2));
			if (!double.IsFinite(radius) || radius >= 1)
				return false;
			coefficients = new(g, k);
			return true;
		}

		public void CommitRealtime(RealtimeTptCoefficients coefficients)
		{
			if (_currentG == coefficients.G && _currentK == coefficients.K)
			{
				_targetG = coefficients.G;
				_targetK = coefficients.K;
				_startG = _currentG;
				_startK = _currentK;
				_coefficients = IntegratorCoefficients.From(_currentG, _currentK);
				_remaining = 0;
				return;
			}
			_startG = _currentG;
			_startK = _currentK;
			_targetG = coefficients.G;
			_targetK = coefficients.K;
			_remaining = ParameterTransitionFrames;
		}
	}
}

using System;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Linear, four-pole ladder-style low-pass with an analytically solved
/// zero-delay feedback path. Four cascaded TPT one-pole integrators each
/// have their own state; the resonance loop is never approximated with
/// a one-sample feedback delay. This is not the nonlinear ladder.
/// </summary>
public sealed class LinearLadderFilterStageFactory : IStreamingFilterStageFactory
{
	public static LinearLadderFilterStageFactory Instance { get; } = new();

	/// <summary>Compatible numeric edits crossfade the strictly positive
	/// TPT g and bounded resonance feedback over a fixed number of samples.
	/// Every intermediate configuration remains an actual stable ladder.</summary>
	public const int ParameterTransitionFrames = 32;

	/// <summary>Feedback 4 is the marginally stable analog four-pole
	/// boundary; map the authored maximum Resonance=1 to 3.8 instead.
	/// The linear processor must never self-oscillate indefinitely.</summary>
	public const double MaximumFeedback = 3.8;

	private LinearLadderFilterStageFactory() { }

	public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (stage.Kind != FilterKind.LadderLinear)
			throw new ArgumentException(
				"A linear ladder factory requires a linear ladder stage.",
				nameof(stage));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		double cutoff = stage.Parameters["CutoffHz"].NumericValue;
		double resonance = stage.Parameters["Resonance"].NumericValue;
		if (!TryPrepareCoefficients(cutoff, resonance, sampleRate,
			out RealtimeLinearLadderCoefficients coefficients,
			out double radius))
			throw new ArgumentOutOfRangeException(nameof(stage),
				"Linear ladder CutoffHz/Resonance must be finite, within "
				+ "descriptor bounds and stable below Nyquist.");
		double g = coefficients.G;
		double feedback = coefficients.Feedback;

		// Four repeated poles at zero resonance require more allowance
		// than a single exponential: their impulse includes polynomial
		// n^3 factors. This finite upper work budget has an extra margin
		// for the fourth-order response; the invocation-wide cap is final.
		double decay = radius == 0 ? 0 : -Math.Log(radius);
		double basicFrames = radius == 0 ? 2 : Math.Log(1e20) / decay;
		double margin = radius == 0 ? 0
			: 3 * Math.Log(1 + basicFrames) / decay;
		double budget = Math.Ceiling(basicFrames + margin) + 8;
		int tailFrames = !double.IsFinite(budget) || budget >= int.MaxValue
			? int.MaxValue
			: Math.Max(8, (int)budget);
		return new LadderPlan(g, feedback, tailFrames);
	}

	/// <summary>
	/// Bounded, allocation-free parameter preflight shared with full
	/// immutable preparation. Only the initial Prepare computes tail bounds;
	/// a compatible realtime change keeps its invocation's tail budget.
	/// </summary>
	private static bool TryPrepareCoefficients(double cutoff, double resonance,
		int sampleRate, out RealtimeLinearLadderCoefficients coefficients,
		out double radius)
	{
		coefficients = default;
		radius = double.NaN;
		if (sampleRate <= 0 || !double.IsFinite(cutoff)
			|| cutoff < 1 || cutoff > 20000 || cutoff >= sampleRate * 0.5
			|| !double.IsFinite(resonance) || resonance < 0 || resonance > 1)
			return false;
		double g = Math.Tan(Math.PI * cutoff / sampleRate);
		double feedback = MaximumFeedback * resonance;
		if (!double.IsFinite(g) || g <= 0 || !double.IsFinite(feedback)
			|| feedback < 0 || feedback >= 4)
			return false;

		// (1+s/w)^4+feedback has all poles in the analog left half-plane
		// for feedback < 4. Verify the bilinear-mapped radius, as Prepare
		// historically does, before publishing any target to the DSP.
		radius = MaximumDigitalPoleRadius(g, feedback);
		if (!double.IsFinite(radius) || !(radius >= 0 && radius < 1))
			return false;
		coefficients = new(g, feedback);
		return true;
	}

	private static double MaximumDigitalPoleRadius(double g, double feedback)
	{
		double rootRadius = Math.Sqrt(Math.Sqrt(feedback));
		double maximum = 0;
		for (int i = 0; i < 4; i++)
		{
			double angle = (Math.PI + 2 * Math.PI * i) / 4;
			double real = -1 + rootRadius * Math.Cos(angle);
			double imaginary = rootRadius * Math.Sin(angle);
			double gi = g * imaginary;
			double numerator = Math.Sqrt((1 + g * real) * (1 + g * real) + gi * gi);
			double denominator = Math.Sqrt((1 - g * real) * (1 - g * real) + gi * gi);
			double mapped = numerator / denominator;
			if (!double.IsFinite(mapped))
				return double.NaN;
			maximum = Math.Max(maximum, mapped);
		}
		return maximum;
	}

	private sealed class LadderPlan(double g, double feedback, int tailFrames)
		: IStreamingFilterStagePlan
	{
		public double G { get; } = g;
		public double Feedback { get; } = feedback;
		public int TailFrames { get; } = tailFrames;
		public IStreamingFilterStageState CreateState() => new LadderState(this);
	}

	private sealed class LadderState : IStreamingFilterStageState,
		IRealtimeLinearLadderFilterState
	{
		private readonly double[] _integrators = new double[4];
		private LadderPlan _plan;
		private double _g;
		private double _feedback;
		private double _startG;
		private double _startFeedback;
		private double _targetG;
		private double _targetFeedback;
		private double _gain;
		private double _denominatorInverse;
		private int _remaining;

		public LadderState(LadderPlan plan)
		{
			_plan = plan;
			_g = plan.G;
			_feedback = plan.Feedback;
			_startG = _g;
			_startFeedback = _feedback;
			_targetG = _g;
			_targetFeedback = _feedback;
			UpdateCoefficients();
		}

		private LadderState(LadderState previous)
		{
			Array.Copy(previous._integrators, _integrators, 4);
			_plan = previous._plan;
			_g = previous._g;
			_feedback = previous._feedback;
			_startG = previous._startG;
			_startFeedback = previous._startFeedback;
			_targetG = previous._targetG;
			_targetFeedback = previous._targetFeedback;
			_gain = previous._gain;
			_denominatorInverse = previous._denominatorInverse;
			_remaining = previous._remaining;
		}

		private void UpdateCoefficients()
		{
			_gain = _g / (1 + _g);
			double gain2 = _gain * _gain;
			_denominatorInverse = 1 / (1 + _feedback * gain2 * gain2);
		}

		public float Process(float sample)
		{
			if (_remaining != 0)
			{
				double progress = (double)(ParameterTransitionFrames - _remaining + 1)
					/ ParameterTransitionFrames;
				_g = _startG + (_targetG - _startG) * progress;
				_feedback = _startFeedback
					+ (_targetFeedback - _startFeedback) * progress;
				UpdateCoefficients();
				_remaining--;
			}
			double excitation = float.IsFinite(sample) ? sample : 0;
			double gain2 = _gain * _gain;
			double gain3 = gain2 * _gain;
			double gain4 = gain2 * gain2;
			double bypass = 1 - _gain;

			// Solve the instantaneous fourth-stage output analytically.
			// y4 = G^4 (x - K*y4)
			//    + (1-G)(G^3*s1 + G^2*s2 + G*s3 + s4).
			// This removes the algebraic feedback loop without introducing
			// a unit delay or iterating to convergence on every sample.
			double memory = bypass * (gain3 * _integrators[0]
				+ gain2 * _integrators[1]
				+ _gain * _integrators[2] + _integrators[3]);
			double fourth = (gain4 * excitation + memory)
				* _denominatorInverse;
			double input = excitation - _feedback * fourth;
			for (int i = 0; i < _integrators.Length; i++)
			{
				double output = _gain * input + bypass * _integrators[i];
				double state = 2 * output - _integrators[i];
				_integrators[i] = double.IsFinite(state)
					&& Math.Abs(state) >= 1e-20 ? state : 0;
				input = output;
			}

			// Finite PCM may exceed ±1; never let extreme signals poison
			// downstream processors with NaN, infinity or denormals.
			if (!double.IsFinite(input) || Math.Abs(input) < 1e-20)
				return 0;
			return (float)Math.Clamp(input, -float.MaxValue, float.MaxValue);
		}

		public IStreamingFilterStageState Clone() => new LadderState(this);

		public bool TryPrepareRealtime(double cutoff, double resonance,
			int sampleRate, out RealtimeLinearLadderCoefficients coefficients)
			=> TryPrepareCoefficients(cutoff, resonance, sampleRate,
				out coefficients, out _);

		public void CommitRealtime(RealtimeLinearLadderCoefficients coefficients)
			=> SetTarget(coefficients.G, coefficients.Feedback);

		private void SetTarget(double g, double feedback)
		{
			if (_g == g && _feedback == feedback)
			{
				_targetG = g;
				_targetFeedback = feedback;
				_startG = g;
				_startFeedback = feedback;
				_remaining = 0;
				return;
			}
			// Compatible changes preserve all four integrator histories.
			// Start again from the currently interpolated g and feedback
			// if another edit interrupts a 32-frame transition.
			_startG = _g;
			_startFeedback = _feedback;
			_targetG = g;
			_targetFeedback = feedback;
			_remaining = ParameterTransitionFrames;
		}

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not LadderPlan next)
				throw new ArgumentException(
					"Linear ladder state requires a linear ladder plan.",
					nameof(plan));
			_plan = next;
			SetTarget(next.G, next.Feedback);
		}
	}
}

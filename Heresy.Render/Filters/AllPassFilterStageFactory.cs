using System;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Stable first- or second-order phase shifter: one or two identical
/// first-order digital all-pass sections in series. Each section has
/// unity steady-state magnitude and -90 degrees phase at CutoffHz.
/// </summary>
public sealed class AllPassFilterStageFactory : IStreamingFilterStageFactory
{
	public static AllPassFilterStageFactory Instance { get; } = new();

	/// <summary>Numeric cutoff changes interpolate the reflection coefficient
	/// in the stable open interval (-1, 1). Order is structural and resets
	/// both section histories through the streaming engine.</summary>
	public const int CoefficientTransitionFrames = 32;

	private AllPassFilterStageFactory() { }

	public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (stage.Kind != FilterKind.AllPass)
			throw new ArgumentException(
				"An All-Pass factory requires an All-Pass stage.", nameof(stage));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		double cutoff = stage.Parameters["CutoffHz"].NumericValue;
		if (!double.IsFinite(cutoff) || cutoff <= 0 || cutoff >= sampleRate * 0.5)
			throw new ArgumentOutOfRangeException(nameof(stage),
				$"All-Pass cutoff {cutoff} Hz must be strictly below Nyquist "
				+ $"({sampleRate * 0.5} Hz).");

		int order = stage.Parameters["Order"].ChoiceValue switch
		{
			"1" => 1,
			"2" => 2,
			_ => throw new ArgumentException(
				"All-Pass order must be 1 or 2.", nameof(stage)),
		};

		// H(z) = (a + z^-1)/(1 + a*z^-1).
		// The bilinear frequency warp places -pi/2 phase at cutoff for
		// each section, with unity magnitude at *every* frequency.
		double warped = Math.Tan(Math.PI * cutoff / sampleRate);
		double coefficient = (warped - 1) / (warped + 1);
		if (!double.IsFinite(coefficient) || Math.Abs(coefficient) >= 1)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"All-Pass coefficient must have a pole inside the unit disk.");

		// A cascade of two repeated poles includes an n polynomial
		// envelope. Account for that in the estimated finite IIR work
		// budget; the invocation's maximumTailSeconds remains the cap.
		double radius = Math.Abs(coefficient);
		double baseFrames = radius == 0 ? 0
			: Math.Log(1e20) / (-Math.Log(radius));
		double repeatedPoleAllowance = order == 1 || radius == 0 ? 0
			: Math.Log(1 + baseFrames) / (-Math.Log(radius));
		double estimate = Math.Ceiling(
			baseFrames + repeatedPoleAllowance) + 2 * order + 4;
		int tail = !double.IsFinite(estimate) || estimate >= int.MaxValue
			? int.MaxValue : Math.Max(2 * order, (int)estimate);
		return new AllPassPlan(coefficient, order, tail);
	}

	private sealed class AllPassPlan(
		double coefficient, int order, int tailFrames) : IStreamingFilterStagePlan
	{
		public double Coefficient { get; } = coefficient;
		public int Order { get; } = order;
		public int TailFrames { get; } = tailFrames;
		public IStreamingFilterStageState CreateState() => new AllPassState(this);
	}

	private sealed class AllPassState : IStreamingFilterStageState
	{
		private AllPassPlan _plan;
		private double _currentCoefficient;
		private double _startCoefficient;
		private int _remaining;
		// Transposed first-order all-pass recurrence:
		// y = a*x + history; history' = x - a*y.
		// The second slot is unused at Order=1, but stored in the
		// same compact state layout for deterministic deep cloning.
		private double _history1;
		private double _history2;

		public AllPassState(AllPassPlan plan)
		{
			_plan = plan;
			_currentCoefficient = plan.Coefficient;
			_startCoefficient = _currentCoefficient;
		}

		private AllPassState(AllPassState previous)
		{
			_plan = previous._plan;
			_currentCoefficient = previous._currentCoefficient;
			_startCoefficient = previous._startCoefficient;
			_remaining = previous._remaining;
			_history1 = previous._history1;
			_history2 = previous._history2;
		}

		public float Process(float sample)
		{
			if (_remaining > 0)
			{
				double position = (double)(
					CoefficientTransitionFrames - _remaining + 1)
					/ CoefficientTransitionFrames;
				_currentCoefficient = _startCoefficient
					+ (_plan.Coefficient - _startCoefficient) * position;
				_remaining--;
			}
			double input = float.IsFinite(sample) ? sample : 0;
			double output = _currentCoefficient * input + _history1;
			_history1 = Flush(input - _currentCoefficient * output);
			if (_plan.Order == 2)
			{
				input = output;
				output = _currentCoefficient * input + _history2;
				_history2 = Flush(input - _currentCoefficient * output);
			}

			// Hardening at the render boundary; invalid source samples
			// never contaminate the recursive state. Flush well above
			// the float denormal threshold.
			if (!double.IsFinite(output) || Math.Abs(output) < 1e-20)
				return 0;
			return (float)Math.Clamp(output, -float.MaxValue, float.MaxValue);
		}

		private static double Flush(double value)
			=> double.IsFinite(value) && Math.Abs(value) >= 1e-20
				? value : 0;

		public IStreamingFilterStageState Clone() => new AllPassState(this);

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not AllPassPlan replacement
				|| replacement.Order != _plan.Order)
				throw new ArgumentException(
					"All-Pass state requires the same structural order.",
					nameof(plan));
			if (_currentCoefficient == replacement.Coefficient)
			{
				_plan = replacement;
				_startCoefficient = replacement.Coefficient;
				_remaining = 0;
				return;
			}
			// Compatible retuning never discards either section's
			// delay state. Interruptions restart from the *current*
			// interpolated coefficient instead of the old target.
			_startCoefficient = _currentCoefficient;
			_plan = replacement;
			_remaining = CoefficientTransitionFrames;
		}
	}
}

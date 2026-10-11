using System;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// First-order, topology-preserving-transform (TPT) low/high-pass filter.
/// Cutoff is the digital -3 dB corner frequency, including at high fractions
/// of Nyquist. Coefficients are prepared outside the PCM processing loop.
/// </summary>
public sealed class OnePoleFilterStageFactory : IStreamingFilterStageFactory
{
	public static OnePoleFilterStageFactory Instance { get; } = new();

	private OnePoleFilterStageFactory() { }

	public IStreamingFilterStagePlan Prepare(
		PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (stage.Kind != FilterKind.OnePole)
			throw new ArgumentException(
				"A One-Pole factory requires a One-Pole stage.", nameof(stage));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		double cutoff = stage.Parameters["CutoffHz"].NumericValue;
		if (!double.IsFinite(cutoff) || cutoff <= 0
			|| cutoff >= sampleRate * 0.5)
			throw new ArgumentOutOfRangeException(nameof(stage),
				$"One-Pole cutoff {cutoff} Hz must be strictly below "
				+ $"Nyquist ({sampleRate * 0.5} Hz).");

		string mode = stage.Parameters["Mode"].ChoiceValue
			?? throw new ArgumentException("One-Pole Mode is missing.", nameof(stage));
		bool highPass = mode switch
		{
			"LowPass" => false,
			"HighPass" => true,
			_ => throw new ArgumentException(
				$"Unknown One-Pole mode '{mode}'.", nameof(stage)),
		};

		// Bilinear/TPT integrator: v = (x - s) * g; LP = v + s;
		// s' = LP + v; HP = x - LP. Both outputs have unity gain in
		// their respective passbands and are -3.0103 dB at cutoff.
		double warped = Math.Tan(Math.PI * cutoff / sampleRate);
		double g = warped / (1.0 + warped);
		if (!double.IsFinite(g) || g <= 0 || g >= 1)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"One-Pole preparation produced an unstable coefficient.");

		// The IIR has an asymptotic tail. Bound its drain at -400 dB
		// (amplitude 1e-20) for unit-normalized excitation, plus a
		// two-frame margin. The chain additionally enforces its own
		// invocation-wide maximum tail duration.
		double pole = Math.Abs(1.0 - 2.0 * g);
		int tailFrames;
		if (pole == 0)
			tailFrames = 2;
		else
		{
			double frames = Math.Ceiling(Math.Log(1e-20) / Math.Log(pole)) + 2;
			tailFrames = !double.IsFinite(frames) || frames >= int.MaxValue
				? int.MaxValue
				: Math.Max(2, (int)frames);
		}
		return new OnePolePlan(g, highPass, tailFrames);
	}

	private sealed class OnePolePlan(
		double gain, bool highPass, int tailFrames) : IStreamingFilterStagePlan
	{
		public double Gain { get; } = gain;
		public bool HighPass { get; } = highPass;
		public int TailFrames { get; } = tailFrames;
		public IStreamingFilterStageState CreateState()
			=> new OnePoleState(this, 0);
	}

	private sealed class OnePoleState(
		OnePolePlan plan, double integrator) : IStreamingFilterStageState
	{
		// A scalar is sufficient for one first-order TPT integrator.
		// No state is shared with another output channel or invocation.
		private OnePolePlan _plan = plan;
		private double _integrator = integrator;

		public float Process(float sample)
		{
			// An invalid PCM sample must not permanently poison the IIR.
			double input = float.IsFinite(sample) ? sample : 0.0;
			double v = (input - _integrator) * _plan.Gain;
			double low = _integrator + v;
			double next = low + v;
			double output = _plan.HighPass ? input - low : low;

			// Above the representable float-noise floor. Eliminating
			// residuals here also prevents subnormal feedback and ensures
			// finite zero-input tails converge to exactly silent samples.
			_integrator = Math.Abs(next) < 1e-20 ? 0 : next;
			if (!double.IsFinite(_integrator))
				_integrator = 0;
			if (!double.IsFinite(output) || Math.Abs(output) < 1e-20)
				return 0;
			// An alternating full-scale input can create a legitimate
			// transient high-pass amplitude above one float's range.
			// Saturate it rather than emitting Infinity into the mixer.
			return (float)Math.Clamp(output, -float.MaxValue, float.MaxValue);
		}

		public IStreamingFilterStageState Clone()
			=> new OnePoleState(_plan, _integrator);

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not OnePolePlan replacement)
				throw new ArgumentException(
					"One-Pole state requires a One-Pole plan.", nameof(plan));
			// Cutoff changes preserve the integrator history. Mode is a
			// structural parameter and the chain replaces the state.
			_plan = replacement;
		}
	}
}

/// <summary>
/// Built-in routing for implemented filter kinds. Unimplemented kinds remain
/// explicit neutral processors, not accidentally treated as One-Pole.
/// This is the default factory for One-Pole, biquad, SVF and both ladder responses;
/// callers can
/// still request the all-bypass factory explicitly for an identity chain.
/// </summary>
public sealed class BuiltInFilterStageFactory : IStreamingFilterStageFactory
{
	public static BuiltInFilterStageFactory Instance { get; } = new();

	private BuiltInFilterStageFactory() { }

	public IStreamingFilterStagePlan Prepare(
		PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (stage.Kind == FilterKind.OnePole)
			return OnePoleFilterStageFactory.Instance.Prepare(stage, sampleRate);
		if (BiquadFilterStageFactory.IsBiquad(stage.Kind))
			return BiquadFilterStageFactory.Instance.Prepare(stage, sampleRate);
		if (SvfFilterStageFactory.IsSvf(stage.Kind))
			return SvfFilterStageFactory.Instance.Prepare(stage, sampleRate);
		if (stage.Kind == FilterKind.LadderLinear)
			return LinearLadderFilterStageFactory.Instance.Prepare(stage, sampleRate);
		if (stage.Kind == FilterKind.LadderSaturated)
			return SaturatedLadderFilterStageFactory.Instance.Prepare(stage, sampleRate);
		return BypassFilterStageFactory.Instance.Prepare(stage, sampleRate);
	}
}

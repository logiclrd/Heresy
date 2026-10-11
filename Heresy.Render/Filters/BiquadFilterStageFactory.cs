using System;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Seven Audio EQ biquad responses. Designs use normalized RBJ audio-EQ
/// cookbook coefficients; per-channel DSP uses transposed direct form II.
/// All trigonometry, pole validation and coefficient design are performed
/// during preparation, never in the PCM render loop.
/// </summary>
public sealed class BiquadFilterStageFactory : IStreamingFilterStageFactory
{
	public static BiquadFilterStageFactory Instance { get; } = new();

	/// <summary>Short, fixed transition for compatible numeric updates.
	/// Stable second-order denominator polynomials form a convex region,
	/// so interpolating two validated stable coefficient sets keeps poles
	/// inside the unit circle throughout this transition.</summary>
	public const int CoefficientTransitionFrames = 32;

	private BiquadFilterStageFactory() { }

	public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (!IsBiquad(stage.Kind))
			throw new ArgumentException("A biquad factory requires a biquad stage.",
				nameof(stage));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		double cutoff = stage.Parameters["CutoffHz"].NumericValue;
		if (!double.IsFinite(cutoff) || cutoff <= 0 || cutoff >= sampleRate * 0.5)
			throw new ArgumentOutOfRangeException(nameof(stage),
				$"Biquad cutoff {cutoff} Hz must be strictly below Nyquist "
				+ $"({sampleRate * 0.5} Hz).");

		double omega = 2 * Math.PI * cutoff / sampleRate;
		double sine = Math.Sin(omega);
		double cosine = Math.Cos(omega);
		double a = stage.Parameters.TryGetValue("GainDb", out FilterParameterValue? gain)
			? Math.Pow(10, gain.NumericValue / 40.0)
			: 1;
		double alpha;
		if (stage.Parameters.TryGetValue("Q", out FilterParameterValue? quality))
			alpha = sine / (2 * quality.NumericValue);
		else
		{
			double slope = stage.Parameters["ShelfSlope"].NumericValue;
			double radicand = (a + 1 / a) * (1 / slope - 1) + 2;
			if (!(radicand > 0) || !double.IsFinite(radicand))
				throw new ArgumentOutOfRangeException(nameof(stage),
					"ShelfSlope and GainDb combination does not admit a "
					+ "stable Audio EQ shelving response.");
			alpha = 0.5 * sine * Math.Sqrt(radicand);
		}

		double b0;
		double b1;
		double b2;
		double a0;
		double a1;
		double a2;
		double twoRootAAlpha = 2 * Math.Sqrt(a) * alpha;
		switch (stage.Kind)
		{
			case FilterKind.BiquadLowPass:
				b0 = (1 - cosine) / 2;
				b1 = 1 - cosine;
				b2 = b0;
				a0 = 1 + alpha;
				a1 = -2 * cosine;
				a2 = 1 - alpha;
				break;
			case FilterKind.BiquadHighPass:
				b0 = (1 + cosine) / 2;
				b1 = -(1 + cosine);
				b2 = b0;
				a0 = 1 + alpha;
				a1 = -2 * cosine;
				a2 = 1 - alpha;
				break;
			case FilterKind.BiquadBandPass:
				// Constant 0 dB peak (rather than constant skirt gain);
				// Q controls relative bandwidth, not peak amplification.
				b0 = alpha;
				b1 = 0;
				b2 = -alpha;
				a0 = 1 + alpha;
				a1 = -2 * cosine;
				a2 = 1 - alpha;
				break;
			case FilterKind.BiquadNotch:
				b0 = 1;
				b1 = -2 * cosine;
				b2 = 1;
				a0 = 1 + alpha;
				a1 = -2 * cosine;
				a2 = 1 - alpha;
				break;
			case FilterKind.BiquadPeaking:
				b0 = 1 + alpha * a;
				b1 = -2 * cosine;
				b2 = 1 - alpha * a;
				a0 = 1 + alpha / a;
				a1 = -2 * cosine;
				a2 = 1 - alpha / a;
				break;
			case FilterKind.BiquadLowShelf:
				b0 = a * ((a + 1) - (a - 1) * cosine + twoRootAAlpha);
				b1 = 2 * a * ((a - 1) - (a + 1) * cosine);
				b2 = a * ((a + 1) - (a - 1) * cosine - twoRootAAlpha);
				a0 = (a + 1) + (a - 1) * cosine + twoRootAAlpha;
				a1 = -2 * ((a - 1) + (a + 1) * cosine);
				a2 = (a + 1) + (a - 1) * cosine - twoRootAAlpha;
				break;
			case FilterKind.BiquadHighShelf:
				b0 = a * ((a + 1) + (a - 1) * cosine + twoRootAAlpha);
				b1 = -2 * a * ((a - 1) + (a + 1) * cosine);
				b2 = a * ((a + 1) + (a - 1) * cosine - twoRootAAlpha);
				a0 = (a + 1) - (a - 1) * cosine + twoRootAAlpha;
				a1 = 2 * ((a - 1) - (a + 1) * cosine);
				a2 = (a + 1) - (a - 1) * cosine - twoRootAAlpha;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(stage));
		}

		Coefficients coefficients = new(
			b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
		ValidateCoefficients(coefficients);

		// Every stable IIR response is formally infinite. Use a generous
		// -400 dB pole decay budget for unit impulse excitation, plus two
		// samples, bounded by the chain's maximumTailSeconds allowance.
		double rootRadius = PoleRadius(coefficients);
		double frames = rootRadius == 0
			? 2
			: Math.Ceiling(Math.Log(1e-20) / Math.Log(rootRadius)) + 2;
		int tail = !double.IsFinite(frames) || frames >= int.MaxValue
			? int.MaxValue
			: Math.Max(2, (int)frames);
		return new BiquadPlan(coefficients, tail);
	}

	public static bool IsBiquad(FilterKind kind)
		=> kind is FilterKind.BiquadLowPass
			or FilterKind.BiquadHighPass
			or FilterKind.BiquadLowShelf
			or FilterKind.BiquadHighShelf
			or FilterKind.BiquadPeaking
			or FilterKind.BiquadBandPass
			or FilterKind.BiquadNotch;

	private static void ValidateCoefficients(Coefficients c)
	{
		if (!double.IsFinite(c.B0) || !double.IsFinite(c.B1)
			|| !double.IsFinite(c.B2) || !double.IsFinite(c.A1)
			|| !double.IsFinite(c.A2)
			|| !(Math.Abs(c.A2) < 1)
			|| !(1 + c.A1 + c.A2 > 0)
			|| !(1 - c.A1 + c.A2 > 0))
			throw new ArgumentOutOfRangeException(nameof(c),
				"Biquad coefficients are nonfinite or have poles outside "
				+ "the stable unit disk.");
	}

	private static double PoleRadius(Coefficients c)
	{
		double discriminant = c.A1 * c.A1 - 4 * c.A2;
		if (discriminant < 0)
			return Math.Sqrt(c.A2);
		double root = Math.Sqrt(discriminant);
		return Math.Max(Math.Abs((-c.A1 + root) / 2),
			Math.Abs((-c.A1 - root) / 2));
	}

	private readonly record struct Coefficients(
		double B0, double B1, double B2, double A1, double A2)
	{
		public static Coefficients Lerp(Coefficients x, Coefficients y, double t)
			=> new(
				x.B0 + (y.B0 - x.B0) * t,
				x.B1 + (y.B1 - x.B1) * t,
				x.B2 + (y.B2 - x.B2) * t,
				x.A1 + (y.A1 - x.A1) * t,
				x.A2 + (y.A2 - x.A2) * t);
	}

	private sealed class BiquadPlan(
		Coefficients coefficients, int tailFrames) : IStreamingFilterStagePlan
	{
		public Coefficients Coefficients { get; } = coefficients;
		public int TailFrames { get; } = tailFrames;
		public IStreamingFilterStageState CreateState()
			=> new BiquadState(this);
	}

	private sealed class BiquadState : IStreamingFilterStageState
	{
		private BiquadPlan _plan;
		private Coefficients _current;
		private Coefficients _start;
		private double _z1;
		private double _z2;
		private int _remaining;

		public BiquadState(BiquadPlan plan)
		{
			_plan = plan;
			_current = plan.Coefficients;
			_start = _current;
		}

		private BiquadState(BiquadState source)
		{
			_plan = source._plan;
			_current = source._current;
			_start = source._start;
			_z1 = source._z1;
			_z2 = source._z2;
			_remaining = source._remaining;
		}

		public float Process(float sample)
		{
			if (_remaining != 0)
			{
				int elapsed = CoefficientTransitionFrames - _remaining + 1;
				_current = Coefficients.Lerp(_start, _plan.Coefficients,
					(double)elapsed / CoefficientTransitionFrames);
				_remaining--;
			}
			double input = float.IsFinite(sample) ? sample : 0;
			double output = _current.B0 * input + _z1;
			double next1 = _current.B1 * input
				- _current.A1 * output + _z2;
			double next2 = _current.B2 * input - _current.A2 * output;
			// Double-precision histories provide headroom for valid
			// high-gain EQ applied to full-range float PCM samples.
			// Flush before subnormal feedback, rather than rely on CPU flags.
			_z1 = double.IsFinite(next1) && Math.Abs(next1) >= 1e-20
				? next1 : 0;
			_z2 = double.IsFinite(next2) && Math.Abs(next2) >= 1e-20
				? next2 : 0;
			if (!double.IsFinite(output) || Math.Abs(output) < 1e-20)
				return 0;
			return (float)Math.Clamp(output, -float.MaxValue, float.MaxValue);
		}

		public IStreamingFilterStageState Clone() => new BiquadState(this);

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not BiquadPlan next)
				throw new ArgumentException(
					"Biquad state requires a Biquad plan.", nameof(plan));
			if (_current == next.Coefficients)
			{
				_plan = next;
				_start = next.Coefficients;
				_remaining = 0;
				return;
			}
			// Do not clear z1/z2. A compatible numeric change begins a
			// bounded coefficient interpolation from the current state,
			// even when a previous interpolation has not yet completed.
			_start = _current;
			_plan = next;
			_remaining = CoefficientTransitionFrames;
		}
	}
}

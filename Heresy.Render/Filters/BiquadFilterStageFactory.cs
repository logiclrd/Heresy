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
		double q = stage.Parameters.TryGetValue("Q", out FilterParameterValue? quality)
			? quality.NumericValue : 1;
		double shelfSlope = stage.Parameters.TryGetValue(
			"ShelfSlope", out FilterParameterValue? slope)
			? slope.NumericValue : 1;
		double gainDb = stage.Parameters.TryGetValue(
			"GainDb", out FilterParameterValue? gain)
			? gain.NumericValue : 0;
		if (!TryDesign(stage.Kind, cutoff, q, shelfSlope, gainDb,
			sampleRate, out Coefficients coefficients))
			throw new ArgumentOutOfRangeException(nameof(stage),
				"Biquad parameter values must admit stable, finite audio-EQ "
				+ "coefficients with CutoffHz strictly below Nyquist.");

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
		return new BiquadPlan(stage.Kind, coefficients, tail);
	}

	public static bool IsBiquad(FilterKind kind)
		=> kind is FilterKind.BiquadLowPass
			or FilterKind.BiquadHighPass
			or FilterKind.BiquadLowShelf
			or FilterKind.BiquadHighShelf
			or FilterKind.BiquadPeaking
			or FilterKind.BiquadBandPass
			or FilterKind.BiquadNotch;

	private static bool IsShelf(FilterKind kind)
		=> kind is FilterKind.BiquadLowShelf or FilterKind.BiquadHighShelf;

	/// <summary>
	/// Common RBJ design shared by immutable plans and the realtime
	/// preflight. No allocations or exceptions for invalid modulation.
	/// </summary>
	private static bool TryDesign(FilterKind kind, double cutoff,
		double q, double slope, double gainDb, int sampleRate,
		out Coefficients coefficients)
	{
		coefficients = default;
		if (!IsBiquad(kind) || sampleRate <= 0
			|| !double.IsFinite(cutoff) || cutoff < 1 || cutoff > 20000
			|| cutoff >= sampleRate * 0.5
			|| (!IsShelf(kind) && (!double.IsFinite(q) || q < 0.1 || q > 20))
			|| (IsShelf(kind) && (!double.IsFinite(slope)
				|| slope < 0.1 || slope > 2))
			|| (kind is FilterKind.BiquadPeaking
				or FilterKind.BiquadLowShelf or FilterKind.BiquadHighShelf)
				&& (!double.IsFinite(gainDb) || gainDb < -36 || gainDb > 36))
			return false;
		double omega = 2 * Math.PI * cutoff / sampleRate;
		double sine = Math.Sin(omega);
		double cosine = Math.Cos(omega);
		double a = Math.Pow(10, gainDb / 40.0);
		double alpha;
		if (!IsShelf(kind))
			alpha = sine / (2 * q);
		else
		{
			double radicand = (a + 1 / a) * (1 / slope - 1) + 2;
			if (!(radicand > 0) || !double.IsFinite(radicand))
				return false;
			alpha = 0.5 * sine * Math.Sqrt(radicand);
		}

		double b0;
		double b1;
		double b2;
		double a0;
		double a1;
		double a2;
		double twoRootAAlpha = 2 * Math.Sqrt(a) * alpha;
		switch (kind)
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
				return false;
		}

		coefficients = new(
			b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
		return AreCoefficientsValid(coefficients);
	}

	private static bool AreCoefficientsValid(Coefficients c)
	{
		return double.IsFinite(c.B0) && double.IsFinite(c.B1)
			&& double.IsFinite(c.B2) && double.IsFinite(c.A1)
			&& double.IsFinite(c.A2)
			&& Math.Abs(c.A2) < 1
			&& 1 + c.A1 + c.A2 > 0
			&& 1 - c.A1 + c.A2 > 0;
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
		FilterKind kind, Coefficients coefficients, int tailFrames) : IStreamingFilterStagePlan
	{
		public FilterKind Kind { get; } = kind;
		public Coefficients Coefficients { get; } = coefficients;
		public int TailFrames { get; } = tailFrames;
		public IStreamingFilterStageState CreateState()
			=> new BiquadState(this);
	}

	private sealed class BiquadState : IStreamingFilterStageState,
		IRealtimeBiquadFilterState
	{
		private BiquadPlan _plan;
		private Coefficients _current;
		private Coefficients _start;
		private Coefficients _target;
		private double _z1;
		private double _z2;
		private int _remaining;

		public BiquadState(BiquadPlan plan)
		{
			_plan = plan;
			_current = plan.Coefficients;
			_start = _current;
			_target = _current;
		}

		private BiquadState(BiquadState source)
		{
			_plan = source._plan;
			_current = source._current;
			_start = source._start;
			_target = source._target;
			_z1 = source._z1;
			_z2 = source._z2;
			_remaining = source._remaining;
		}

		public float Process(float sample)
		{
			if (_remaining != 0)
			{
				int elapsed = CoefficientTransitionFrames - _remaining + 1;
				_current = Coefficients.Lerp(_start, _target,
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

		public bool TryPrepareRealtime(double cutoff, double q,
			double shelfSlope, double gainDb, int sampleRate,
			out RealtimeBiquadCoefficients target)
		{
			bool valid = TryDesign(_plan.Kind, cutoff, q, shelfSlope,
				gainDb, sampleRate, out Coefficients designed);
			target = valid ? new(designed.B0, designed.B1, designed.B2,
				designed.A1, designed.A2) : default;
			return valid;
		}

		public void CommitRealtime(RealtimeBiquadCoefficients target)
			=> SetTarget(new Coefficients(target.B0, target.B1, target.B2,
				target.A1, target.A2));

		private void SetTarget(Coefficients next)
		{
			if (_current == next)
			{
				_target = next;
				_start = next;
				_remaining = 0;
				return;
			}
			_start = _current;
			_target = next;
			_remaining = CoefficientTransitionFrames;
		}

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not BiquadPlan next || next.Kind != _plan.Kind)
				throw new ArgumentException(
					"Biquad state requires a matching Biquad plan.",
					nameof(plan));
			_plan = next;
			SetTarget(next.Coefficients);
		}
	}
}

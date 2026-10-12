using System;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Streaming feedback comb IIR with an integer-sample circular delay line.
/// The feedback loop stores w[n] = x[n] + feedback * w[n-D]; the
/// externally routed wet tap is w[n-D], so Mix still acts as a plain
/// delay when Feedback is zero. Each invocation, stage and PCM channel
/// owns its own circular buffer and write position.
/// </summary>
public sealed class FeedbackCombFilterStageFactory : IStreamingFilterStageFactory
{
	public static FeedbackCombFilterStageFactory Instance { get; } = new();

	/// <summary>Bound the memory of a single stage/channel even at unusual
	/// host sample rates. DelaySeconds itself remains capped at two seconds
	/// by the shared catalogue. Each history uses 8 bytes per frame.</summary>
	public const int MaximumDelayFrames = 1_000_000;

	/// <summary>Compatible numeric gain/mix edits retain the entire delay
	/// ring and crossfade coefficients over a fixed number of frames.</summary>
	public const int ParameterTransitionFrames = 32;

	private FeedbackCombFilterStageFactory() { }

	public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (stage.Kind != FilterKind.CombFeedback)
			throw new ArgumentException(
				"A feedback comb factory requires a feedback comb stage.",
				nameof(stage));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		double seconds = stage.Parameters["DelaySeconds"].NumericValue;
		double feedback = stage.Parameters["Feedback"].NumericValue;
		double mix = stage.Parameters["Mix"].NumericValue;
		if (!double.IsFinite(seconds) || seconds < 0.001 || seconds > 2)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"Comb delay must be 0.001–2 seconds.");
		if (!TryPrepareNumeric(feedback, mix,
			out RealtimeFeedbackCombCoefficients coefficients))
			throw new ArgumentOutOfRangeException(nameof(stage),
				"Comb Feedback must be finite and in [-0.95, 0.95], "
				+ "and Mix must be finite and in [0, 1].");

		// Halfway values are rounded away from zero, consistently across
		// operating systems. This defines the actual audible discrete
		// delay; the structural authored parameter remains in seconds.
		double frames = Math.Round(seconds * sampleRate,
			MidpointRounding.AwayFromZero);
		if (!double.IsFinite(frames) || frames < 1
			|| frames > MaximumDelayFrames)
			throw new ArgumentOutOfRangeException(nameof(stage),
				$"Comb delay requires 1–{MaximumDelayFrames} samples "
				+ "at the selected render sample rate.");
		int delayFrames = (int)frames;

		// An impulse produces a wet echo at D, then D-spaced echoes
		// scaled by successive powers of feedback. A feedback coefficient
		// of zero produces exactly one echo. The IIR budget ends once a
		// unit-normalized echo's attenuation reaches -400 dB (1e-20),
		// with a one-cycle margin. StreamingFilterChain further clamps
		// the combined serial tail to maximumTailSeconds.
		int tailFrames;
		if (mix == 0)
			tailFrames = 0;
		else
		{
			double cycles = feedback == 0 ? 1
				: 1 + Math.Ceiling(Math.Log(1e-20) / Math.Log(Math.Abs(feedback)));
			double requiredFrames = cycles * delayFrames;
			tailFrames = !double.IsFinite(requiredFrames)
				|| requiredFrames >= int.MaxValue
					? int.MaxValue : (int)requiredFrames;
		}
		return new FeedbackCombPlan(delayFrames,
			coefficients.Feedback, coefficients.Mix, tailFrames);
	}

	/// <summary>
	/// Shared static and allocation-free realtime numeric preflight.
	/// DelaySeconds is structural and never re-quantized or resized during
	/// an audio-frame update.
	/// </summary>
	private static bool TryPrepareNumeric(double feedback, double mix,
		out RealtimeFeedbackCombCoefficients coefficients)
	{
		coefficients = default;
		if (!double.IsFinite(feedback) || Math.Abs(feedback) > 0.95
			|| !double.IsFinite(mix) || mix < 0 || mix > 1)
			return false;
		coefficients = new(feedback, mix);
		return true;
	}

	private sealed class FeedbackCombPlan(
		int delayFrames, double feedback, double mix, int tailFrames)
		: IStreamingFilterStagePlan
	{
		public int DelayFrames { get; } = delayFrames;
		public double Feedback { get; } = feedback;
		public double Mix { get; } = mix;
		public int TailFrames { get; } = tailFrames;
		public IStreamingFilterStageState CreateState()
			=> new FeedbackCombState(this);
	}

	private sealed class FeedbackCombState : IStreamingFilterStageState,
		IRealtimeFeedbackCombFilterState
	{
		private readonly double[] _ring;
		private FeedbackCombPlan _plan;
		private int _position;
		private double _feedback;
		private double _mix;
		private double _startFeedback;
		private double _startMix;
		private double _targetFeedback;
		private double _targetMix;
		private int _remaining;

		public FeedbackCombState(FeedbackCombPlan plan)
		{
			_plan = plan;
			_ring = new double[plan.DelayFrames];
			_feedback = plan.Feedback;
			_mix = plan.Mix;
			_startFeedback = _feedback;
			_startMix = _mix;
			_targetFeedback = _feedback;
			_targetMix = _mix;
		}

		private FeedbackCombState(FeedbackCombState source)
		{
			_plan = source._plan;
			_ring = (double[])source._ring.Clone();
			_position = source._position;
			_feedback = source._feedback;
			_mix = source._mix;
			_startFeedback = source._startFeedback;
			_startMix = source._startMix;
			_targetFeedback = source._targetFeedback;
			_targetMix = source._targetMix;
			_remaining = source._remaining;
		}

		public float Process(float sample)
		{
			if (_remaining != 0)
			{
				double progress = (double)(
					ParameterTransitionFrames - _remaining + 1)
					/ ParameterTransitionFrames;
				_feedback = _startFeedback
					+ (_targetFeedback - _startFeedback) * progress;
				_mix = _startMix + (_targetMix - _startMix) * progress;
				_remaining--;
			}

			double input = float.IsFinite(sample) ? sample : 0;
			double delayed = _ring[_position];
			double recorded = input + _feedback * delayed;
			_ring[_position] = Flush(recorded);
			if (++_position == _ring.Length)
				_position = 0;

			double output = (1 - _mix) * input + _mix * delayed;
			if (!double.IsFinite(output) || Math.Abs(output) < 1e-20)
				return 0;
			return (float)Math.Clamp(output, -float.MaxValue, float.MaxValue);
		}

		private static double Flush(double value)
			=> double.IsFinite(value) && Math.Abs(value) >= 1e-20
				? value : 0;

		public IStreamingFilterStageState Clone()
			=> new FeedbackCombState(this);

		public bool TryPrepareRealtime(double feedback, double mix,
			out RealtimeFeedbackCombCoefficients coefficients)
			=> TryPrepareNumeric(feedback, mix, out coefficients);

		public void CommitRealtime(RealtimeFeedbackCombCoefficients coefficients)
			=> SetTarget(coefficients.Feedback, coefficients.Mix);

		private void SetTarget(double feedback, double mix)
		{
			if (_feedback == feedback && _mix == mix)
			{
				_targetFeedback = feedback;
				_targetMix = mix;
				_startFeedback = feedback;
				_startMix = mix;
				_remaining = 0;
				return;
			}
			// Preserve all stored echoes, the ring position and any
			// already-interpolated feedback/mix values.
			_startFeedback = _feedback;
			_startMix = _mix;
			_targetFeedback = feedback;
			_targetMix = mix;
			_remaining = ParameterTransitionFrames;
		}

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not FeedbackCombPlan next
				|| next.DelayFrames != _ring.Length)
				throw new ArgumentException(
					"Feedback comb history requires an identical "
					+ "structural delay length.", nameof(plan));
			_plan = next;
			SetTarget(next.Feedback, next.Mix);
		}
	}
}

using System;

using Heresy.Core.Filters;

namespace Heresy.Render.Filters;

/// <summary>
/// Two-tap feedforward FIR comb. The wet signal is
/// x[n] + Feedforward*x[n-D]; blending with dry input yields
/// x[n] + Mix*Feedforward*x[n-D], with no recirculating delay.
/// </summary>
public sealed class FeedforwardCombFilterStageFactory : IStreamingFilterStageFactory
{
	public static FeedforwardCombFilterStageFactory Instance { get; } = new();
	public const int MaximumDelayFrames = 1_000_000;
	public const int ParameterTransitionFrames = 32;

	private FeedforwardCombFilterStageFactory() { }

	public IStreamingFilterStagePlan Prepare(PreparedFilterStage stage, int sampleRate)
	{
		ArgumentNullException.ThrowIfNull(stage);
		if (stage.Kind != FilterKind.CombFeedforward)
			throw new ArgumentException(
				"A feedforward comb factory requires a feedforward comb stage.",
				nameof(stage));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		double seconds = stage.Parameters["DelaySeconds"].NumericValue;
		double feedforward = stage.Parameters["Feedforward"].NumericValue;
		double mix = stage.Parameters["Mix"].NumericValue;
		if (!double.IsFinite(seconds) || seconds < 0.001 || seconds > 2)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"Comb delay must be 0.001–2 seconds.");
		if (!TryPrepareNumeric(feedforward, mix,
			out RealtimeFeedforwardCombCoefficients coefficients))
			throw new ArgumentOutOfRangeException(nameof(stage),
				"Comb Feedforward must be finite in [-1, 1], "
				+ "and Mix must be finite in [0, 1].");

		double frames = Math.Round(seconds * sampleRate,
			MidpointRounding.AwayFromZero);
		if (!double.IsFinite(frames) || frames < 1
			|| frames > MaximumDelayFrames)
			throw new ArgumentOutOfRangeException(nameof(stage),
				$"Comb delay requires 1–{MaximumDelayFrames} samples "
				+ "at the selected render sample rate.");
		// An in-flight ramp may be audible after the target gains become
		// neutral; always reserve the exact worst-case D-frame FIR tail.
		return new FeedforwardCombPlan((int)frames,
			coefficients.Feedforward, coefficients.Mix);
	}

	/// <summary>
	/// Shared numeric validation for full preparation and audio-rate
	/// modulation; structural delay is never touched in this path.
	/// </summary>
	private static bool TryPrepareNumeric(double feedforward, double mix,
		out RealtimeFeedforwardCombCoefficients coefficients)
	{
		coefficients = default;
		if (!double.IsFinite(feedforward) || Math.Abs(feedforward) > 1
			|| !double.IsFinite(mix) || mix < 0 || mix > 1)
			return false;
		coefficients = new(feedforward, mix);
		return true;
	}

	private sealed class FeedforwardCombPlan(
		int delayFrames, double feedforward, double mix)
		: IStreamingFilterStagePlan
	{
		public int DelayFrames { get; } = delayFrames;
		public double Feedforward { get; } = feedforward;
		public double Mix { get; } = mix;
		public int TailFrames => DelayFrames;
		public IStreamingFilterStageState CreateState()
			=> new FeedforwardCombState(this);
	}

	private sealed class FeedforwardCombState : IStreamingFilterStageState,
		IRealtimeFeedforwardCombFilterState
	{
		private readonly double[] _ring;
		private FeedforwardCombPlan _plan;
		private int _position;
		private double _feedforward;
		private double _mix;
		private double _startFeedforward;
		private double _startMix;
		private double _targetFeedforward;
		private double _targetMix;
		private int _remaining;

		public FeedforwardCombState(FeedforwardCombPlan plan)
		{
			_plan = plan;
			_ring = new double[plan.DelayFrames];
			_feedforward = plan.Feedforward;
			_mix = plan.Mix;
			_startFeedforward = _feedforward;
			_startMix = _mix;
			_targetFeedforward = _feedforward;
			_targetMix = _mix;
		}

		private FeedforwardCombState(FeedforwardCombState source)
		{
			_plan = source._plan;
			_ring = (double[])source._ring.Clone();
			_position = source._position;
			_feedforward = source._feedforward;
			_mix = source._mix;
			_startFeedforward = source._startFeedforward;
			_startMix = source._startMix;
			_targetFeedforward = source._targetFeedforward;
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
				_feedforward = _startFeedforward
					+ (_targetFeedforward - _startFeedforward) * progress;
				_mix = _startMix + (_targetMix - _startMix) * progress;
				_remaining--;
			}
			double input = float.IsFinite(sample) && Math.Abs(sample) >= 1e-20
				? sample : 0;
			double delayed = _ring[_position];
			_ring[_position] = input;
			if (++_position == _ring.Length)
				_position = 0;
			double output = input + _mix * _feedforward * delayed;
			if (!double.IsFinite(output) || Math.Abs(output) < 1e-20)
				return 0;
			return (float)Math.Clamp(output, -float.MaxValue, float.MaxValue);
		}

		public IStreamingFilterStageState Clone()
			=> new FeedforwardCombState(this);

		public bool TryPrepareRealtime(double feedforward, double mix,
			out RealtimeFeedforwardCombCoefficients coefficients)
			=> TryPrepareNumeric(feedforward, mix, out coefficients);

		public void CommitRealtime(RealtimeFeedforwardCombCoefficients coefficients)
			=> SetTarget(coefficients.Feedforward, coefficients.Mix);

		private void SetTarget(double feedforward, double mix)
		{
			if (_feedforward == feedforward && _mix == mix)
			{
				_targetFeedforward = feedforward;
				_targetMix = mix;
				_startFeedforward = feedforward;
				_startMix = mix;
				_remaining = 0;
				return;
			}
			// Keep the quantized ring, write position, and every delayed
			// sample. Interrupt an in-flight ramp at its applied values.
			_startFeedforward = _feedforward;
			_startMix = _mix;
			_targetFeedforward = feedforward;
			_targetMix = mix;
			_remaining = ParameterTransitionFrames;
		}

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not FeedforwardCombPlan next
				|| next.DelayFrames != _ring.Length)
				throw new ArgumentException(
					"Feedforward comb history requires an identical "
					+ "structural delay length.", nameof(plan));
			_plan = next;
			SetTarget(next.Feedforward, next.Mix);
		}
	}
}

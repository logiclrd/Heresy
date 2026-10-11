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
		if (!double.IsFinite(feedforward) || Math.Abs(feedforward) > 1)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"Comb feedforward coefficient must be in [-1, 1].");
		if (!double.IsFinite(mix) || mix < 0 || mix > 1)
			throw new ArgumentOutOfRangeException(nameof(stage),
				"Comb wet/dry mix must be in [0, 1].");

		double frames = Math.Round(seconds * sampleRate,
			MidpointRounding.AwayFromZero);
		if (!double.IsFinite(frames) || frames < 1
			|| frames > MaximumDelayFrames)
			throw new ArgumentOutOfRangeException(nameof(stage),
				$"Comb delay requires 1–{MaximumDelayFrames} samples "
				+ "at the selected render sample rate.");
		// An in-flight ramp may be audible after the target gains become
		// neutral; always reserve the exact worst-case D-frame FIR tail.
		return new FeedforwardCombPlan((int)frames, feedforward, mix);
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

	private sealed class FeedforwardCombState : IStreamingFilterStageState
	{
		private readonly double[] _ring;
		private FeedforwardCombPlan _plan;
		private int _position;
		private double _feedforward;
		private double _mix;
		private double _startFeedforward;
		private double _startMix;
		private int _remaining;

		public FeedforwardCombState(FeedforwardCombPlan plan)
		{
			_plan = plan;
			_ring = new double[plan.DelayFrames];
			_feedforward = plan.Feedforward;
			_mix = plan.Mix;
			_startFeedforward = _feedforward;
			_startMix = _mix;
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
					+ (_plan.Feedforward - _startFeedforward) * progress;
				_mix = _startMix + (_plan.Mix - _startMix) * progress;
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

		public void Reconfigure(IStreamingFilterStagePlan plan)
		{
			if (plan is not FeedforwardCombPlan next
				|| next.DelayFrames != _ring.Length)
				throw new ArgumentException(
					"Feedforward comb history requires an identical "
					+ "structural delay length.", nameof(plan));
			if (_feedforward == next.Feedforward && _mix == next.Mix)
			{
				_plan = next;
				_startFeedforward = _feedforward;
				_startMix = _mix;
				_remaining = 0;
				return;
			}
			_startFeedforward = _feedforward;
			_startMix = _mix;
			_plan = next;
			_remaining = ParameterTransitionFrames;
		}
	}
}

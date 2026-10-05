using System;
using System.Collections.Generic;

namespace Heresy.Render.Playback;

/// <summary>
/// Canonical additive coordinates used by playback operators.
/// </summary>
public enum PlaybackParameter
{
	PitchLinearUnits,
	NoteVolume,
	OverallVolume,
	SpatialX,
	GlobalVolume,
	Tempo,
}

/// <summary>
/// Sparse-by-convention fixed vector of additive parameter changes.
/// </summary>
public sealed class PlaybackParameterDeltas
{
	private readonly double[] _values =
		new double[Enum.GetValues<PlaybackParameter>().Length];

	public double this[PlaybackParameter parameter]
	{
		get => _values[(int)parameter];
		set
		{
			if (double.IsNaN(value)
				|| double.IsInfinity(value))
			{
				throw new ArgumentOutOfRangeException(nameof(value));
			}

			_values[(int)parameter] = value;
		}
	}

	internal PlaybackParameterDeltas Clone()
	{
		PlaybackParameterDeltas clone = new();
		Array.Copy(_values, clone._values, _values.Length);
		return clone;
	}
}

public interface IRowPlaybackOperator
{
	PlaybackParameterDeltas Deltas { get; }
	bool CommitOnExpire { get; }

	void Update(
		double wallTimeSeconds,
		double rowTime);
}

/// <summary>
/// Generic row operator whose one additive contribution traverses zero to a
/// fixed total delta over the captured row span.
/// </summary>
public sealed class LinearRowPlaybackOperator : IRowPlaybackOperator
{
	private readonly PlaybackParameter _parameter;
	private readonly double _totalDelta;
	private readonly double _rowSpan;

	public LinearRowPlaybackOperator(
		PlaybackParameter parameter,
		double totalDelta,
		double rowSpan,
		bool commitOnExpire)
	{
		if (double.IsNaN(totalDelta)
			|| double.IsInfinity(totalDelta))
		{
			throw new ArgumentOutOfRangeException(nameof(totalDelta));
		}
		if (!(rowSpan > 0.0)
			|| double.IsNaN(rowSpan)
			|| double.IsInfinity(rowSpan))
		{
			throw new ArgumentOutOfRangeException(nameof(rowSpan));
		}

		_parameter = parameter;
		_totalDelta = totalDelta;
		_rowSpan = rowSpan;
		CommitOnExpire = commitOnExpire;
	}

	public PlaybackParameterDeltas Deltas { get; } = new();
	public bool CommitOnExpire { get; }

	public void Update(
		double wallTimeSeconds,
		double rowTime)
	{
		if (double.IsNaN(wallTimeSeconds)
			|| double.IsInfinity(wallTimeSeconds)
			|| wallTimeSeconds < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(wallTimeSeconds));
		}
		if (double.IsNaN(rowTime)
			|| double.IsInfinity(rowTime))
		{
			throw new ArgumentOutOfRangeException(nameof(rowTime));
		}

		double progress =
			Math.Clamp(rowTime / _rowSpan, 0.0, 1.0);
		Deltas[_parameter] =
			_totalDelta * progress;
	}
}

/// <summary>
/// Row operator backed by an absolute delta function. The function receives
/// wall time and row time and returns this operator's current contribution.
/// </summary>
public sealed class FunctionalRowPlaybackOperator : IRowPlaybackOperator
{
	private readonly PlaybackParameter _parameter;
	private readonly Func<double, double, double> _evaluate;

	public FunctionalRowPlaybackOperator(
		PlaybackParameter parameter,
		Func<double, double, double> evaluate,
		bool commitOnExpire)
	{
		_parameter = parameter;
		_evaluate = evaluate
			?? throw new ArgumentNullException(nameof(evaluate));
		CommitOnExpire = commitOnExpire;
	}

	public PlaybackParameterDeltas Deltas { get; } = new();
	public bool CommitOnExpire { get; }

	public void Update(
		double wallTimeSeconds,
		double rowTime)
	{
		if (double.IsNaN(wallTimeSeconds)
			|| double.IsInfinity(wallTimeSeconds)
			|| wallTimeSeconds < 0.0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(wallTimeSeconds));
		}
		if (double.IsNaN(rowTime)
			|| double.IsInfinity(rowTime))
		{
			throw new ArgumentOutOfRangeException(nameof(rowTime));
		}

		double value = _evaluate(
			wallTimeSeconds,
			rowTime);
		if (double.IsNaN(value)
			|| double.IsInfinity(value))
		{
			throw new InvalidOperationException(
				"Operator produced a non-finite parameter delta.");
		}

		Deltas[_parameter] = value;
	}
}

/// <summary>
/// Owns simultaneously active operators. Operators retain independent delta
/// vectors; composition sums those vectors only when effective state is read.
/// </summary>
public sealed class PlaybackOperatorCollection
{
	private readonly List<IRowPlaybackOperator> _operators = [];

	public int Count => _operators.Count;

	public void Add(IRowPlaybackOperator playbackOperator)
	{
		ArgumentNullException.ThrowIfNull(playbackOperator);
		if (_operators.Contains(playbackOperator))
		{
			throw new InvalidOperationException(
				"Operator is already active.");
		}

		_operators.Add(playbackOperator);
	}

	public void Update(
		double wallTimeSeconds,
		double rowTime)
	{
		foreach (IRowPlaybackOperator playbackOperator
			in _operators)
		{
			playbackOperator.Update(
				wallTimeSeconds,
				rowTime);
		}
	}

	public double GetTotalDelta(PlaybackParameter parameter)
	{
		double total = 0.0;
		foreach (IRowPlaybackOperator playbackOperator
			in _operators)
		{
			total += playbackOperator.Deltas[parameter];
		}
		return total;
	}

	public PlaybackParameterDeltas Expire(
		IRowPlaybackOperator playbackOperator,
		double wallTimeSeconds,
		double rowTime)
	{
		ArgumentNullException.ThrowIfNull(playbackOperator);
		if (!_operators.Contains(playbackOperator))
		{
			throw new InvalidOperationException(
				"Operator is not active.");
		}

		playbackOperator.Update(
			wallTimeSeconds,
			rowTime);
		_operators.Remove(playbackOperator);

		return playbackOperator.CommitOnExpire
			? playbackOperator.Deltas.Clone()
			: new PlaybackParameterDeltas();
	}

	public bool Remove(IRowPlaybackOperator playbackOperator)
	{
		ArgumentNullException.ThrowIfNull(playbackOperator);
		return _operators.Remove(playbackOperator);
	}
}

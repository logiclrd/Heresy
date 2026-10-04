using System;

namespace Heresy.Core.Timing;

/// <summary>
/// Mutable timing state shared by all channels participating in one flattened
/// sequencing context. A nested mixdown receives a cloned state; a flattened
/// child shares the parent's state.
/// </summary>
public sealed class SequencingState
{
	private double _tempo;
	private int _speed;

	public SequencingState(

		double tempo = SequencingConstants.DefaultTempo,
		int speed = SequencingConstants.DefaultSpeed)
	{
		Tempo = tempo;
		Speed = speed;
	}

	/// <summary>Ticks per 2.5-second diachron.</summary>
	public double Tempo
	{
		get => _tempo;
		set
		{
			if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
				throw new ArgumentOutOfRangeException(nameof(value));
			_tempo = value;
		}
	}

	/// <summary>Ticks per row.</summary>
	public int Speed
	{
		get => _speed;
		set
		{
			if (value <= 0)
				throw new ArgumentOutOfRangeException(nameof(value));
			_speed = value;
		}
	}

	public TimeSpan TickDuration => TimeSpan.FromSeconds(SequencingConstants.Diachron.TotalSeconds / Tempo);

	public TimeSpan RowDuration => TimeSpan.FromSeconds(TickDuration.TotalSeconds * Speed);

	public SequencingState Clone() => new(Tempo, Speed);
}

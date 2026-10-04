using System;

using Heresy.Core.Timing;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Per-invocation sequencing context. Flattened child sequencers share State;
/// mixdown children receive a fork whose State begins with the parent's current
/// values but subsequently changes independently.
/// </summary>
public sealed class SequencingContext
{
	public const ulong DefaultRootRandomSeed = 0x484552455359UL; // "HERESY"

	public SequencingContext(
		SequencingState? state = null,
		DeterministicRandom? random = null,
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0)
	{
		State = state ?? new SequencingState();
		Random = random ?? new DeterministicRandom(DefaultRootRandomSeed);
		PitchMultiplier = ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier));
		PlaybackSpeedMultiplier = ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier));
	}

	public SequencingState State { get; }
	public DeterministicRandom Random { get; }

	public double PitchMultiplier { get; }
	public double PlaybackSpeedMultiplier { get; }

	public SequencingContext FlattenedChild(double pitchMultiplier = 1.0, double playbackSpeedMultiplier = 1.0)
		=> new(
			State,
			Random.CreateChild(),
			PitchMultiplier * ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier)),
			PlaybackSpeedMultiplier * ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier)));

	public SequencingContext MixdownChild(double pitchMultiplier = 1.0, double playbackSpeedMultiplier = 1.0)
		=> new(
			State.Clone(),
			Random.CreateChild(),
			PitchMultiplier * ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier)),
			PlaybackSpeedMultiplier * ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier)));

	private static double ValidateMultiplier(double value, string paramName)
	{
		if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
			throw new ArgumentOutOfRangeException(paramName);
		return value;
	}
}

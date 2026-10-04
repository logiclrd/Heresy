using System;

using Heresy.Core.Timing;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Per-invocation sequencing context. Flattened child sequencers share State
/// and physical-channel identity with their parent, offset by the channel on
/// which they are flattened. Mixdown children begin a new local channel space
/// while inheriting the parent's current timing state.
/// </summary>
public sealed class SequencingContext
{
	public const ulong DefaultRootRandomSeed = 0x484552455359UL; // "HERESY"

	public SequencingContext(
		SequencingState? state = null,
		DeterministicRandom? random = null,
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		int physicalChannelBase = 0)
	{
		if (physicalChannelBase < 0)
			throw new ArgumentOutOfRangeException(nameof(physicalChannelBase));

		State = state ?? new SequencingState();
		Random = random ?? new DeterministicRandom(DefaultRootRandomSeed);
		PitchMultiplier = ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier));
		PlaybackSpeedMultiplier = ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier));
		PhysicalChannelBase = physicalChannelBase;
	}

	public SequencingState State { get; }
	public DeterministicRandom Random { get; }

	public double PitchMultiplier { get; }
	public double PlaybackSpeedMultiplier { get; }

	/// <summary>
	/// Parent playback channel corresponding to local physical channel zero.
	/// </summary>
	public int PhysicalChannelBase { get; }

	public int MapPhysicalChannel(int localChannel)
	{
		if (localChannel < 0)
			throw new ArgumentOutOfRangeException(nameof(localChannel));

		return checked(PhysicalChannelBase + localChannel);
	}

	public ChannelTarget MapTarget(ChannelTarget target)
		=> target.Kind == ChannelTargetKind.Physical
			? ChannelTarget.Physical(MapPhysicalChannel(target.PhysicalChannel))
			: target;

	public SequencingContext FlattenedChild(
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		int physicalChannelOffset = 0)
	{
		if (physicalChannelOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(physicalChannelOffset));

		return new(
			State,
			Random.CreateChild(),
			PitchMultiplier * ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier)),
			PlaybackSpeedMultiplier * ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier)),
			checked(PhysicalChannelBase + physicalChannelOffset));
	}

	public SequencingContext MixdownChild(double pitchMultiplier = 1.0, double playbackSpeedMultiplier = 1.0)
		=> new(
			State.Clone(),
			Random.CreateChild(),
			PitchMultiplier * ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier)),
			PlaybackSpeedMultiplier * ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier)),
			physicalChannelBase: 0);

	private static double ValidateMultiplier(double value, string paramName)
	{
		if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
			throw new ArgumentOutOfRangeException(paramName);
		return value;
	}
}

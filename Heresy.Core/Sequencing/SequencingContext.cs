using System;

using Heresy.Core.Diagnostics;
using Heresy.Core.Timing;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Per-invocation sequencing context. Flattened children share only the
/// global tempo/speed clock with their parent: their logical Source and
/// tracker-effect channel memory belongs to the child invocation, even
/// though its note events are mapped onto physical playback host channels.
/// Sequence order Patterns reuse this context and therefore retain memory.
/// Private mixdown children also have independent channel memory and state.
/// </summary>
public sealed class SequencingContext
{
	public const ulong DefaultRootRandomSeed = 0x484552455359UL; // "HERESY"

	public SequencingContext(
		SequencingState? state = null,
		DeterministicRandom? random = null,
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		int physicalChannelBase = 0,
		SequencingChannelStateMap? channelStates = null,
		TrackerMidiMacroConfiguration? trackerMidiMacros = null,
		SequencingDiagnosticLog? diagnostics = null,
		double gainMultiplier = 1.0,
		bool useLocalChannelMemory = false)
	{
		if (physicalChannelBase < 0)
			throw new ArgumentOutOfRangeException(nameof(physicalChannelBase));

		State = state ?? new SequencingState();
		Random = random ?? new DeterministicRandom(DefaultRootRandomSeed);
		PitchMultiplier = ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier));
		PlaybackSpeedMultiplier = ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier));
		GainMultiplier = ValidateGain(gainMultiplier, nameof(gainMultiplier));
		PhysicalChannelBase = physicalChannelBase;
		ChannelStates = channelStates ?? new SequencingChannelStateMap();
		_useLocalChannelMemory = useLocalChannelMemory;
		TrackerMidiMacros = trackerMidiMacros
			?? TrackerMidiMacroConfiguration.CreateImpulseTrackerDefault();
		Diagnostics = diagnostics ?? new SequencingDiagnosticLog();
	}

	private readonly bool _useLocalChannelMemory;

	public SequencingState State { get; }
	public DeterministicRandom Random { get; }
	public SequencingChannelStateMap ChannelStates { get; }
	public TrackerMidiMacroConfiguration TrackerMidiMacros { get; }

	/// <summary>
	/// Bounded recoverable sequencing warnings, shared by child contexts.
	/// </summary>
	public SequencingDiagnosticLog Diagnostics { get; }

	public double PitchMultiplier { get; }
	public double PlaybackSpeedMultiplier { get; }
	/// <summary>Invoked source amplitude, independent of shared tracker volume memory.</summary>
	public double GainMultiplier { get; }

	/// <summary>
	/// Parent playback channel corresponding to local physical channel zero.
	/// </summary>
	public int PhysicalChannelBase { get; }

	/// <summary>
	/// Preparation-stage expander shared by flattened child contexts, if
	/// provided by the song compiler.
	/// </summary>
	public IFlattenedNoteSourceExpander? FlattenedSourceExpander { get; set; }

	/// <summary>
	/// Data patterns normally expose their already-resolved raw note events.
	/// The song compiler enables deferred Source-column resolution so source
	/// memory is read when each row executes, including nested child changes.
	/// </summary>
	public bool ResolvePatternSourcesAtRowTime { get; set; }

	/// <summary>
	/// Absolute timeline origin while sequencers generate local-time notes.
	/// </summary>
	public TimeSpan TimelineOrigin { get; set; }

	/// <summary>Shared mailbox for future nested timing changes.</summary>
	public DeferredTempoEventQueue DeferredTempoEvents { get; private set; } = new();

	/// <summary>
	/// Nested compilers produce the child schedule but must not consume
	/// parent-time events while compiling their own future rows.
	/// </summary>
	public bool IsPreparingFlattenedChild { get; set; }


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

	/// <summary>The local index selects independent logical state for
	/// flattened children, not a state indexed by its playback host.</summary>
	public SequencingChannelState GetPhysicalChannelState(int localChannel)
		=> ChannelStates.GetPhysical(_useLocalChannelMemory
			? CheckedLocalChannel(localChannel)
			: MapPhysicalChannel(localChannel));

	private static int CheckedLocalChannel(int channel)
		=> channel >= 0 ? channel
			: throw new ArgumentOutOfRangeException(nameof(channel));

	public SequencingContext FlattenedChild(
		double pitchMultiplier = 1.0,
		double playbackSpeedMultiplier = 1.0,
		int physicalChannelOffset = 0,
		double gainMultiplier = 1.0)
	{
		if (physicalChannelOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(physicalChannelOffset));

		return new SequencingContext(
			State,
			Random.CreateChild(),
			PitchMultiplier * ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier)),
			PlaybackSpeedMultiplier * ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier)),
			checked(PhysicalChannelBase + physicalChannelOffset),
			new SequencingChannelStateMap(),
			TrackerMidiMacros,
			Diagnostics,
			ValidateGain(GainMultiplier * ValidateGain(gainMultiplier, nameof(gainMultiplier)),
				nameof(gainMultiplier)),
			useLocalChannelMemory: true)
		{
			FlattenedSourceExpander = FlattenedSourceExpander,
			ResolvePatternSourcesAtRowTime = ResolvePatternSourcesAtRowTime,
			TimelineOrigin = TimelineOrigin,
			DeferredTempoEvents = DeferredTempoEvents,
			IsPreparingFlattenedChild = true,
		};
	}

	public SequencingContext MixdownChild(double pitchMultiplier = 1.0, double playbackSpeedMultiplier = 1.0)
		=> new(
			State.Clone(),
			Random.CreateChild(),
			PitchMultiplier * ValidateMultiplier(pitchMultiplier, nameof(pitchMultiplier)),
			PlaybackSpeedMultiplier * ValidateMultiplier(playbackSpeedMultiplier, nameof(playbackSpeedMultiplier)),
			physicalChannelBase: 0,
			channelStates: new SequencingChannelStateMap(),
			trackerMidiMacros: TrackerMidiMacros,
			diagnostics: Diagnostics,
			gainMultiplier: GainMultiplier);

	private static double ValidateGain(double value, string paramName)
	{
		if (value < 0.0 || !double.IsFinite(value))
			throw new ArgumentOutOfRangeException(paramName);
		return value;
	}

	private static double ValidateMultiplier(double value, string paramName)
	{
		if (!(value > 0.0) || double.IsNaN(value) || double.IsInfinity(value))
			throw new ArgumentOutOfRangeException(paramName);
		return value;
	}
}

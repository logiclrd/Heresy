using System;

using Heresy.Core.Objects;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Atomic operation carried by a note event. The command vocabulary is expected
/// to grow as effects are implemented; grouping several commands in one event
/// preserves their atomic emission ordering.
/// </summary>
public abstract record NoteCommand;

public sealed record StartNoteCommand(
	ObjectId SourceId,
	double PitchMultiplier = 1.0,
	double PlaybackSpeedMultiplier = 1.0,
	bool Mixdown = false) : NoteCommand;

public sealed record NoteOffCommand : NoteCommand;

public sealed record NoteCutCommand : NoteCommand;

public sealed record SetTempoCommand(double TicksPerDiachron) : NoteCommand;

public sealed record SetSpeedCommand(int TicksPerRow) : NoteCommand;

/// <summary>Sets the per-note volume used by the current playback voice.</summary>
public sealed record SetNoteVolumeCommand(double Volume) : NoteCommand;

/// <summary>Sets the persistent overall volume belonging to the playback channel.</summary>
public sealed record SetOverallChannelVolumeCommand(double Volume) : NoteCommand;

/// <summary>Sets the current playback frequency in Hz.</summary>
public sealed record SetPlaybackFrequencyCommand(double Frequency) : NoteCommand;

/// <summary>Moves the current source directly to a new playback time offset.</summary>
public sealed record SetPlaybackOffsetCommand(TimeSpan Offset) : NoteCommand;

/// <summary>
/// Raw tracker-style vibrato operation. Parameter packs speed in the high
/// nibble and depth in the low nibble. Zero nibbles use channel effect memory.
/// The common pattern processor resolves this into SetVibratoCommand.
/// </summary>
public sealed record ApplyVibratoCommand(byte Parameter) : NoteCommand;

/// <summary>
/// Resolved normal vibrato parameters in tracker units. The renderer will turn
/// this into the actual pitch modulation once tick timing and waveform state
/// are available.
/// </summary>
public sealed record SetVibratoCommand(byte Speed, byte Depth) : NoteCommand;

/// <summary>
/// Removes the transient pitch modulation installed for the preceding row and
/// resets its pitch-delta contribution to zero.
/// </summary>
public sealed record ClearPitchModulationCommand : NoteCommand;

/// <summary>
/// Sets persistent normalized resonant low-pass filter parameters on a physical
/// playback channel.
/// </summary>
public sealed record SetResonantFilterCommand(
	double Cutoff,
	double Resonance) : NoteCommand;

/// <summary>
/// Applies a continuous pitch slide measured in IT linear pitch units per
/// legacy tracker tick. Positive values raise pitch; negative values lower it.
/// </summary>
public sealed record SetPitchSlideCommand(
	double LinearUnitsPerTick) : NoteCommand;

/// <summary>Stops the active pitch slide while preserving its accumulated pitch.</summary>
public sealed record ClearPitchSlideCommand : NoteCommand;

/// <summary>
/// Applies a continuous note-volume slide in tracker volume units per legacy
/// tick, where 64 units span the normalized [0,1] note-volume range.
/// </summary>
public sealed record SetNoteVolumeSlideCommand(
	double TrackerUnitsPerTick) : NoteCommand;

/// <summary>Stops the active note-volume slide while preserving its accumulated volume.</summary>
public sealed record ClearNoteVolumeSlideCommand : NoteCommand;

/// <summary>Raw tracker Dxy volume-slide operation with whole-byte effect memory.</summary>
public sealed record ApplyVolumeSlideCommand(byte Parameter) : NoteCommand;

/// <summary>Raw tracker Exx pitch-slide-down operation with shared E/F memory.</summary>
public sealed record ApplyPitchSlideDownCommand(byte Parameter) : NoteCommand;

/// <summary>Raw tracker Fxx pitch-slide-up operation with shared E/F memory.</summary>
public sealed record ApplyPitchSlideUpCommand(byte Parameter) : NoteCommand;

/// <summary>Applies one immediate persistent pitch change in IT linear units.</summary>
public sealed record AdjustPitchLinearUnitsCommand(
	double LinearUnits) : NoteCommand;

/// <summary>Applies one immediate persistent note-volume change in tracker units.</summary>
public sealed record AdjustNoteVolumeCommand(
	double TrackerUnits) : NoteCommand;

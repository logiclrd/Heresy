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
public sealed record SetVibratoCommand(
	byte Speed,
	byte Depth,
	TrackerWaveform Waveform = TrackerWaveform.Sine) : NoteCommand;

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
	double LinearUnitsPerTick,
	int? TicksPerRow = null) : NoteCommand;

/// <summary>Stops the active pitch slide while preserving its accumulated pitch.</summary>
public sealed record ClearPitchSlideCommand : NoteCommand;

/// <summary>
/// Applies a continuous note-volume slide in tracker volume units per legacy
/// tick, where 64 units span the normalized [0,1] note-volume range.
/// </summary>
public sealed record SetNoteVolumeSlideCommand(
	double TrackerUnitsPerTick,
	int? TicksPerRow = null) : NoteCommand;

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

/// <summary>
/// Raw tracker Gxx tone-portamento operation. TargetNote is present when the
/// same tracker cell also contains a note; an active voice uses only its target
/// pitch, while an empty channel may use the full note as a fallback start.
/// </summary>
public sealed record ApplyTonePortamentoCommand(
	byte Parameter,
	StartNoteCommand? TargetNote = null) : NoteCommand;

/// <summary>
/// Resolved tone portamento. LinearUnitsPerTick is non-negative. TargetNote is
/// null when continuing toward the previously established target.
/// </summary>
public sealed record SetTonePortamentoCommand(
	double LinearUnitsPerTick,
	StartNoteCommand? TargetNote = null,
	int? TicksPerRow = null,
	bool Glissando = false) : NoteCommand;

/// <summary>
/// Stops tone-portamento movement for the row while retaining its target.
/// </summary>
public sealed record ClearTonePortamentoCommand : NoteCommand;

/// <summary>Raw tracker Jxy arpeggio operation with whole-byte memory.</summary>
public sealed record ApplyArpeggioCommand(byte Parameter) : NoteCommand;

/// <summary>Resolved tracker arpeggio semitone offsets.</summary>
public sealed record SetArpeggioCommand(
	byte FirstSemitones,
	byte SecondSemitones) : NoteCommand;

/// <summary>Stops the row-scoped arpeggio modulation.</summary>
public sealed record ClearArpeggioCommand : NoteCommand;

/// <summary>
/// Raw tracker Rxy tremolo. Zero nibbles independently recall speed/depth.
/// </summary>
public sealed record ApplyTremoloCommand(byte Parameter) : NoteCommand;

/// <summary>Resolved normal tremolo parameters in tracker units.</summary>
public sealed record SetTremoloCommand(
	byte Speed,
	byte Depth,
	TrackerWaveform Waveform = TrackerWaveform.Sine) : NoteCommand;

/// <summary>Stops transient tremolo modulation without resetting its phase.</summary>
public sealed record ClearTremoloCommand : NoteCommand;

/// <summary>Raw tracker Qxy retrigger operation with whole-byte memory.</summary>
public sealed record ApplyRetriggerCommand(byte Parameter) : NoteCommand;

/// <summary>
/// Restarts the current voice's source playback without reallocating the voice.
/// VolumeTransform is the tracker Qxy high nibble (0..15).
/// </summary>
public sealed record RetriggerCurrentVoiceCommand(
	byte VolumeTransform) : NoteCommand;

/// <summary>Raw tracker Oxx sample-offset operation with whole-byte memory.</summary>
public sealed record ApplySampleOffsetCommand(byte Parameter) : NoteCommand;

/// <summary>
/// Sets the current source's native playback offset in source frames.
/// </summary>
public sealed record SetSourceFrameOffsetCommand(
	long SourceFrameOffset) : NoteCommand;

/// <summary>
/// Raw tracker SAx high-order sample-offset operation. The value is the
/// persistent high nibble used by later Oxx sample-offset commands.
/// </summary>
public sealed record ApplySampleOffsetHighCommand(
	byte HighOffset) : NoteCommand;

/// <summary>
/// Raw tracker SCx note cut. Tick is the low S-command nibble (0..15).
/// </summary>
public sealed record ApplyTrackerNoteCutCommand(
	byte Tick) : NoteCommand;

/// <summary>
/// Raw tracker SDx note delay. Tick is the low S-command nibble (0..15).
/// </summary>
public sealed record ApplyTrackerNoteDelayCommand(
	byte Tick) : NoteCommand;

/// <summary>
/// Raw tracker SEx pattern delay. ExtraRows is the number of additional
/// tracker-row spans for which the current row remains active.
/// </summary>
public sealed record ApplyTrackerPatternDelayCommand(
	byte ExtraRows) : NoteCommand;

/// <summary>
/// Raw tracker SBx pattern-loop control. A zero count marks the current row as
/// this physical channel's loop start; a non-zero count repeats from that start.
/// </summary>
public sealed record ApplyTrackerPatternLoopCommand(
	byte RepeatCount) : NoteCommand;

/// <summary>
/// Raw tracker S3x vibrato-waveform selection. Values 0..3 select a waveform;
/// higher nibble values are valid tracker data but ignored by Impulse Tracker.
/// </summary>
public sealed record ApplyTrackerVibratoWaveformCommand(
	byte Value) : NoteCommand;

/// <summary>
/// Raw tracker S4x tremolo-waveform selection. Values 0..3 select a waveform;
/// higher nibble values are valid tracker data but ignored by Impulse Tracker.
/// </summary>
public sealed record ApplyTrackerTremoloWaveformCommand(
	byte Value) : NoteCommand;

/// <summary>
/// Raw tracker S6x fine-pattern delay. ExtraTicks extends the current row by
/// that many tracker ticks; multiple S6x values on one row are cumulative.
/// </summary>
public sealed record ApplyTrackerFinePatternDelayCommand(
	byte ExtraTicks) : NoteCommand;

/// <summary>
/// Raw tracker S1x glissando control. Zero disables semitone clamping;
/// non-zero values enable it.
/// </summary>
public sealed record ApplyTrackerGlissandoControlCommand(
	byte Value) : NoteCommand;

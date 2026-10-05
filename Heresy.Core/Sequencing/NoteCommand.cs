using System;
using System.Numerics;

using Heresy.Core.Envelopes;
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

/// <summary>
/// Enables or disables one voice envelope without resetting its playback
/// position. Disabled envelopes hold their current value and resume from the
/// held position when re-enabled.
/// </summary>
public sealed record SetEnvelopeEnabledCommand(
	EnvelopeTarget Target,
	bool Enabled) : NoteCommand;

/// <summary>
/// Raw tracker S77-S7C envelope control. PatternNoteProcessor translates the
/// shared IT pitch/filter slot into Heresy's independent semantic targets.
/// </summary>
public sealed record ApplyTrackerEnvelopeControlCommand(
	TrackerEnvelopeControlTarget Target,
	bool Enabled) : NoteCommand;

public sealed record NoteCutCommand : NoteCommand;

/// <summary>
/// Raw tracker Bxx order jump. PatternNoteProcessor consumes this as
/// sequencing control; it is never emitted to playback.
/// </summary>
public sealed record ApplyTrackerOrderJumpCommand(byte Order) : NoteCommand;

/// <summary>
/// Raw tracker Cxx pattern break. PatternNoteProcessor consumes this as
/// sequencing control; it is never emitted to playback.
/// </summary>
public sealed record ApplyTrackerPatternBreakCommand(byte Row) : NoteCommand;

public sealed record SetTempoCommand(double TicksPerDiachron) : NoteCommand;

/// <summary>
/// Smooth tempo transition whose tempo is linear in continuous tracker-tick
/// position. The command begins at its event time and reaches EndingTempo
/// after TrackerTicks tracker ticks.
/// </summary>
public sealed record SetTempoRampCommand : NoteCommand
{
	public SetTempoRampCommand(
		double endingTempo,
		double trackerTicks = 1.0)
	{
		if (!(endingTempo > 0.0)
			|| double.IsNaN(endingTempo)
			|| double.IsInfinity(endingTempo))
		{
			throw new ArgumentOutOfRangeException(nameof(endingTempo));
		}
		if (!(trackerTicks > 0.0)
			|| double.IsNaN(trackerTicks)
			|| double.IsInfinity(trackerTicks))
		{
			throw new ArgumentOutOfRangeException(nameof(trackerTicks));
		}

		EndingTempo = endingTempo;
		TrackerTicks = trackerTicks;
	}

	public double EndingTempo { get; }
	public double TrackerTicks { get; }
}

/// <summary>
/// Raw tracker Txx tempo operation. Memory and slide/set interpretation are
/// resolved by the common pattern processor because Txx changes tick timing.
/// </summary>
public sealed record ApplyTrackerTempoCommand(byte Parameter) : NoteCommand;

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
/// Raw tracker Uxy fine-vibrato operation. Speed/depth nibble memory is shared
/// with Hxy vibrato.
/// </summary>
public sealed record ApplyFineVibratoCommand(byte Parameter) : NoteCommand;

/// <summary>
/// Raw tracker Kxx vibrato-plus-volume-slide operation. Parameter memory is
/// shared with Dxx/Lxx; vibrato parameters come from current Hxx/Uxx state.
/// </summary>
public sealed record ApplyVibratoVolumeSlideCommand(
	byte Parameter) : NoteCommand;

/// <summary>
/// Resolved normal vibrato parameters in tracker units. The renderer will turn
/// this into the actual pitch modulation once tick timing and waveform state
/// are available.
/// </summary>
public sealed record SetVibratoCommand(
	byte Speed,
	byte Depth,
	TrackerWaveform Waveform = TrackerWaveform.Sine,
	double DepthScale = 1.0) : NoteCommand;

/// <summary>
/// Removes the transient pitch modulation installed for the preceding row and
/// resets its pitch-delta contribution to zero.
/// </summary>
public sealed record ClearPitchModulationCommand : NoteCommand;

/// <summary>Raw tracker SFx per-channel MIDI-macro selection.</summary>
public sealed record ApplyTrackerMidiMacroSelectCommand(byte Macro) : NoteCommand;

/// <summary>Raw tracker Zxx MIDI-macro invocation.</summary>
public sealed record ApplyTrackerMidiMacroCommand(byte Parameter) : NoteCommand;

/// <summary>
/// Sets persistent normalized resonant low-pass filter parameters on a physical
/// playback channel.
/// </summary>
public sealed record SetResonantFilterCommand(
	double Cutoff,
	double Resonance) : NoteCommand;

/// <summary>Sets only the persistent resonant-filter cutoff.</summary>
public sealed record SetResonantFilterCutoffCommand(
	double Cutoff) : NoteCommand;

/// <summary>Sets only the persistent resonant-filter resonance.</summary>
public sealed record SetResonantFilterResonanceCommand(
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
/// Raw tracker Lxx tone-portamento-plus-volume-slide operation. Parameter
/// belongs to Dxx/Kxx/Lxx volume memory; tone speed comes from Gxx memory.
/// </summary>
public sealed record ApplyTonePortamentoVolumeSlideCommand(
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

/// <summary>Raw tracker Ixy tremor operation with whole-byte memory.</summary>
public sealed record ApplyTremorCommand(byte Parameter) : NoteCommand;

/// <summary>
/// Activates a physical-channel tremor gate for one row span. Phase state
/// persists between activations; OnTicks and OffTicks are both positive.
/// </summary>
public sealed record SetTremorCommand(
	byte OnTicks,
	byte OffTicks,
	int? TicksPerRow = null) : NoteCommand;

/// <summary>
/// Deactivates physical-channel tremor gating while preserving its phase.
/// </summary>
public sealed record ClearTremorCommand : NoteCommand;

/// <summary>Raw tracker Yxy panbrello operation with nibble-wise memory.</summary>
public sealed record ApplyPanbrelloCommand(byte Parameter) : NoteCommand;

/// <summary>
/// Activates smooth physical-channel panbrello around the persistent base
/// position while preserving IT-compatible tick anchors.
/// </summary>
public sealed record SetPanbrelloCommand(
	byte Speed,
	byte Depth,
	TrackerWaveform Waveform = TrackerWaveform.Sine,
	int? TicksPerRow = null) : NoteCommand;

/// <summary>
/// Stops panbrello phase advancement while retaining the last applied offset.
/// </summary>
public sealed record ClearPanbrelloCommand : NoteCommand;

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
/// Raw tracker S5x panbrello-waveform selection. Values 4..15 are valid
/// tracker data but ignored by Impulse Tracker.
/// </summary>
public sealed record ApplyTrackerPanbrelloWaveformCommand(
	byte Value) : NoteCommand;

/// <summary>
/// Selects the panbrello waveform and resets its runtime phase to zero while
/// preserving any currently held panbrello offset.
/// </summary>
public sealed record SetPanbrelloWaveformCommand(
	TrackerWaveform Waveform) : NoteCommand;

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

/// <summary>
/// Raw tracker S70/S71/S72 past-note action.
/// </summary>
public sealed record ApplyTrackerPastNoteActionCommand(
	TrackerPastNoteAction Action) : NoteCommand;

/// <summary>
/// Resolved operation over past/NNA voices belonging to the target physical
/// tracker channel. The current physical voice is intentionally excluded.
/// </summary>
public sealed record ApplyPastNoteActionCommand(
	TrackerPastNoteAction Action) : NoteCommand;

/// <summary>
/// Raw tracker S73/S74/S75/S76 new-note-action override.
/// </summary>
public sealed record ApplyTrackerNewNoteActionCommand(
	NoteDisplacementAction Action) : NoteCommand;

/// <summary>
/// Generic resolved operation that changes how the current voice will be
/// displaced by the next note. A later note starts with a fresh source
/// snapshot rather than inheriting this override.
/// </summary>
public sealed record SetCurrentVoiceDisplacementActionCommand(
	NoteDisplacementAction Action) : NoteCommand;

/// <summary>
/// Raw tracker S8x 4-bit panning value.
/// </summary>
public sealed record ApplyTrackerPanningCommand : NoteCommand
{
	public ApplyTrackerPanningCommand(byte value)
	{
		if (value > 0x0F)
			throw new ArgumentOutOfRangeException(nameof(value));

		Value = value;
	}

	public byte Value { get; }
}

/// <summary>
/// Sets the persistent spatial position of a physical playback channel and
/// its currently attached voice.
/// </summary>
public sealed record SetSpatialPositionCommand : NoteCommand
{
	public SetSpatialPositionCommand(Vector3 position)
	{
		if (!float.IsFinite(position.X)
			|| !float.IsFinite(position.Y)
			|| !float.IsFinite(position.Z))
		{
			throw new ArgumentOutOfRangeException(nameof(position));
		}

		Position = position;
	}

	public Vector3 Position { get; }
}

/// <summary>
/// Enables or disables persistent surround routing on a physical playback
/// channel. Tracker S91 enables it; ordinary absolute panning disables it.
/// </summary>
public sealed record SetSurroundCommand(bool Enabled) : NoteCommand;

/// <summary>
/// Raw tracker Xxx 8-bit panning value.
/// </summary>
public sealed record ApplyTrackerPanning8BitCommand(
	byte Parameter) : NoteCommand;

/// <summary>Raw tracker Pxx panning-slide operation with whole-byte memory.</summary>
public sealed record ApplyPanningSlideCommand(
	byte Parameter) : NoteCommand;

/// <summary>
/// Applies an immediate bounded adjustment to the physical channel's spatial X
/// position.
/// </summary>
public sealed record AdjustSpatialXCommand(
	double DeltaX,
	double MinimumX,
	double MaximumX) : NoteCommand;

/// <summary>
/// Applies a continuous bounded spatial-X slide in spatial units per legacy
/// tracker tick.
/// </summary>
public sealed record SetSpatialXSlideCommand(
	double SpatialUnitsPerTick,
	int? TicksPerRow = null,
	double MinimumX = -1.0,
	double MaximumX = 1.0) : NoteCommand;

/// <summary>
/// Stops the active spatial-X slide while preserving the accumulated position.
/// </summary>
public sealed record ClearSpatialXSlideCommand : NoteCommand;

/// <summary>
/// Raw tracker Mxx channel-volume operation. Values above 64 are valid pattern
/// data but are ignored by Impulse Tracker.
/// </summary>
public sealed record ApplyTrackerChannelVolumeCommand(
	byte Parameter) : NoteCommand;

/// <summary>Raw tracker Nxx channel-volume slide with whole-byte memory.</summary>
public sealed record ApplyChannelVolumeSlideCommand(
	byte Parameter) : NoteCommand;

/// <summary>
/// Applies an immediate adjustment to persistent playback-channel volume in
/// tracker units, where 64 units span the normalized [0,1] range.
/// </summary>
public sealed record AdjustOverallChannelVolumeCommand(
	double TrackerUnits) : NoteCommand;

/// <summary>
/// Applies a continuous playback-channel volume slide in tracker units per
/// legacy tick.
/// </summary>
public sealed record SetOverallChannelVolumeSlideCommand(
	double TrackerUnitsPerTick,
	int? TicksPerRow = null) : NoteCommand;

/// <summary>
/// Stops the active playback-channel volume slide while preserving its
/// accumulated volume.
/// </summary>
public sealed record ClearOverallChannelVolumeSlideCommand : NoteCommand;

/// <summary>
/// Raw tracker Vxx global-volume operation. Values above 128 are valid pattern
/// data but are ignored by Impulse Tracker.
/// </summary>
public sealed record ApplyTrackerGlobalVolumeCommand(
	byte Parameter) : NoteCommand;

/// <summary>Sets normalized session-wide output gain.</summary>
public sealed record SetGlobalVolumeCommand : NoteCommand
{
	public SetGlobalVolumeCommand(double volume)
	{
		if (double.IsNaN(volume)
			|| double.IsInfinity(volume)
			|| volume < 0.0
			|| volume > 1.0)
		{
			throw new ArgumentOutOfRangeException(nameof(volume));
		}

		Volume = volume;
	}

	public double Volume { get; }
}

/// <summary>
/// Raw tracker Wxx global-volume slide. It remains on its physical-channel
/// target so W00 memory and same-timestamp channel ordering are preserved.
/// </summary>
public sealed record ApplyGlobalVolumeSlideCommand(
	byte Parameter) : NoteCommand;

/// <summary>
/// Immediately adjusts session-wide gain in tracker global-volume units,
/// where 128 units span the normalized [0,1] range.
/// </summary>
public sealed record AdjustGlobalVolumeCommand(
	double TrackerUnits) : NoteCommand;

/// <summary>
/// Registers a continuous session-wide volume slide contributed by the
/// originating physical channel.
/// </summary>
public sealed record SetGlobalVolumeSlideCommand(
	double TrackerUnitsPerTick,
	int? TicksPerRow = null) : NoteCommand;

/// <summary>
/// Stops the originating physical channel's active contribution to global
/// volume sliding while preserving the accumulated session-wide volume.
/// </summary>
public sealed record ClearGlobalVolumeSlideCommand : NoteCommand;

using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;

namespace Heresy.Core.Patterns;

/// <summary>
/// A non-mixdown Pattern/Sequence start represents multiple future voices,
/// not one playing voice. Drop individual-voice effects before resolution
/// or tracker-tick scheduling, while leaving persisted data unchanged.
/// </summary>
public static class FlattenedSourceEffectPolicy
{
	public static bool IsVoiceSpecific(PatternEffect effect)
	{
		ArgumentNullException.ThrowIfNull(effect);
		return effect is
			SetPlaybackFrequencyPatternEffect or SetPlaybackOffsetPatternEffect
			or VibratoPatternEffect or FineVibratoPatternEffect
			or VibratoVolumeSlidePatternEffect
			// Kxx/Lxx contain a valid source-note volume slide; their
			// incompatible pitch component is ignored separately.
			or PitchSlidePatternEffect or TrackerPitchSlideDownPatternEffect
			or TrackerPitchSlideUpPatternEffect
			or TonePortamentoPatternEffect or TonePortamentoVolumeSlidePatternEffect
			or ArpeggioPatternEffect or TremoloPatternEffect
			or TremorPatternEffect or PanbrelloPatternEffect
			or RetriggerPatternEffect or SampleOffsetPatternEffect
			or SampleOffsetHighPatternEffect or TrackerNoteCutPatternEffect
			or TrackerVibratoWaveformPatternEffect
			or TrackerTremoloWaveformPatternEffect
			or TrackerPanbrelloWaveformPatternEffect
			or TrackerGlissandoControlPatternEffect
			or TrackerPastNoteActionPatternEffect
			or TrackerNewNoteActionPatternEffect
			or TrackerEnvelopeControlPatternEffect
			or TrackerVolumeColumnPatternEffect
				{ Kind: TrackerVolumeColumnEffectKind.PitchSlideDown
					or TrackerVolumeColumnEffectKind.PitchSlideUp
					or TrackerVolumeColumnEffectKind.TonePortamento
					or TrackerVolumeColumnEffectKind.Vibrato };
	}

	public static bool IsVoiceSpecific(NoteCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		return command is
			SetPlaybackFrequencyCommand or SetPlaybackOffsetCommand
			or ApplyVibratoCommand or ApplyFineVibratoCommand
			or ApplyVibratoVolumeSlideCommand
			or SetVibratoCommand or ClearPitchModulationCommand
			or SetPitchSlideCommand or ClearPitchSlideCommand
			or ApplyPitchSlideDownCommand or ApplyPitchSlideUpCommand
			or AdjustPitchLinearUnitsCommand
			or ApplyTonePortamentoCommand or ApplyTonePortamentoVolumeSlideCommand
			or SetTonePortamentoCommand or ClearTonePortamentoCommand
			or ApplyArpeggioCommand or SetArpeggioCommand or ClearArpeggioCommand
			or ApplyTremoloCommand or SetTremoloCommand or ClearTremoloCommand
			or ApplyTremorCommand or SetTremorCommand or ClearTremorCommand
			or ApplyPanbrelloCommand or SetPanbrelloCommand or ClearPanbrelloCommand
			or ApplyRetriggerCommand or RetriggerCurrentVoiceCommand
			or ApplySampleOffsetCommand or SetSourceFrameOffsetCommand
			or ApplySampleOffsetHighCommand or ApplyTrackerNoteCutCommand
			or ApplyTrackerVibratoWaveformCommand
			or ApplyTrackerTremoloWaveformCommand
			or ApplyTrackerPanbrelloWaveformCommand
			or SetPanbrelloWaveformCommand
			or ApplyTrackerGlissandoControlCommand
			or ApplyTrackerPastNoteActionCommand or ApplyPastNoteActionCommand
			or ApplyTrackerNewNoteActionCommand
			or SetCurrentVoiceDisplacementActionCommand
			or ApplyTrackerEnvelopeControlCommand or SetEnvelopeEnabledCommand
			or ApplyTrackerVolumeColumnCommand
				{ Kind: TrackerVolumeColumnEffectKind.PitchSlideDown
					or TrackerVolumeColumnEffectKind.PitchSlideUp
					or TrackerVolumeColumnEffectKind.TonePortamento
					or TrackerVolumeColumnEffectKind.Vibrato };
	}

	public static NoteEvent Filter(NoteEvent note, SequencingContext context)
	{
		if (context.IsFlattenedSource is null
			|| note.Target.Kind != ChannelTargetKind.Physical)
			return note;

		SequencingChannelState channel = context.GetPhysicalChannelState(
			note.Target.PhysicalChannel);
		ObjectId source = channel.CurrentSourceId;
		StartNoteCommand? start = null;
		bool hasNewStart = false;
		foreach (NoteCommand command in note.Commands)
		{
			if (command is SelectPatternSourceCommand selected)
				source = selected.SourceId;
			if (command is StartNoteCommand candidate)
			{
				hasNewStart = true;
				if (!candidate.Mixdown)
					start = candidate;
			}
		}
		if (start is not null && !start.SourceId.IsNone)
			source = start.SourceId;
		bool controlsFlattenedSource = hasNewStart
			? start is not null && !source.IsNone
				&& context.IsFlattenedSource(source)
			: channel.ActiveFlattenedSourceScopeId > 0;
		if (!controlsFlattenedSource)
			return note;

		List<NoteCommand>? filtered = null;
		for (int index = 0; index < note.Commands.Count; index++)
		{
			NoteCommand command = note.Commands[index];
			if (!IsVoiceSpecific(command))
			{
				filtered?.Add(command);
				continue;
			}
			if (filtered is null)
			{
				filtered = new List<NoteCommand>(note.Commands.Count);
				for (int j = 0; j < index; j++)
					filtered.Add(note.Commands[j]);
			}
			switch (command)
			{
				case ApplyVibratoVolumeSlideCommand combined:
					filtered.Add(new ApplyVolumeSlideCommand(combined.Parameter));
					break;
				case ApplyTonePortamentoVolumeSlideCommand combined:
					filtered.Add(new ApplyVolumeSlideCommand(combined.Parameter));
					break;
			}
			context.Diagnostics.ReportIgnoredFlatteningEffect(
				command.GetType().Name, note.Offset.RowOffset);
		}
		return filtered is null ? note : note with { Commands = filtered };
	}
}

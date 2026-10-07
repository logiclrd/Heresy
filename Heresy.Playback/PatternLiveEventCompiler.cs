using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;

namespace Heresy.Playback;

/// <summary>
/// Converts tracker editing gestures into the same live NoteCommand events
/// consumed by PlaybackSession during ordinary realtime rendering.
/// </summary>
public static class PatternLiveEventCompiler
{
	public static LivePlaybackEvent? CompileEnteredNote(
		DataPatternDefinition pattern,
		int row,
		int channel)
	{
		NoteSchedule schedule =
			PatternAuditionCompiler.CompileNote(
				pattern,
				row,
				channel);

		if (schedule.Count == 0)
			return null;

		List<NoteCommand> commands =
			schedule
				.SelectMany(noteEvent => noteEvent.Commands)
				.ToList();
		if (commands.Count == 0)
			return null;

		// Tracker editing is monophonic per physical channel. Override the
		// current voice's displacement to Off before starting the replacement
		// so its release is preserved independently of the source's normal NNA.
		if (commands.Any(command => command is StartNoteCommand))
		{
			commands.Insert(
				0,
				new SetCurrentVoiceDisplacementActionCommand(
					NoteDisplacementAction.Off));
		}

		return new LivePlaybackEvent(
			ChannelTarget.Physical(channel),
			commands);
	}

	public static LivePlaybackEvent? CompileHeldPreviewStart(
		DataPatternDefinition pattern,
		int row,
		int channel,
		uint virtualChannelId,
		double pitchMultiplier)
	{
		StartNoteCommand? command =
			PatternAuditionCompiler.CompileHeldNoteStart(
				pattern,
				row,
				channel,
				pitchMultiplier);
		if (command is null)
			return null;

		return new LivePlaybackEvent(
			ChannelTarget.Virtual(virtualChannelId),
			new NoteCommand[] { command });
	}

	public static LivePlaybackEvent CompileHeldPreviewRelease(
		uint virtualChannelId)
		=> new(
			ChannelTarget.Virtual(virtualChannelId),
			new NoteCommand[] { new NoteOffCommand() });
}

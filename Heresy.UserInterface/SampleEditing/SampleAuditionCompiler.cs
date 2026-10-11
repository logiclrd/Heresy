using System;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;

namespace Heresy.UserInterface.SampleEditing;

/// <summary>Sample preview voices have their own virtual voice IDs and
/// outlive their physical key presses. Explicit Cut is used for the
/// near-note looping safety rule and for F8/closing the dialog.</summary>
public static class SampleAuditionCompiler
{
	public const uint VoiceIdBase = 0x53410000;

	public static LivePlaybackEvent Start(
		ObjectId sampleId, SampleAuditionVoice note, bool restart = false)
	{
		ArgumentNullException.ThrowIfNull(note);
		return new LivePlaybackEvent(
			ChannelTarget.Virtual(note.VoiceId),
			restart
				? new NoteCommand[]
				{
					new NoteCutCommand(),
					new StartNoteCommand(sampleId,
						PitchMultiplier: note.PitchMultiplier),
				}
				: new NoteCommand[]
				{
					new StartNoteCommand(sampleId,
						PitchMultiplier: note.PitchMultiplier),
				});
	}

	public static LivePlaybackEvent Cut(SampleAuditionVoice note)
	{
		ArgumentNullException.ThrowIfNull(note);
		return new LivePlaybackEvent(
			ChannelTarget.Virtual(note.VoiceId),
			[new NoteCutCommand()]);
	}
}

using System;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;
using Heresy.UserInterface.FmEditing;

namespace Heresy.UserInterface.SampleEditing;

/// <summary>Sample Test reuses the FM editor's exact tracker-key pitch,
/// octave and held-key conventions, but owns a separate virtual voice bank.
/// Repeating a held note replaces its old voice before reading new loop
/// metadata from the freshly frozen audio snapshot.</summary>
public static class SampleAuditionCompiler
{
	public const uint VoiceIdBase = 0x53410000;

	public static LivePlaybackEvent Start(
		ObjectId sampleId, StartFmSynthAuditionNote note,
		bool restart = false)
	{
		ArgumentNullException.ThrowIfNull(note);
		return new LivePlaybackEvent(
			ChannelTarget.Virtual(note.VoiceId),
			restart
				? new NoteCommand[] {
					new NoteCutCommand(),
					new StartNoteCommand(sampleId,
						PitchMultiplier: note.PitchMultiplier) }
				: new NoteCommand[] {
					new StartNoteCommand(sampleId,
						PitchMultiplier: note.PitchMultiplier) });
	}

	public static LivePlaybackEvent Release(ReleaseFmSynthAuditionNote note)
	{
		ArgumentNullException.ThrowIfNull(note);
		return new LivePlaybackEvent(
			ChannelTarget.Virtual(note.VoiceId),
			[new NoteOffCommand()]);
	}
}

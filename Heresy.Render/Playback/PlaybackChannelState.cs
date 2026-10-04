using Heresy.Render.Sounds;

namespace Heresy.Render.Playback;

/// <summary>
/// Runtime state for one physical playback/tracker channel. Voice-specific
/// state lives in CurrentVoice so it can migrate intact to a virtual voice.
/// </summary>
public sealed class PlaybackChannelState
{
	internal PlaybackChannelState(int outputChannelCount, int sampleRate)
	{
		AntiClickTail = new AntiClickTail(outputChannelCount, sampleRate);
	}

	public PlaybackVoice? CurrentVoice { get; internal set; }

	/// <summary>
	/// Persistent per-note volume used to initialize a newly attached voice and
	/// kept synchronized with the current physical voice.
	/// </summary>
	public double NoteVolume { get; private set; } = 1.0;

	/// <summary>
	/// Persistent overall playback-channel volume.
	/// </summary>
	public double OverallVolume { get; private set; } = 1.0;

	public AntiClickTail AntiClickTail { get; }

	// Convenience accessors retained while callers migrate to CurrentVoice.
	public ISound? CurrentSound => CurrentVoice?.Sound;
	public SoundState? CurrentSoundState => CurrentVoice?.SoundState;
	public long? NoteStartFrame => CurrentVoice?.StartFrame;

	internal void SetNoteVolume(double volume)
	{
		NoteVolume = volume;
		if (CurrentVoice is not null)
			CurrentVoice.NoteVolume = volume;
	}

	internal void SetOverallVolume(double volume)
	{
		OverallVolume = volume;
		if (CurrentVoice is not null)
			CurrentVoice.OverallVolume = volume;
	}

	internal PlaybackVoice? DetachCurrentVoice()
	{
		PlaybackVoice? voice = CurrentVoice;
		CurrentVoice = null;
		return voice;
	}

	internal void AttachVoice(PlaybackVoice voice)
	{
		CurrentVoice = voice;
		voice.NoteVolume = NoteVolume;
		voice.OverallVolume = OverallVolume;
	}

	internal void CutCurrentVoice()
	{
		PlaybackVoice? voice = DetachCurrentVoice();
		voice?.AddCutTo(AntiClickTail);
	}
}

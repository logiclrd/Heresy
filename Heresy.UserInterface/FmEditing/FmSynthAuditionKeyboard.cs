using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Input;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.FmEditing;

public sealed record StartFmSynthAuditionNote(
	PhysicalKey Key,
	uint VoiceId,
	double PitchMultiplier);

public sealed record ReleaseFmSynthAuditionNote(
	PhysicalKey Key,
	uint VoiceId);

/// <summary>
/// The FM Test area's held-key state. Piano mapping and octave shortcuts
/// deliberately reuse the pattern editor's physical-key conventions, without
/// the pattern editor's momentary Caps Lock requirement.
/// </summary>
public sealed class FmSynthAuditionKeyboard
{
	// Keep FM preview voices disjoint from pattern editor preview voices.
	public const uint VoiceIdBase = 0x464D0000;

	private readonly Dictionary<PhysicalKey, StartFmSynthAuditionNote>
		_activeKeys = [];
	private readonly uint _voiceIdBase;

	public FmSynthAuditionKeyboard(uint voiceIdBase = VoiceIdBase)
		=> _voiceIdBase = voiceIdBase;

	/// <summary>Snapshot the exact held pitches, including their octave at
	/// key-down, so a changed sound source can restart these same notes.</summary>
	public IReadOnlyList<StartFmSynthAuditionNote> ActiveNotes
		=> _activeKeys.Values.ToArray();

	public int BaseOctave { get; private set; } = 4;
	public int ActiveNoteCount => _activeKeys.Count;

	public StartFmSynthAuditionNote? KeyDown(
		PhysicalKey key,
		KeyModifiers modifiers)
	{
		if (PatternOctaveKeyboard.TryAdjust(
			key,
			modifiers,
			BaseOctave,
			out int octave))
		{
			BaseOctave = octave;
			return null;
		}

		if ((modifiers & (KeyModifiers.Control
			| KeyModifiers.Alt | KeyModifiers.Meta)) != 0
			|| !PatternNoteKeyboard.TryGetSemitoneOffset(
				key,
				out int semitone)
			|| _activeKeys.ContainsKey(key))
		{
			return null;
		}

		uint voiceId = _voiceIdBase + (uint)semitone;
		double pitchMultiplier =
			Math.Pow(2.0,
				(((BaseOctave - 4) * 12) + semitone) / 12.0);
		StartFmSynthAuditionNote note =
			new(key, voiceId, pitchMultiplier);
		_activeKeys.Add(key, note);
		return note;
	}

	public ReleaseFmSynthAuditionNote? KeyUp(PhysicalKey key)
	{
		if (!_activeKeys.Remove(key, out StartFmSynthAuditionNote? note))
			return null;
		return new ReleaseFmSynthAuditionNote(key, note.VoiceId);
	}

	public IReadOnlyList<ReleaseFmSynthAuditionNote> ReleaseAll()
	{
		ReleaseFmSynthAuditionNote[] actions = _activeKeys
			.Select(pair =>
				new ReleaseFmSynthAuditionNote(
					pair.Key, pair.Value.VoiceId))
			.ToArray();
		_activeKeys.Clear();
		return actions;
	}
}

/// <summary>
/// Compiles FM audition gestures into the existing realtime live-note
/// transport, without touching the document or introducing new sound paths.
/// </summary>
public static class FmSynthAuditionCompiler
{
	public static LivePlaybackEvent Start(
		ObjectId sourceId,
		StartFmSynthAuditionNote note)
	{
		ArgumentNullException.ThrowIfNull(note);
		return new LivePlaybackEvent(
			ChannelTarget.Virtual(note.VoiceId),
			new NoteCommand[]
			{
				new StartNoteCommand(
					sourceId,
					PitchMultiplier: note.PitchMultiplier),
			});
	}

	public static LivePlaybackEvent Release(
		ReleaseFmSynthAuditionNote note)
	{
		ArgumentNullException.ThrowIfNull(note);
		return new LivePlaybackEvent(
			ChannelTarget.Virtual(note.VoiceId),
			new NoteCommand[] { new NoteOffCommand() });
	}
}

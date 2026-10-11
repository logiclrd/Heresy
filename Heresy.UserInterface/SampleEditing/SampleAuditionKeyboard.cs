using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Input;

using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.SampleEditing;

/// <summary>
/// The Sample Test keyboard has two distinct lifetimes: physical keys are
/// held only until key-up, but preview voices persist after key-up until
/// displaced, explicitly stopped, or naturally exhausted by the renderer.
/// FM uses held-key voices; Sample audition deliberately does not.
/// </summary>
public sealed record SampleAuditionVoice(
	uint VoiceId, int ChromaticIndex, double PitchMultiplier);

public sealed record SampleAuditionStart(
	SampleAuditionVoice Note, IReadOnlyList<SampleAuditionVoice> Displaced);

public sealed class SampleAuditionKeyboard
{
	private readonly FmSynthAuditionKeyboard _pressed = new();
	private readonly List<SampleAuditionVoice> _voices = [];
	private uint _nextVoiceId = SampleAuditionCompiler.VoiceIdBase;

	public int BaseOctave => _pressed.BaseOctave;
	public int PressedKeyCount => _pressed.ActiveNoteCount;
	public IReadOnlyList<SampleAuditionVoice> OngoingNotes => _voices.ToArray();

	/// <summary>Auto-repeat is rejected while a key is down, but the same
	/// physical key may trigger a new independent voice after its release.
	/// If the sample is looping, notes strictly closer than a minor third
	/// (zero, one, or two semitones) are cut before the new voice starts.</summary>
	public SampleAuditionStart? KeyDown(
		PhysicalKey key, KeyModifiers modifiers, bool looping)
	{
		StartFmSynthAuditionNote? held = _pressed.KeyDown(key, modifiers);
		if (held is null)
			return null;
		if (!PatternNoteKeyboard.TryGetSemitoneOffset(key, out int offset))
			throw new InvalidOperationException("FM piano returned a non-note key.");
		int chromatic = ((BaseOctave - 4) * 12) + offset;
		// Allocate unique virtual voices even for repeated notes and even
		// when no loop exists; overlapping one-shots must not displace.
		if (_nextVoiceId == uint.MaxValue)
			throw new InvalidOperationException("Sample preview voice IDs exhausted.");
		SampleAuditionVoice voice = new(
			_nextVoiceId++, chromatic, held.PitchMultiplier);
		SampleAuditionVoice[] displaced = looping
			? _voices.Where(old => Math.Abs(old.ChromaticIndex - chromatic) < 3)
				.ToArray()
			: [];
		foreach (SampleAuditionVoice previous in displaced)
			_voices.Remove(previous);
		_voices.Add(voice);
		return new SampleAuditionStart(voice, displaced);
	}

	/// <summary>Physical key-up merely enables the next key-down; it never
	/// sends Note Off or removes an existing playback voice.</summary>
	public bool KeyUp(PhysicalKey key)
		=> _pressed.KeyUp(key) is not null;

	/// <summary>Used if a start fails to reach audio output.</summary>
	public void Discard(SampleAuditionVoice voice)
		=> _voices.Remove(voice);

	public IReadOnlyList<SampleAuditionVoice> StopAll()
	{
		_pressed.ReleaseAll();
		SampleAuditionVoice[] stopped = _voices.ToArray();
		_voices.Clear();
		return stopped;
	}
}

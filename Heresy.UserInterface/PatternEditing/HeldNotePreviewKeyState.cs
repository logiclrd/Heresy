using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

public abstract record HeldNotePreviewAction;

public sealed record StartHeldNotePreviewAction(
	PhysicalKey Key,
	int VoiceId,
	double PitchMultiplier)
	: HeldNotePreviewAction;

public sealed record ReleaseHeldNotePreviewAction(
	PhysicalKey Key,
	int VoiceId)
	: HeldNotePreviewAction;

/// <summary>
/// Treats the physical Caps Lock key as a momentary tracker-preview modifier.
/// The locking/toggled state is deliberately irrelevant: preview mode exists
/// only while the key is physically held.
/// </summary>
public sealed class HeldNotePreviewKeyState
{
	private readonly HashSet<PhysicalKey> _activeNotes = [];
	private bool _capsLockHeld;

	public bool CapsLockHeld => _capsLockHeld;

	public int ActiveNoteCount => _activeNotes.Count;

	public bool IsPreviewKeyActive(PhysicalKey key)
		=> _activeNotes.Contains(key);

	public HeldNotePreviewAction? KeyDown(
		PhysicalKey key,
		int baseOctave)
	{
		if (baseOctave is < 0 or > 8)
			throw new ArgumentOutOfRangeException(nameof(baseOctave));

		if (key == PhysicalKey.CapsLock)
		{
			_capsLockHeld = true;
			return null;
		}

		if (!_capsLockHeld
			|| !PatternNoteKeyboard.TryGetSemitoneOffset(
				key,
				out int semitoneOffset)
			|| !_activeNotes.Add(key))
		{
			return null;
		}

		int relativeSemitone =
			((baseOctave - 4) * 12)
				+ semitoneOffset;
		double pitchMultiplier =
			Math.Pow(
				2.0,
				relativeSemitone / 12.0);

		return new StartHeldNotePreviewAction(
			key,
			semitoneOffset,
			pitchMultiplier);
	}

	public IReadOnlyList<ReleaseHeldNotePreviewAction> ReleaseAll()
	{
		ReleaseHeldNotePreviewAction[] releases =
			_activeNotes
				.Select(
					key =>
					{
						PatternNoteKeyboard.TryGetSemitoneOffset(
							key,
							out int semitoneOffset);
						return new ReleaseHeldNotePreviewAction(
							key,
							semitoneOffset);
					})
				.ToArray();

		_activeNotes.Clear();
		_capsLockHeld = false;
		return releases;
	}

	public HeldNotePreviewAction? KeyUp(
		PhysicalKey key)
	{
		if (key == PhysicalKey.CapsLock)
		{
			_capsLockHeld = false;
			return null;
		}

		if (!_activeNotes.Remove(key)
			|| !PatternNoteKeyboard.TryGetSemitoneOffset(
				key,
				out int semitoneOffset))
		{
			return null;
		}

		return new ReleaseHeldNotePreviewAction(
			key,
			semitoneOffset);
	}
}

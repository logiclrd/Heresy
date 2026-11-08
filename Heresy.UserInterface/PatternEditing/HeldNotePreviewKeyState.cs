using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

public abstract record HeldNotePreviewAction;

public sealed record StartHeldNotePreviewAction(
    PhysicalKey Key, int VoiceId, double PitchMultiplier)
    : HeldNotePreviewAction;

public sealed record ReleaseHeldNotePreviewAction(
    PhysicalKey Key, int VoiceId)
    : HeldNotePreviewAction;

/// <summary>
/// Pattern preview is enabled by Shift on a piano key press in the Note
/// field, not by Caps Lock's physical or toggled state. Once a key starts
/// preview it remains exclusive to preview until its own confirmed release,
/// even if Shift is released earlier.
/// </summary>
public sealed class HeldNotePreviewKeyState
{
    private readonly HashSet<PhysicalKey> _activeNotes = [];

    public int ActiveNoteCount => _activeNotes.Count;

    public bool IsPreviewKeyActive(PhysicalKey key)
        => _activeNotes.Contains(key);

    public HeldNotePreviewAction? KeyDown(
        PhysicalKey key, int baseOctave, KeyModifiers modifiers)
    {
        if (baseOctave is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(baseOctave));

        if (!PatternPreviewKeyRouting.IsPreviewOnly(this, key, modifiers)
            || !_activeNotes.Add(key)
            || !PatternNoteKeyboard.TryGetSemitoneOffset(
                key, out int semitone))
            return null;

        double pitchMultiplier = Math.Pow(2.0,
            (((baseOctave - 4) * 12) + semitone) / 12.0);
        return new StartHeldNotePreviewAction(key, semitone, pitchMultiplier);
    }

    public IReadOnlyList<ReleaseHeldNotePreviewAction> ReleaseAll()
    {
        ReleaseHeldNotePreviewAction[] releases = _activeNotes
            .Select(key =>
            {
                PatternNoteKeyboard.TryGetSemitoneOffset(key, out int semitone);
                return new ReleaseHeldNotePreviewAction(key, semitone);
            })
            .ToArray();
        _activeNotes.Clear();
        return releases;
    }

    public HeldNotePreviewAction? KeyUp(PhysicalKey key)
    {
        if (!_activeNotes.Remove(key)
            || !PatternNoteKeyboard.TryGetSemitoneOffset(key, out int semitone))
            return null;
        return new ReleaseHeldNotePreviewAction(key, semitone);
    }
}

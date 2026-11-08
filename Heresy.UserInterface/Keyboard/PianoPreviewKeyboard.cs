using System;
using System.Collections.Generic;

using Avalonia.Input;

using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Keyboard;

/// <summary>Musical intent emitted by the Test keyboard. There are no voice
/// IDs or renderer-specific types at this boundary.</summary>
public abstract record PianoPreviewEvent;
public sealed record PianoPreviewNoteOn(
    uint PressId, int ChromaticNote, double PitchMultiplier,
    bool SuppressNeighbours) : PianoPreviewEvent;
public sealed record PianoPreviewNoteOff(uint PressId) : PianoPreviewEvent;
public sealed record PianoPreviewStopAll : PianoPreviewEvent;

/// <summary>
/// A pure input state machine. The caller has already filtered synthetic
/// X11 repeat releases, and supplies the actual Caps Lock toggle snapshot
/// at the instant of each new piano key press.
/// </summary>
public sealed class PianoPreviewKeyboard
{
    private sealed record Held(uint PressId, bool ReleaseOnKeyUp);
    private readonly Dictionary<PhysicalKey, Held> _pressed = [];
    private uint _nextPressId = 1;

    public int BaseOctave { get; private set; } = 4;
    public int HeldCount => _pressed.Count;

    public static bool IsApplicable(
        PhysicalKey key, KeyModifiers modifiers)
    {
        if (key == PhysicalKey.F8)
            return true;
        KeyModifiers blocked = KeyModifiers.Control
            | KeyModifiers.Alt | KeyModifiers.Meta;
        if ((modifiers & blocked) != 0)
            return false;
        return key is PhysicalKey.NumPadMultiply or PhysicalKey.NumPadDivide
            || PatternNoteKeyboard.TryGetSemitoneOffset(key, out _);
    }

    /// <summary>Returns null for octave changes or non-note keys.</summary>
    public PianoPreviewEvent? KeyDown(
        PhysicalKey key, KeyModifiers modifiers, bool capsLockOn)
    {
        if (key == PhysicalKey.F8)
            return new PianoPreviewStopAll();

        if (!IsApplicable(key, modifiers))
            return null;

        if (PatternOctaveKeyboard.TryAdjust(
            key, modifiers, BaseOctave, out int octave))
        {
            BaseOctave = octave;
            return null;
        }

        if (!PatternNoteKeyboard.TryGetSemitoneOffset(
            key, out int semitone))
            return null;

        if (_pressed.ContainsKey(key))
            return null;

        if (_nextPressId == uint.MaxValue)
            throw new InvalidOperationException("Preview press IDs exhausted.");
        uint pressId = _nextPressId++;
        _pressed.Add(key, new Held(pressId, ReleaseOnKeyUp: !capsLockOn));
        int chromatic = (BaseOctave - 4) * 12 + semitone;
        return new PianoPreviewNoteOn(
            pressId, chromatic, Math.Pow(2, chromatic / 12.0),
            SuppressNeighbours: capsLockOn);
    }

    public PianoPreviewEvent? KeyUp(PhysicalKey key)
    {
        if (!_pressed.Remove(key, out Held? press)
            || !press.ReleaseOnKeyUp)
            return null;
        return new PianoPreviewNoteOff(press.PressId);
    }

    /// <summary>Focus loss stops only notes started in momentary mode;
    /// latched notes continue to exist in the separate voice controller.</summary>
    public IReadOnlyList<PianoPreviewEvent> ReleaseHeld()
    {
        List<PianoPreviewEvent> releases = [];
        foreach (Held press in _pressed.Values)
            if (press.ReleaseOnKeyUp)
                releases.Add(new PianoPreviewNoteOff(press.PressId));
        _pressed.Clear();
        return releases;
    }
}

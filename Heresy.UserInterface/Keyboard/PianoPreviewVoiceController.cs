using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Realtime;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Keyboard;

public enum PianoVoiceActionKind { Start, Release, Cut }

public sealed record PianoPreviewVoice(
    uint VoiceId, uint PressId, int ChromaticNote,
    double PitchMultiplier, bool Released = false);

public sealed record PianoVoiceAction(
    PianoVoiceActionKind Kind, PianoPreviewVoice Voice);

/// <summary>
/// Pure renderer-independent voice-lifetime state machine. It does not
/// inspect key state, Caps Lock, or modifier flags; all policy arrives in
/// PianoPreviewEvents. Different sources may share this controller.
/// </summary>
public sealed class PianoPreviewVoiceController
{
    // Release tails are tracked for F8 and neighbour cuts; an ISound does
    // not report asynchronous natural EOF to this UI class. Bound history.
    public const int MaximumTrackedVoices = 512;

    private readonly List<PianoPreviewVoice> _voices = [];
    private readonly Dictionary<uint, uint> _voiceForPress = [];
    private uint _nextVoiceId;

    public PianoPreviewVoiceController(uint voiceIdBase) =>
        _nextVoiceId = voiceIdBase;

    public IReadOnlyList<PianoPreviewVoice> Voices => _voices.ToArray();
    public IReadOnlyList<PianoPreviewVoice> SoundingNotes =>
        _voices.Where(v => !v.Released).ToArray();

    public IReadOnlyList<PianoVoiceAction> Apply(PianoPreviewEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        List<PianoVoiceAction> actions = [];

        switch (evt)
        {
            case PianoPreviewNoteOn start:
                if (_voiceForPress.ContainsKey(start.PressId))
                    return actions;

                if (start.SuppressNeighbours)
                    foreach (PianoPreviewVoice previous in _voices.ToArray())
                        if (Math.Abs(previous.ChromaticNote - start.ChromaticNote) < 3)
                            Cut(previous, actions);

                // Also cap release-tail history, which may have naturally
                // finished long ago. Never let editor-side tracking grow
                // without bound during repeated one-shot previews.
                if (_voices.Count >= MaximumTrackedVoices)
                    Cut(_voices[0], actions);

                if (_nextVoiceId == uint.MaxValue)
                    throw new InvalidOperationException("Preview voice IDs exhausted.");
                PianoPreviewVoice voice = new(
                    _nextVoiceId++, start.PressId,
                    start.ChromaticNote, start.PitchMultiplier);
                _voices.Add(voice);
                _voiceForPress.Add(start.PressId, voice.VoiceId);
                actions.Add(new(PianoVoiceActionKind.Start, voice));
                break;

            case PianoPreviewNoteOff off:
                if (_voiceForPress.Remove(off.PressId, out uint voiceId))
                {
                    int index = _voices.FindIndex(v => v.VoiceId == voiceId);
                    if (index >= 0)
                    {
                        PianoPreviewVoice current = _voices[index];
                        if (!current.Released)
                        {
                            _voices[index] = current with { Released = true };
                            actions.Add(new(PianoVoiceActionKind.Release, current));
                        }
                    }
                }
                break;

            case PianoPreviewStopAll:
                foreach (PianoPreviewVoice previous in _voices)
                    actions.Add(new(PianoVoiceActionKind.Cut, previous));
                _voices.Clear();
                _voiceForPress.Clear();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(evt));
        }
        return actions;
    }

    public void Discard(PianoPreviewVoice voice)
    {
        _voices.RemoveAll(v => v.VoiceId == voice.VoiceId);
        if (_voiceForPress.TryGetValue(voice.PressId, out uint found)
            && found == voice.VoiceId)
            _voiceForPress.Remove(voice.PressId);
    }

    private void Cut(PianoPreviewVoice voice, List<PianoVoiceAction> actions)
    {
        _voices.Remove(voice);
        if (_voiceForPress.TryGetValue(voice.PressId, out uint id)
            && id == voice.VoiceId)
            _voiceForPress.Remove(voice.PressId);
        actions.Add(new(PianoVoiceActionKind.Cut, voice));
    }
}

/// <summary>
/// Transport adapter shared by Sample and Synth. Key events are serialized
/// into ordered live-note commands; the views provide only their Source ID,
/// transport, and optional legacy F8 global-stop callback.
/// </summary>
public sealed class PianoPreviewPlaybackController
{
    public const uint SynthVoiceIdBase = 0x464D0000;
    public const uint SampleVoiceIdBase = 0x53410000;

    private readonly PianoPreviewVoiceController _voices;
    private readonly ObjectId _sourceId;
    private readonly bool _isSynthPreview;
    private readonly PatternLiveAuditionActions? _audition;
    private readonly Func<Task>? _globalStop;
    private readonly Action<string>? _onError;
    private Task _queue = Task.CompletedTask;

    public PianoPreviewPlaybackController(
        ObjectId sourceId, uint voiceIdBase,
        PatternLiveAuditionActions? audition,
        Func<Task>? globalStop = null, Action<string>? onError = null)
    {
        _sourceId = sourceId;
        _isSynthPreview = voiceIdBase == SynthVoiceIdBase;
        _voices = new PianoPreviewVoiceController(voiceIdBase);
        _audition = audition;
        _globalStop = globalStop;
        _onError = onError;
    }

    public bool HasVoices => _voices.Voices.Count != 0;
    public IReadOnlyList<PianoPreviewVoice> SoundingNotes =>
        _voices.SoundingNotes;

    public Task HandleAsync(PianoPreviewEvent evt)
        => Append(async () =>
        {
            IReadOnlyList<PianoVoiceAction> actions = _voices.Apply(evt);
            if (evt is PianoPreviewStopAll && _globalStop is not null)
            {
                await _globalStop();
                return;
            }
            await SendActionsAsync(actions);
        });

    /// <summary>Used when Sample loop settings change while previews play.
    /// The original pitch and unique voice IDs remain intact.</summary>
    public Task RestartSoundingAsync()
        => Append(async () =>
        {
            if (_audition is null)
                return;
            foreach (PianoPreviewVoice voice in _voices.SoundingNotes)
                await _audition.SendEventAsync(
                    Compile(voice, PianoVoiceActionKind.Start, restart: true));
        });

    /// <summary>Closing a window cuts only its own voices. An F8 command
    /// may instead invoke the Sample editor's legacy global stop action.</summary>
    public Task StopOwnVoicesAsync()
        => Append(async () =>
            await SendActionsAsync(_voices.Apply(new PianoPreviewStopAll())));

    private Task Append(Func<Task> operation)
    {
        Task predecessor = _queue;
        _queue = RunAfterAsync(predecessor, operation);
        return _queue;
    }

    private async Task RunAfterAsync(Task predecessor, Func<Task> operation)
    {
        try
        {
            await predecessor;
            await operation();
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"Preview playback failed: {ex.Message}");
        }
    }

    private async Task SendActionsAsync(IReadOnlyList<PianoVoiceAction> actions)
    {
        if (_audition is null)
        {
            if (actions.Any(a => a.Kind == PianoVoiceActionKind.Start))
                _onError?.Invoke("Realtime audition is unavailable in this host.");
            return;
        }

        foreach (PianoVoiceAction action in actions)
            await _audition.SendEventAsync(Compile(action.Voice, action.Kind));
    }

    public LivePlaybackEvent Compile(PianoPreviewVoice voice,
        PianoVoiceActionKind kind, bool restart = false)
    {
        NoteCommand[] commands = kind switch
        {
            PianoVoiceActionKind.Start when restart =>
                [new NoteCutCommand(),
                    new StartNoteCommand(_sourceId,
                        PitchMultiplier: voice.PitchMultiplier)
                    { MonitorFinalSynthDc = _isSynthPreview }],
            PianoVoiceActionKind.Start =>
                [new StartNoteCommand(_sourceId,
                    PitchMultiplier: voice.PitchMultiplier)
                    { MonitorFinalSynthDc = _isSynthPreview }],
            PianoVoiceActionKind.Release => [new NoteOffCommand()],
            PianoVoiceActionKind.Cut => [new NoteCutCommand()],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new LivePlaybackEvent(
            ChannelTarget.Virtual(voice.VoiceId), commands);
    }
}

using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Render.Playback;
using Heresy.Render.Sounds;

namespace Heresy.Playback;

/// <summary>
/// PCM-producer-owned overlay of preprepared Synth definitions for *future*
/// NoteOn resolution. Existing PlaybackVoices already retain their original
/// ISound and SoundState; replacing the lookup cannot reset those voices.
/// Structural graph revisions can therefore coexist until old notes retire.
///
/// Publication is called only on the single producer, from the ordered
/// live-event queue. Preparation must have finished on a control worker.
/// </summary>
internal sealed class PublishedLiveSynthResolver : ISoundResolver
{
    private readonly ISoundResolver _initial;
    private readonly Dictionary<ObjectId, ISound> _synths = [];

    public PublishedLiveSynthResolver(ISoundResolver initial)
        => _initial = initial ?? throw new ArgumentNullException(nameof(initial));

    public void Publish(ObjectId sourceId, ISound preparedSound)
    {
        if (sourceId.IsNone)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        ArgumentNullException.ThrowIfNull(preparedSound);
        _synths[sourceId] = preparedSound;
    }

    public bool TryResolve(ObjectId sourceId, bool mixdown,
        out ISound? sound)
    {
        if (!mixdown && _synths.TryGetValue(sourceId, out sound))
            return true;
        return _initial.TryResolve(sourceId, mixdown, out sound);
    }
}

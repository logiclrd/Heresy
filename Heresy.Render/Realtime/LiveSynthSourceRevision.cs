using System;

using Heresy.Core.Objects;
using Heresy.Render.Sounds;

namespace Heresy.Render.Realtime;

/// <summary>
/// Optional capability for an already-running live audition. A newly prepared
/// immutable Synth source is queued beside note events and adopted on the
/// single PCM producer at the same ordered event boundary. It must never
/// mutate or replace sound definitions owned by existing PlaybackVoices.
/// </summary>
public interface ILiveSynthSourceRevisionOutput
{
    void EnqueueLiveSynthSource(ObjectId sourceId, ISound preparedSound);
}

/// <summary>
/// Off-audio-thread preparation of an immutable Synth source from a frozen
/// document revision. Factories that do not support this optional capability
/// retain the existing playback-restart behavior.
/// </summary>
public interface ILiveSynthSourcePreparationFactory
{
    ISound PrepareLiveSynthSource(
        SongDocumentSnapshot snapshot,
        ObjectId sourceId);
}

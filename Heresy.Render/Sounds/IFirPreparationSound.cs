using Heresy.Render.Filters;

namespace Heresy.Render.Sounds;

/// <summary>Optional invocation-scoped FIR control handoff for the realtime
/// host. The source's mutable histories stay exclusively PCM-thread owned;
/// only immutable authored Filter snapshots reach the preparation worker.</summary>
public interface IFirPreparationSound
{
    void AdoptPreparedFirControls(SoundState state);
    void QueuePendingFirControls(SoundState state,
        FirPreparationScheduler scheduler);
}

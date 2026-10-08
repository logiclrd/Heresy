namespace Heresy.Core.Sequences;

/// <summary>
/// A resumable raw Sequence script operation. Play enqueues exactly one
/// Pattern order when the consumer requests it. Cooperate returns CPU
/// control without playing an order or advancing tracker/wall time.
/// </summary>
public abstract record RawSequenceStep
{
	public sealed record Play(SequenceEntry Entry) : RawSequenceStep;

	public sealed record Cooperate : RawSequenceStep;
}

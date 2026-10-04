using Heresy.Core.Objects;

namespace Heresy.Core.Envelopes;

public abstract class EnvelopeDefinition : SongObject
{
	protected EnvelopeDefinition(ObjectId id, string name) : base(id, name) { }

	public override SongObjectKind Kind => SongObjectKind.Envelope;
}

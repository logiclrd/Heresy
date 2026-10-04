using Heresy.Core.Objects;

namespace Heresy.Core.Sequences;

public abstract class SequenceDefinition : SongObject
{
	protected SequenceDefinition(ObjectId id, string name) : base(id, name) { }

	public override SongObjectKind Kind => SongObjectKind.Sequence;
}

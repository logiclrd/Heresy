using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Core.Sequences;

public sealed class DataSequenceDefinition : SequenceDefinition
{
	public DataSequenceDefinition(ObjectId id, string name) : base(id, name) { }

	public List<SequenceEntry> Entries { get; } = [];
}

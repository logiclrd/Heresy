using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Core.Sequences;

public sealed class DataSequenceDefinition : SequenceDefinition, ISequenceEntryProvider
{
	public DataSequenceDefinition(ObjectId id, string name) : base(id, name) { }

	public List<SequenceEntry> Entries { get; } = [];

	public SequenceEntry? GetSequenceEntry(
		int absoluteIndex, int sequenceIndex, int previousSequenceIndex)
		=> (uint)sequenceIndex < (uint)Entries.Count
			? Entries[sequenceIndex] : null;
}

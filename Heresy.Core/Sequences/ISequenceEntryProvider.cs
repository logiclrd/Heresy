using System;
using System.Collections.Generic;

using Heresy.Core.Sequencing;

namespace Heresy.Core.Sequences;

/// <summary>
/// One lookup per Sequence order visit. AbsoluteIndex increments even after
/// jumps; PreviousSequenceIndex is -1 for the first lookup. Null ends the
/// Sequence. Providers may return different entries for the same order.
/// </summary>
public interface ISequenceEntryProvider
{
	SequenceEntry? GetSequenceEntry(
		int absoluteIndex, int sequenceIndex, int previousSequenceIndex);
}

/// <summary>Creates a fresh stateful provider bound to each invocation context.</summary>
public interface ISequenceEntrySourceFactory
{
	ISequenceEntryProvider Create(SequencingContext context);
}

/// <summary>Immutable, index-addressed data-Sequence adapter.</summary>
public sealed class IndexedSequenceEntryProvider : ISequenceEntryProvider
{
	private readonly IReadOnlyList<SequenceEntry> _entries;

	public IndexedSequenceEntryProvider(IReadOnlyList<SequenceEntry> entries)
	{
		_entries = entries ?? throw new ArgumentNullException(nameof(entries));
	}

	public SequenceEntry? GetSequenceEntry(
		int absoluteIndex, int sequenceIndex, int previousSequenceIndex)
		=> (uint)sequenceIndex < (uint)_entries.Count
			? _entries[sequenceIndex] : null;
}

using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;

namespace Heresy.Core.Patterns;

/// <summary>
/// Initial storage form for a data-driven pattern. These are raw events; the
/// tracker-grid adapter will eventually translate familiar tracker cells/effect
/// memory into this representation before the common pattern processor runs.
/// </summary>
public sealed class DataPatternDefinition : PatternDefinition, IRawPatternNoteGenerator
{
	public DataPatternDefinition(ObjectId id, string name) : base(id, name) { }

	public List<NoteEvent> Events { get; } = [];

	public void GenerateRawNotes(
		SequencingContext context,
		INoteReceiver output,
		out double rowCount)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(output);

		foreach (NoteEvent noteEvent in Events)
			output.Append(noteEvent);

		rowCount = RowCount;
	}
}

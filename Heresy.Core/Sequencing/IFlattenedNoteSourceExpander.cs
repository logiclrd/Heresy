using System;
using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Optional preparation-stage nested-source expansion hook. Called while
/// the parent pattern's row is being sequenced, before the next row reads
/// tracker tempo and channel-effect memory. The returned events replace
/// the original event. Null means no flattening is required.
/// </summary>
public interface IFlattenedNoteSourceExpander
{
	IReadOnlyList<NoteEvent>? Expand(
		NoteEvent noteEvent,
		SequencingContext context);

	/// <summary>
	/// Maximum absolute musical time reached by nested children; this may
	/// outlast their containing pattern/sequence and must extend export.
	/// </summary>
	TimeSpan MaximumAbsoluteEnd { get; }
}

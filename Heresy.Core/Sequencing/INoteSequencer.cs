using System;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Executable sequencer contract. Implementations are deterministic for a given
/// context and song snapshot. The method must assign duration before returning.
/// </summary>
public interface INoteSequencer
{
	void GenerateNotes(
		SequencingContext context,
		INoteReceiver output,
		out TimeSpan duration);
}

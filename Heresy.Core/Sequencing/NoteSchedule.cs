using System.Collections.Generic;

namespace Heresy.Core.Sequencing;

public sealed class NoteSchedule : IReadOnlyList<NoteEvent>
{
	internal NoteSchedule(NoteEvent[] events) => Events = events;

	internal NoteEvent[] Events { get; }

	public int Count => Events.Length;
	public NoteEvent this[int index] => Events[index];

	public IEnumerator<NoteEvent> GetEnumerator()
		=> ((IEnumerable<NoteEvent>)Events).GetEnumerator();

	System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
		=> Events.GetEnumerator();
}

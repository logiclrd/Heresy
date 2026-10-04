using System.Collections.Generic;

using Heresy.Core.Timing;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Raw note event prior to row/time resolution. EmissionOrder is assigned by a
/// receiver and is used as the stable final ordering key when other keys tie.
/// </summary>
public sealed record NoteEvent(
	MusicalTime Offset,
	ChannelTarget Target,
	IReadOnlyList<NoteCommand> Commands,
	long EmissionOrder = -1)
{
	public NoteEvent WithEmissionOrder(long order) => this with { EmissionOrder = order };
}

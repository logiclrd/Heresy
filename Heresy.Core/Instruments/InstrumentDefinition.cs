using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Core.Instruments;

public sealed class InstrumentDefinition : SongObject
{
	public InstrumentDefinition(ObjectId id, string name) : base(id, name) { }

	public override SongObjectKind Kind => SongObjectKind.Instrument;

	/// <summary>Divisions applied to log2(pitch relative to middle C).</summary>
	public double Divisions { get; set; } = 12.0;

	/// <summary>Offset added after pitch quantization to obtain a tone-table index.</summary>
	public int Offset { get; set; }

	public List<ToneSpecification> ToneSpecifications { get; } = [];

	/// <summary>
	/// Maps tone indices to ToneSpecifications. A value of -1 means silent.
	/// Several entries may point to the same specification.
	/// </summary>
	public List<int> ToneTable { get; } = [];
}

using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Core.Instruments;

/// <summary>
/// Programmable tone selection from a finite, declared list of sources.
/// The persisted expression returns a zero-based specification index, or -1
/// for silence. Explicit specifications preserve all references for import,
/// tombstones and deterministic snapshot binding.
/// </summary>
public sealed class ScriptInstrumentDefinition : SongObject
{
	public ScriptInstrumentDefinition(ObjectId id, string name) : base(id, name) { }

	public override SongObjectKind Kind => SongObjectKind.Instrument;

	/// <summary>Restricted C# integer expression of pitchMultiplier (C4=1).</summary>
	public string Source { get; set; } = "-1";

	/// <summary>
	/// Source, pitch ratio, and per-target optional Envelope overrides.
	/// Selector results are indices into this list; no dynamic ID creation.
	/// </summary>
	public List<ToneSpecification> ToneSpecifications { get; } = [];
}

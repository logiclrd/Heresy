namespace Heresy.Core.Envelopes;

/// <summary>
/// The three envelope-control domains exposed by IT S77-S7C. IT's third
/// envelope slot may be interpreted as pitch or resonant-filter modulation.
/// </summary>
public enum TrackerEnvelopeControlTarget
{
	Volume,
	Panning,
	PitchOrFilter,
}

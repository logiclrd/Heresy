namespace Heresy.Core.Envelopes;

/// <summary>
/// Semantic destination of one independently controllable voice envelope.
/// Tracker S77-S7C will map onto the volume, panning and pitch targets; filter
/// remains a separate Heresy target even though IT may encode it as pitch-env.
/// </summary>
public enum EnvelopeTarget
{
	Volume,
	Pitch,
	Panning,
	Filter,
}

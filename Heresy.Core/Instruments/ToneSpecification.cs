using Heresy.Core.Objects;

namespace Heresy.Core.Instruments;

/// <summary>
/// One reusable tone specification. Tone table entries contain indices into a
/// shared list of these specifications so several notes can intentionally point
/// at the same specification without duplicating it on disk.
/// </summary>
public sealed class ToneSpecification
{
	public required ObjectId SourceId { get; init; }
	public double PitchMultiplier { get; init; } = 1.0;

	public ObjectId? VolumeEnvelopeId { get; init; }
	public ObjectId? PitchEnvelopeId { get; init; }
	public ObjectId? PanningEnvelopeId { get; init; }
	public ObjectId? FilterEnvelopeId { get; init; }
}

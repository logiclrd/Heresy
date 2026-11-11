namespace Heresy.Render.Envelopes;

/// <summary>
/// Immutable set of scalar envelope curves captured when a note starts.
/// Curve output remains parameter-agnostic; playback interprets each target in
/// its native domain.
/// </summary>
public sealed record EnvelopeConfigurationSnapshot(
	IEnvelopeCurve? Volume = null,
	IEnvelopeCurve? Pitch = null,
	IEnvelopeCurve? Panning = null,
	IEnvelopeCurve? Filter = null)
{
	public bool HasSustainLoop => Volume?.HasSustainLoop == true
		|| Pitch?.HasSustainLoop == true
		|| Panning?.HasSustainLoop == true
		|| Filter?.HasSustainLoop == true;

	public static EnvelopeConfigurationSnapshot Empty { get; } = new();
}

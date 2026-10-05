namespace Heresy.Render.Envelopes;

/// <summary>
/// Immutable scalar envelope curve. activeFrame advances only while the
/// envelope is enabled; noteOffActiveFrame is expressed in that same active
/// timeline so pause/resume does not consume envelope time.
/// </summary>
public interface IEnvelopeCurve
{
	double GetValue(
		long activeFrame,
		int sampleRate,
		long? noteOffActiveFrame);
}

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

	/// <summary>
	/// Returns the first active-envelope frame which is guaranteed to be silent
	/// after Note Off, or null when the curve has no deterministic finite end.
	/// </summary>
	long? GetEndActiveFrameExclusiveAfterNoteOff(
		long noteOffActiveFrame,
		int sampleRate)
		=> null;
}

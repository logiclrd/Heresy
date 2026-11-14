namespace Heresy.Render.Envelopes;

/// <summary>
/// Immutable scalar envelope curve. activeFrame advances only while the
/// envelope is enabled; noteOffActiveFrame is expressed in that same active
/// timeline so pause/resume does not consume envelope time.
/// </summary>
public interface IEnvelopeCurve
{
	/// <summary>Whether Note Off has an actual sustain loop to release.</summary>
	bool HasSustainLoop => false;

	/// <summary>Whether completed playback is permanently zero.</summary>
	bool IsZeroAfterCompletion => false;

	double GetValue(
		long activeFrame,
		int sampleRate,
		long? noteOffActiveFrame);

	/// <summary>
	/// First active frame beyond a natural, held (no Note Off) ending.
	/// Null when the curve sustains or cannot prove a finite ending.
	/// Script envelopes without an explicit natural duration do not guess
	/// an end by observing zero output values.
	/// </summary>
	long? GetEndActiveFrameExclusiveWithoutNoteOff(int sampleRate) => null;

	/// <summary>
	/// Returns the first active-envelope frame after release traversal finishes,
	/// not necessarily a zero-amplitude boundary. A nonzero terminal value
	/// remains in effect until the enclosing note retires.
	/// </summary>
	long? GetEndActiveFrameExclusiveAfterNoteOff(
		long noteOffActiveFrame,
		int sampleRate)
		=> null;
}

using System;

using Heresy.Render.Timing;

namespace Heresy.Render.Envelopes;

/// <summary>
/// Immutable compiled scalar Envelope. A declared release duration is an
/// enforced silence boundary, not a duration inferred from script output.
/// No Roslyn invocation, allocations, or mutable script state per frame.
/// </summary>
public sealed class ScriptEnvelopeCurve : IEnvelopeCurve
{
	private readonly Func<double, double, double> _evaluate;
	private readonly TimeSpan? _releaseDuration;

	public ScriptEnvelopeCurve(
		Func<double, double, double> evaluate,
		TimeSpan? releaseDuration)
	{
		_evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
		if (releaseDuration < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(releaseDuration));
		_releaseDuration = releaseDuration;
	}

	public double GetValue(long activeFrame, int sampleRate, long? noteOffActiveFrame)
	{
		if (activeFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(activeFrame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (noteOffActiveFrame is long off
			&& (off < 0 || off > activeFrame))
			throw new ArgumentOutOfRangeException(nameof(noteOffActiveFrame));

		if (noteOffActiveFrame.HasValue && _releaseDuration.HasValue
			&& activeFrame >= GetEndActiveFrameExclusiveAfterNoteOff(
				noteOffActiveFrame.Value, sampleRate)!.Value)
			return 0.0;

		double releaseSeconds = noteOffActiveFrame is long noteOff
			? (activeFrame - noteOff) / (double)sampleRate
			: -1.0;
		double value = _evaluate(activeFrame / (double)sampleRate, releaseSeconds);
		if (!double.IsFinite(value))
			throw new InvalidOperationException(
				"Script Envelope returned a non-finite value.");
		return value;
	}

	public long? GetEndActiveFrameExclusiveAfterNoteOff(
		long noteOffActiveFrame, int sampleRate)
	{
		if (noteOffActiveFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(noteOffActiveFrame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		return _releaseDuration.HasValue
			? checked(noteOffActiveFrame
				+ FrameTime.Ceiling(_releaseDuration.Value, sampleRate))
			: null;
	}
}

using System;

using Heresy.Core.Envelopes;

namespace Heresy.Render.Envelopes;

/// <summary>
/// Immutable ADSR curve snapshot. The scalar value is not clamped: consumers
/// interpret it in their own domain, so a volume envelope may be negative and
/// therefore invert phase.
/// </summary>
public sealed class AdsrEnvelopeCurve : IEnvelopeCurve
{
	private readonly TimeSpan _attack;
	private readonly TimeSpan _decay;
	private readonly double _sustainLevel;
	private readonly TimeSpan _release;

	public AdsrEnvelopeCurve(AdsrEnvelopeDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		if (definition.Attack < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(definition));
		if (definition.Decay < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(definition));
		if (definition.Release < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(definition));
		if (double.IsNaN(definition.SustainLevel)
			|| double.IsInfinity(definition.SustainLevel))
		{
			throw new ArgumentOutOfRangeException(nameof(definition));
		}

		_attack = definition.Attack;
		_decay = definition.Decay;
		_sustainLevel = definition.SustainLevel;
		_release = definition.Release;
	}

	public long? GetEndActiveFrameExclusiveAfterNoteOff(
		long noteOffActiveFrame,
		int sampleRate)
	{
		if (noteOffActiveFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(noteOffActiveFrame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		long releaseFrames =
			Heresy.Render.Timing.FrameTime.Ceiling(
				_release,
				sampleRate);
		return checked(
			noteOffActiveFrame
				+ releaseFrames);
	}

	public double GetValue(
		long activeFrame,
		int sampleRate,
		long? noteOffActiveFrame)
	{
		if (activeFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(activeFrame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (noteOffActiveFrame.HasValue
			&& (noteOffActiveFrame.Value < 0
				|| noteOffActiveFrame.Value > activeFrame))
		{
			throw new ArgumentOutOfRangeException(nameof(noteOffActiveFrame));
		}

		if (!noteOffActiveFrame.HasValue)
			return GetHeldValue(activeFrame, sampleRate);

		long releaseStartFrame = noteOffActiveFrame.Value;
		double releaseStartValue =
			GetHeldValue(releaseStartFrame, sampleRate);
		if (_release == TimeSpan.Zero)
			return 0.0;

		double releaseSeconds =
			(activeFrame - releaseStartFrame) / (double)sampleRate;
		double progress = Math.Clamp(
			releaseSeconds / _release.TotalSeconds,
			0.0,
			1.0);
		return releaseStartValue * (1.0 - progress);
	}

	private double GetHeldValue(long activeFrame, int sampleRate)
	{
		double seconds = activeFrame / (double)sampleRate;

		if (_attack > TimeSpan.Zero
			&& seconds < _attack.TotalSeconds)
		{
			return seconds / _attack.TotalSeconds;
		}

		seconds -= _attack.TotalSeconds;
		if (_decay > TimeSpan.Zero
			&& seconds < _decay.TotalSeconds)
		{
			double progress = seconds / _decay.TotalSeconds;
			return 1.0 + (_sustainLevel - 1.0) * progress;
		}

		return _sustainLevel;
	}
}

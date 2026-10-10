using System;

using Heresy.Core.Envelopes;

namespace Heresy.UserInterface.EnvelopeEditing;

public enum AdsrGraphHandle
{
	None,
	Attack,
	Decay,
	Sustain,
	Release,
}

public readonly record struct AdsrGraphValues(
	double AttackSeconds,
	double DecaySeconds,
	double SustainLevel,
	double ReleaseSeconds)
{
	public static AdsrGraphValues FromEnvelope(AdsrEnvelopeDefinition envelope)
	{
		ArgumentNullException.ThrowIfNull(envelope);
		return new(
			envelope.Attack.TotalSeconds,
			envelope.Decay.TotalSeconds,
			envelope.SustainLevel,
			envelope.Release.TotalSeconds);
	}

	public static AdsrGraphValues MoveHandle(
		AdsrGraphValues initial,
		AdsrGraphHandle handle,
		double deltaX,
		double pointerY,
		AdsrGraphLayout fixedLayout)
	{
		double durationDelta = deltaX / fixedLayout.PixelsPerSecond;
		double duration(double initialSeconds) =>
			Math.Clamp(initialSeconds + durationDelta, 0,
				TimeSpan.MaxValue.TotalSeconds - 1);
		return handle switch
		{
			AdsrGraphHandle.Attack =>
				initial with { AttackSeconds = duration(initial.AttackSeconds) },
			AdsrGraphHandle.Decay =>
				initial with { DecaySeconds = duration(initial.DecaySeconds) },
			AdsrGraphHandle.Release =>
				initial with { ReleaseSeconds = duration(initial.ReleaseSeconds) },
			AdsrGraphHandle.Sustain =>
				initial with { SustainLevel = fixedLayout.YToValue(pointerY) },
			_ => initial,
		};
	}
}

/// <summary>
/// Testable ADSR viewport. The graph deliberately shows the 0..1 note
/// volume reference without clamping the *stored* Sustain scalar; negative
/// and greater-than-one sustain lines display on the boundary with their
/// actual numeric level shown by the surrounding UI.
/// </summary>
public readonly record struct AdsrGraphLayout(
	double Left,
	double Top,
	double Width,
	double Height,
	double PixelsPerSecond,
	double HoldSeconds,
	double AttackX,
	double DecayX,
	double ReleaseStartX,
	double ReleaseEndX,
	double SustainY)
{
	public static AdsrGraphLayout Create(
		AdsrGraphValues values,
		double width,
		double height,
		double? fixedPixelsPerSecond = null,
		double? fixedHoldSeconds = null)
	{
		double left = 12;
		double top = 14;
		double usableWidth = Math.Max(1, width - 24);
		double usableHeight = Math.Max(1, height - 28);
		double durations = values.AttackSeconds + values.DecaySeconds
			+ values.ReleaseSeconds;
		double hold = fixedHoldSeconds
			?? Math.Max(0.5, durations / 4);
		double timeline = Math.Max(1.0, (durations + hold) * 1.35);
		double scale = fixedPixelsPerSecond
			?? usableWidth / timeline;
		double xAttack = left + scale * values.AttackSeconds;
		double xDecay = xAttack + scale * values.DecaySeconds;
		double xReleaseStart = xDecay + scale * hold;
		return new(
			left, top, usableWidth, usableHeight, scale, hold,
			xAttack, xDecay, xReleaseStart,
			xReleaseStart + scale * values.ReleaseSeconds,
			top + (1 - Math.Clamp(values.SustainLevel, 0, 1)) * usableHeight);
	}

	public double YToValue(double y)
		=> 1 - (y - Top) / Height;

	public AdsrGraphHandle HitHandle(double x, double y, double tolerance = 9)
	{
		// Sustain is draggable over its actual horizontal segment; the
		// duration endpoint caps have distinct vertical lanes so a user can
		// recover *either* of two simultaneously-zero segments.
		if (y <= Top + 50)
		{
			AdsrGraphHandle cap = HitCap(x, y, tolerance);
			if (cap != AdsrGraphHandle.None)
				return cap;
		}

		if (x >= DecayX - tolerance
			&& x <= ReleaseStartX + tolerance
			&& Math.Abs(y - SustainY) <= tolerance)
		{
			return AdsrGraphHandle.Sustain;
		}

		if (Math.Abs(x - ReleaseEndX) <= tolerance)
			return AdsrGraphHandle.Release;
		if (Math.Abs(x - DecayX) <= tolerance)
			return AdsrGraphHandle.Decay;
		if (Math.Abs(x - AttackX) <= tolerance)
			return AdsrGraphHandle.Attack;
		return AdsrGraphHandle.None;
	}

	private AdsrGraphHandle HitCap(double x, double y, double tolerance)
	{
		// The three caps have fixed lanes, independently accessible even
		// when Attack and Decay endpoints coincide at zero duration.
		if (Math.Abs(y - (Top + 8)) <= 6
			&& Math.Abs(x - AttackX) <= tolerance)
			return AdsrGraphHandle.Attack;
		if (Math.Abs(y - (Top + 23)) <= 6
			&& Math.Abs(x - DecayX) <= tolerance)
			return AdsrGraphHandle.Decay;
		if (Math.Abs(y - (Top + 38)) <= 6
			&& Math.Abs(x - ReleaseEndX) <= tolerance)
			return AdsrGraphHandle.Release;
		return AdsrGraphHandle.None;
	}
}

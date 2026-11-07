using System;
using System.Runtime.CompilerServices;

using Heresy.Core.FmSynthesis;

namespace Heresy.Render.FmSynthesis;

/// <summary>
/// The oscillator's unshaped [-1,1] waveform. The realtime renderer and
/// editor preview share this exact implementation so phase and reset behavior
/// cannot drift apart.
/// </summary>
public static class FmOscillatorWaveformEvaluator
{
	/// <summary>Evaluate the native waveform for a phase in [0,1).</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static double EvaluateNormalized(
		FmOscillatorWaveform waveform, double phase)
		=> waveform switch
		{
			FmOscillatorWaveform.Sine =>
				Math.Sin(2.0 * Math.PI * phase),
			FmOscillatorWaveform.Triangle =>
				1.0 - (4.0 * Math.Abs(phase - 0.5)),
			FmOscillatorWaveform.Sawtooth =>
				(2.0 * phase) - 1.0,
			FmOscillatorWaveform.Square =>
				phase < 0.5 ? 1.0 : -1.0,
			_ => throw new ArgumentOutOfRangeException(nameof(waveform)),
		};

	/// <summary>Evaluate for any finite phase, wrapping exactly as the
	/// oscillator's frequency integrator wraps between cycles.</summary>
	public static double EvaluateAtPhase(
		FmOscillatorWaveform waveform, double phase)
	{
		if (!double.IsFinite(phase))
			throw new ArgumentOutOfRangeException(nameof(phase));
		phase -= Math.Floor(phase);
		return EvaluateNormalized(waveform, phase);
	}
}

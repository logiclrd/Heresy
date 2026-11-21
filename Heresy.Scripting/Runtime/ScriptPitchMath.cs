using System;

namespace Heresy.Scripting.Runtime;

/// <summary>
/// Pure 12-tone-equal-temperament offset/ratio conversions shared by every
/// restricted Heresy script context. All functions return doubles; no note
/// notation, reference frequency, or Instrument Divisions is implied.
/// </summary>
public static class ScriptPitchMath
{
    /// <summary>Convert signed 12-TET semitones to a frequency ratio.</summary>
    public static double Semitone(double semitones) =>
        OffsetToRatio(semitones, 12.0, nameof(semitones));

    /// <summary>Convert signed cents (1200 per octave) to a frequency ratio.</summary>
    public static double Cent(double cents) =>
        OffsetToRatio(cents, 1200.0, nameof(cents));

    /// <summary>Convert signed octaves to a frequency ratio.</summary>
    public static double Octave(double octaves) =>
        OffsetToRatio(octaves, 1.0, nameof(octaves));

    /// <summary>
    /// Convert a positive finite frequency ratio to signed 12-TET semitones.
    /// No rounding or quantization is performed.
    /// </summary>
    public static double SemitoneOffset(double pitchMultiplier)
    {
        if (!(pitchMultiplier > 0.0) || !double.IsFinite(pitchMultiplier))
            throw new ArgumentOutOfRangeException(nameof(pitchMultiplier),
                "The pitch multiplier must be positive and finite.");

        return 12.0 * Math.Log2(pitchMultiplier);
    }

    private static double OffsetToRatio(
        double offset, double unitsPerOctave, string parameterName)
    {
        if (!double.IsFinite(offset))
            throw new ArgumentOutOfRangeException(parameterName,
                "The musical offset must be finite.");

        double ratio = Math.Pow(2.0, offset / unitsPerOctave);
        if (!(ratio > 0.0) || !double.IsFinite(ratio))
            throw new ArgumentOutOfRangeException(parameterName,
                "The resulting pitch multiplier must be positive and finite.");

        return ratio;
    }
}

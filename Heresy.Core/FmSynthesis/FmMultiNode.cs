using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Heresy.Core.FmSynthesis;

public enum FmMultiDistribution
{
    Spread,
    Harmonic,
    Chord,
}

/// <summary>Immutable polyphonic graph wrapper. Frequency ratios are resolved
/// when compiling a Synth, never by parsing text in the PCM sample loop.</summary>
public sealed class FmMultiNode : FmSynthNode
{
    private readonly IReadOnlyList<int> _inputs;
    public const double NeutralFrequencyHz = 440.0 * 0.59460355750136053336;
    public const int ReferenceSampleRate = 48000;
    public const int MaximumVoices = 128;
    public const double MaximumCycleLengthAtReference = 16384.0;

    public FmMultiNode(
        int id, int? inputNodeId = null,
        FmMultiDistribution distribution = FmMultiDistribution.Spread,
        double detune = 12.0, int voiceCount = 7,
        string harmonicComponents = "1 2 3",
        string chordComponents = "0 4 7",
        bool randomizePhase = true, bool? gainCompensation = null,
        double? cycleLength = null)
        : base(id)
    {
        if (!Enum.IsDefined(distribution))
            throw new ArgumentOutOfRangeException(nameof(distribution));
        if (inputNodeId is < 0)
            throw new ArgumentOutOfRangeException(nameof(inputNodeId));
        if (!double.IsFinite(detune) || detune < 0 || detune > 12000)
            throw new ArgumentOutOfRangeException(nameof(detune));
        if (voiceCount is < 1 or > MaximumVoices)
            throw new ArgumentOutOfRangeException(nameof(voiceCount));
        if (cycleLength.HasValue
            && (!(cycleLength.Value > 0) || !double.IsFinite(cycleLength.Value)
                || cycleLength.Value > MaximumCycleLengthAtReference))
            throw new ArgumentOutOfRangeException(nameof(cycleLength));
        // Validate the two fields independently. A switch of Distribution must
        // never reinterpret a chord's zero as an invalid harmonic ratio.
        ParseComponents(harmonicComponents, requirePositive: true);
        ParseComponents(chordComponents, requirePositive: false);
        InputNodeId = inputNodeId;
        _inputs = inputNodeId is int input
            ? Array.AsReadOnly(new[] { input }) : Array.Empty<int>();
        Distribution = distribution;
        Detune = detune;
        VoiceCount = voiceCount;
        HarmonicComponents = harmonicComponents;
        ChordComponents = chordComponents;
        RandomizePhase = randomizePhase;
        GainCompensation = gainCompensation ?? distribution == FmMultiDistribution.Spread;
        if (GetFrequencyRatios().Any(ratio => !double.IsFinite(ratio) || ratio <= 0))
            throw new ArgumentOutOfRangeException(nameof(chordComponents),
                "Component frequencies must resolve to positive finite ratios.");
        CycleLength = cycleLength;
    }

    public int? InputNodeId { get; }
    public FmMultiDistribution Distribution { get; }
    public double Detune { get; }
    public int VoiceCount { get; }
    public string HarmonicComponents { get; }
    public string ChordComponents { get; }
    public string Components => Distribution == FmMultiDistribution.Harmonic
        ? HarmonicComponents : ChordComponents;
    public bool RandomizePhase { get; }
    public bool GainCompensation { get; }
    /// <summary>Optional cycle length in samples at C4 and 48 kHz.</summary>
    public double? CycleLength { get; }
    public override IReadOnlyList<int> InputNodeIds => _inputs;

    public FmMultiNode With(
        int? inputNodeId, FmMultiDistribution distribution,
        double detune, int voiceCount, string harmonicComponents,
        string chordComponents, bool randomizePhase, bool gainCompensation,
        double? cycleLength)
        => new(Id, inputNodeId, distribution, detune, voiceCount,
            harmonicComponents, chordComponents, randomizePhase,
            gainCompensation, cycleLength);

    public FmMultiNode WithInput(int? id) => With(id, Distribution,
        Detune, VoiceCount, HarmonicComponents, ChordComponents,
        RandomizePhase, GainCompensation, CycleLength);

    public FmMultiNode WithDistribution(FmMultiDistribution value)
        => With(InputNodeId, value, Detune, VoiceCount,
            HarmonicComponents, ChordComponents, RandomizePhase,
            value == FmMultiDistribution.Spread, CycleLength);

    public FmMultiNode WithPreset(string name)
    {
        FmMultiDistribution distribution;
        double detune = Detune;
        int voices = VoiceCount;
        string harmonics = HarmonicComponents;
        string chord = ChordComponents;
        switch (name)
        {
            case "Super saw/super square":
                distribution = FmMultiDistribution.Spread;
                detune = 12; voices = 7; break;
            case "Unison":
                distribution = FmMultiDistribution.Spread;
                detune = 15; voices = 2; break;
            case "Chorus":
                distribution = FmMultiDistribution.Spread;
                detune = 25; voices = 4; break;
            case "Power Chord":
                distribution = FmMultiDistribution.Harmonic;
                harmonics = "1 3/2 2"; break;
            case "Major Third":
                distribution = FmMultiDistribution.Chord;
                chord = "0 4"; break;
            case "Major Sixth":
                distribution = FmMultiDistribution.Chord;
                chord = "0 9"; break;
            case "Major Triad":
                distribution = FmMultiDistribution.Chord;
                chord = "0 4 7"; break;
            case "Minor Third":
                distribution = FmMultiDistribution.Chord;
                chord = "0 3"; break;
            case "Minor Sixth":
                distribution = FmMultiDistribution.Chord;
                chord = "0 8"; break;
            case "Minor Triad":
                distribution = FmMultiDistribution.Chord;
                chord = "0 3 7"; break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
        // Presets are unconditional operations. Even if Distribution is the
        // same, reset gain and enable independently randomized phases.
        return With(InputNodeId, distribution, detune, voices, harmonics,
            chord, true, distribution == FmMultiDistribution.Spread,
            CycleLength);
    }

    public static IReadOnlyList<string> Presets { get; } = Array.AsReadOnly(
        new[] { "Super saw/super square", "Unison", "Chorus", "Power Chord",
            "Major Third", "Major Sixth", "Major Triad",
            "Minor Third", "Minor Sixth", "Minor Triad" });

    public double[] GetFrequencyRatios()
    {
        if (Distribution == FmMultiDistribution.Spread)
        {
            double[] ratios = new double[VoiceCount];
            for (int i = 0; i < ratios.Length; i++)
            {
                double cents = ratios.Length == 1 ? 0.0
                    : Detune * ((double)i / (ratios.Length - 1) - 0.5);
                ratios[i] = Math.Pow(2.0, cents / 1200.0);
            }
            return ratios;
        }
        double[] values = ParseComponents(Components,
            requirePositive: Distribution == FmMultiDistribution.Harmonic);
        return Distribution == FmMultiDistribution.Harmonic ? values
            : values.Select(semitones => Math.Pow(2.0, semitones / 12.0)).ToArray();
    }

    public static double[] ParseComponents(string text, bool requirePositive)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] tokens = text.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > MaximumVoices)
            throw new ArgumentException("Components must contain 1 to 128 values.", nameof(text));
        double[] result = new double[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            string[] parts = token.Split('/');
            if (parts.Length is < 1 or > 2
                || !double.TryParse(parts[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double number))
                throw new ArgumentException($"Invalid component '{token}'.", nameof(text));
            if (parts.Length == 2)
            {
                if (!double.TryParse(parts[1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double denominator)
                    || denominator == 0 || !double.IsFinite(denominator))
                    throw new ArgumentException($"Invalid fraction '{token}'.", nameof(text));
                number /= denominator;
            }
            if (!double.IsFinite(number) || (requirePositive && number <= 0))
                throw new ArgumentException($"Invalid component '{token}'.", nameof(text));
            result[i] = number;
        }
        return result;
    }
}

using System;

using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.Styling;

/// <summary>
/// Canonical light background for a song object's TYPE, independent of
/// SongObject.SourceColourArgb (the Pattern editor's per-source row colour).
/// Every object-name surface must reuse this palette rather than duplicating
/// colour constants.
/// </summary>
public static class SongObjectTypePalette
{
	public static readonly Color Sample = Color.FromRgb(0xFB, 0xBD, 0x73);
	public static readonly Color Synth = Color.FromRgb(0x73, 0xFB, 0xF3);
	public static readonly Color FilterOneStage =
		Color.FromRgb(0xFE, 0xE0, 0xDE);
	public static readonly Color FilterFourStages =
		Color.FromRgb(0xF7, 0x98, 0x93);
	public static readonly Color Envelope = Color.FromRgb(0xD2, 0x9B, 0xF9);
	public static readonly Color ScriptedEnvelope =
		Color.FromRgb(0xDE, 0x97, 0xF9);
	public static readonly Color Instrument =
		Color.FromRgb(0xB2, 0xDB, 0xFA);
	public static readonly Color ScriptedInstrument =
		Color.FromRgb(0xB8, 0xCF, 0xFA);
	public static readonly Color Pattern = Color.FromRgb(0xB8, 0xFA, 0xB8);
	public static readonly Color ScriptedPattern =
		Color.FromRgb(0x91, 0xF9, 0x75);
	public static readonly Color Sequence = Color.FromRgb(0xFF, 0xFF, 0xC0);
	public static readonly Color ScriptedSequence =
		Color.FromRgb(0xFF, 0xFF, 0x40);

	/// <summary>
	/// Unrecognized objects, folders and unresolved references have no type
	/// highlight. Scripted subclasses must precede their base object kinds.
	/// </summary>
	public static Color? ForObject(SongObject? songObject)
		=> songObject switch
		{
			null => null,
			SampleDefinition => Sample,
			FmSynthDefinition => Synth,
			FilterDefinition filter => ForFilterStages(filter.Stages.Count),
			ScriptEnvelopeDefinition => ScriptedEnvelope,
			EnvelopeDefinition => Envelope,
			ScriptInstrumentDefinition => ScriptedInstrument,
			InstrumentDefinitionBase => Instrument,
			ScriptPatternDefinition => ScriptedPattern,
			PatternDefinition => Pattern,
			ScriptSequenceDefinition => ScriptedSequence,
			SequenceDefinition => Sequence,
			_ => null,
		};

	/// <summary>Linear RGB interpolation with one-stage and four-or-more
	/// stage endpoints. Two/three stages are at 1/3 and 2/3 respectively.</summary>
	public static Color ForFilterStages(int stageCount)
	{
		if (stageCount < 0)
			throw new ArgumentOutOfRangeException(nameof(stageCount));
		double fraction = Math.Clamp(stageCount - 1, 0, 3) / 3.0;
		return Color.FromRgb(
			Interpolate(FilterOneStage.R, FilterFourStages.R, fraction),
			Interpolate(FilterOneStage.G, FilterFourStages.G, fraction),
			Interpolate(FilterOneStage.B, FilterFourStages.B, fraction));
	}

	private static byte Interpolate(byte start, byte end, double fraction)
		=> checked((byte)Math.Round(
			start + (end - start) * fraction,
			MidpointRounding.AwayFromZero));
}

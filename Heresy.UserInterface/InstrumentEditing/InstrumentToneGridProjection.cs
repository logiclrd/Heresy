using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Instruments;
using Heresy.Core.Objects;

namespace Heresy.UserInterface.InstrumentEditing;

/// <summary>
/// Mutable editor-local tone cells. No Source means an unpublished draft:
/// even a populated pitch/envelope set is *not* a persisted tone specification.
/// </summary>
public readonly record struct InstrumentToneGridCells(
	ObjectId? SourceId = null,
	double PitchMultiplier = 1.0,
	ObjectId? VolumeEnvelopeId = null,
	ObjectId? PitchEnvelopeId = null,
	ObjectId? PanningEnvelopeId = null,
	ObjectId? FilterEnvelopeId = null)
{
	public bool HasSource => SourceId is ObjectId id && !id.IsNone;

	public static InstrumentToneGridCells FromTone(ToneSpecification tone)
		=> new(
			tone.SourceId,
			tone.PitchMultiplier,
			tone.VolumeEnvelopeId,
			tone.PitchEnvelopeId,
			tone.PanningEnvelopeId,
			tone.FilterEnvelopeId);

	public ToneSpecification ToTone()
	{
		if (SourceId is not ObjectId id || id.IsNone)
			throw new InvalidOperationException("Unassigned grid drafts have no persistent tone specification.");
		return new ToneSpecification
		{
			SourceId = id,
			PitchMultiplier = PitchMultiplier,
			VolumeEnvelopeId = VolumeEnvelopeId,
			PitchEnvelopeId = PitchEnvelopeId,
			PanningEnvelopeId = PanningEnvelopeId,
			FilterEnvelopeId = FilterEnvelopeId,
		};
	}
}

/// <summary>
/// The first row has no index; all other rows are in descending index order.
/// Out-of-range rows have the requested translucent red ARGB 40FF0000.
/// </summary>
public sealed record InstrumentToneGridRow(
	int? Index,
	InstrumentToneGridCells Cells,
	bool IsOutOfRange)
{
	public bool IsEntry => !Index.HasValue;
	public uint? BackgroundArgb =>
		IsOutOfRange ? 0x40FF0000U : null;
}

/// <summary>
/// Pure, deterministic pitch-index projection. Core lookup uses
/// Round(log2(pitch) * Divisions + Offset, AwayFromZero); C-11 is seven
/// octaves above its C-4 reference (pitch multiplier 128).
/// </summary>
public static class InstrumentToneGridProjection
{
	/// <summary>
	/// Exclusive upper bound for regular rows: index is nonnegative,
	/// index &lt; 10 * Divisions, and index &lt;= the rounded C-11 index.
	/// Index arithmetic is never coerced by rounding Divisions itself.
	/// </summary>
	public static int RegularExclusiveEnd(double divisions, int offset)
	{
		if (!(divisions > 0) || !double.IsFinite(divisions))
			throw new ArgumentOutOfRangeException(nameof(divisions));

		double tenOctaves = 10.0 * divisions;
		double c11Continuous = 7.0 * divisions + offset;
		// C-11 is inclusive; the ten-octave limit is exclusive.
		// Ceiling below uses double until the final exact-int bound.
		double c11Index = Math.Round(
			c11Continuous, 0, MidpointRounding.AwayFromZero);
		double upper = Math.Min(
			Math.Ceiling(tenOctaves), c11Index + 1.0);
		if (upper <= 0)
			return 0;
		if (upper > int.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(divisions),
				"The requested instrument grid exceeds the supported index range.");
		}
		return checked((int)upper);
	}

	/// <summary>
	/// Creation is capped to avoid accidentally materializing millions of
	/// visual rows from a syntactically valid but enormous Divisions value.
	/// This is an explicit failure rather than silently dropping tone rows.
	/// </summary>
	public static int MaxRegularRows { get; } = 100_000;

	public static IReadOnlyList<InstrumentToneGridRow> Project(
		InstrumentDefinition instrument,
		IReadOnlyDictionary<int, InstrumentToneGridCells>? editorDrafts = null)
	{
		ArgumentNullException.ThrowIfNull(instrument);
		int regularEnd = RegularExclusiveEnd(
			instrument.Divisions, instrument.Offset);
		if (regularEnd > MaxRegularRows)
		{
			throw new InvalidOperationException(
				$"Instrument would require {regularEnd} regular tone rows, exceeding the {MaxRegularRows} row editor limit.");
		}

		Dictionary<int, InstrumentToneGridCells> visible = [];
		for (int i = 0; i < instrument.ToneTable.Count; i++)
		{
			int specIndex = instrument.ToneTable[i];
			if (specIndex == -1)
				continue;
			if ((uint)specIndex >= (uint)instrument.ToneSpecifications.Count)
			{
				throw new InvalidOperationException(
					$"Tone-table index {i} references invalid specification {specIndex}.");
			}
			visible[i] = InstrumentToneGridCells.FromTone(
				instrument.ToneSpecifications[specIndex]);
		}

		// Drafts override persisted cells without mutating the song.
		// In particular, clearing a Source keeps other UI cell values.
		if (editorDrafts is not null)
		{
			foreach ((int index, InstrumentToneGridCells cells) in editorDrafts)
			{
				if (index < 0)
					throw new ArgumentOutOfRangeException(nameof(editorDrafts));
				visible[index] = cells;
			}
		}

		List<InstrumentToneGridRow> rows = [
			new InstrumentToneGridRow(null, new(), false),
		];
		foreach (int index in visible.Keys.Where(i => i >= regularEnd)
			.OrderByDescending(i => i))
		{
			rows.Add(new InstrumentToneGridRow(
				index, visible[index], true));
		}
		for (int index = regularEnd - 1; index >= 0; index--)
		{
			rows.Add(new InstrumentToneGridRow(
				index, visible.GetValueOrDefault(index), false));
		}
		return rows;
	}
}

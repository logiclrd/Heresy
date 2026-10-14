using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>Finite displayed rectangle for a known, non-mixdown nested source.
/// Coordinates are displayed rows (Sequence occurrence-specific) and host
/// channels, both inclusive. These are editor hints, not playback commands.</summary>
public sealed record FlattenedSourceRange(
    ObjectId SourceId, int FirstRow, int FirstChannel,
    int LastRow, int LastChannel, uint FillArgb, uint OutlineArgb);

/// <summary>One of the possibly several range decorations at a displayed
/// cell. Edge flags draw only the exterior of the flattened rectangle; the
/// origin already has its own 20%-alpha Note-start cell tint.</summary>
public readonly record struct FlattenedSourceRangeCell(
    ObjectId SourceId, uint FillArgb, uint OutlineArgb,
    bool IsOrigin, bool LeftEdge, bool TopEdge,
    bool RightEdge, bool BottomEdge);

/// <summary>
/// Editor-only materialized projection. The expensive work is performed once
/// per edit/visual refresh, not by searching every Pattern at paint time.
/// </summary>
public sealed class FlattenedSourceRangeProjection
{
    public const uint ConflictingNoteStartArgb = 0x80FF0000U;
    private FlattenedSourceRangeProjection(
        IReadOnlyList<FlattenedSourceRange> ranges,
        IReadOnlyDictionary<(int Row, int Channel),
            IReadOnlyList<FlattenedSourceRangeCell>> cells,
        IReadOnlySet<(int Row, int Channel)> conflictingNoteStarts)
    {
        Ranges = ranges;
        Cells = cells;
        ConflictingNoteStarts = conflictingNoteStarts;
    }

    public IReadOnlyList<FlattenedSourceRange> Ranges { get; }

    public IReadOnlyDictionary<(int Row, int Channel),
        IReadOnlyList<FlattenedSourceRangeCell>> Cells { get; }

    /// <summary>Later authored Note starts in a preceding flattening range,
    /// ordered by ascending display row then channel. Their own Source colour
    /// is replaced by the red conflict diagnostic, not their note or routing.</summary>
    public IReadOnlySet<(int Row, int Channel)> ConflictingNoteStarts { get; }

    /// <summary>Compose red diagnostic tints above ordinary effective-source
    /// colours without changing the source attribution projection or notes.
    /// Unresolved later authored starts can still carry a conflict warning,
    /// but never acquire a fictional playable-source colour.</summary>
    public IReadOnlyDictionary<(int Row, int Channel), uint> ApplyConflictTints(
        IReadOnlyDictionary<(int Row, int Channel), uint> sourceTints)
    {
        ArgumentNullException.ThrowIfNull(sourceTints);
        Dictionary<(int Row, int Channel), uint> display = new(sourceTints);
        foreach (var address in ConflictingNoteStarts)
            display[address] = ConflictingNoteStartArgb;
        return display;
    }

    public static FlattenedSourceRangeProjection Analyze(
        SongDocument document, PatternEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        List<FlattenedSourceRange> ranges = [];
        Dictionary<(int Row, int Channel),
            List<FlattenedSourceRangeCell>> cells = [];
        HashSet<(int Row, int Channel)> conflicts = [];

        // The same live source-memory projection supplies base note tinting.
        // Avoid separately guessing inline Source, missing refs or Bxx/Cxx.
        var starts = PatternSourceTintResolver.ResolveNoteStarts(document, context);
        // Traverse every *authored* Note start in row-major order, including
        // starts whose Source is currently unknown. A known earlier flattened
        // shadow plus an authored later start is a visible overlap regardless
        // of whether the later start can resolve to a playable Source.
        // Crucially, inspect the already accumulated cells BEFORE inserting
        // the current start's own range: a range never conflicts with itself.
        for (int displayRow = 0; displayRow < context.Rows.Count; displayRow++)
        {
            PatternEditorRow editorRow = context.GetRow(displayRow);
            for (int hostChannel = 0; hostChannel < editorRow.Pattern.ChannelCount; hostChannel++)
            {
                PatternCell? cell = editorRow.Pattern.Grid[
                    editorRow.PatternRow, hostChannel];
                if (cell?.Note is not StartPatternNote start)
                    continue;

                var address = (Row: displayRow, Channel: hostChannel);
                if (cells.TryGetValue(address, out var earlierRanges)
                    && earlierRanges.Count != 0)
                    conflicts.Add(address);

                if (start.Mixdown
                    || !starts.TryGetValue(address, out SongObject? source)
                    || source.SourceColourArgb is not uint tint
                    || !TryGetFootprint(document, source,
                        out int height, out int width))
                    continue;

                // A displayed Sequence entry represents only its own (possibly
                // StartRow-skipped) part of the host Pattern. Never leak its range
                // into the next order or a separate occurrence of that Pattern.
                var (_, segmentLastRow) =
                    context.GetPatternDisplayBounds(address.Row);
                int lastRow = (int)Math.Min(segmentLastRow,
                    Math.Min((long)address.Row + height - 1,
                        (long)address.Row + editorRow.Pattern.RowCount
                            - editorRow.PatternRow - 1));
                int lastChannel = (int)Math.Min(
                    editorRow.Pattern.ChannelCount - 1,
                    (long)address.Channel + width - 1);
                if (lastRow < address.Row || lastChannel < address.Channel)
                    continue;

                // The originating host channel owns the flattened logical
                // note. A later Start, OFF or CUT on that same channel ends
                // its entire multichannel flattened footprint BEFORE the
                // terminating row. Notes in the other covered channels are
                // independent of this controller and remain genuine
                // overlapping starts (red diagnostics). This is purely
                // an editor footprint: it never changes note lifetimes.
                for (int laterRow = address.Row + 1; laterRow <= lastRow; laterRow++)
                {
                    PatternEditorRow later = context.GetRow(laterRow);
                    PatternNoteEntry? laterNote = later.Pattern.Grid[
                        later.PatternRow, address.Channel]?.Note;
                    if (laterNote is StartPatternNote
                        or PatternNoteOff or PatternNoteCut)
                    {
                        lastRow = laterRow - 1;
                        break;
                    }
                }

                uint outline = (tint & 0x00FFFFFFU)
                    | ((uint)Math.Min(255, (int)(tint >> 24) * 2) << 24);
                ranges.Add(new FlattenedSourceRange(
                    source.Id, address.Row, address.Channel,
                    lastRow, lastChannel, tint, outline));

                for (int row = address.Row; row <= lastRow; row++)
                {
                    for (int channel = address.Channel;
                        channel <= lastChannel; channel++)
                    {
                        var position = (row, channel);
                        if (!cells.TryGetValue(position, out var layers))
                        {
                            layers = [];
                            cells.Add(position, layers);
                        }
                        layers.Add(new FlattenedSourceRangeCell(
                            source.Id, tint, outline,
                            row == address.Row && channel == address.Channel,
                            channel == address.Channel,
                            row == address.Row,
                            channel == lastChannel,
                            row == lastRow));
                    }
                }
            }
        }

        Dictionary<(int Row, int Channel),
            IReadOnlyList<FlattenedSourceRangeCell>> projected = [];
        foreach (var (address, layers) in cells)
            projected.Add(address, layers.ToArray());
        return new FlattenedSourceRangeProjection(ranges, projected, conflicts);
    }

    private static bool TryGetFootprint(SongDocument document,
        SongObject source, out int height, out int width)
    {
        height = 1;
        width = 1;

        if (source is DataPatternDefinition pattern)
        {
            PatternNoteEventBounds bounds = pattern.NoteEventBounds;
            height = bounds.RowCount;
            width = bounds.ChannelCount;
            return true;
        }
        if (source is not DataSequenceDefinition sequence)
            return false; // Script/unknown Pattern or Sequence has no finite map.

        long elapsed = 0;
        long lastEventRow = 0;
        int lastEventChannel = 0;
        foreach (SequenceEntry entry in sequence.Entries)
        {
            if (!document.TryGet(entry.PatternId, out SongObject? target)
                || target is not DataPatternDefinition part)
                return false;

            int start = Math.Min(entry.StartRow, part.RowCount);
            int visibleRows = part.RowCount - start;
            // An explicitly jumping/breaking Pattern does not have the
            // deterministic linear order/timing required for a rectangle.
            for (int row = start; row < part.RowCount; row++)
            {
                for (int channel = 0; channel < part.ChannelCount; channel++)
                {
                    PatternCell? cell = part.Grid[row, channel];
                    if (cell is not null
                        && cell.Effects.Any(effect =>
                            effect is TrackerOrderJumpPatternEffect
                                or TrackerPatternBreakPatternEffect))
                        return false;
                }
            }

            // The inclusive cache is exact for a full Pattern entry. For a
            // StartRow-skipped entry, only the actually executed suffix counts
            // toward either bound; a Note before StartRow is not included.
            PatternNoteEventBounds bounds = part.NoteEventBounds;
            if (bounds.HasNoteEvents && bounds.LastRow >= start)
            {
                if (start == 0)
                {
                    lastEventRow = Math.Max(lastEventRow,
                        elapsed + bounds.LastRow);
                    lastEventChannel = Math.Max(lastEventChannel,
                        bounds.LastChannel);
                }
                else
                {
                    for (int row = start; row < part.RowCount; row++)
                    {
                        for (int channel = 0; channel < part.ChannelCount; channel++)
                        {
                            if (part.Grid[row, channel]?.Note is null)
                                continue;
                            lastEventRow = Math.Max(lastEventRow,
                                elapsed + row - start);
                            lastEventChannel = Math.Max(lastEventChannel, channel);
                        }
                    }
                }
            }
            // A later order begins after the FULL played duration, even if
            // the previous Pattern's last Note event is much earlier.
            elapsed += visibleRows;
            if (elapsed > int.MaxValue)
                return false;
        }

        height = (int)Math.Min(int.MaxValue, lastEventRow + 1);
        width = lastEventChannel + 1;
        return true;
    }
}

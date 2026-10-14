using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Editor-only projection of resolved note-start Source colours. This follows
/// the same explicit Source-column / inline-note precedence as the existing
/// flattened-source analysis, with independent channel Source memory.
///
/// The projection is conservative: sources inherited from an unknown caller,
/// missing/scripted sequence segments and Bxx/Cxx flow transfers cannot be
/// resolved statically and therefore receive no invented tint. No song state,
/// playback source dispatch or pattern cells are modified.
/// </summary>
public static class PatternSourceTintResolver
{
    public static IReadOnlyDictionary<(int Row, int Channel), uint> Analyze(
        SongDocument document, PatternEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        Dictionary<(int Row, int Channel), uint> tints = [];
        ObjectId[] sources = new ObjectId[context.MaxChannelCount];

        foreach (PatternEditorSegment segment in context.Segments)
        {
            if (segment.Pattern is null)
            {
                // A Script Pattern or unavailable order can change the source
                // of every channel. Never give subsequent notes a stale tint.
                Array.Fill(sources, ObjectId.None);
                continue;
            }

            for (int displayRow = segment.FirstDisplayRow;
                 displayRow < segment.FirstDisplayRow + segment.DisplayRowCount;
                 displayRow++)
            {
                PatternEditorRow editorRow = context.GetRow(displayRow);
                bool uncertainFlow = false;
                for (int channel = 0;
                     channel < editorRow.Pattern.ChannelCount; channel++)
                {
                    PatternCell? cell =
                        editorRow.Pattern.Grid[editorRow.PatternRow, channel];
                    if (cell is null)
                        continue;

                    ObjectId selected =
                        PatternSourceSelection.ResolveSourceSelection(cell);
                    if (!selected.IsNone)
                        sources[channel] = selected;

                    // OFF/CUT, source-only, volume-only and effects-only cells
                    // are not note starts; they never receive a tint.
                    if (cell.Note is StartPatternNote
                        && !sources[channel].IsNone
                        && document.TryGet(sources[channel], out SongObject? source)
                        && source is not null
                        && source.SourceColourArgb is uint colour
                        && PatternSourceCatalog.IsSoundSource(source.Kind))
                    {
                        tints[(displayRow, channel)] = colour;
                    }

                    foreach (PatternEffect effect in cell.Effects)
                    {
                        if (effect is TrackerOrderJumpPatternEffect
                            or TrackerPatternBreakPatternEffect)
                        {
                            uncertainFlow = true;
                            break;
                        }
                    }
                }

                // The current row was processed in deterministic order, but
                // later rows can be skipped, repeated or entered mid-pattern.
                if (uncertainFlow)
                    Array.Fill(sources, ObjectId.None);
            }
        }
        return tints;
    }
}

using System;

using Heresy.Core.Patterns;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Selection-based, read-only keyboard guidance. No code here writes the
/// Pattern editor's diagnostic/status message, which must survive cursor
/// movement, effect-stack expansion and tooltip hover.
/// </summary>
public static class PatternEffectStatusHints
{
	public static string? ForSelection(
		PatternEffectCursor cursor, PatternCell? cell)
	{
		ArgumentNullException.ThrowIfNull(cursor);

		if (cursor.Field is not (
			PatternCellField.EffectCommand
			or PatternCellField.EffectParameter))
			return null;

		if (cell is null || cell.Effects.Count == 0)
			return "Type a tracker command A–Z, or right-click to insert an effect.";

		if (!cursor.IsExpanded && cell.Effects.Count > 1)
			return "Expand stacked effects with Enter.";

		int index = cursor.IsExpanded ? cursor.ExpandedEffectIndex : 0;
		if ((uint)index >= (uint)cell.Effects.Count)
			return null;

		if (!PatternEffectCodec.IsTrackerStyle(cell.Effects[index]))
			return "Configure effect with Enter.";

		return cursor.IsExpanded
			? "Edit the tracker command or parameter; Enter collapses the effect stack."
			: "Type a tracker command A–Z or edit its two-digit hex parameter.";
	}

	public static string RejectedInput(
		PatternEffectCursor cursor, PatternCell? cell)
	{
		ArgumentNullException.ThrowIfNull(cursor);

		if (cell is not null)
		{
			if (!cursor.IsExpanded && cell.Effects.Count > 1)
				return "Cannot type into a collapsed effect stack. Expand stacked effects with Enter.";

			int index = cursor.IsExpanded ? cursor.ExpandedEffectIndex : 0;
			if ((uint)index < (uint)cell.Effects.Count
				&& !PatternEffectCodec.IsTrackerStyle(cell.Effects[index]))
				return "Native effects cannot be edited as tracker text. Configure effect with Enter.";
		}

		return "Invalid tracker effect input. Enter a supported command A–Z or a hexadecimal parameter.";
	}
}

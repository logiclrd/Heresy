using System;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.PatternEditing;

public sealed class PatternVolumeInputState
{
	internal int? PendingTensDigit { get; private set; }

	internal void SetPendingTensDigit(int digit)
		=> PendingTensDigit = digit;

	public void Reset()
		=> PendingTensDigit = null;
}

public readonly record struct PatternVolumeInputResult(
	bool Handled,
	bool Changed,
	bool Rejected)
{
	public bool Completed { get; init; }
}

public static class PatternVolumeKeyboardEditor
{
	public static PatternVolumeInputResult Type(
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		PatternEffectCursor cursor,
		PatternVolumeInputState state,
		char value)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(cursor);
		ArgumentNullException.ThrowIfNull(state);

		if (cursor.Field != PatternCellField.Volume)
			return new PatternVolumeInputResult(false, false, false);

		if (value == '.')
		{
			bool cleared =
				pattern.Grid[cursor.Row, cursor.Channel]?.Volume is not null;
			PatternDocumentEditor.SetVolume(
				workspace,
				pattern,
				cursor.Row,
				cursor.Channel,
				null);
			state.Reset();
			cursor.AdvanceAfterCollapsedEntry(pattern.RowCount);
			return new PatternVolumeInputResult(true, cleared, false)
			{
				Completed = true,
			};
		}

		if (value is < '0' or > '9')
			return new PatternVolumeInputResult(false, false, false);

		int digit = value - '0';
		if (!state.PendingTensDigit.HasValue)
		{
			if (digit > 6)
			{
				state.Reset();
				return new PatternVolumeInputResult(true, false, true);
			}

			state.SetPendingTensDigit(digit);
			return new PatternVolumeInputResult(true, false, false);
		}

		int trackerVolume =
			(state.PendingTensDigit.Value * 10) + digit;
		state.Reset();

		if (trackerVolume > 64)
			return new PatternVolumeInputResult(true, false, true);

		double volume = trackerVolume / 64.0;
		bool changed =
			pattern.Grid[cursor.Row, cursor.Channel]?.Volume != volume;
		PatternDocumentEditor.SetVolume(
			workspace,
			pattern,
			cursor.Row,
			cursor.Channel,
			volume);
		cursor.AdvanceAfterCollapsedEntry(pattern.RowCount);
		return new PatternVolumeInputResult(true, changed, false)
		{
			Completed = true,
		};
	}
}

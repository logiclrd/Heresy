using System;

using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.PatternEditing;

public enum PatternRowMutationKind
{
	Insert,
	Delete,
}

/// <summary>
/// Applies fixed-length row shifts to the live pattern row represented by the
/// current editor cursor.
/// </summary>
public static class PatternEditorRowMutation
{
	public static bool Apply(
		DocumentWorkspace workspace,
		PatternEditorContext context,
		PatternEffectCursor cursor,
		PatternRowMutationKind kind,
		bool allChannels)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(cursor);

		if (context.Rows.Count == 0)
			return false;

		PatternEditorRow row =
			context.GetRow(cursor.Row);
		int? channel =
			allChannels
				? null
				: cursor.Channel;

		return kind switch
		{
			PatternRowMutationKind.Insert =>
				PatternDocumentEditor.InsertRow(
					workspace,
					row.Pattern,
					row.PatternRow,
					channel),
			PatternRowMutationKind.Delete =>
				PatternDocumentEditor.DeleteRow(
					workspace,
					row.Pattern,
					row.PatternRow,
					channel),
			_ => throw new ArgumentOutOfRangeException(nameof(kind)),
		};
	}
}

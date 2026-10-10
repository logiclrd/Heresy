using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// One stable layout for the live document pane projection. Columns are
/// sixths: two panes across the top span three, three below span two.
/// This keeps fixed section routing independent of the tree's persisted order.
/// </summary>
public readonly record struct DocumentPanePosition(
	SongTreeSection Section, int Row, int Column, int ColumnSpan);

public static class DocumentPaneLayout
{
	public static IReadOnlyList<DocumentPanePosition> Panes { get; } =
	[
		new(SongTreeSection.Sequences, 0, 0, 3),
		new(SongTreeSection.Patterns, 0, 3, 3),
		new(SongTreeSection.Samples, 1, 0, 2),
		new(SongTreeSection.Envelopes, 1, 2, 2),
		new(SongTreeSection.Instruments, 1, 4, 2),
	];
}

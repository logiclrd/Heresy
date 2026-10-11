using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Four equal tracks: two half-width panes above four quarter-width panes.
/// Fixed semantic section routing is independent of persisted tree order.
/// </summary>
public readonly record struct DocumentPanePosition(
	SongTreeSection Section, int Row, int Column, int ColumnSpan);

public static class DocumentPaneLayout
{
	public const int ColumnCount = 4;

	public static IReadOnlyList<DocumentPanePosition> Panes { get; } =
	[
		new(SongTreeSection.Sequences, 0, 0, 2),
		new(SongTreeSection.Patterns, 0, 2, 2),
		new(SongTreeSection.Samples, 1, 0, 1),
		new(SongTreeSection.Filters, 1, 1, 1),
		new(SongTreeSection.Envelopes, 1, 2, 1),
		new(SongTreeSection.Instruments, 1, 3, 1),
	];
}

using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class DocumentPaneLayoutTests
{
	[Test]
	public void TwoTopPanesAndFourBottomPanesFillFourEqualTracks()
	{
		DocumentPanePosition[] panes = DocumentPaneLayout.Panes.ToArray();
		panes.Should().Equal(
			new DocumentPanePosition(SongTreeSection.Sequences, 0, 0, 2),
			new DocumentPanePosition(SongTreeSection.Patterns, 0, 2, 2),
			new DocumentPanePosition(SongTreeSection.Samples, 1, 0, 1),
			new DocumentPanePosition(SongTreeSection.Filters, 1, 1, 1),
			new DocumentPanePosition(SongTreeSection.Envelopes, 1, 2, 1),
			new DocumentPanePosition(SongTreeSection.Instruments, 1, 3, 1));

		foreach (int row in new[] { 0, 1 })
		{
			bool[] occupied = new bool[DocumentPaneLayout.ColumnCount];
			foreach (DocumentPanePosition pane in panes.Where(x => x.Row == row))
			{
				for (int column = pane.Column;
					column < pane.Column + pane.ColumnSpan; column++)
				{
					occupied[column].Should().BeFalse();
					occupied[column] = true;
				}
			}
			occupied.Should().OnlyContain(value => value);
		}
	}

	[Test]
	public void EachSongTreeSectionHasExactlyOnePane()
	{
		DocumentPaneLayout.Panes.Select(x => x.Section).Should()
			.BeEquivalentTo(SongTreeSections.DocumentOrder);
		DocumentPaneLayout.Panes.Select(x => x.Section).Distinct()
			.Should().HaveCount(6);
	}
}

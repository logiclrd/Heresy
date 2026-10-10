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
	public void TwoTopPanesAndThreeBottomPanesFillSixEqualTracks()
	{
		DocumentPanePosition[] panes = DocumentPaneLayout.Panes.ToArray();
		panes.Should().Equal(
			new DocumentPanePosition(SongTreeSection.Sequences, 0, 0, 3),
			new DocumentPanePosition(SongTreeSection.Patterns, 0, 3, 3),
			new DocumentPanePosition(SongTreeSection.Samples, 1, 0, 2),
			new DocumentPanePosition(SongTreeSection.Envelopes, 1, 2, 2),
			new DocumentPanePosition(SongTreeSection.Instruments, 1, 4, 2));

		foreach (int row in new[] { 0, 1 })
		{
			bool[] occupied = new bool[6];
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
			.Should().HaveCount(5);
	}
}

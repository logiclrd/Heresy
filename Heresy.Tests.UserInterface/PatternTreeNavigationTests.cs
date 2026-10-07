using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternTreeNavigationTests
{
	[Test]
	public void TreeNavigationFollowsVisibleDepthFirstPatternOrder()
	{
		SongDocument document = new();
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);

		DataPatternDefinition first = AddPattern(document, "First");
		DataPatternDefinition second = AddPattern(document, "Second");
		DataPatternDefinition third = AddPattern(document, "Third");

		SongTreeObject firstNode = (SongTreeObject)patterns.Children[0];
		SongTreeObject secondNode = (SongTreeObject)patterns.Children[1];
		SongTreeObject thirdNode = (SongTreeObject)patterns.Children[2];

		SongTreeFolder folder =
			SongTreeEditor.CreateFolder(document, patterns, "Folder");
		SongTreeEditor.MoveInto(document, secondNode, folder);

		PatternTreeNavigation.FindAdjacent(
				document,
				firstNode,
				delta: 1)
			.Should().BeSameAs(thirdNode);
		PatternTreeNavigation.FindAdjacent(
				document,
				thirdNode,
				delta: 1)
			.Should().BeSameAs(secondNode);
		PatternTreeNavigation.FindAdjacent(
				document,
				secondNode,
				delta: -1)
			.Should().BeSameAs(thirdNode);

		first.Id.Should().NotBe(ObjectId.None);
		second.Id.Should().NotBe(ObjectId.None);
		third.Id.Should().NotBe(ObjectId.None);
	}

	[Test]
	public void TreeNavigationSkipsScriptPatternsAndMissingReferences()
	{
		SongDocument document = new();
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);

		AddPattern(document, "First");
		ObjectId scriptId = document.AllocateObjectId();
		document.Add(new ScriptPatternDefinition(scriptId, "Script"));
		DataPatternDefinition missing = AddPattern(document, "Missing");
		DataPatternDefinition last = AddPattern(document, "Last");

		SongTreeObject firstNode = (SongTreeObject)patterns.Children[0];
		SongTreeObject missingNode = (SongTreeObject)patterns.Children[2];
		SongTreeObject lastNode = (SongTreeObject)patterns.Children[3];
		document.Remove(missing.Id);
		patterns.Children.Insert(2, missingNode);

		PatternTreeNavigation.FindAdjacent(
				document,
				firstNode,
				delta: 1)
			.Should().BeSameAs(lastNode);
	}

	[Test]
	public void TreeNavigationUsesPlacementIdentityForDuplicateObjects()
	{
		SongDocument document = new();
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);
		DataPatternDefinition shared = AddPattern(document, "Shared");
		DataPatternDefinition other = AddPattern(document, "Other");
		SongTreeObject firstPlacement = (SongTreeObject)patterns.Children[0];
		SongTreeObject secondPlacement =
			new("Shared elsewhere", shared.Id);
		patterns.Children.Add(secondPlacement);

		PatternTreeNavigation.FindAdjacent(
				document,
				(SongTreeObject)patterns.Children[1],
				delta: 1)
			.Should().BeSameAs(secondPlacement);
		PatternTreeNavigation.FindAdjacent(
				document,
				secondPlacement,
				delta: -1)
			.Should().BeSameAs((SongTreeObject)patterns.Children[1]);

		other.Id.Should().NotBe(ObjectId.None);
	}

	[Test]
	public void TreeNavigationStopsAtPatternSectionEdges()
	{
		SongDocument document = new();
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);
		AddPattern(document, "First");
		AddPattern(document, "Last");
		SongTreeObject first = (SongTreeObject)patterns.Children[0];
		SongTreeObject last = (SongTreeObject)patterns.Children[1];

		PatternTreeNavigation.FindAdjacent(
				document,
				first,
				delta: -1)
			.Should().BeNull();
		PatternTreeNavigation.FindAdjacent(
				document,
				last,
				delta: 1)
			.Should().BeNull();
	}

	private static DataPatternDefinition AddPattern(
		SongDocument document,
		string name)
	{
		ObjectId id = document.AllocateObjectId();
		DataPatternDefinition pattern = new(id, name);
		document.Add(pattern);
		return pattern;
	}
}

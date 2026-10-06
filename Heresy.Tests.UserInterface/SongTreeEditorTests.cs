using System;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongTreeEditorTests
{
	[Test]
	public void CreateFolderAddsOrganizationalNodeWithoutChangingAudioRevision()
	{
		SongDocument document = new();
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);
		uint documentRevision = document.DocumentRevision;
		uint audioRevision = document.AudioRevision;

		SongTreeFolder folder =
			SongTreeEditor.CreateFolder(document, patterns, "Drums");

		patterns.Children.Should().ContainSingle().Which.Should().BeSameAs(folder);
		folder.Name.Should().Be("Drums");
		document.DocumentRevision.Should().Be(documentRevision + 1);
		document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void CreateFolderRejectsDocumentRootBecauseOnlySectionRootsMayOwnTopLevelContent()
	{
		SongDocument document = new();

		Action create = () =>
			SongTreeEditor.CreateFolder(document, document.Root, "Loose");

		create.Should().Throw<InvalidOperationException>();
	}

	[Test]
	public void RenameFolderIsOrganizationalOnly()
	{
		SongDocument document = new();
		SongTreeFolder folder =
			SongTreeEditor.CreateFolder(
				document,
				document.GetSectionRoot(SongTreeSection.Patterns),
				"Old");
		uint documentRevision = document.DocumentRevision;
		uint audioRevision = document.AudioRevision;

		SongTreeEditor.RenameFolder(document, folder, "New");

		folder.Name.Should().Be("New");
		document.DocumentRevision.Should().Be(documentRevision + 1);
		document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void RenameRejectsFixedSectionRoot()
	{
		SongDocument document = new();
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);

		Action rename = () =>
			SongTreeEditor.RenameFolder(document, patterns, "Bananas");

		rename.Should().Throw<InvalidOperationException>();
	}

	[Test]
	public void RenameObjectUpdatesEveryTreePlacementWithoutChangingIdentityOrAudioRevision()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		DataPatternDefinition pattern = new(id, "Old Name");
		document.Add(pattern);
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);
		SongTreeObject first = (SongTreeObject)patterns.Children.Single();
		SongTreeFolder folder = new("Folder");
		SongTreeObject second = new("Stale Label", id);
		patterns.Children.Add(folder);
		folder.Children.Add(second);
		uint documentRevision = document.DocumentRevision;
		uint audioRevision = document.AudioRevision;

		SongTreeEditor.RenameObject(document, id, "New Name");

		pattern.Name.Should().Be("New Name");
		first.Name.Should().Be("New Name");
		second.Name.Should().Be("New Name");
		pattern.Id.Should().Be(id);
		document.DocumentRevision.Should().Be(documentRevision + 1);
		document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void DeleteObjectRemovesAllTreePlacementsButLeavesMusicalReferencesIntact()
	{
		SongDocument document = new();
		ObjectId sourceId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(sourceId, "Source"));
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);
		SongTreeObject first = (SongTreeObject)patterns.Children.Single();
		SongTreeFolder folder = new("Elsewhere");
		SongTreeObject second = new("Source", sourceId);
		patterns.Children.Add(folder);
		folder.Children.Add(second);

		ObjectId referrerId = document.AllocateObjectId();
		DataPatternDefinition referrer = new(referrerId, "Referrer");
		referrer.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sourceId);
		document.Add(referrer);
		uint audioRevision = document.AudioRevision;

		SongTreeEditor.DeleteObject(document, sourceId);

		document.Objects.Should().NotContainKey(sourceId);
		document.Tombstones[sourceId].LastKnownName.Should().Be("Source");
		patterns.Children.Should().NotContain(first);
		folder.Children.Should().NotContain(second);
		((StartPatternNote)referrer.Grid[0, 0]!.Note!).SourceId.Should().Be(sourceId);
		document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void MoveReorganizesWithinSectionWithoutChangingObjectIdentityOrAudioRevision()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Pattern"));
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);
		SongTreeObject node = (SongTreeObject)patterns.Children.Single();
		SongTreeFolder destination =
			SongTreeEditor.CreateFolder(document, patterns, "Drums");

		uint documentRevision = document.DocumentRevision;
		uint audioRevision = document.AudioRevision;

		SongTreeEditor.Move(document, node, destination, 0);

		patterns.Children.Should().NotContain(node);
		destination.Children.Should().ContainSingle().Which.Should().BeSameAs(node);
		node.ObjectId.Should().Be(id);
		document.DocumentRevision.Should().Be(documentRevision + 1);
		document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void MoveRejectsCrossingSectionBoundary()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Pattern"));
		SongTreeObject node =
			(SongTreeObject)document.GetSectionRoot(SongTreeSection.Patterns).Children.Single();

		Action move = () =>
			SongTreeEditor.MoveInto(
				document,
				node,
				document.GetSectionRoot(SongTreeSection.Samples));

		move.Should().Throw<InvalidOperationException>();
	}

	[Test]
	public void MoveBeforeReordersSiblings()
	{
		SongDocument document = new();
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);
		SongTreeFolder first = new("First");
		SongTreeFolder second = new("Second");
		SongTreeFolder third = new("Third");
		patterns.Children.Add(first);
		patterns.Children.Add(second);
		patterns.Children.Add(third);

		SongTreeEditor.MoveBefore(document, third, first);

		patterns.Children.Should().ContainInOrder(third, first, second);
	}

	[Test]
	public void MoveRejectsMovingFolderIntoItsOwnDescendant()
	{
		SongDocument document = new();
		SongTreeFolder patterns =
			document.GetSectionRoot(SongTreeSection.Patterns);
		SongTreeFolder parent = new("Parent");
		SongTreeFolder child = new("Child");
		parent.Children.Add(child);
		patterns.Children.Add(parent);

		Action move = () =>
			SongTreeEditor.Move(document, parent, child, 0);

		move.Should().Throw<InvalidOperationException>();
	}
}

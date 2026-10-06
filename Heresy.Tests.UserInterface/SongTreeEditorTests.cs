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
		uint documentRevision = document.DocumentRevision;
		uint audioRevision = document.AudioRevision;

		SongTreeFolder folder =
			SongTreeEditor.CreateFolder(document, document.Root, "Drums");

		document.Root.Children.Should().ContainSingle().Which.Should().BeSameAs(folder);
		folder.Name.Should().Be("Drums");
		document.DocumentRevision.Should().Be(documentRevision + 1);
		document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void RenameFolderIsOrganizationalOnly()
	{
		SongDocument document = new();
		SongTreeFolder folder = new("Old");
		document.Root.Children.Add(folder);
		uint documentRevision = document.DocumentRevision;
		uint audioRevision = document.AudioRevision;

		SongTreeEditor.RenameFolder(document, folder, "New");

		folder.Name.Should().Be("New");
		document.DocumentRevision.Should().Be(documentRevision + 1);
		document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void RenameObjectUpdatesEveryTreePlacementWithoutChangingIdentityOrAudioRevision()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		DataPatternDefinition pattern = new(id, "Old Name");
		document.Add(pattern);
		SongTreeObject first = new("Old Name", id);
		SongTreeFolder folder = new("Folder");
		SongTreeObject second = new("Stale Label", id);
		document.Root.Children.Add(first);
		document.Root.Children.Add(folder);
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
		SongTreeObject first = new("Source", sourceId);
		SongTreeFolder folder = new("Elsewhere");
		SongTreeObject second = new("Source", sourceId);
		document.Root.Children.Add(first);
		document.Root.Children.Add(folder);
		folder.Children.Add(second);

		ObjectId referrerId = document.AllocateObjectId();
		DataPatternDefinition referrer = new(referrerId, "Referrer");
		referrer.Grid.GetOrCreateCell(0, 0).Note = new StartPatternNote(sourceId);
		document.Add(referrer);
		uint audioRevision = document.AudioRevision;

		SongTreeEditor.DeleteObject(document, sourceId);

		document.Objects.Should().NotContainKey(sourceId);
		document.Tombstones[sourceId].LastKnownName.Should().Be("Source");
		document.Root.Children.Should().NotContain(first);
		folder.Children.Should().NotContain(second);
		((StartPatternNote)referrer.Grid[0, 0]!.Note!).SourceId.Should().Be(sourceId);
		document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void MoveReorganizesTreeWithoutChangingObjectIdentityOrAudioRevision()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Pattern"));

		SongTreeFolder destination = new("Patterns");
		SongTreeObject node = new("Pattern", id);
		document.Root.Children.Add(destination);
		document.Root.Children.Add(node);

		uint documentRevision = document.DocumentRevision;
		uint audioRevision = document.AudioRevision;

		SongTreeEditor.Move(document, node, destination, 0);

		document.Root.Children.Should().NotContain(node);
		destination.Children.Should().ContainSingle().Which.Should().BeSameAs(node);
		node.ObjectId.Should().Be(id);
		document.DocumentRevision.Should().Be(documentRevision + 1);
		document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void MoveBeforeReordersSiblings()
	{
		SongDocument document = new();
		SongTreeFolder first = new("First");
		SongTreeFolder second = new("Second");
		SongTreeFolder third = new("Third");
		document.Root.Children.Add(first);
		document.Root.Children.Add(second);
		document.Root.Children.Add(third);

		SongTreeEditor.MoveBefore(document, third, first);

		document.Root.Children.Should().ContainInOrder(third, first, second);
	}

	[Test]
	public void MoveRejectsMovingFolderIntoItsOwnDescendant()
	{
		SongDocument document = new();
		SongTreeFolder parent = new("Parent");
		SongTreeFolder child = new("Child");
		parent.Children.Add(child);
		document.Root.Children.Add(parent);

		Action move = () =>
			SongTreeEditor.Move(document, parent, child, 0);

		move.Should().Throw<InvalidOperationException>();
	}
}

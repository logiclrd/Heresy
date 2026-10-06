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

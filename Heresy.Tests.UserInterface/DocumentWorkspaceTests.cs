using System;
using System.IO;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class DocumentWorkspaceTests
{
	[Test]
	public void NewWorkspaceStartsWithCleanUntitledDocument()
	{
		DocumentWorkspace workspace = new();

		workspace.FilePath.Should().BeNull();
		workspace.DisplayName.Should().Be("Untitled");
		workspace.IsModified.Should().BeFalse();
		workspace.Document.DocumentRevision.Should().Be(0);
	}

	[Test]
	public void DocumentRevisionMakesWorkspaceModifiedUntilSave()
	{
		DocumentWorkspace workspace = new();
		workspace.Document.MarkChanged(affectsAudio: false);

		workspace.IsModified.Should().BeTrue();

		string path = Path.Combine(
			Path.GetTempPath(),
			$"heresy-ui-{Guid.NewGuid():N}.json");
		try
		{
			workspace.SaveAs(path);

			workspace.IsModified.Should().BeFalse();
			workspace.FilePath.Should().Be(Path.GetFullPath(path));
			File.Exists(path).Should().BeTrue();
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public void OpenUsesSongDocumentJsonAndStartsAtCleanSaveBaseline()
	{
		string path = Path.Combine(
			Path.GetTempPath(),
			$"heresy-ui-{Guid.NewGuid():N}.json");
		try
		{
			SongDocument source = new();
			ObjectId id = source.AllocateObjectId();
			source.Add(new DataPatternDefinition(id, "Verse"));
			SongDocumentJson.Save(path, source);

			DocumentWorkspace workspace = new();
			workspace.Open(path);

			workspace.IsModified.Should().BeFalse();
			workspace.FilePath.Should().Be(Path.GetFullPath(path));
			workspace.Document.Objects[id].Name.Should().Be("Verse");
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public void TreeProjectionUsesLiveObjectNameRatherThanStoredTreeLabel()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Live Name"));
		SongTreeObject node = new("Old Tree Label", id);
		document.Root.Children.Add(node);

		SongTreeItemViewModel item =
			SongTreeItemViewModel.Create(document, node);

		item.DisplayName.Should().Be("Live Name");
		item.IsMissingReference.Should().BeFalse();
		item.ObjectId.Should().Be(id);
	}

	[Test]
	public void TreeProjectionUsesTombstoneNameForDeletedObject()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Kick Pattern"));
		SongTreeObject node = new("Kick Pattern", id);
		document.Root.Children.Add(node);
		document.Remove(id);

		SongTreeItemViewModel item =
			SongTreeItemViewModel.Create(document, node);

		item.DisplayName.Should().Be("Kick Pattern");
		item.IsMissingReference.Should().BeTrue();
		item.Kind.Should().Be(SongObjectKind.Pattern);
	}

	[Test]
	public void TreeProjectionFallsBackToRawIdWhenNoObjectOrTombstoneExists()
	{
		SongDocument document = new();
		SongTreeObject node = new("Unknown", new ObjectId(3456));

		SongTreeItemViewModel item =
			SongTreeItemViewModel.Create(document, node);

		item.DisplayName.Should().Be("<3456>");
		item.IsMissingReference.Should().BeTrue();
		item.Kind.Should().Be(SongObjectKind.Unknown);
	}
}

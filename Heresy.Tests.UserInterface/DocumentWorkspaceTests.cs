using System;
using System.IO;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
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
			$"heresy-ui-{Guid.NewGuid():N}.hm.json");
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
			$"heresy-ui-{Guid.NewGuid():N}.hm.json");
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
	[Test]
	public void AbsoluteJsonSaveModeIsRememberedForSubsequentSave()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			$"heresy-ui-mode-{Guid.NewGuid():N}");
		string assetPath = Path.Combine(root, "outside", "tone.wav");
		string jsonPath = Path.Combine(root, "song", "track.hm.json");
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
			Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
			File.WriteAllText(assetPath, "tone");

			DocumentWorkspace workspace = new();
			ObjectId id = workspace.Document.AllocateObjectId();
			workspace.Document.Add(
				new SampleDefinition(
					id,
					"Tone",
					ExternalAssetIntegrity.CreateReference(assetPath)));

			workspace.SaveAs(jsonPath, JsonAssetPathMode.Absolute);
			workspace.JsonPathMode.Should().Be(JsonAssetPathMode.Absolute);
			workspace.Document.MarkChanged(affectsAudio: false);
			workspace.Save();

			string json = File.ReadAllText(jsonPath);
			json.Should().Contain(Path.GetFullPath(assetPath).Replace("\\", "\\\\"));
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void OpenInfersAbsoluteJsonSaveMode()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			$"heresy-ui-open-mode-{Guid.NewGuid():N}");
		string assetPath = Path.Combine(root, "outside", "tone.wav");
		string jsonPath = Path.Combine(root, "song", "track.hm.json");
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
			Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
			File.WriteAllText(assetPath, "tone");
			SongDocument source = new();
			ObjectId id = source.AllocateObjectId();
			source.Add(
				new SampleDefinition(
					id,
					"Tone",
					ExternalAssetIntegrity.CreateReference(assetPath)));
			SongDocumentStorage.Save(jsonPath, source, JsonAssetPathMode.Absolute);

			DocumentWorkspace workspace = new();
			workspace.Open(jsonPath);

			workspace.JsonPathMode.Should().Be(JsonAssetPathMode.Absolute);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

}

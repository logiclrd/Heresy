using System;
using System.IO;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleDocumentEditorTests
{
	[Test]
	public void ImportIntoUnsavedDocumentKeepsActualFullPath()
	{
		using TempProject project = new();
		string assetPath = project.Write("samples", "Kick.wav", "hello");
		DocumentWorkspace workspace = new();

		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, assetPath);

		sample.Asset.FullPath.Should().Be(Path.GetFullPath(assetPath));
		sample.Asset.Sha256.Should().Be(
			"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
		workspace.Document.GetSectionRoot(SongTreeSection.Samples)
			.Children.Should().ContainSingle()
			.Which.As<SongTreeObject>().ObjectId.Should().Be(sample.Id);
	}

	[Test]
	public void AssetCheckDoesNotRequireSavedWorkspace()
	{
		using TempProject project = new();
		string assetPath = project.Write("tone.wav", "hello");
		DocumentWorkspace workspace = new();
		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, assetPath);

		ExternalAssetCheck check =
			SampleDocumentEditor.CheckAsset(workspace, sample);

		check.Status.Should().Be(ExternalAssetStatus.Match);
		check.FullPath.Should().Be(Path.GetFullPath(assetPath));
	}

	[Test]
	public void RelinkStoresReplacementAsFullPath()
	{
		using TempProject project = new();
		string first = project.Write("first.wav", "first");
		string second = project.Write("second.wav", "second");
		DocumentWorkspace workspace = new();
		SampleDefinition sample = SampleDocumentEditor.Import(workspace, first);
		uint audioRevision = workspace.Document.AudioRevision;

		SampleDocumentEditor.Relink(workspace, sample, second);

		sample.Asset.FullPath.Should().Be(Path.GetFullPath(second));
		sample.Asset.Sha256.Should().Be(ExternalAssetIntegrity.ComputeSha256(second));
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void RefreshHashUsesFullPathAndIsDocumentOnly()
	{
		using TempProject project = new();
		string assetPath = project.Write("tone.wav", "hello");
		DocumentWorkspace workspace = new();
		SampleDefinition sample = SampleDocumentEditor.Import(workspace, assetPath);
		File.WriteAllText(assetPath, "changed");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		SampleDocumentEditor.RefreshHash(workspace, sample);

		sample.Asset.Sha256.Should().Be(ExternalAssetIntegrity.ComputeSha256(assetPath));
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}

	private sealed class TempProject : IDisposable
	{
		private readonly string _root =
			Path.Combine(
				Path.GetTempPath(),
				$"heresy-sample-ui-{Guid.NewGuid():N}");

		public TempProject() => Directory.CreateDirectory(_root);

		public string Write(params string[] partsAndContent)
		{
			string content = partsAndContent[^1];
			string path = _root;
			foreach (string part in partsAndContent[..^1])
				path = Path.Combine(path, part);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
			return path;
		}

		public void Dispose()
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
	}
}

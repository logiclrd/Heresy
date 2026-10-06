using System;
using System.IO;
using System.Text;

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
	public void ImportRequiresSavedDocumentSoAssetPathCanBeRelative()
	{
		DocumentWorkspace workspace = new();

		Action import = () =>
			SampleDocumentEditor.Import(workspace, "sample.wav");

		import.Should().Throw<InvalidOperationException>();
	}

	[Test]
	public void ImportCreatesHashedRelativeSampleAndCanonicalTreePlacement()
	{
		using TempProject project = new();
		string songPath = project.Path("songs", "track.json");
		string assetPath = project.Path("assets", "Kick.wav");
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(songPath)!);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(assetPath)!);
		File.WriteAllBytes(assetPath, Encoding.UTF8.GetBytes("hello"));

		DocumentWorkspace workspace = new();
		workspace.SaveAs(songPath);

		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, assetPath);

		sample.Name.Should().Be("Kick");
		sample.Asset.RelativePath.Should().Be("../assets/Kick.wav");
		sample.Asset.Sha256.Should().Be(
			"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
		workspace.Document.Objects[sample.Id].Should().BeSameAs(sample);
		workspace.Document.GetSectionRoot(SongTreeSection.Samples)
			.Children.Should().ContainSingle()
			.Which.As<SongTreeObject>().ObjectId.Should().Be(sample.Id);
		workspace.IsModified.Should().BeTrue();
	}

	[Test]
	public void AssetCheckUsesWorkspaceSongPath()
	{
		using TempProject project = new();
		string songPath = project.Path("track.json");
		string assetPath = project.Path("tone.wav");
		File.WriteAllText(assetPath, "hello");

		DocumentWorkspace workspace = new();
		workspace.SaveAs(songPath);
		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, assetPath);

		ExternalAssetCheck check =
			SampleDocumentEditor.CheckAsset(workspace, sample);

		check.Status.Should().Be(ExternalAssetStatus.Match);
		check.ResolvedPath.Should().Be(System.IO.Path.GetFullPath(assetPath));
	}

	[Test]
	public void MetadataChangeAdvancesAudioAndDocumentRevisionOnce()
	{
		using TempProject project = new();
		string songPath = project.Path("track.json");
		string assetPath = project.Path("tone.wav");
		File.WriteAllText(assetPath, "hello");

		DocumentWorkspace workspace = new();
		workspace.SaveAs(songPath);
		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, assetPath);
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		SampleDocumentEditor.UpdateMetadata(
			workspace,
			sample,
			440.0,
			new SampleLoop(SampleLoopMode.Forward, 10, 20));

		sample.ReferenceFrequencyHz.Should().Be(440.0);
		sample.Loop.Should().Be(new SampleLoop(SampleLoopMode.Forward, 10, 20));
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void RefreshHashIsDocumentOnlyBecauseItDoesNotChangeRenderedAudio()
	{
		using TempProject project = new();
		string songPath = project.Path("track.json");
		string assetPath = project.Path("tone.wav");
		File.WriteAllText(assetPath, "hello");

		DocumentWorkspace workspace = new();
		workspace.SaveAs(songPath);
		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, assetPath);
		File.WriteAllText(assetPath, "changed");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		SampleDocumentEditor.RefreshHash(workspace, sample);

		sample.Asset.Sha256.Should().Be(
			ExternalAssetIntegrity.ComputeSha256(assetPath));
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void RelinkChangesAudioReferenceAndRehashesReplacement()
	{
		using TempProject project = new();
		string songPath = project.Path("songs", "track.json");
		string firstPath = project.Path("assets", "first.wav");
		string secondPath = project.Path("assets", "second.wav");
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(songPath)!);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(firstPath)!);
		File.WriteAllText(firstPath, "first");
		File.WriteAllText(secondPath, "second");

		DocumentWorkspace workspace = new();
		workspace.SaveAs(songPath);
		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, firstPath);
		uint audioRevision = workspace.Document.AudioRevision;

		SampleDocumentEditor.Relink(workspace, sample, secondPath);

		sample.Asset.RelativePath.Should().Be("../assets/second.wav");
		sample.Asset.Sha256.Should().Be(
			ExternalAssetIntegrity.ComputeSha256(secondPath));
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	private sealed class TempProject : IDisposable
	{
		private readonly string _root =
			System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				$"heresy-sample-ui-{Guid.NewGuid():N}");

		public TempProject()
			=> Directory.CreateDirectory(_root);

		public string Path(params string[] parts)
		{
			string result = _root;
			foreach (string part in parts)
				result = System.IO.Path.Combine(result, part);
			return result;
		}

		public void Dispose()
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
	}
}

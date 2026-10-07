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
	public void ImportIntoUnsavedDocumentOwnsPendingEncodingAndDecodedPcm()
	{
		using TempProject project = new();
		string assetPath = project.WriteWave("samples", "Kick.wav", sample: 8192);
		DocumentWorkspace workspace = new();

		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, assetPath);

		sample.Asset.Should().BeNull();
		sample.PendingAsset.Should().NotBeNull();
		sample.PendingAsset!.FileName.Should().Be("Kick.wav");
		sample.PcmData.Should().NotBeNull();
		sample.PcmData!.SampleRate.Should().Be(8000);
		sample.PcmData.GetSample(0, 0).Should().BeApproximately(0.25f, 1e-6f);
		File.Delete(assetPath);
		sample.PcmData.GetSample(0, 0).Should().BeApproximately(0.25f, 1e-6f);
		workspace.Document.GetSectionRoot(SongTreeSection.Samples)
			.Children.Should().ContainSingle()
			.Which.As<SongTreeObject>().ObjectId.Should().Be(sample.Id);
	}

	[Test]
	public void AssetCheckReportsPendingUntilSongOwnsPersistedCopy()
	{
		using TempProject project = new();
		string assetPath = project.WriteWave("tone.wav", sample: 0);
		DocumentWorkspace workspace = new();
		SampleDefinition sample =
			SampleDocumentEditor.Import(workspace, assetPath);

		ExternalAssetCheck check =
			SampleDocumentEditor.CheckAsset(workspace, sample);

		check.Status.Should().Be(ExternalAssetStatus.Pending);
		check.FullPath.Should().Be("(pending song asset)");
		check.ExpectedSha256.Should().Be(sample.PendingAsset!.Sha256);
		check.ActualSha256.Should().Be(sample.PendingAsset.Sha256);
	}

	[Test]
	public void RelinkImportsReplacementWithoutKeepingSourceDependency()
	{
		using TempProject project = new();
		string first = project.WriteWave("first.wav", sample: 0);
		string second = project.WriteWave("second.wav", sample: 16384);
		DocumentWorkspace workspace = new();
		SampleDefinition sample = SampleDocumentEditor.Import(workspace, first);
		uint audioRevision = workspace.Document.AudioRevision;

		SampleDocumentEditor.Relink(workspace, sample, second);

		sample.Asset.Should().BeNull();
		sample.PendingAsset.Should().NotBeNull();
		sample.PendingAsset!.FileName.Should().Be("second.wav");
		sample.PcmData!.GetSample(0, 0).Should().BeApproximately(0.5f, 1e-6f);
		File.Delete(second);
		sample.PcmData.GetSample(0, 0).Should().BeApproximately(0.5f, 1e-6f);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void AcceptCurrentPersistedFileReloadsPcmAndHash()
	{
		using TempProject project = new();
		string imported = project.WriteWave("imports", "tone.wav", sample: 0);
		string jsonPath = project.Path("song", "track.hm.json");
		DocumentWorkspace workspace = new();
		SampleDefinition sample = SampleDocumentEditor.Import(workspace, imported);
		workspace.SaveAs(jsonPath);
		string persisted = sample.Asset!.FullPath;
		project.WriteWaveAt(persisted, sample: -16384);
		uint audioRevision = workspace.Document.AudioRevision;

		SampleDocumentEditor.RefreshHash(workspace, sample);

		sample.PcmData!.GetSample(0, 0).Should().BeApproximately(-0.5f, 1e-6f);
		sample.Asset!.Sha256.Should().Be(ExternalAssetIntegrity.ComputeSha256(persisted));
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}


	private sealed class TempProject : IDisposable
	{
		private readonly string _root =
			Path.Combine(
				Path.GetTempPath(),
				$"heresy-sample-ui-{Guid.NewGuid():N}");

		public TempProject() => Directory.CreateDirectory(_root);

		public string Path(params string[] parts)
		{
			string path = _root;
			foreach (string part in parts)
				path = System.IO.Path.Combine(path, part);
			return path;
		}

		public string WriteWave(
			params object[] partsAndSample)
		{
			short sample = (short)partsAndSample[^1];
			string path = _root;
			for (int index = 0; index < partsAndSample.Length - 1; index++)
				path = System.IO.Path.Combine(path, (string)partsAndSample[index]);
			WriteWaveAt(path, sample);
			return path;
		}

		public void WriteWaveAt(
			string path,
			short sample)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			using FileStream file = File.Create(path);
			using BinaryWriter writer = new(file);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(38);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((ushort)1);
			writer.Write((ushort)1);
			writer.Write(8000);
			writer.Write(16000);
			writer.Write((ushort)2);
			writer.Write((ushort)16);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
			writer.Write(2);
			writer.Write(sample);
		}

		public void Dispose()
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
	}
}

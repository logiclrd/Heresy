using System;
using System.IO;
using System.Linq;
using System.Numerics;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
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
	public void LoadSongSamplesEnumeratesHydratedSamplesInObjectIdOrder()
	{
		using TempProject project = new();
		string firstPath = project.WriteWave("first.wav", sample: 4096);
		string secondPath = project.WriteWave("second.wav", sample: 8192);
		string sourcePath = project.GetPath("source.hm");
		DocumentWorkspace sourceWorkspace = new();
		SampleDefinition first =
			SampleDocumentEditor.Import(sourceWorkspace, firstPath, "First");
		SampleDefinition second =
			SampleDocumentEditor.Import(sourceWorkspace, secondPath, "Second");
		sourceWorkspace.SaveAs(sourcePath);

		SongSampleImportSource source =
			SampleDocumentEditor.LoadImportSource(sourcePath);

		source.Samples.Select(sample => sample.Id)
			.Should().Equal(first.Id, second.Id);
		source.Samples.Select(sample => sample.Name)
			.Should().Equal("First", "Second");
		source.Samples.Should().OnlyContain(sample => sample.PcmData is not null);
		source.Samples.Should().OnlyContain(sample => sample.PendingAsset is null);
	}

	[Test]
	public void ImportFromSongCopiesMetadataPcmAndEncodedPayloadWithoutSourceDependency()
	{
		using TempProject project = new();
		string wavePath = project.WriteWave("source", "tone.wav", sample: 8192);
		string sourcePath = project.GetPath("source.hm");
		DocumentWorkspace sourceWorkspace = new();
		SampleDefinition original =
			SampleDocumentEditor.Import(sourceWorkspace, wavePath, "Imported Tone");
		original.ReferenceFrequencyHz = 440.0;
		original.Loop = new SampleLoop(SampleLoopMode.Forward, 0, 1);
		original.SourceChannelPositions.Add(new Vector3(0.25f, -0.5f, 0.75f));
		sourceWorkspace.SaveAs(sourcePath);

		SongSampleImportSource source =
			SampleDocumentEditor.LoadImportSource(sourcePath);
		DocumentWorkspace targetWorkspace = new();

		SampleDefinition imported =
			SampleDocumentEditor.ImportFromSong(
				targetWorkspace,
				source,
				new[] { original.Id })
				.Should().ContainSingle().Which;

		imported.Should().NotBeSameAs(source.Samples.Single());
		imported.Name.Should().Be("Imported Tone");
		imported.ReferenceFrequencyHz.Should().Be(440.0);
		imported.Loop.Should().Be(new SampleLoop(SampleLoopMode.Forward, 0, 1));
		imported.SourceChannelPositions.Should()
			.Equal(new Vector3(0.25f, -0.5f, 0.75f));
		imported.PcmData.Should().BeSameAs(source.Samples.Single().PcmData);
		imported.PendingAsset.Should().NotBeNull();
		imported.Asset.Should().BeNull();
		imported.PendingAsset!.FileName.Should().Be("tone.wav");

		File.Delete(sourcePath);
		imported.PcmData!.GetSample(0, 0)
			.Should().BeApproximately(0.25f, 1e-6f);

		string targetPath = project.GetPath("target.hm");
		targetWorkspace.SaveAs(targetPath);
		SongDocument reloaded = SongDocumentStorage.Load(targetPath);
		SampleDefinition persisted =
			reloaded.Objects.Values.OfType<SampleDefinition>().Single();
		persisted.PcmData!.GetSample(0, 0)
			.Should().BeApproximately(0.25f, 1e-6f);
	}

	[Test]
	public void ImportFromSongImportsOnlySelectedSamples()
	{
		using TempProject project = new();
		string firstPath = project.WriteWave("first.wav", sample: 4096);
		string secondPath = project.WriteWave("second.wav", sample: 8192);
		string sourcePath = project.GetPath("source.hm.json");
		DocumentWorkspace sourceWorkspace = new();
		SampleDefinition first =
			SampleDocumentEditor.Import(sourceWorkspace, firstPath, "First");
		SampleDefinition second =
			SampleDocumentEditor.Import(sourceWorkspace, secondPath, "Second");
		sourceWorkspace.SaveAs(sourcePath);

		SongSampleImportSource source =
			SampleDocumentEditor.LoadImportSource(sourcePath);
		DocumentWorkspace targetWorkspace = new();

		var imported =
			SampleDocumentEditor.ImportFromSong(
				targetWorkspace,
				source,
				new[] { second.Id });

		imported.Should().ContainSingle();
		imported[0].Name.Should().Be("Second");
		targetWorkspace.Document.Objects.Values
			.OfType<SampleDefinition>()
			.Should().ContainSingle()
			.Which.Name.Should().Be("Second");
		first.Name.Should().Be("First");
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
		string jsonPath = project.GetPath("song", "track.hm.json");
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
			System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				$"heresy-sample-ui-{Guid.NewGuid():N}");

		public TempProject() => Directory.CreateDirectory(_root);

		public string GetPath(params string[] parts)
		{
			string path = _root;
			foreach (string part in parts)
				path = System.IO.Path.Combine(path, part);
			return path;
		}

		public string WriteWave(
			string fileName,
			short sample)
		{
			string path = GetPath(fileName);
			WriteWaveAt(path, sample);
			return path;
		}

		public string WriteWave(
			string directory,
			string fileName,
			short sample)
		{
			string path = GetPath(directory, fileName);
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

using System;
using System.IO;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent sample authoring operations used by the Avalonia UI.
/// Imported encoded assets are copied into memory immediately and decoded to
/// PCM. The original source path is not retained as a live song dependency.
/// </summary>
public static class SampleDocumentEditor
{
	public static SampleDefinition Import(
		DocumentWorkspace workspace,
		string assetPath,
		string? name = null)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);

		string fullAssetPath = Path.GetFullPath(assetPath);
		if (!File.Exists(fullAssetPath))
			throw new FileNotFoundException("The sample asset does not exist.", fullAssetPath);

		string sampleName = string.IsNullOrWhiteSpace(name)
			? Path.GetFileNameWithoutExtension(fullAssetPath)
			: name.Trim();
		if (string.IsNullOrWhiteSpace(sampleName))
			sampleName = Path.GetFileName(fullAssetPath);

		byte[] encoded = File.ReadAllBytes(fullAssetPath);
		ObjectId id = workspace.Document.AllocateObjectId();
		SampleDefinition sample =
			SampleDefinition.CreateImported(
				id,
				sampleName,
				Path.GetFileName(fullAssetPath),
				encoded);
		workspace.Document.Add(sample, affectsAudio: true);
		return sample;
	}

	public static ExternalAssetCheck CheckAsset(
		DocumentWorkspace workspace,
		SampleDefinition sample)
	{
		ValidateSample(workspace, sample);
		if (sample.Asset is ExternalAssetReference asset)
			return ExternalAssetIntegrity.Check(asset);

		PendingSampleAsset pending =
			sample.PendingAsset
				?? throw new InvalidOperationException(
					"The sample has no persisted or pending encoded representation.");
		return new ExternalAssetCheck(
			ExternalAssetStatus.Pending,
			"(pending song asset)",
			pending.Sha256,
			pending.Sha256);
	}

	public static void UpdateMetadata(
		DocumentWorkspace workspace,
		SampleDefinition sample,
		double referenceFrequencyHz,
		SampleLoop loop)
	{
		ValidateSample(workspace, sample);
		ArgumentNullException.ThrowIfNull(loop);
		if (!(referenceFrequencyHz > 0.0)
			|| double.IsNaN(referenceFrequencyHz)
			|| double.IsInfinity(referenceFrequencyHz))
		{
			throw new ArgumentOutOfRangeException(nameof(referenceFrequencyHz));
		}

		if (sample.ReferenceFrequencyHz == referenceFrequencyHz
			&& sample.Loop == loop)
		{
			return;
		}

		sample.ReferenceFrequencyHz = referenceFrequencyHz;
		sample.Loop = loop;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void RefreshHash(
		DocumentWorkspace workspace,
		SampleDefinition sample)
	{
		ValidateSample(workspace, sample);
		if (sample.Asset is not ExternalAssetReference asset)
			return;

		byte[] encoded =
			Heresy.Core.Persistence.SampleAssetPersistence.ReadAllBytes(
				asset.FullPath);
		ExternalAssetReference refreshed =
			asset with
			{
				Sha256 =
					Heresy.Core.Persistence.SampleAssetPersistence.Hash(
						encoded),
			};
		SamplePcmData pcm =
			SampleAudioCodec.Decode(
				encoded,
				asset.FullPath);
		sample.SetLoadedPcm(
			pcm,
			refreshed);
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	public static void Relink(
		DocumentWorkspace workspace,
		SampleDefinition sample,
		string assetPath)
	{
		ValidateSample(workspace, sample);
		ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);

		string fullAssetPath = Path.GetFullPath(assetPath);
		if (!File.Exists(fullAssetPath))
			throw new FileNotFoundException("The sample asset does not exist.", fullAssetPath);

		byte[] encoded = File.ReadAllBytes(fullAssetPath);
		sample.ReplaceImportedEncoding(
			Path.GetFileName(fullAssetPath),
			encoded);
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	private static void ValidateSample(
		DocumentWorkspace workspace,
		SampleDefinition sample)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(sample);

		if (!workspace.Document.TryGet(sample.Id, out SongObject? existing)
			|| !ReferenceEquals(existing, sample))
		{
			throw new InvalidOperationException(
				"The sample is not part of the active song document.");
		}
	}
}

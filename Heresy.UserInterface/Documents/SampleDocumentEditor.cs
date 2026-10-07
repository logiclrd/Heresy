using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

public sealed record SongSampleImportSource(
	string Path,
	SongDocument Document,
	IReadOnlyList<SampleDefinition> Samples);

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

	public static SongSampleImportSource LoadImportSource(
		string songPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
		string fullPath = Path.GetFullPath(songPath);
		SongDocument document =
			SongDocumentStorage.Load(fullPath);
		SampleDefinition[] samples =
			document.Objects
				.OrderBy(pair => pair.Key.Value)
				.Select(pair => pair.Value)
				.OfType<SampleDefinition>()
				.ToArray();
		return new SongSampleImportSource(
			fullPath,
			document,
			samples);
	}

	public static IReadOnlyList<SampleDefinition> ImportFromSong(
		DocumentWorkspace workspace,
		SongSampleImportSource source,
		IEnumerable<ObjectId> sampleIds)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(sampleIds);

		HashSet<ObjectId> selected = new(sampleIds);
		List<SampleDefinition> imported = [];
		foreach (SampleDefinition sample in source.Samples)
		{
			if (!selected.Contains(sample.Id))
				continue;

			(byte[] Encoded, string FileName) encoding =
				ReadEncodedRepresentation(sample);
			ObjectId id = workspace.Document.AllocateObjectId();
			SampleDefinition copy =
				SampleDefinition.CreateImportedCopy(
					id,
					sample,
					encoding.FileName,
					encoding.Encoded);
			workspace.Document.Add(copy, affectsAudio: true);
			imported.Add(copy);
		}

		return imported;
	}

	private static (byte[] Encoded, string FileName) ReadEncodedRepresentation(
		SampleDefinition sample)
	{
		if (sample.PendingAsset is PendingSampleAsset pending)
		{
			return (
				pending.Bytes.ToArray(),
				pending.FileName);
		}

		ExternalAssetReference asset =
			sample.Asset
				?? throw new InvalidOperationException(
					$"Sample '{sample.Name}' ({sample.Id}) has no encoded representation to import.");
		using Stream source =
			ExternalAssetIntegrity.OpenRead(asset.FullPath);
		using MemoryStream destination = new();
		source.CopyTo(destination);
		string fileName = Path.GetFileName(asset.FullPath);
		if (string.IsNullOrWhiteSpace(fileName))
			fileName = $"sample-{sample.Id.Value}.bin";
		return (
			destination.ToArray(),
			fileName);
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

		if (loop.Mode != SampleLoopMode.None
			&& sample.PcmData is SamplePcmData pcm)
		{
			if (pcm.FrameCount == 0
				|| loop.StartFrame >= pcm.FrameCount
				|| loop.EndFrameExclusive > pcm.FrameCount)
			{
				throw new ArgumentException(
					"An active sample loop must lie entirely within the decoded sample PCM.",
					nameof(loop));
			}
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

		byte[] encoded;
		using (Stream source =
			ExternalAssetIntegrity.OpenRead(asset.FullPath))
		using (MemoryStream destination = new())
		{
			source.CopyTo(destination);
			encoded = destination.ToArray();
		}
		ExternalAssetReference refreshed =
			asset with
			{
				Sha256 =
					Convert.ToHexString(
						System.Security.Cryptography.SHA256.HashData(encoded))
						.ToLowerInvariant(),
			};
		sample.ReloadPersistedEncoding(
			refreshed,
			encoded);
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

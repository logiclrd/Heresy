using System;
using System.IO;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent sample authoring operations used by the Avalonia UI.
/// External assets remain referenced in place rather than copied into the song.
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

		string songPath = RequireSongPath(workspace);
		string fullAssetPath = Path.GetFullPath(assetPath);
		if (!File.Exists(fullAssetPath))
			throw new FileNotFoundException("The sample asset does not exist.", fullAssetPath);

		string sampleName = string.IsNullOrWhiteSpace(name)
			? Path.GetFileNameWithoutExtension(fullAssetPath)
			: name.Trim();
		if (string.IsNullOrWhiteSpace(sampleName))
			sampleName = Path.GetFileName(fullAssetPath);

		ExternalAssetReference reference =
			ExternalAssetIntegrity.CreateReference(songPath, fullAssetPath);
		ObjectId id = workspace.Document.AllocateObjectId();
		SampleDefinition sample = new(id, sampleName, reference);
		workspace.Document.Add(sample, affectsAudio: true);
		return sample;
	}

	public static ExternalAssetCheck CheckAsset(
		DocumentWorkspace workspace,
		SampleDefinition sample)
	{
		ValidateSample(workspace, sample);
		return ExternalAssetIntegrity.Check(
			RequireSongPath(workspace),
			sample.Asset);
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
		ExternalAssetReference refreshed =
			ExternalAssetIntegrity.RefreshHash(
				RequireSongPath(workspace),
				sample.Asset);
		if (sample.Asset == refreshed)
			return;

		sample.Asset = refreshed;
		workspace.Document.MarkChanged(affectsAudio: false);
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

		ExternalAssetReference replacement =
			ExternalAssetIntegrity.CreateReference(
				RequireSongPath(workspace),
				fullAssetPath);
		if (sample.Asset == replacement)
			return;

		sample.Asset = replacement;
		workspace.Document.MarkChanged(affectsAudio: true);
	}

	private static string RequireSongPath(DocumentWorkspace workspace)
		=> workspace.FilePath
			?? throw new InvalidOperationException(
				"Save the song before importing or resolving external sample assets.");

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

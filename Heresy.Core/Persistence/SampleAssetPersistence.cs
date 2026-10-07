using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.Core.Persistence;

/// <summary>
/// Bridges encoded sample persistence and live decoded PCM. This layer is used
/// only while loading, importing, or saving a song; realtime playback must not
/// call it.
/// </summary>
internal static class SampleAssetPersistence
{
	public static void HydratePersistedSamples(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		foreach (SampleDefinition sample in
			document.Objects.Values.OfType<SampleDefinition>())
		{
			ExternalAssetReference asset =
				sample.Asset
					?? throw new InvalidDataException(
						$"Persisted sample '{sample.Name}' ({sample.Id}) has no encoded asset identity.");

			byte[] encoded =
				ReadAllBytes(asset.FullPath);
			string actualHash =
				Hash(encoded);

			if (!string.IsNullOrWhiteSpace(asset.Sha256)
				&& !string.Equals(
					asset.Sha256,
					actualHash,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException(
					$"Persisted sample asset '{asset.FullPath}' changed: its SHA-256 does not match the song manifest.");
			}

			ExternalAssetReference verified =
				asset with
				{
					Sha256 = actualHash,
				};
			SamplePcmData pcm =
				SampleAudioCodec.Decode(
					encoded,
					asset.FullPath);
			sample.SetLoadedPcm(
				pcm,
				verified);
		}
	}

	public static void VerifyPersistedAssetsForSave(
		SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);

		foreach (SampleDefinition sample in
			document.Objects.Values.OfType<SampleDefinition>())
		{
			if (sample.PendingAsset is not null)
				continue;

			ExternalAssetReference asset =
				sample.Asset
					?? throw new InvalidOperationException(
						$"Sample '{sample.Name}' ({sample.Id}) has neither a persisted encoded asset nor pending encoded bytes.");

			string actualHash =
				ExternalAssetIntegrity.ComputeSha256(
					asset.FullPath);
			if (!string.IsNullOrWhiteSpace(asset.Sha256)
				&& !string.Equals(
					asset.Sha256,
					actualHash,
					StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					$"Sample asset '{asset.FullPath}' changed since it was loaded or saved. The persisted encoding will not be reused.");
			}

			if (string.IsNullOrWhiteSpace(asset.Sha256))
			{
				sample.Asset =
					asset with
					{
						Sha256 = actualHash,
					};
			}
		}
	}

	public static byte[] ReadAllBytes(
		string fullPath)
	{
		using Stream source =
			ExternalAssetIntegrity.OpenRead(fullPath);
		using MemoryStream destination = new();
		source.CopyTo(destination);
		return destination.ToArray();
	}

	public static string Hash(
		ReadOnlySpan<byte> bytes)
		=> Convert.ToHexString(
			SHA256.HashData(bytes))
				.ToLowerInvariant();
}

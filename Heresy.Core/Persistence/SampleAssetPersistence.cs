using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

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


	/// <summary>
	/// Decode into a staged document, consulting the UI only when the original
	/// encoded asset is missing, unreadable or fails its integrity check.
	/// Replacements must pass the original SHA-256 (if present) and decode
	/// successfully. They become song-owned pending bytes, never dependencies
	/// on the replacement's temporary location.
	/// </summary>
	public static async Task HydratePersistedSamplesWithRecoveryAsync(
		SongDocument document,
		Func<SampleAssetRecoveryRequest, Task<string?>> locateAsync)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(locateAsync);

		foreach (SampleDefinition sample in
			document.Objects.Values.OfType<SampleDefinition>())
		{
			ExternalAssetReference asset =
				sample.Asset
					?? throw new InvalidDataException(
						$"Persisted sample '{sample.Name}' ({sample.Id}) has no encoded asset identity.");

			Exception failure;
			try
			{
				byte[] encoded = ReadAllBytes(asset.FullPath);
				string hash = Hash(encoded);
				if (!string.IsNullOrWhiteSpace(asset.Sha256)
					&& !string.Equals(asset.Sha256, hash, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException(
						$"Persisted sample asset '{asset.FullPath}' changed: its SHA-256 does not match the song manifest.");
				}

				SamplePcmData pcm = SampleAudioCodec.Decode(encoded, asset.FullPath);
				sample.SetLoadedPcm(pcm, asset with { Sha256 = hash });
				continue;
			}
			catch (Exception ex) when (IsRecoverableAssetFailure(ex))
			{
				failure = ex;
			}

			// The UI can offer another file or stop. Bound faulty callback loops
			// even if a noninteractive caller returns the same invalid path.
			bool recovered = false;
			for (int attempt = 0; attempt < 32; attempt++)
			{
				string? selected = await locateAsync(
					new SampleAssetRecoveryRequest(
						sample.Id,
						sample.Name,
						asset.FullPath,
						asset.Sha256,
						failure.Message));
				if (string.IsNullOrWhiteSpace(selected))
					throw new OperationCanceledException("Sample asset recovery was canceled.");

				try
				{
					string fullPath = Path.GetFullPath(selected);
					byte[] bytes = ReadAllBytes(fullPath);
					string hash = Hash(bytes);
					if (!string.IsNullOrWhiteSpace(asset.Sha256)
						&& !string.Equals(asset.Sha256, hash, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException(
							$"Selected replacement '{fullPath}' does not match the original SHA-256.");
					}

					// Decode before publishing any replacement to this sample.
					// Imported encoding is retained for a later package/JSON
					// save regardless of whether the selected file disappears.
					sample.ReplaceImportedEncoding(Path.GetFileName(fullPath), bytes);
					recovered = true;
					break;
				}
				catch (Exception ex) when (IsRecoverableAssetFailure(ex)
					|| ex is NotSupportedException
					|| ex is ArgumentException)
				{
					failure = ex;
				}
			}

			if (!recovered)
			{
				throw new InvalidDataException(
					$"Could not recover sample '{sample.Name}' ({sample.Id}) after 32 attempts.",
					failure);
			}
		}
	}

	private static bool IsRecoverableAssetFailure(Exception exception)
		=> exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException;

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

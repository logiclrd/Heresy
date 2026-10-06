using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.Core.Assets;

public enum ExternalAssetStatus
{
	Missing,
	Unhashed,
	Match,
	HashMismatch,
}

public sealed record ExternalAssetCheck(
	ExternalAssetStatus Status,
	string RelativePath,
	string ResolvedPath,
	string? ExpectedSha256,
	string? ActualSha256);

public sealed record ExternalAssetDiagnostic(
	ObjectId ObjectId,
	string ObjectName,
	ExternalAssetStatus Status,
	string RelativePath,
	string ResolvedPath,
	string? ExpectedSha256,
	string? ActualSha256);

/// <summary>
/// Resolves and verifies external binary assets referenced by a song document.
/// This layer is deliberately separate from JSON persistence: a song file can
/// always be deserialized even when one or more external assets are missing or
/// have changed.
/// </summary>
public static class ExternalAssetIntegrity
{
	public static string ResolvePath(
		string songPath,
		ExternalAssetReference reference)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
		ArgumentNullException.ThrowIfNull(reference);

		string relativePath = reference.RelativePath;
		if (string.IsNullOrWhiteSpace(relativePath))
			throw new ArgumentException(
				"External asset paths must be non-empty.",
				nameof(reference));

		string portablePath =
			relativePath
				.Replace('\\', Path.DirectorySeparatorChar)
				.Replace('/', Path.DirectorySeparatorChar);
		if (Path.IsPathRooted(portablePath))
			throw new ArgumentException(
				"External asset paths must be relative to the song file.",
				nameof(reference));

		string fullSongPath = Path.GetFullPath(songPath);
		string songDirectory =
			Path.GetDirectoryName(fullSongPath)
			?? throw new ArgumentException(
				"The song path must identify a file.",
				nameof(songPath));

		return Path.GetFullPath(
			Path.Combine(songDirectory, portablePath));
	}

	public static ExternalAssetReference CreateReference(
		string songPath,
		string assetPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);

		string fullSongPath = Path.GetFullPath(songPath);
		string songDirectory =
			Path.GetDirectoryName(fullSongPath)
			?? throw new ArgumentException(
				"The song path must identify a file.",
				nameof(songPath));
		string fullAssetPath = Path.GetFullPath(assetPath);

		string relativePath =
			Path.GetRelativePath(
					songDirectory,
					fullAssetPath)
				.Replace(
					Path.DirectorySeparatorChar,
					'/');
		if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
		{
			relativePath = relativePath.Replace(
				Path.AltDirectorySeparatorChar,
				'/');
		}

		return new ExternalAssetReference(
			relativePath,
			ComputeSha256(fullAssetPath));
	}

	public static ExternalAssetReference RefreshHash(
		string songPath,
		ExternalAssetReference reference)
	{
		ArgumentNullException.ThrowIfNull(reference);

		string resolvedPath =
			ResolvePath(songPath, reference);
		return reference with
		{
			Sha256 = ComputeSha256(resolvedPath),
		};
	}

	public static ExternalAssetCheck Check(
		string songPath,
		ExternalAssetReference reference)
	{
		ArgumentNullException.ThrowIfNull(reference);

		string resolvedPath =
			ResolvePath(songPath, reference);

		if (!File.Exists(resolvedPath))
		{
			return new ExternalAssetCheck(
				ExternalAssetStatus.Missing,
				reference.RelativePath,
				resolvedPath,
				reference.Sha256,
				null);
		}

		string actualHash =
			ComputeSha256(resolvedPath);

		if (string.IsNullOrWhiteSpace(reference.Sha256))
		{
			return new ExternalAssetCheck(
				ExternalAssetStatus.Unhashed,
				reference.RelativePath,
				resolvedPath,
				reference.Sha256,
				actualHash);
		}

		ExternalAssetStatus status =
			string.Equals(
				reference.Sha256,
				actualHash,
				StringComparison.OrdinalIgnoreCase)
				? ExternalAssetStatus.Match
				: ExternalAssetStatus.HashMismatch;

		return new ExternalAssetCheck(
			status,
			reference.RelativePath,
			resolvedPath,
			reference.Sha256,
			actualHash);
	}

	public static IReadOnlyList<ExternalAssetDiagnostic> Scan(
		string songPath,
		SongDocument document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
		ArgumentNullException.ThrowIfNull(document);

		List<ExternalAssetDiagnostic> diagnostics = [];

		foreach ((ObjectId id, SongObject songObject) in
			document.Objects.OrderBy(pair => pair.Key.Value))
		{
			if (songObject is not SampleDefinition sample)
				continue;

			ExternalAssetCheck check =
				Check(songPath, sample.Asset);
			diagnostics.Add(
				new ExternalAssetDiagnostic(
					id,
					sample.Name,
					check.Status,
					check.RelativePath,
					check.ResolvedPath,
					check.ExpectedSha256,
					check.ActualSha256));
		}

		return diagnostics;
	}

	public static string ComputeSha256(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		using FileStream stream =
			File.OpenRead(path);
		byte[] hash = SHA256.HashData(stream);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}
}

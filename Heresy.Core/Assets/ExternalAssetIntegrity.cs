using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.Core.Assets;

public enum ExternalAssetStatus
{
	Pending,
	Missing,
	Unhashed,
	Match,
	HashMismatch,
}

public sealed record ExternalAssetCheck(
	ExternalAssetStatus Status,
	string FullPath,
	string? ExpectedSha256,
	string? ActualSha256);

public sealed record ExternalAssetDiagnostic(
	ObjectId ObjectId,
	string ObjectName,
	ExternalAssetStatus Status,
	string FullPath,
	string? ExpectedSha256,
	string? ActualSha256);

/// <summary>
/// Verifies external assets using their in-memory absolute locations. Synthetic
/// .hm paths are opened from the corresponding ZIP entry transparently.
/// </summary>
public static class ExternalAssetIntegrity
{
	public static ExternalAssetReference CreateReference(string assetPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
		string fullPath = Path.GetFullPath(assetPath);
		return new ExternalAssetReference(fullPath, ComputeSha256(fullPath));
	}

	public static ExternalAssetReference RefreshHash(ExternalAssetReference reference)
	{
		ArgumentNullException.ThrowIfNull(reference);
		return reference with
		{
			Sha256 = ComputeSha256(reference.FullPath),
		};
	}

	public static ExternalAssetCheck Check(ExternalAssetReference reference)
	{
		ArgumentNullException.ThrowIfNull(reference);

		if (!Exists(reference.FullPath))
		{
			return new ExternalAssetCheck(
				ExternalAssetStatus.Missing,
				reference.FullPath,
				reference.Sha256,
				null);
		}

		string actualHash = ComputeSha256(reference.FullPath);
		if (string.IsNullOrWhiteSpace(reference.Sha256))
		{
			return new ExternalAssetCheck(
				ExternalAssetStatus.Unhashed,
				reference.FullPath,
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
			reference.FullPath,
			reference.Sha256,
			actualHash);
	}

	public static IReadOnlyList<ExternalAssetDiagnostic> Scan(SongDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		List<ExternalAssetDiagnostic> diagnostics = [];

		foreach ((ObjectId id, SongObject songObject) in
			document.Objects.OrderBy(pair => pair.Key.Value))
		{
			if (songObject is not SampleDefinition sample)
				continue;

			ExternalAssetCheck check =
				sample.Asset is ExternalAssetReference asset
					? Check(asset)
					: new ExternalAssetCheck(
						ExternalAssetStatus.Pending,
						"(pending song asset)",
						sample.PendingAsset?.Sha256,
						sample.PendingAsset?.Sha256);
			diagnostics.Add(
				new ExternalAssetDiagnostic(
					id,
					sample.Name,
					check.Status,
					check.FullPath,
					check.ExpectedSha256,
					check.ActualSha256));
		}

		return diagnostics;
	}

	public static bool Exists(string fullPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
		if (File.Exists(fullPath))
			return true;

		if (!HeresyModulePath.TrySplit(fullPath, out string archivePath, out string entryPath)
			|| !File.Exists(archivePath))
		{
			return false;
		}

		try
		{
			using ZipArchive archive = ZipFile.OpenRead(archivePath);
			return archive.GetEntry(entryPath) is not null;
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	public static Stream OpenRead(string fullPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
		if (File.Exists(fullPath))
			return File.OpenRead(fullPath);

		if (!HeresyModulePath.TrySplit(fullPath, out string archivePath, out string entryPath)
			|| !File.Exists(archivePath))
		{
			throw new FileNotFoundException("The external asset could not be found.", fullPath);
		}

		FileStream file = File.OpenRead(archivePath);
		ZipArchive archive = new(file, ZipArchiveMode.Read, leaveOpen: false);
		ZipArchiveEntry? entry = archive.GetEntry(entryPath);
		if (entry is null)
		{
			archive.Dispose();
			throw new FileNotFoundException(
				$"The .hm archive does not contain '{entryPath}'.",
				fullPath);
		}

		return new OwnedArchiveEntryStream(archive, entry.Open());
	}

	public static string ComputeSha256(string fullPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
		using Stream stream = OpenRead(fullPath);
		byte[] hash = SHA256.HashData(stream);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private sealed class OwnedArchiveEntryStream : Stream
	{
		private readonly ZipArchive _archive;
		private readonly Stream _inner;

		public OwnedArchiveEntryStream(ZipArchive archive, Stream inner)
		{
			_archive = archive;
			_inner = inner;
		}

		public override bool CanRead => _inner.CanRead;
		public override bool CanSeek => _inner.CanSeek;
		public override bool CanWrite => false;
		public override long Length => _inner.Length;
		public override long Position { get => _inner.Position; set => _inner.Position = value; }
		public override void Flush() => _inner.Flush();
		public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
		public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_inner.Dispose();
				_archive.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}

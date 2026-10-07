using System;
using System.IO;
using System.Security.Cryptography;

namespace Heresy.Core.Samples;

/// <summary>
/// Encoded sample bytes retained only until the current song successfully saves
/// its own copy. The original import location is deliberately not retained.
/// </summary>
public sealed class PendingSampleAsset
{
	private readonly byte[] _bytes;

	public PendingSampleAsset(
		string fileName,
		ReadOnlySpan<byte> bytes)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		FileName = Path.GetFileName(fileName);
		if (string.IsNullOrWhiteSpace(FileName))
			throw new ArgumentException("The encoded sample must have a file name.", nameof(fileName));

		_bytes = bytes.ToArray();
		Sha256 =
			Convert.ToHexString(
				SHA256.HashData(_bytes))
				.ToLowerInvariant();
	}

	public string FileName { get; }
	public string Sha256 { get; }
	public ReadOnlyMemory<byte> Bytes => _bytes;
}

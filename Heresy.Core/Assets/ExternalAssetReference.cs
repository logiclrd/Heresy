using System;
using System.IO;

namespace Heresy.Core.Assets;

/// <summary>
/// In-memory reference to binary asset data. FullPath is always the actual
/// absolute filesystem path, or a synthetic absolute path whose .hm component
/// identifies an archive and whose suffix identifies an entry within it.
/// </summary>
public sealed record ExternalAssetReference
{
	public ExternalAssetReference(string fullPath, string? sha256 = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
		FullPath = HeresyModulePath.TrySplit(fullPath, out _, out _)
			? fullPath
			: Path.GetFullPath(fullPath);
		Sha256 = sha256;
	}

	public string FullPath { get; init; }
	public string? Sha256 { get; init; }
}

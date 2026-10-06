using System;

using Heresy.Core.Objects;

namespace Heresy.Core.Persistence;

/// <summary>
/// Selects the transparent .hm.json or consolidated .hm persistence form.
/// </summary>
public static class SongDocumentStorage
{
	public static void Save(string path, SongDocument document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(document);

		if (IsPackagePath(path))
		{
			SongDocumentPackage.Save(path, document);
			return;
		}
		if (IsJsonPath(path))
		{
			SongDocumentJson.Save(path, document);
			return;
		}

		throw new NotSupportedException(
			"Heresy songs must use the .hm or .hm.json extension.");
	}

	public static SongDocument Load(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (IsPackagePath(path))
			return SongDocumentPackage.Load(path);
		if (IsJsonPath(path))
			return SongDocumentJson.Load(path);

		throw new NotSupportedException(
			"Heresy songs must use the .hm or .hm.json extension.");
	}

	public static bool IsPackagePath(string path)
		=> path.EndsWith(".hm", StringComparison.OrdinalIgnoreCase);

	public static bool IsJsonPath(string path)
		=> path.EndsWith(".hm.json", StringComparison.OrdinalIgnoreCase);
}

using System;
using System.IO;

namespace Heresy.Core.Assets;

/// <summary>
/// Converts between .hm archive entry names and the directory-like synthetic
/// absolute paths used by the authoring model.
/// </summary>
public static class HeresyModulePath
{
	public static string MakeSyntheticPath(string archivePath, string entryPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
		ValidateEntryPath(entryPath);

		string fullArchivePath = Path.GetFullPath(archivePath);
		string suffix = entryPath.Replace('/', Path.DirectorySeparatorChar);
		return fullArchivePath + Path.DirectorySeparatorChar + suffix;
	}

	public static bool TrySplit(
		string path,
		out string archivePath,
		out string entryPath)
	{
		archivePath = string.Empty;
		entryPath = string.Empty;
		if (string.IsNullOrWhiteSpace(path))
			return false;

		int boundary = -1;
		for (int index = 0; index <= path.Length - 4; index++)
		{
			if (path[index] != '.')
				continue;
			if (!path.AsSpan(index, 3).Equals(".hm", StringComparison.OrdinalIgnoreCase))
				continue;

			int separator = index + 3;
			if (separator < path.Length && IsSeparator(path[separator]))
				boundary = separator;
		}

		if (boundary < 0 || boundary + 1 >= path.Length)
			return false;

		string candidateEntry = path[(boundary + 1)..]
			.Replace('\\', '/');
		try
		{
			ValidateEntryPath(candidateEntry);
		}
		catch (ArgumentException)
		{
			return false;
		}

		archivePath = path[..boundary];
		entryPath = candidateEntry;
		return true;
	}

	public static void ValidateEntryPath(string entryPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);
		if (entryPath.Contains('\\'))
			throw new ArgumentException("Paths within .hm files may not contain backslashes.", nameof(entryPath));
		if (entryPath.StartsWith("/", StringComparison.Ordinal)
			|| entryPath.EndsWith('/', StringComparison.Ordinal))
		{
			throw new ArgumentException("Paths within .hm files must be relative entry paths.", nameof(entryPath));
		}

		string[] parts = entryPath.Split('/');
		foreach (string part in parts)
		{
			if (part.Length == 0 || part == "." || part == "..")
				throw new ArgumentException("Paths within .hm files may not contain empty, '.' or '..' components.", nameof(entryPath));
		}
	}

	private static bool IsSeparator(char value)
		=> value == '/' || value == '\\';
}

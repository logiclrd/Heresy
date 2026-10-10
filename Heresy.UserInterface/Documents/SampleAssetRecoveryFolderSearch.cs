using System;
using System.Collections.Generic;
using System.IO;

using Heresy.Core.Assets;
using Heresy.Core.Persistence;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Bounded directory search for a missing encoded asset. Never guess an
/// unverified replacement: folder auto-matches require the manifest SHA-256
/// and original filename. A renamed or unhashed asset requires explicit
/// user file selection instead.
/// </summary>
public static class SampleAssetRecoveryFolderSearch
{
	public static string? FindMatch(
		SampleAssetRecoveryRequest request,
		string root,
		int maxEntries = 4096)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(root);
		if (maxEntries <= 0)
			throw new ArgumentOutOfRangeException(nameof(maxEntries));
		if (string.IsNullOrWhiteSpace(request.ExpectedSha256))
			return null;

		string targetName = Path.GetFileName(request.OriginalFullPath);
		StringComparison comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		Queue<string> directories = new();
		directories.Enqueue(Path.GetFullPath(root));
		int inspected = 0;
		while (directories.Count > 0 && inspected < maxEntries)
		{
			string directory = directories.Dequeue();
			try
			{
				foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
				{
					if (++inspected > maxEntries)
						return null;
					try
					{
						FileAttributes attributes = File.GetAttributes(entry);
						if ((attributes & FileAttributes.ReparsePoint) != 0)
							continue;
						if ((attributes & FileAttributes.Directory) != 0)
						{
							directories.Enqueue(entry);
							continue;
						}
						if (!string.Equals(Path.GetFileName(entry), targetName, comparison))
							continue;
						string sha = ExternalAssetIntegrity.ComputeSha256(entry);
						if (string.Equals(sha, request.ExpectedSha256,
							StringComparison.OrdinalIgnoreCase))
							return Path.GetFullPath(entry);
					}
					catch (IOException) { /* inaccessible candidate */ }
					catch (UnauthorizedAccessException) { /* inaccessible candidate */ }
				}
			}
			catch (IOException) { /* inaccessible directory */ }
			catch (UnauthorizedAccessException) { /* inaccessible directory */ }
		}
		return null;
	}
}

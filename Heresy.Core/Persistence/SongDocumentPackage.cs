using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

namespace Heresy.Core.Persistence;

public static class SongDocumentPackage
{
	public static void Save(string path, SongDocument document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(document);

		string packagePath = Path.GetFullPath(path);
		string packageDirectory = Path.GetDirectoryName(packagePath)
			?? throw new ArgumentException("The package path must identify a file.", nameof(path));
		Directory.CreateDirectory(packageDirectory);

		string manifestName = Path.GetFileName(packagePath) + ".json";
		Dictionary<ObjectId, string> entries = AssignEntries(
			packagePath,
			document,
			manifestName);

		string temporaryPath = packagePath + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			using (FileStream file = File.Create(temporaryPath))
			using (ZipArchive archive = new(file, ZipArchiveMode.Create))
			{
				ZipArchiveEntry manifest = archive.CreateEntry(
					manifestName,
					CompressionLevel.Optimal);
				using (StreamWriter writer =
					new(manifest.Open(), new UTF8Encoding(false)))
				{
					writer.Write(
						SongDocumentJson.Serialize(
							document,
							sample => entries[sample.Id]));
				}

				Dictionary<string, string> writtenSources =
					new(PathComparer);
				foreach (SampleDefinition sample in Samples(document))
				{
					string entryName = entries[sample.Id];
					if (writtenSources.TryGetValue(sample.Asset.FullPath, out string? existingEntry)
						&& string.Equals(existingEntry, entryName, StringComparison.Ordinal))
					{
						continue;
					}

					ZipArchiveEntry entry = archive.CreateEntry(
						entryName,
						CompressionLevel.Optimal);
					using Stream source = ExternalAssetIntegrity.OpenRead(sample.Asset.FullPath);
					using Stream destination = entry.Open();
					source.CopyTo(destination);
					writtenSources[sample.Asset.FullPath] = entryName;
				}
			}

			File.Move(temporaryPath, packagePath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}

		foreach (SampleDefinition sample in Samples(document))
		{
			sample.Asset = sample.Asset with
			{
				FullPath = HeresyModulePath.MakeSyntheticPath(
					packagePath,
					entries[sample.Id]),
			};
		}
	}

	public static SongDocument Load(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		string packagePath = Path.GetFullPath(path);

		using ZipArchive archive = ZipFile.OpenRead(packagePath);
		ZipArchiveEntry[] manifests = archive.Entries
			.Where(entry =>
				!entry.FullName.Contains('/')
				&& entry.FullName.EndsWith(".hm.json", StringComparison.OrdinalIgnoreCase))
			.ToArray();
		if (manifests.Length != 1)
		{
			throw new InvalidDataException(
				"A .hm file must contain exactly one root-level .hm.json manifest.");
		}

		string json;
		using (StreamReader reader = new(manifests[0].Open(), Encoding.UTF8))
			json = reader.ReadToEnd();

		return SongDocumentJson.Deserialize(
			json,
			storedPath =>
			{
				try
				{
					HeresyModulePath.ValidateEntryPath(storedPath);
				}
				catch (ArgumentException ex)
				{
					throw new InvalidDataException(
						$"Invalid asset path '{storedPath}' in .hm manifest.",
						ex);
				}

				if (archive.GetEntry(storedPath) is null)
				{
					throw new InvalidDataException(
						$"The .hm manifest references missing archive entry '{storedPath}'.");
				}

				return HeresyModulePath.MakeSyntheticPath(packagePath, storedPath);
			});
	}

	private static Dictionary<ObjectId, string> AssignEntries(
		string packagePath,
		SongDocument document,
		string manifestName)
	{
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase) { manifestName };
		Dictionary<string, string> bySource = new(PathComparer);
		Dictionary<ObjectId, string> result = [];

		foreach (SampleDefinition sample in Samples(document))
		{
			if (bySource.TryGetValue(sample.Asset.FullPath, out string? shared))
			{
				result[sample.Id] = shared;
				continue;
			}

			string preferred = PreferredEntryPath(packagePath, sample.Asset.FullPath);
			string entry = MakeUnique(preferred, used);
			used.Add(entry);
			bySource.Add(sample.Asset.FullPath, entry);
			result.Add(sample.Id, entry);
		}

		return result;
	}

	private static string PreferredEntryPath(string packagePath, string sourcePath)
	{
		if (HeresyModulePath.TrySplit(sourcePath, out string sourceArchive, out string sourceEntry)
			&& File.Exists(sourceArchive))
		{
			return sourceEntry;
		}

		string fullSource = Path.GetFullPath(sourcePath);
		string packageDirectory = Path.GetDirectoryName(packagePath)!;
		string relative = Path.GetRelativePath(packageDirectory, fullSource);
		if (IsStrictDescendant(relative))
			return ToArchiveEntry(relative);

		string fileName = Path.GetFileName(fullSource).Replace('\\', '_');
		return "pcm/" + fileName;
	}

	private static bool IsStrictDescendant(string relative)
	{
		if (Path.IsPathRooted(relative)
			|| relative == "."
			|| relative == ".."
			|| relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
				&& relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
		{
			return false;
		}

		return relative.Contains(Path.DirectorySeparatorChar)
			|| (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
				&& relative.Contains(Path.AltDirectorySeparatorChar));
	}

	private static string ToArchiveEntry(string relative)
	{
		string result;
		if (OperatingSystem.IsWindows())
			result = relative.Replace('\\', '/');
		else
			result = relative.Replace('\\', '_');

		if (Path.DirectorySeparatorChar != '/')
			result = result.Replace(Path.DirectorySeparatorChar, '/');
		if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
			&& Path.AltDirectorySeparatorChar != '/')
		{
			result = result.Replace(Path.AltDirectorySeparatorChar, '/');
		}

		HeresyModulePath.ValidateEntryPath(result);
		return result;
	}

	private static string MakeUnique(string preferred, HashSet<string> used)
	{
		HeresyModulePath.ValidateEntryPath(preferred);
		if (!used.Contains(preferred))
			return preferred;

		int slash = preferred.LastIndexOf('/');
		string directory = slash >= 0 ? preferred[..(slash + 1)] : string.Empty;
		string file = slash >= 0 ? preferred[(slash + 1)..] : preferred;
		string extension = Path.GetExtension(file);
		string stem = file[..^extension.Length];
		for (int suffix = 2; ; suffix++)
		{
			string candidate = $"{directory}{stem}-{suffix}{extension}";
			if (!used.Contains(candidate))
				return candidate;
		}
	}

	private static IEnumerable<SampleDefinition> Samples(SongDocument document)
		=> document.Objects
			.OrderBy(pair => pair.Key.Value)
			.Select(pair => pair.Value)
			.OfType<SampleDefinition>();

	private static StringComparer PathComparer
		=> OperatingSystem.IsWindows()
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
}

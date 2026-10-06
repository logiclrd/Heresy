using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class AssetPersistenceTests
{
	[Test]
	public void BareJsonSaveStoresRelativePathWhileDocumentKeepsFullPath()
	{
		using TempProject project = new();
		string assetPath = project.Write("songs", "assets", "kick.wav", "kick");
		string jsonPath = project.Path("songs", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);

		SongDocumentStorage.Save(jsonPath, document);

		SampleDefinition sample = (SampleDefinition)document.Objects[sampleId];
		Assert.That(sample.Asset.FullPath, Is.EqualTo(Path.GetFullPath(assetPath)));

		JsonObject json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
		Assert.That(
			json["objects"]![sampleId.Value.ToString()]!["asset"]!["path"]!.GetValue<string>(),
			Is.EqualTo("assets/kick.wav"));
	}

	[Test]
	public void BareJsonLoadResolvesAssetToAbsolutePath()
	{
		using TempProject project = new();
		string assetPath = project.Write("songs", "assets", "kick.wav", "kick");
		string jsonPath = project.Path("songs", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

		SongDocument source = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(jsonPath, source);

		SongDocument loaded = SongDocumentStorage.Load(jsonPath);

		SampleDefinition sample = (SampleDefinition)loaded.Objects[sampleId];
		Assert.That(sample.Asset.FullPath, Is.EqualTo(Path.GetFullPath(assetPath)));
	}

	[Test]
	public void BareJsonRelativeSaveRejectsAssetOutsideJsonSubtree()
	{
		using TempProject project = new();
		string assetPath = project.Write("library", "snare.wav", "snare");
		string jsonPath = project.Path("songs", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
		SongDocument document = DocumentWithSample(assetPath, out _);

		TestDelegate save = () =>
			SongDocumentStorage.Save(
				jsonPath,
				document,
				JsonAssetPathMode.Relative);

		Assert.That(save, Throws.TypeOf<InvalidOperationException>()
			.With.Message.Contains("Sample")
			.And.Message.Contains(Path.GetFullPath(assetPath))
			.And.Message.Contains("absolute"));
		Assert.That(File.Exists(jsonPath), Is.False);
	}

	[Test]
	public void BareJsonAbsoluteSaveAlwaysStoresOsConventionalFullPath()
	{
		using TempProject project = new();
		string assetPath = project.Write("songs", "assets", "kick.wav", "kick");
		string jsonPath = project.Path("songs", "track.hm.json");
		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);

		SongDocumentStorage.Save(
			jsonPath,
			document,
			JsonAssetPathMode.Absolute);

		JsonObject json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
		Assert.That(
			json["objects"]![sampleId.Value.ToString()]!["asset"]!["path"]!.GetValue<string>(),
			Is.EqualTo(Path.GetFullPath(assetPath)));
	}

	[Test]
	public void AbsoluteBareJsonSaveAtDifferentLocationDoesNotRetargetExternalAsset()
	{
		using TempProject project = new();
		string assetPath = project.Write("library", "snare.wav", "snare");
		string first = project.Path("one", "track.hm.json");
		string second = project.Path("two", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(first)!);
		Directory.CreateDirectory(Path.GetDirectoryName(second)!);
		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);

		SongDocumentStorage.Save(first, document, JsonAssetPathMode.Absolute);
		SongDocumentStorage.Save(second, document, JsonAssetPathMode.Absolute);

		SampleDefinition sample = (SampleDefinition)document.Objects[sampleId];
		Assert.That(sample.Asset.FullPath, Is.EqualTo(Path.GetFullPath(assetPath)));
		JsonObject json = JsonNode.Parse(File.ReadAllText(second))!.AsObject();
		Assert.That(
			json["objects"]![sampleId.Value.ToString()]!["asset"]!["path"]!.GetValue<string>(),
			Is.EqualTo(Path.GetFullPath(assetPath)));
	}

	[Test]
	public void BareJsonLoadResolvesAbsoluteAssetPath()
	{
		using TempProject project = new();
		string assetPath = project.Write("library", "snare.wav", "snare");
		string jsonPath = project.Path("songs", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
		SongDocument source = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(jsonPath, source, JsonAssetPathMode.Absolute);

		SongDocument loaded = SongDocumentStorage.Load(jsonPath);

		Assert.That(
			((SampleDefinition)loaded.Objects[sampleId]).Asset.FullPath,
			Is.EqualTo(Path.GetFullPath(assetPath)));
	}

	[Test]
	public void BareJsonLoadFailsWhenAssetCannotBeResolved()
	{
		using TempProject project = new();
		string assetPath = project.Write("songs", "assets", "kick.wav", "kick");
		string jsonPath = project.Path("songs", "track.hm.json");
		SongDocument source = DocumentWithSample(assetPath, out _);
		SongDocumentStorage.Save(jsonPath, source);
		File.Delete(assetPath);

		Assert.That(
			() => SongDocumentStorage.Load(jsonPath),
			Throws.TypeOf<FileNotFoundException>()
				.With.Message.Contains("kick.wav"));
	}

	[Test]
	public void BareJsonLoadRejectsForeignAbsolutePathConvention()
	{
		using TempProject project = new();
		string jsonPath = project.Path("track.hm.json");
		string foreignPath = OperatingSystem.IsWindows()
			? "/home/bob/samples/hihat.wav"
			: @"C:\samples\hihat.wav";
		File.WriteAllText(jsonPath, MinimalManifest(foreignPath));

		Assert.That(
			() => SongDocumentStorage.Load(jsonPath),
			Throws.TypeOf<InvalidDataException>()
				.With.Message.Contains("host"));
	}

	[Test]
	public void BareJsonLoadRejectsRelativePathThatEscapesContainer()
	{
		using TempProject project = new();
		string outside = project.Write("outside.wav", "outside");
		string jsonPath = project.Path("songs", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
		File.WriteAllText(jsonPath, MinimalManifest("../outside.wav"));

		Assert.That(
			() => SongDocumentStorage.Load(jsonPath),
			Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void PackageSaveBundlesExternalAssetAndRetargetsDocumentToSyntheticPath()
	{
		using TempProject project = new();
		string assetPath = project.Write("elsewhere", "hihat.wav", "hat");
		string packagePath = project.Path("songs", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);

		SongDocumentStorage.Save(packagePath, document);

		using ZipArchive zip = ZipFile.OpenRead(packagePath);
		Assert.That(zip.GetEntry("test.hm.json"), Is.Not.Null);
		Assert.That(zip.GetEntry("pcm/hihat.wav"), Is.Not.Null);

		string manifest = ReadEntry(zip, "test.hm.json");
		JsonObject json = JsonNode.Parse(manifest)!.AsObject();
		Assert.That(
			json["objects"]![sampleId.Value.ToString()]!["asset"]!["path"]!.GetValue<string>(),
			Is.EqualTo("pcm/hihat.wav"));

		SampleDefinition sample = (SampleDefinition)document.Objects[sampleId];
		Assert.That(
			sample.Asset.FullPath,
			Is.EqualTo(HeresyModulePath.MakeSyntheticPath(packagePath, "pcm/hihat.wav")));
	}

	[Test]
	public void PackageSavePreservesFilesystemSubdirectoryBelowPackageContainer()
	{
		using TempProject project = new();
		string assetPath = project.Write("song", "samples", "drums", "hihat.wav", "hat");
		string packagePath = project.Path("song", "test.hm");
		SongDocument document = DocumentWithSample(assetPath, out _);

		SongDocumentStorage.Save(packagePath, document);

		using ZipArchive zip = ZipFile.OpenRead(packagePath);
		Assert.That(zip.GetEntry("samples/drums/hihat.wav"), Is.Not.Null);
	}

	[Test]
	public void PackageLoadUsesSyntheticAbsoluteAssetPathAndIntegrityCanReadIt()
	{
		using TempProject project = new();
		string assetPath = project.Write("source", "tone.wav", "hello");
		string packagePath = project.Path("song", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(packagePath, document);

		SongDocument loaded = SongDocumentStorage.Load(packagePath);
		SampleDefinition sample = (SampleDefinition)loaded.Objects[sampleId];

		Assert.That(
			sample.Asset.FullPath,
			Is.EqualTo(HeresyModulePath.MakeSyntheticPath(packagePath, "pcm/tone.wav")));
		ExternalAssetCheck check = ExternalAssetIntegrity.Check(sample.Asset);
		Assert.That(check.Status, Is.EqualTo(ExternalAssetStatus.Match));
	}

	[Test]
	public void SavingPackageBackedDocumentAsBareJsonExtractsAssetHierarchy()
	{
		using TempProject project = new();
		string assetPath = project.Write("source", "tone.wav", "hello");
		string packagePath = project.Path("package", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
		SongDocument original = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(packagePath, original);

		SongDocument loaded = SongDocumentStorage.Load(packagePath);
		string jsonPath = project.Path("export", "copy.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

		SongDocumentStorage.Save(jsonPath, loaded);

		string extracted = project.Path("export", "pcm", "tone.wav");
		Assert.That(File.ReadAllText(extracted), Is.EqualTo("hello"));
		SampleDefinition sample = (SampleDefinition)loaded.Objects[sampleId];
		Assert.That(sample.Asset.FullPath, Is.EqualTo(Path.GetFullPath(extracted)));

		JsonObject json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
		Assert.That(
			json["objects"]![sampleId.Value.ToString()]!["asset"]!["path"]!.GetValue<string>(),
			Is.EqualTo("pcm/tone.wav"));
	}

	[Test]
	public void LoadedPackageCanBundleNewExternalSampleWhenSavedAgain()
	{
		using TempProject project = new();
		string originalAsset = project.Write("source", "original.wav", "original");
		string packagePath = project.Path("song", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
		SongDocument original = DocumentWithSample(originalAsset, out _);
		SongDocumentStorage.Save(packagePath, original);

		SongDocument loaded = SongDocumentStorage.Load(packagePath);
		string newAsset = project.Write("imports", "clap.wav", "clap");
		ObjectId newId = loaded.AllocateObjectId();
		loaded.Add(
			new SampleDefinition(
				newId,
				"Clap",
				ExternalAssetIntegrity.CreateReference(newAsset)));

		SongDocumentStorage.Save(packagePath, loaded);

		using ZipArchive zip = ZipFile.OpenRead(packagePath);
		Assert.That(zip.GetEntry("pcm/original.wav"), Is.Not.Null);
		Assert.That(zip.GetEntry("pcm/clap.wav"), Is.Not.Null);
		SampleDefinition added = (SampleDefinition)loaded.Objects[newId];
		Assert.That(
			added.Asset.FullPath,
			Is.EqualTo(HeresyModulePath.MakeSyntheticPath(packagePath, "pcm/clap.wav")));
	}

	[Test]
	public void PackageJsonMayNotEscapeArchiveOrUseBackslashes()
	{
		using TempProject project = new();
		string packagePath = project.Path("bad.hm");
		using (FileStream stream = File.Create(packagePath))
		using (ZipArchive zip = new(stream, ZipArchiveMode.Create))
		{
			WriteEntry(
				zip,
				"bad.hm.json",
				MinimalManifest("../outside.wav"));
			WriteEntry(zip, "outside.wav", "bad");
		}

		Assert.That(
			() => SongDocumentStorage.Load(packagePath),
			Throws.TypeOf<InvalidDataException>());

		File.Delete(packagePath);
		using (FileStream stream = File.Create(packagePath))
		using (ZipArchive zip = new(stream, ZipArchiveMode.Create))
		{
			WriteEntry(
				zip,
				"bad.hm.json",
				MinimalManifest("pcm\\hihat.wav"));
			WriteEntry(zip, "pcm/hihat.wav", "bad");
		}

		Assert.That(
			() => SongDocumentStorage.Load(packagePath),
			Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void PackageJsonMayOnlyReferenceEntriesActuallyPresent()
	{
		using TempProject project = new();
		string packagePath = project.Path("bad.hm");
		using (FileStream stream = File.Create(packagePath))
		using (ZipArchive zip = new(stream, ZipArchiveMode.Create))
		{
			WriteEntry(
				zip,
				"bad.hm.json",
				MinimalManifest("pcm/missing.wav"));
		}

		Assert.That(
			() => SongDocumentStorage.Load(packagePath),
			Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void SyntheticPathParserRecognizesWindowsAndUnixShapes()
	{
		Assert.That(
			HeresyModulePath.TrySplit(
				@"C:\Users\Bob\My Documents\test.hm\pcm\hihat.wav",
				out string windowsArchive,
				out string windowsEntry),
			Is.True);
		Assert.That(windowsArchive, Is.EqualTo(@"C:\Users\Bob\My Documents\test.hm"));
		Assert.That(windowsEntry, Is.EqualTo("pcm/hihat.wav"));

		Assert.That(
			HeresyModulePath.TrySplit(
				"/home/bob/Documents/test.hm/pcm/hihat.wav",
				out string unixArchive,
				out string unixEntry),
			Is.True);
		Assert.That(unixArchive, Is.EqualTo("/home/bob/Documents/test.hm"));
		Assert.That(unixEntry, Is.EqualTo("pcm/hihat.wav"));
	}

	[Test]
	public void LinuxBackslashInFilenameIsSanitizedWhenBundled()
	{
		if (OperatingSystem.IsWindows())
			Assert.Ignore("Windows does not permit backslashes inside a filename.");

		using TempProject project = new();
		string assetPath = project.Write("song", "samples", @"odd\name.wav", "odd");
		string packagePath = project.Path("song", "test.hm");
		SongDocument document = DocumentWithSample(assetPath, out _);

		SongDocumentStorage.Save(packagePath, document);

		using ZipArchive zip = ZipFile.OpenRead(packagePath);
		Assert.That(zip.GetEntry("samples/odd_name.wav"), Is.Not.Null);
		Assert.That(zip.Entries.Any(entry => entry.FullName.Contains('\\')), Is.False);
	}

	private static SongDocument DocumentWithSample(
		string assetPath,
		out ObjectId id)
	{
		SongDocument document = new();
		id = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				id,
				"Sample",
				new ExternalAssetReference(
					Path.GetFullPath(assetPath),
					ExternalAssetIntegrity.ComputeSha256(assetPath))));
		return document;
	}

	private static string MinimalManifest(string assetPath)
	{
		JsonArray sections =
		[
			new JsonObject
			{
				["type"] = "folder",
				["name"] = "Sequences",
				["children"] = new JsonArray(),
			},
			new JsonObject
			{
				["type"] = "folder",
				["name"] = "Patterns",
				["children"] = new JsonArray(),
			},
			new JsonObject
			{
				["type"] = "folder",
				["name"] = "Instruments",
				["children"] = new JsonArray(),
			},
			new JsonObject
			{
				["type"] = "folder",
				["name"] = "Samples",
				["children"] = new JsonArray
				{
					new JsonObject
					{
						["type"] = "object",
						["name"] = "Sample",
						["objectId"] = 1,
					},
				},
			},
		];

		JsonObject root =
			new()
			{
				["format"] = "Heresy",
				["version"] = 1,
				["nextObjectId"] = 2,
				["rootSequenceId"] = 0,
				["objects"] = new JsonObject
				{
					["1"] = new JsonObject
					{
						["name"] = "Sample",
						["type"] = "sample",
						["asset"] = new JsonObject
						{
							["path"] = assetPath,
							["sha256"] = null,
						},
						["referenceFrequencyHz"] = 261.6255653005986,
						["loop"] = new JsonObject
						{
							["mode"] = "none",
							["startFrame"] = 0,
							["endFrameExclusive"] = 0,
						},
						["sourceChannelPositions"] = new JsonArray(),
					},
				},
				["tombstones"] = new JsonObject(),
				["tree"] = new JsonObject
				{
					["type"] = "folder",
					["name"] = "Song",
					["children"] = sections,
				},
			};

		return root.ToJsonString();
	}

	private static void WriteEntry(ZipArchive zip, string name, string content)
	{
		ZipArchiveEntry entry = zip.CreateEntry(name);
		using StreamWriter writer = new(entry.Open());
		writer.Write(content);
	}

	private static string ReadEntry(ZipArchive zip, string name)
	{
		using StreamReader reader = new(zip.GetEntry(name)!.Open());
		return reader.ReadToEnd();
	}

	private sealed class TempProject : IDisposable
	{
		private readonly string _root =
			System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				$"heresy-storage-{Guid.NewGuid():N}");

		public TempProject() => Directory.CreateDirectory(_root);

		public string Path(params string[] parts)
		{
			string result = _root;
			foreach (string part in parts)
				result = System.IO.Path.Combine(result, part);
			return result;
		}

		public string Write(params string[] partsAndContent)
		{
			string content = partsAndContent[^1];
			string[] parts = partsAndContent[..^1];
			string path = Path(parts);
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
			return path;
		}

		public void Dispose()
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
	}
}

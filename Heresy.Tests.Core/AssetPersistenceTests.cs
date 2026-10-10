using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

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
	public async Task MissingJsonSampleCanBeLocatedAndBecomesSongOwnedPendingAsset()
	{
		using TempProject project = new();
		string original = WritePcm16MonoWave(
			project.Path("songs", "pcm", "tone.wav"), 8000, [12000]);
		string jsonPath = project.Path("songs", "track.hm.json");
		SongDocument source = DocumentWithSample(original, out ObjectId id);
		SongDocumentStorage.Save(jsonPath, source);
		string relocated = project.Path("recovery", "renamed.wav");
		Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
		File.Move(original, relocated);
		int requests = 0;

		SongDocument loaded = await SongDocumentStorage.LoadWithRecoveryAsync(
			jsonPath,
			request =>
			{
				requests++;
				Assert.That(request.SampleId, Is.EqualTo(id));
				Assert.That(request.OriginalFullPath, Is.EqualTo(Path.GetFullPath(original)));
				Assert.That(request.ExpectedSha256, Is.Not.Empty);
				return Task.FromResult<string?>(relocated);
			});

		SampleDefinition sample = (SampleDefinition)loaded.Objects[id];
		Assert.That(requests, Is.EqualTo(1));
		Assert.That(sample.PcmData!.GetSample(0, 0), Is.EqualTo(12000 / 32768.0f).Within(1e-6));
		Assert.That(sample.Asset, Is.Null);
		Assert.That(sample.PendingAsset, Is.Not.Null);
		File.Delete(relocated);
		string packagePath = project.Path("songs", "restored.hm");
		SongDocumentStorage.Save(packagePath, loaded);
		Assert.That(((SampleDefinition)SongDocumentStorage.Load(packagePath).Objects[id]).PcmData,
			Is.Not.Null, "Recovered encoding must survive disappearance of the replacement file.");
	}

	[Test]
	public async Task RecoveryRejectsDifferentContentAndRetriesWithoutChangingManifestIdentity()
	{
		using TempProject project = new();
		string original = WritePcm16MonoWave(
			project.Path("songs", "pcm", "tone.wav"), 8000, [8192]);
		string jsonPath = project.Path("songs", "track.hm.json");
		SongDocument source = DocumentWithSample(original, out ObjectId id);
		SongDocumentStorage.Save(jsonPath, source);
		string correct = project.Path("elsewhere", "correct.wav");
		string wrong = WritePcm16MonoWave(
			project.Path("elsewhere", "wrong.wav"), 8000, [-8192]);
		File.Move(original, correct);
		int prompts = 0;

		SongDocument loaded = await SongDocumentStorage.LoadWithRecoveryAsync(
			jsonPath,
			request =>
			{
				prompts++;
				if (prompts == 2)
					Assert.That(request.FailureMessage, Does.Contain("SHA-256"));
				return Task.FromResult<string?>(prompts == 1 ? wrong : correct);
			});

		Assert.That(prompts, Is.EqualTo(2));
		Assert.That(((SampleDefinition)loaded.Objects[id]).PcmData!.GetSample(0, 0),
			Is.GreaterThan(0));
	}

	[Test]
	public void CancelingSampleRecoveryAbortsTheEntireLoad()
	{
		using TempProject project = new();
		string original = WritePcm16MonoWave(
			project.Path("songs", "pcm", "tone.wav"), 8000, [0]);
		string jsonPath = project.Path("songs", "track.hm.json");
		SongDocumentStorage.Save(jsonPath, DocumentWithSample(original, out _));
		File.Delete(original);

		Assert.ThrowsAsync<OperationCanceledException>(async () =>
			await SongDocumentStorage.LoadWithRecoveryAsync(
				jsonPath,
				_ => Task.FromResult<string?>(null)));
	}

	[Test]
	public async Task PackageMissingEntryCanRecoverWithoutWeakeningDefaultValidation()
	{
		using TempProject project = new();
		string sourcePath = WritePcm16MonoWave(
			project.Path("samples", "tone.wav"), 8000, [7000]);
		string package = project.Path("song", "song.hm");
		SongDocumentStorage.Save(package, DocumentWithSample(sourcePath, out ObjectId id));
		string manifest;
		using (ZipArchive originalZip = ZipFile.OpenRead(package))
			manifest = ReadEntry(originalZip, "song.hm.json");
		File.Delete(package);
		using (FileStream file = File.Create(package))
		using (ZipArchive archive = new(file, ZipArchiveMode.Create))
			WriteEntry(archive, "song.hm.json", manifest);

		Assert.That(() => SongDocumentStorage.Load(package),
			Throws.TypeOf<InvalidDataException>()
				.With.Message.Contains("missing archive entry"));

		SongDocument recovered = await SongDocumentStorage.LoadWithRecoveryAsync(
			package,
			request =>
			{
				Assert.That(request.SampleId, Is.EqualTo(id));
				Assert.That(request.OriginalFullPath, Does.Contain("song.hm"));
				return Task.FromResult<string?>(sourcePath);
			});
		Assert.That(((SampleDefinition)recovered.Objects[id]).PendingAsset, Is.Not.Null);
	}


	[Test]
	public async Task RecoveryCanResolveMultipleDistinctMissingAssetsBeforePublishing()
	{
		using TempProject project = new();
		string first = WritePcm16MonoWave(
			project.Path("song", "pcm", "first.wav"), 8000, [1000]);
		string second = WritePcm16MonoWave(
			project.Path("song", "pcm", "second.wav"), 8000, [2000]);
		SongDocument source = DocumentWithSample(first, out ObjectId firstId);
		ObjectId secondId = source.AllocateObjectId();
		source.Add(new SampleDefinition(secondId, "Second",
			ExternalAssetIntegrity.CreateReference(second)));
		string path = project.Path("song", "track.hm.json");
		SongDocumentStorage.Save(path, source);
		string relocatedFirst = project.Path("relocated", "first.wav");
		string relocatedSecond = project.Path("relocated", "second.wav");
		Directory.CreateDirectory(Path.GetDirectoryName(relocatedFirst)!);
		File.Move(first, relocatedFirst);
		File.Move(second, relocatedSecond);
		int attempts = 0;

		SongDocument loaded = await SongDocumentStorage.LoadWithRecoveryAsync(
			path,
			request =>
			{
				attempts++;
				return Task.FromResult<string?>(
					request.SampleId == firstId ? relocatedFirst : relocatedSecond);
			});

		Assert.That(attempts, Is.EqualTo(2));
		Assert.That(((SampleDefinition)loaded.Objects[firstId]).PendingAsset, Is.Not.Null);
		Assert.That(((SampleDefinition)loaded.Objects[secondId]).PendingAsset, Is.Not.Null);
	}

	[Test]
	public void InvalidArchiveManifestCannotBeBypassedByRecoveryCallback()
	{
		using TempProject project = new();
		string package = project.Path("song.hm");
		using (FileStream file = File.Create(package))
		using (ZipArchive archive = new(file, ZipArchiveMode.Create))
			WriteEntry(archive, "song.hm.json", MinimalManifest("../outside.wav"));
		int attempted = 0;

		Assert.ThrowsAsync<InvalidDataException>(async () =>
			await SongDocumentStorage.LoadWithRecoveryAsync(
				package,
				_ =>
				{
					attempted++;
					return Task.FromResult<string?>("irrelevant.wav");
				}));
		Assert.That(attempted, Is.Zero);
	}

	[Test]
	public void PackageLoadHydratesDecodedPcmAndDropsEncodedBytes()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("source", "tone.wav"),
				sampleRate: 8000,
				samples: [-32768, 16384]);
		string packagePath = project.Path("song", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(packagePath, document);

		SongDocument loaded = SongDocumentStorage.Load(packagePath);
		SampleDefinition sample = (SampleDefinition)loaded.Objects[sampleId];
		File.Delete(packagePath);

		Assert.That(sample.PcmData, Is.Not.Null);
		Assert.That(sample.PcmData!.SampleRate, Is.EqualTo(8000));
		Assert.That(sample.PcmData.ChannelCount, Is.EqualTo(1));
		Assert.That(sample.PcmData.FrameCount, Is.EqualTo(2));
		Assert.That(sample.PcmData.GetSample(0, 0), Is.EqualTo(-1.0f).Within(1e-6));
		Assert.That(sample.PcmData.GetSample(1, 0), Is.EqualTo(0.5f).Within(1e-6));
		Assert.That(sample.PendingAsset, Is.Null);
		Assert.That(sample.Asset, Is.Not.Null);
	}

	[Test]
	public void ImportedEncodedPayloadSurvivesSourceDeletionUntilPackageSave()
	{
		using TempProject project = new();
		string sourcePath =
			WritePcm16MonoWave(
				project.Path("imports", "tone.wav"),
				sampleRate: 8000,
				samples: [8192]);
		byte[] originalBytes = File.ReadAllBytes(sourcePath);
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		SampleDefinition sample =
			SampleDefinition.CreateImported(
				id,
				"Tone",
				Path.GetFileName(sourcePath),
				originalBytes);
		document.Add(sample);
		File.Delete(sourcePath);
		string packagePath = project.Path("song", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);

		SongDocumentStorage.Save(packagePath, document);

		Assert.That(sample.PendingAsset, Is.Null);
		Assert.That(sample.Asset, Is.Not.Null);
		Assert.That(
			sample.Asset!.FullPath,
			Is.EqualTo(
				HeresyModulePath.MakeSyntheticPath(
					packagePath,
					"pcm/tone.wav")));
		using ZipArchive zip = ZipFile.OpenRead(packagePath);
		JsonObject manifest =
			JsonNode.Parse(
				ReadEntry(zip, "test.hm.json"))!
				.AsObject();
		Assert.That(
			manifest["objects"]![id.Value.ToString()]!["asset"]!["sha256"]!
				.GetValue<string>(),
			Is.EqualTo(sample.Asset.Sha256));
		Assert.That(manifest["objects"]![id.Value.ToString()]!
			.AsObject().ContainsKey("referenceFrequencyHz"), Is.False,
			"Packages must not emit dead sample tuning metadata.");
		using Stream stream = zip.GetEntry("pcm/tone.wav")!.Open();
		using MemoryStream copy = new();
		stream.CopyTo(copy);
		Assert.That(copy.ToArray(), Is.EqualTo(originalBytes));
	}

	[Test]
	public void SaveRejectsChangedPersistedEncodingRatherThanSilentlyUsingIt()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("assets", "tone.wav"),
				sampleRate: 8000,
				samples: [0]);
		string jsonPath = project.Path("song", "test.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
		SongDocument document = DocumentWithSample(assetPath, out _);
		SongDocumentStorage.Save(
			jsonPath,
			document,
			JsonAssetPathMode.Absolute);
		SongDocument loaded = SongDocumentStorage.Load(jsonPath);
		File.WriteAllBytes(assetPath, [1, 2, 3, 4]);

		Assert.That(
			() => SongDocumentStorage.Save(
				jsonPath,
				loaded,
				JsonAssetPathMode.Absolute),
			Throws.TypeOf<InvalidOperationException>()
				.With.Message.Contains("changed"));
	}

	[Test]
	public void BareJsonSaveStoresRelativePathWhileDocumentKeepsFullPath()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("songs", "assets", "kick.wav"),
				sampleRate: 8000,
				samples: [0]);
		string jsonPath = project.Path("songs", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);

		SongDocumentStorage.Save(jsonPath, document);

		SampleDefinition sample = (SampleDefinition)document.Objects[sampleId];
		Assert.That(sample.Asset!.FullPath, Is.EqualTo(Path.GetFullPath(assetPath)));

		JsonObject json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
		Assert.That(
			json["objects"]![sampleId.Value.ToString()]!["asset"]!["path"]!.GetValue<string>(),
			Is.EqualTo("assets/kick.wav"));
	}

	[Test]
	public void BareJsonLoadResolvesAssetToAbsolutePath()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("songs", "assets", "kick.wav"),
				sampleRate: 8000,
				samples: [0]);
		string jsonPath = project.Path("songs", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

		SongDocument source = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(jsonPath, source);

		SongDocument loaded = SongDocumentStorage.Load(jsonPath);

		SampleDefinition sample = (SampleDefinition)loaded.Objects[sampleId];
		Assert.That(sample.Asset!.FullPath, Is.EqualTo(Path.GetFullPath(assetPath)));
	}

	[Test]
	public void BareJsonRelativeSaveRejectsAssetOutsideJsonSubtree()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("library", "snare.wav"),
				sampleRate: 8000,
				samples: [4096]);
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
		string assetPath =
			WritePcm16MonoWave(
				project.Path("songs", "assets", "kick.wav"),
				sampleRate: 8000,
				samples: [0]);
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
		string assetPath =
			WritePcm16MonoWave(
				project.Path("library", "snare.wav"),
				sampleRate: 8000,
				samples: [4096]);
		string first = project.Path("one", "track.hm.json");
		string second = project.Path("two", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(first)!);
		Directory.CreateDirectory(Path.GetDirectoryName(second)!);
		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);

		SongDocumentStorage.Save(first, document, JsonAssetPathMode.Absolute);
		SongDocumentStorage.Save(second, document, JsonAssetPathMode.Absolute);

		SampleDefinition sample = (SampleDefinition)document.Objects[sampleId];
		Assert.That(sample.Asset!.FullPath, Is.EqualTo(Path.GetFullPath(assetPath)));
		JsonObject json = JsonNode.Parse(File.ReadAllText(second))!.AsObject();
		Assert.That(
			json["objects"]![sampleId.Value.ToString()]!["asset"]!["path"]!.GetValue<string>(),
			Is.EqualTo(Path.GetFullPath(assetPath)));
	}

	[Test]
	public void BareJsonLoadResolvesAbsoluteAssetPath()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("library", "snare.wav"),
				sampleRate: 8000,
				samples: [4096]);
		string jsonPath = project.Path("songs", "track.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
		SongDocument source = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(jsonPath, source, JsonAssetPathMode.Absolute);

		SongDocument loaded = SongDocumentStorage.Load(jsonPath);

		Assert.That(
			((SampleDefinition)loaded.Objects[sampleId]).Asset!.FullPath,
			Is.EqualTo(Path.GetFullPath(assetPath)));
	}

	[Test]
	public void BareJsonLoadFailsWhenAssetCannotBeResolved()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("songs", "assets", "kick.wav"),
				sampleRate: 8000,
				samples: [0]);
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
		string assetPath =
			WritePcm16MonoWave(
				project.Path("elsewhere", "hihat.wav"),
				sampleRate: 8000,
				samples: [8192]);
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
			sample.Asset!.FullPath,
			Is.EqualTo(HeresyModulePath.MakeSyntheticPath(packagePath, "pcm/hihat.wav")));
	}

	[Test]
	public void PackageSavePreservesFilesystemSubdirectoryBelowPackageContainer()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("song", "samples", "drums", "hihat.wav"),
				sampleRate: 8000,
				samples: [8192]);
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
		string assetPath =
			WritePcm16MonoWave(
				project.Path("source", "tone.wav"),
				sampleRate: 8000,
				samples: [16384]);
		string packagePath = project.Path("song", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
		SongDocument document = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(packagePath, document);

		SongDocument loaded = SongDocumentStorage.Load(packagePath);
		SampleDefinition sample = (SampleDefinition)loaded.Objects[sampleId];

		Assert.That(
			sample.Asset!.FullPath,
			Is.EqualTo(HeresyModulePath.MakeSyntheticPath(packagePath, "pcm/tone.wav")));
		ExternalAssetCheck check = ExternalAssetIntegrity.Check(sample.Asset);
		Assert.That(check.Status, Is.EqualTo(ExternalAssetStatus.Match));
	}

	[Test]
	public void SavingPackageBackedDocumentAsBareJsonExtractsAssetHierarchy()
	{
		using TempProject project = new();
		string assetPath =
			WritePcm16MonoWave(
				project.Path("source", "tone.wav"),
				sampleRate: 8000,
				samples: [16384]);
		string packagePath = project.Path("package", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
		SongDocument original = DocumentWithSample(assetPath, out ObjectId sampleId);
		SongDocumentStorage.Save(packagePath, original);

		byte[] originalEncoded = File.ReadAllBytes(assetPath);
		SongDocument loaded = SongDocumentStorage.Load(packagePath);
		string jsonPath = project.Path("export", "copy.hm.json");
		Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);

		SongDocumentStorage.Save(jsonPath, loaded);

		string extracted = project.Path("export", "pcm", "tone.wav");
		Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(originalEncoded));
		SampleDefinition sample = (SampleDefinition)loaded.Objects[sampleId];
		Assert.That(sample.Asset!.FullPath, Is.EqualTo(Path.GetFullPath(extracted)));

		JsonObject json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
		Assert.That(
			json["objects"]![sampleId.Value.ToString()]!["asset"]!["path"]!.GetValue<string>(),
			Is.EqualTo("pcm/tone.wav"));
	}

	[Test]
	public void LoadedPackageCanBundleNewExternalSampleWhenSavedAgain()
	{
		using TempProject project = new();
		string originalAsset =
			WritePcm16MonoWave(
				project.Path("source", "original.wav"),
				sampleRate: 8000,
				samples: [0]);
		string packagePath = project.Path("song", "test.hm");
		Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
		SongDocument original = DocumentWithSample(originalAsset, out _);
		SongDocumentStorage.Save(packagePath, original);

		SongDocument loaded = SongDocumentStorage.Load(packagePath);
		string newAsset =
			WritePcm16MonoWave(
				project.Path("imports", "clap.wav"),
				sampleRate: 8000,
				samples: [24576]);
		ObjectId newId = loaded.AllocateObjectId();
		loaded.Add(
			SampleDefinition.CreateImported(
				newId,
				"Clap",
				Path.GetFileName(newAsset),
				File.ReadAllBytes(newAsset)));

		SongDocumentStorage.Save(packagePath, loaded);

		using ZipArchive zip = ZipFile.OpenRead(packagePath);
		Assert.That(zip.GetEntry("pcm/original.wav"), Is.Not.Null);
		Assert.That(zip.GetEntry("pcm/clap.wav"), Is.Not.Null);
		SampleDefinition added = (SampleDefinition)loaded.Objects[newId];
		Assert.That(
			added.Asset!.FullPath,
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
		string assetPath =
			WritePcm16MonoWave(
				project.Path("song", "samples", @"odd\name.wav"),
				sampleRate: 8000,
				samples: [0]);
		string packagePath = project.Path("song", "test.hm");
		SongDocument document = DocumentWithSample(assetPath, out _);

		SongDocumentStorage.Save(packagePath, document);

		using ZipArchive zip = ZipFile.OpenRead(packagePath);
		Assert.That(zip.GetEntry("samples/odd_name.wav"), Is.Not.Null);
		Assert.That(zip.Entries.Any(entry => entry.FullName.Contains('\\')), Is.False);
	}

	private static string WritePcm16MonoWave(
		string path,
		int sampleRate,
		short[] samples)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		using FileStream file = File.Create(path);
		using BinaryWriter writer = new(file);
		int dataBytes = checked(samples.Length * 2);
		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
		writer.Write(36 + dataBytes);
		writer.Write(Encoding.ASCII.GetBytes("WAVE"));
		writer.Write(Encoding.ASCII.GetBytes("fmt "));
		writer.Write(16);
		writer.Write((ushort)1);
		writer.Write((ushort)1);
		writer.Write(sampleRate);
		writer.Write(sampleRate * 2);
		writer.Write((ushort)2);
		writer.Write((ushort)16);
		writer.Write(Encoding.ASCII.GetBytes("data"));
		writer.Write(dataBytes);
		foreach (short sample in samples)
			writer.Write(sample);
		return path;
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
						// Legacy version-one modules may carry obsolete metadata.
						// Loading ignores it without changing PCM tuning.
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

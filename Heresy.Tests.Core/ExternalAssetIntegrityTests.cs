using System;
using System.IO;
using System.Linq;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class ExternalAssetIntegrityTests
{
	[Test]
	public void CreateReferenceStoresAbsolutePathAndLowercaseSha256()
	{
		using TempProject project = new();
		string assetPath = project.Write("tone.bin", "hello");

		ExternalAssetReference reference =
			ExternalAssetIntegrity.CreateReference(assetPath);

		Assert.That(reference.FullPath, Is.EqualTo(Path.GetFullPath(assetPath)));
		Assert.That(
			reference.Sha256,
			Is.EqualTo(
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
	}

	[Test]
	public void MatchingHashReportsMatch()
	{
		using TempProject project = new();
		string assetPath = project.Write("tone.bin", "hello");
		ExternalAssetReference reference =
			new(
				assetPath,
				"2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824");

		ExternalAssetCheck check = ExternalAssetIntegrity.Check(reference);

		Assert.That(check.Status, Is.EqualTo(ExternalAssetStatus.Match));
		Assert.That(check.FullPath, Is.EqualTo(Path.GetFullPath(assetPath)));
		Assert.That(
			check.ActualSha256,
			Is.EqualTo(
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
	}

	[Test]
	public void ChangedFileReportsExpectedAndActualHashes()
	{
		using TempProject project = new();
		string assetPath = project.Write("tone.bin", "changed");
		ExternalAssetReference reference =
			new(
				assetPath,
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");

		ExternalAssetCheck check = ExternalAssetIntegrity.Check(reference);

		Assert.That(check.Status, Is.EqualTo(ExternalAssetStatus.HashMismatch));
		Assert.That(check.ExpectedSha256, Is.EqualTo(reference.Sha256));
		Assert.That(check.ActualSha256, Is.Not.EqualTo(reference.Sha256));
	}

	[Test]
	public void ExistingAssetWithoutHashReportsUnhashedAndComputesHash()
	{
		using TempProject project = new();
		string assetPath = project.Write("tone.bin", "hello");

		ExternalAssetCheck check =
			ExternalAssetIntegrity.Check(new ExternalAssetReference(assetPath));

		Assert.That(check.Status, Is.EqualTo(ExternalAssetStatus.Unhashed));
		Assert.That(
			check.ActualSha256,
			Is.EqualTo(
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
	}

	[Test]
	public void MissingAssetReportsMissingWithoutThrowing()
	{
		using TempProject project = new();
		string missing = project.Path("missing.wav");

		ExternalAssetCheck check =
			ExternalAssetIntegrity.Check(
				new ExternalAssetReference(missing, new string('0', 64)));

		Assert.That(check.Status, Is.EqualTo(ExternalAssetStatus.Missing));
		Assert.That(check.ActualSha256, Is.Null);
		Assert.That(check.FullPath, Is.EqualTo(Path.GetFullPath(missing)));
	}

	[Test]
	public void ScanReportsEverySampleWithObjectIdentity()
	{
		using TempProject project = new();
		string present = project.Write("present.wav", "hello");
		string missing = project.Path("missing.wav");
		SongDocument document = new();
		ObjectId presentId = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				presentId,
				"Present",
				new ExternalAssetReference(present)));
		ObjectId missingId = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				missingId,
				"Missing",
				new ExternalAssetReference(missing)));

		ExternalAssetDiagnostic[] diagnostics =
			ExternalAssetIntegrity.Scan(document).ToArray();

		Assert.That(diagnostics, Has.Length.EqualTo(2));
		Assert.That(diagnostics[0].ObjectId, Is.EqualTo(presentId));
		Assert.That(diagnostics[0].Status, Is.EqualTo(ExternalAssetStatus.Unhashed));
		Assert.That(diagnostics[0].FullPath, Is.EqualTo(Path.GetFullPath(present)));
		Assert.That(diagnostics[1].ObjectId, Is.EqualTo(missingId));
		Assert.That(diagnostics[1].Status, Is.EqualTo(ExternalAssetStatus.Missing));
	}

	[Test]
	public void RefreshHashReturnsNewReferenceWithoutMutatingOriginal()
	{
		using TempProject project = new();
		string assetPath = project.Write("tone.wav", "hello");
		ExternalAssetReference original =
			new(assetPath, new string('0', 64));

		ExternalAssetReference refreshed =
			ExternalAssetIntegrity.RefreshHash(original);

		Assert.That(refreshed.FullPath, Is.EqualTo(original.FullPath));
		Assert.That(
			refreshed.Sha256,
			Is.EqualTo(
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
		Assert.That(original.Sha256, Is.EqualTo(new string('0', 64)));
	}

	private sealed class TempProject : IDisposable
	{
		private readonly string _root =
			System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				$"heresy-assets-{Guid.NewGuid():N}");

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
			string path = Path(partsAndContent[..^1]);
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

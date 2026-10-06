using System;
using System.IO;
using System.Linq;
using System.Text;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class ExternalAssetIntegrityTests
{
	[Test]
	public void ResolvePathUsesSongDirectory()
	{
		using TempProject project = new();
		string songPath = project.Path("songs", "track.heresy");
		ExternalAssetReference reference =
			new("../assets/kick.wav");

		string resolved =
			ExternalAssetIntegrity.ResolvePath(
				songPath,
				reference);

		Assert.That(
			resolved,
			Is.EqualTo(
				System.IO.Path.GetFullPath(
					project.Path("assets", "kick.wav"))));
	}

	[Test]
	public void CreateReferenceStoresPortableRelativePathAndLowercaseSha256()
	{
		using TempProject project = new();
		string songPath = project.Path("songs", "track.heresy");
		string assetPath = project.Path("assets", "tone.bin");
		Directory.CreateDirectory(
			System.IO.Path.GetDirectoryName(assetPath)!);
		File.WriteAllBytes(
			assetPath,
			Encoding.UTF8.GetBytes("hello"));

		ExternalAssetReference reference =
			ExternalAssetIntegrity.CreateReference(
				songPath,
				assetPath);

		Assert.That(
			reference.RelativePath,
			Is.EqualTo("../assets/tone.bin"));
		Assert.That(
			reference.Sha256,
			Is.EqualTo(
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
	}

	[Test]
	public void MatchingHashReportsMatch()
	{
		using TempProject project = new();
		string songPath = project.Path("track.heresy");
		string assetPath = project.Path("tone.bin");
		File.WriteAllText(assetPath, "hello");

		ExternalAssetReference reference =
			new(
				"tone.bin",
				"2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824");

		ExternalAssetCheck check =
			ExternalAssetIntegrity.Check(
				songPath,
				reference);

		Assert.That(
			check.Status,
			Is.EqualTo(ExternalAssetStatus.Match));
		Assert.That(
			check.ActualSha256,
			Is.EqualTo(
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
	}

	[Test]
	public void ChangedFileReportsExpectedAndActualHashes()
	{
		using TempProject project = new();
		string songPath = project.Path("track.heresy");
		string assetPath = project.Path("tone.bin");
		File.WriteAllText(assetPath, "changed");

		ExternalAssetReference reference =
			new(
				"tone.bin",
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");

		ExternalAssetCheck check =
			ExternalAssetIntegrity.Check(
				songPath,
				reference);

		Assert.That(
			check.Status,
			Is.EqualTo(ExternalAssetStatus.HashMismatch));
		Assert.That(
			check.ExpectedSha256,
			Is.EqualTo(reference.Sha256));
		Assert.That(
			check.ActualSha256,
			Is.Not.EqualTo(reference.Sha256));
	}

	[Test]
	public void ExistingAssetWithoutHashReportsUnhashedAndComputesHash()
	{
		using TempProject project = new();
		string songPath = project.Path("track.heresy");
		File.WriteAllText(project.Path("tone.bin"), "hello");

		ExternalAssetCheck check =
			ExternalAssetIntegrity.Check(
				songPath,
				new ExternalAssetReference("tone.bin"));

		Assert.That(
			check.Status,
			Is.EqualTo(ExternalAssetStatus.Unhashed));
		Assert.That(
			check.ActualSha256,
			Is.EqualTo(
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
	}

	[Test]
	public void MissingAssetReportsMissingWithoutThrowing()
	{
		using TempProject project = new();
		string songPath = project.Path("track.heresy");

		ExternalAssetCheck check =
			ExternalAssetIntegrity.Check(
				songPath,
				new ExternalAssetReference(
					"missing.wav",
					new string('0', 64)));

		Assert.That(
			check.Status,
			Is.EqualTo(ExternalAssetStatus.Missing));
		Assert.That(check.ActualSha256, Is.Null);
		Assert.That(
			check.ResolvedPath,
			Is.EqualTo(
				System.IO.Path.GetFullPath(
					project.Path("missing.wav"))));
	}

	[Test]
	public void ScanReportsEverySampleWithObjectIdentity()
	{
		using TempProject project = new();
		string songPath = project.Path("track.heresy");
		File.WriteAllText(project.Path("present.wav"), "hello");

		SongDocument document = new();
		ObjectId presentId = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				presentId,
				"Present",
				new ExternalAssetReference("present.wav")));
		ObjectId missingId = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				missingId,
				"Missing",
				new ExternalAssetReference("missing.wav")));

		ExternalAssetDiagnostic[] diagnostics =
			ExternalAssetIntegrity.Scan(
				songPath,
				document)
			.ToArray();

		Assert.That(diagnostics, Has.Length.EqualTo(2));
		Assert.That(
			diagnostics[0],
			Is.EqualTo(
				new ExternalAssetDiagnostic(
					presentId,
					"Present",
					ExternalAssetStatus.Unhashed,
					"present.wav",
					System.IO.Path.GetFullPath(
						project.Path("present.wav")),
					null,
					"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")));
		Assert.That(diagnostics[1].ObjectId, Is.EqualTo(missingId));
		Assert.That(diagnostics[1].ObjectName, Is.EqualTo("Missing"));
		Assert.That(
			diagnostics[1].Status,
			Is.EqualTo(ExternalAssetStatus.Missing));
	}

	[Test]
	public void RefreshHashReturnsNewReferenceWithoutMutatingDefinition()
	{
		using TempProject project = new();
		string songPath = project.Path("track.heresy");
		File.WriteAllText(project.Path("tone.wav"), "hello");

		ExternalAssetReference original =
			new("tone.wav", new string('0', 64));

		ExternalAssetReference refreshed =
			ExternalAssetIntegrity.RefreshHash(
				songPath,
				original);

		Assert.That(
			refreshed.RelativePath,
			Is.EqualTo(original.RelativePath));
		Assert.That(
			refreshed.Sha256,
			Is.EqualTo(
				"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
		Assert.That(
			original.Sha256,
			Is.EqualTo(new string('0', 64)));
	}

	[TestCase("")]
	[TestCase("/absolute/file.wav")]
	public void ReferencePathMustBeRelativeAndNonEmpty(
		string relativePath)
	{
		using TempProject project = new();
		string songPath = project.Path("track.heresy");

		Assert.That(
			() => ExternalAssetIntegrity.ResolvePath(
				songPath,
				new ExternalAssetReference(relativePath)),
			Throws.TypeOf<ArgumentException>());
	}

	private sealed class TempProject : IDisposable
	{
		private readonly string _root =
			System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				$"heresy-assets-{Guid.NewGuid():N}");

		public TempProject()
			=> Directory.CreateDirectory(_root);

		public string Path(params string[] parts)
		{
			string result = _root;
			foreach (string part in parts)
				result = System.IO.Path.Combine(result, part);
			return result;
		}

		public void Dispose()
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
	}
}

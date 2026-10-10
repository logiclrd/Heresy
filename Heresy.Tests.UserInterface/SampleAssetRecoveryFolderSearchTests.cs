using System;
using System.IO;

using Heresy.Core.Objects;
using Heresy.Core.Persistence;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SampleAssetRecoveryFolderSearchTests
{
	[Test]
	public void FolderSearchRequiresMatchingNameAndManifestHash()
	{
		string root = Path.Combine(Path.GetTempPath(),
			$"heresy-recovery-search-{Guid.NewGuid():N}");
		try
		{
			string wrong = Path.Combine(root, "tone.wav");
			string correct = Path.Combine(root, "nested", "tone.wav");
			Directory.CreateDirectory(Path.GetDirectoryName(correct)!);
			File.WriteAllBytes(wrong, [1, 2, 3]);
			File.WriteAllBytes(correct, [5, 6, 7]);
			string expectedHash = Heresy.Core.Assets.ExternalAssetIntegrity
				.ComputeSha256(correct);
			SampleAssetRecoveryRequest request = new(
				new ObjectId(1),
				"Tone",
				Path.Combine(root, "missing", "tone.wav"),
				expectedHash,
				"missing");

			Assert.That(
				SampleAssetRecoveryFolderSearch.FindMatch(request, root),
				Is.EqualTo(Path.GetFullPath(correct)));
			Assert.That(
				SampleAssetRecoveryFolderSearch.FindMatch(
					request with { ExpectedSha256 = null }, root),
				Is.Null,
				"an unhashed sample cannot be automatically substituted");
			Assert.That(
				SampleAssetRecoveryFolderSearch.FindMatch(
					request with { ExpectedSha256 = "not-the-original" }, root),
				Is.Null);
			Assert.That(
				SampleAssetRecoveryFolderSearch.FindMatch(
					request, root, maxEntries: 1),
				Is.Null, "an enormous tree must not be searched without a bound");
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}
}

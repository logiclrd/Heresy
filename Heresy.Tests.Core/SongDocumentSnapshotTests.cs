using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SongDocumentSnapshotTests
{
	[Test]
	public void SnapshotDeepClonesAuthoringDocumentAndCapturesRevisions()
	{
		SongDocument document = new();
		ObjectId patternId = document.AllocateObjectId();
		DataPatternDefinition pattern =
			new(patternId, "Original")
			{
				RowCount = 16,
			};
		document.Add(pattern);
		document.RootSequenceId = (ObjectId)99U;
		document.MarkChanged(affectsAudio: true);

		SongDocumentSnapshot snapshot =
			SongDocumentSnapshot.Create(document);

		snapshot.Document.Should().NotBeSameAs(document);
		snapshot.DocumentRevision.Should().Be(document.DocumentRevision);
		snapshot.AudioRevision.Should().Be(document.AudioRevision);
		snapshot.Document.RootSequenceId.Should().Be((ObjectId)99U);

		document.TryGet(patternId, out SongObject? sourceObject).Should().BeTrue();
		DataPatternDefinition sourcePattern =
			sourceObject.Should().BeOfType<DataPatternDefinition>().Subject;
		sourcePattern.Name = "Changed";
		sourcePattern.RowCount = 32;
		document.MarkChanged(affectsAudio: true);

		snapshot.Document.TryGet(patternId, out SongObject? clonedObject)
			.Should().BeTrue();
		DataPatternDefinition clonedPattern =
			clonedObject.Should().BeOfType<DataPatternDefinition>().Subject;
		clonedPattern.Name.Should().Be("Original");
		clonedPattern.RowCount.Should().Be(16);
		snapshot.DocumentRevision.Should().NotBe(document.DocumentRevision);
		snapshot.AudioRevision.Should().NotBe(document.AudioRevision);

		clonedPattern.Name = "Snapshot Changed";
		sourcePattern.Name.Should().Be("Changed");
	}

	[Test]
	public void SnapshotCarriesDecodedPcmAndPendingEncodingWithoutFilesystemDependency()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		byte[] wave = CreateWave(sample: 8192);
		SampleDefinition sample =
			SampleDefinition.CreateImported(
				id,
				"Pending",
				"pending.wav",
				wave);
		document.Add(sample);

		SongDocumentSnapshot snapshot =
			SongDocumentSnapshot.Create(document);

		SampleDefinition clone =
			snapshot.Document.Objects[id]
				.Should().BeOfType<SampleDefinition>().Subject;
		clone.Should().NotBeSameAs(sample);
		clone.PcmData.Should().BeSameAs(sample.PcmData);
		clone.PendingAsset.Should().BeSameAs(sample.PendingAsset);
		clone.Asset.Should().BeNull();
		clone.PcmData!.GetSample(0, 0)
			.Should().BeApproximately(0.25f, 1e-6f);
	}

	[Test]
	public void SnapshotPreservesUnreferencedTombstonesWithoutMutatingSource()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(id, "Gone"));
		document.Remove(id);
		document.Tombstones.Should().ContainKey(id);

		SongDocumentSnapshot snapshot =
			SongDocumentSnapshot.Create(document);

		document.Tombstones.Should().ContainKey(id);
		snapshot.Document.Tombstones.Should().ContainKey(id);
	}
	private static byte[] CreateWave(short sample)
	{
		using System.IO.MemoryStream stream = new();
		using (System.IO.BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true))
		{
			writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
			writer.Write(38);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
			writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
			writer.Write(16);
			writer.Write((ushort)1);
			writer.Write((ushort)1);
			writer.Write(8000);
			writer.Write(16000);
			writer.Write((ushort)2);
			writer.Write((ushort)16);
			writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
			writer.Write(2);
			writer.Write(sample);
		}
		return stream.ToArray();
	}

}

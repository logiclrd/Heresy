using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SongTreeSectionTests
{
	[Test]
	public void NewDocumentHasFourFixedSectionRootsInDocumentPaneOrder()
	{
		SongDocument document = new();

		document.Root.Children.Should().ContainInOrder(
			document.GetSectionRoot(SongTreeSection.Sequences),
			document.GetSectionRoot(SongTreeSection.Patterns),
			document.GetSectionRoot(SongTreeSection.Instruments),
			document.GetSectionRoot(SongTreeSection.Samples));
		document.Root.Children.Select(node => node.Name).Should().Equal(
			"Sequences",
			"Patterns",
			"Instruments",
			"Samples");
		document.DocumentRevision.Should().Be(0);
		document.AudioRevision.Should().Be(0);
	}

	[Test]
	public void AddCreatesOneCanonicalTreePlacementInSectionForObjectKind()
	{
		SongDocument document = new();

		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(new DataSequenceDefinition(sequenceId, "Sequence"));

		ObjectId patternId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(patternId, "Pattern"));

		ObjectId instrumentId = document.AllocateObjectId();
		document.Add(new InstrumentDefinition(instrumentId, "Instrument"));

		ObjectId envelopeId = document.AllocateObjectId();
		document.Add(new AdsrEnvelopeDefinition(envelopeId, "Envelope"));

		ObjectId sampleId = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				sampleId,
				"Sample",
				new ExternalAssetReference("sample.wav")));

		Ids(document.GetSectionRoot(SongTreeSection.Sequences))
			.Should().Equal(sequenceId);
		Ids(document.GetSectionRoot(SongTreeSection.Patterns))
			.Should().Equal(patternId);
		Ids(document.GetSectionRoot(SongTreeSection.Instruments))
			.Should().Equal(instrumentId, envelopeId);
		Ids(document.GetSectionRoot(SongTreeSection.Samples))
			.Should().Equal(sampleId);
	}

	[Test]
	public void AddAdvancesRevisionOnlyOnceDespiteAutomaticTreePlacement()
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();

		document.Add(new DataPatternDefinition(id, "Pattern"));

		document.DocumentRevision.Should().Be(1);
		document.AudioRevision.Should().Be(1);
	}

	private static ObjectId[] Ids(SongTreeFolder folder)
		=> folder.Children
			.Cast<SongTreeObject>()
			.Select(node => node.ObjectId)
			.ToArray();
}

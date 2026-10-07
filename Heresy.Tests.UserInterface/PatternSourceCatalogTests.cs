using System.IO;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Assets;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternSourceCatalogTests
{
	[Test]
	public void FmSynthIsASelectableSoundSourceInSampleLikeOrder()
	{
		SongDocument document = new();

		ObjectId sequenceId = document.AllocateObjectId();
		document.Add(new DataSequenceDefinition(sequenceId, "Sequence"));

		ObjectId patternId = document.AllocateObjectId();
		document.Add(new DataPatternDefinition(patternId, "Pattern"));

		ObjectId instrumentId = document.AllocateObjectId();
		document.Add(new InstrumentDefinition(instrumentId, "Instrument"));

		ObjectId synthId = document.AllocateObjectId();
		document.Add(
			new FmSynthDefinition(
				synthId,
				"FM",
				new FmSynthGraph(
					[
						new FmConstantNode(0, 0.0),
					],
					outputNodeId: 0)));

		ObjectId sampleId = document.AllocateObjectId();
		document.Add(
			new SampleDefinition(
				sampleId,
				"Sample",
				new ExternalAssetReference(
					Path.Combine(
						Path.GetTempPath(),
						"source-catalog.wav"))));

		PatternSourceOption[] sources =
			PatternSourceCatalog.GetSources(document);

		sources.Select(source => source.Id).Should().Equal(
			sampleId,
			synthId,
			instrumentId,
			patternId,
			sequenceId);
		sources.Single(source => source.Id == synthId).Kind
			.Should().Be(SongObjectKind.FmSynth);
		PatternSourceCatalog.IsSoundSource(SongObjectKind.FmSynth)
			.Should().BeTrue();
	}
}

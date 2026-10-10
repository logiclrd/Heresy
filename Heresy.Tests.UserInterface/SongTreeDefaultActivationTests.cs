using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class SongTreeDefaultActivationTests
{
	[TestCase(SongTreeSection.Sequences, SongObjectKind.Sequence, SongTreeActivationKind.Sequence)]
	[TestCase(SongTreeSection.Patterns, SongObjectKind.Pattern, SongTreeActivationKind.Pattern)]
	[TestCase(SongTreeSection.Instruments, SongObjectKind.Instrument, SongTreeActivationKind.Instrument)]
	[TestCase(SongTreeSection.Envelopes, SongObjectKind.Envelope, SongTreeActivationKind.Envelope)]
	[TestCase(SongTreeSection.Samples, SongObjectKind.Sample, SongTreeActivationKind.Sample)]
	[TestCase(SongTreeSection.Samples, SongObjectKind.FmSynth, SongTreeActivationKind.FmSynth)]
	public void EditableObjectsResolveToTheirDefaultEditor(
		SongTreeSection section,
		SongObjectKind kind,
		SongTreeActivationKind expected)
	{
		SongTreeItemViewModel item =
			Item(
				kind,
				missing: false);

		SongTreeDefaultActivation.Resolve(
				section,
				item)
			.Should().Be(expected);
	}

	[Test]
	public void MissingReferenceHasNoDefaultActivation()
	{
		SongTreeItemViewModel item =
			Item(
				SongObjectKind.Pattern,
				missing: true);

		SongTreeDefaultActivation.Resolve(
				SongTreeSection.Patterns,
				item)
			.Should().Be(SongTreeActivationKind.None);
	}

	[Test]
	public void WrongSectionHasNoDefaultActivation()
	{
		SongTreeItemViewModel item =
			Item(
				SongObjectKind.Pattern,
				missing: false);

		SongTreeDefaultActivation.Resolve(
				SongTreeSection.Samples,
				item)
			.Should().Be(SongTreeActivationKind.None);
	}

	private static SongTreeItemViewModel Item(
		SongObjectKind kind,
		bool missing)
	{
		SongDocument document = new();
		ObjectId id = document.AllocateObjectId();
		SongObject songObject =
			kind switch
			{
				SongObjectKind.Pattern =>
					new Heresy.Core.Patterns.DataPatternDefinition(
						id,
						"Pattern"),
				SongObjectKind.Sequence =>
					new Heresy.Core.Sequences.DataSequenceDefinition(
						id,
						"Sequence"),
				SongObjectKind.Instrument =>
					new Heresy.Core.Instruments.InstrumentDefinition(
						id,
						"Instrument"),
				SongObjectKind.Envelope =>
					new Heresy.Core.Envelopes.AdsrEnvelopeDefinition(
						id,
						"Envelope"),
				SongObjectKind.Sample =>
					new Heresy.Core.Samples.SampleDefinition(
						id,
						"Sample",
						new Heresy.Core.Assets.ExternalAssetReference(
							System.IO.Path.GetFullPath("sample.wav"))),
				SongObjectKind.FmSynth =>
					new Heresy.Core.FmSynthesis.FmSynthDefinition(
						id,
						"FM",
						new Heresy.Core.FmSynthesis.FmSynthGraph(
							[
								new Heresy.Core.FmSynthesis.FmConstantNode(
									0,
									0.0),
							],
							0)),
				_ => throw new System.ArgumentOutOfRangeException(
					nameof(kind)),
			};
		document.Add(songObject);
		SongTreeObject node =
			new(songObject.Name, id);
		if (missing)
			document.Remove(id);
		return SongTreeItemViewModel.Create(
			document,
			node);
	}
}

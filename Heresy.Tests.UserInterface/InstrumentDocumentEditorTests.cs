using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentDocumentEditorTests
{
	[Test]
	public void CreateInstrumentAddsCanonicalPlacementAndMarksAudio()
	{
		DocumentWorkspace workspace = new();
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(
				workspace,
				"Lead",
				divisions: 24.0,
				offset: 7);

		instrument.Name.Should().Be("Lead");
		instrument.Divisions.Should().Be(24.0);
		instrument.Offset.Should().Be(7);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		SongTreeObject node =
			workspace.Document.GetSectionRoot(SongTreeSection.Instruments)
				.Children.Cast<SongTreeObject>()
				.Single();
		node.ObjectId.Should().Be(instrument.Id);
	}

	[Test]
	public void UpdateLookupMarksAudioAndSameValueIsNoOp()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		InstrumentDocumentEditor.UpdateLookup(
			workspace,
			instrument,
			divisions: 19.0,
			offset: -3);

		instrument.Divisions.Should().Be(19.0);
		instrument.Offset.Should().Be(-3);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		InstrumentDocumentEditor.UpdateLookup(
			workspace,
			instrument,
			divisions: 19.0,
			offset: -3);

		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[TestCase(0.0)]
	[TestCase(-1.0)]
	[TestCase(double.NaN)]
	[TestCase(double.PositiveInfinity)]
	public void LookupRejectsInvalidDivisions(double divisions)
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");

		var action = () =>
			InstrumentDocumentEditor.UpdateLookup(
				workspace,
				instrument,
				divisions,
				offset: 0);

		action.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void AddToneSpecificationStoresSourcePitchAndEnvelopeReferences()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId sourceId = AddSample(workspace, "Osc");
		ObjectId volume = AddEnvelope(workspace, "Volume");
		ObjectId pitch = AddEnvelope(workspace, "Pitch");
		ObjectId panning = AddEnvelope(workspace, "Panning");
		ObjectId filter = AddEnvelope(workspace, "Filter");
		uint audioRevision = workspace.Document.AudioRevision;

		int index =
			InstrumentDocumentEditor.AddToneSpecification(
				workspace,
				instrument,
				sourceId,
				pitchMultiplier: 1.5,
				volumeEnvelopeId: volume,
				pitchEnvelopeId: pitch,
				panningEnvelopeId: panning,
				filterEnvelopeId: filter);

		index.Should().Be(0);
		instrument.ToneSpecifications.Should().ContainSingle();
		ToneSpecification tone = instrument.ToneSpecifications[0];
		tone.SourceId.Should().Be(sourceId);
		tone.PitchMultiplier.Should().Be(1.5);
		tone.VolumeEnvelopeId.Should().Be(volume);
		tone.PitchEnvelopeId.Should().Be(pitch);
		tone.PanningEnvelopeId.Should().Be(panning);
		tone.FilterEnvelopeId.Should().Be(filter);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void ToneSpecificationRejectsNonSoundSourceAndNonEnvelopeReference()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId envelopeId = AddEnvelope(workspace, "Envelope");
		ObjectId patternId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(new DataPatternDefinition(patternId, "Pattern"));

		var badSource = () =>
			InstrumentDocumentEditor.AddToneSpecification(
				workspace,
				instrument,
				envelopeId);

		badSource.Should().Throw<InvalidOperationException>();

		var badEnvelope = () =>
			InstrumentDocumentEditor.AddToneSpecification(
				workspace,
				instrument,
				patternId,
				volumeEnvelopeId: patternId);

		badEnvelope.Should().Throw<InvalidOperationException>();
		instrument.ToneSpecifications.Should().BeEmpty();
	}

	[Test]
	public void UpdateToneSpecificationReplacesImmutableSpecification()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId first = AddSample(workspace, "First");
		ObjectId second = AddSample(workspace, "Second");
		InstrumentDocumentEditor.AddToneSpecification(
			workspace,
			instrument,
			first);
		ToneSpecification previous = instrument.ToneSpecifications[0];
		uint audioRevision = workspace.Document.AudioRevision;

		InstrumentDocumentEditor.UpdateToneSpecification(
			workspace,
			instrument,
			0,
			second,
			pitchMultiplier: 0.5);

		instrument.ToneSpecifications[0].Should().NotBeSameAs(previous);
		instrument.ToneSpecifications[0].SourceId.Should().Be(second);
		instrument.ToneSpecifications[0].PitchMultiplier.Should().Be(0.5);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void ResizeToneTablePreservesMappingsAndFillsNewEntriesWithSilence()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId source = AddSample(workspace, "Osc");
		InstrumentDocumentEditor.AddToneSpecification(
			workspace,
			instrument,
			source);
		InstrumentDocumentEditor.ResizeToneTable(workspace, instrument, 2);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 0, 0);

		InstrumentDocumentEditor.ResizeToneTable(workspace, instrument, 5);

		instrument.ToneTable.Should().Equal(0, -1, -1, -1, -1);
	}

	[Test]
	public void SetToneMappingValidatesSpecificationIndexAndMarksAudio()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId source = AddSample(workspace, "Osc");
		InstrumentDocumentEditor.AddToneSpecification(
			workspace,
			instrument,
			source);
		InstrumentDocumentEditor.ResizeToneTable(workspace, instrument, 1);
		uint audioRevision = workspace.Document.AudioRevision;

		InstrumentDocumentEditor.SetToneMapping(
			workspace,
			instrument,
			toneIndex: 0,
			specificationIndex: 0);

		instrument.ToneTable[0].Should().Be(0);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		var action = () =>
			InstrumentDocumentEditor.SetToneMapping(
				workspace,
				instrument,
				toneIndex: 0,
				specificationIndex: 1);
		action.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Test]
	public void RemovingToneSpecificationSilencesItsMappingsAndShiftsLaterIndices()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId a = AddSample(workspace, "A");
		ObjectId b = AddSample(workspace, "B");
		ObjectId c = AddSample(workspace, "C");
		InstrumentDocumentEditor.AddToneSpecification(workspace, instrument, a);
		InstrumentDocumentEditor.AddToneSpecification(workspace, instrument, b);
		InstrumentDocumentEditor.AddToneSpecification(workspace, instrument, c);
		InstrumentDocumentEditor.ResizeToneTable(workspace, instrument, 4);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 0, 0);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 1, 1);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 2, 2);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 3, 2);

		InstrumentDocumentEditor.RemoveToneSpecification(
			workspace,
			instrument,
			specificationIndex: 1);

		instrument.ToneSpecifications.Select(tone => tone.SourceId)
			.Should().Equal(a, c);
		instrument.ToneTable.Should().Equal(0, -1, 1, 1);
	}

	private static ObjectId AddSample(
		DocumentWorkspace workspace,
		string name)
	{
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new SampleDefinition(
				id,
				name,
				new Heresy.Core.Assets.ExternalAssetReference(
					System.IO.Path.GetFullPath($"{name}.wav"))));
		return id;
	}

	private static ObjectId AddEnvelope(
		DocumentWorkspace workspace,
		string name)
	{
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new AdsrEnvelopeDefinition(id, name));
		return id;
	}
}

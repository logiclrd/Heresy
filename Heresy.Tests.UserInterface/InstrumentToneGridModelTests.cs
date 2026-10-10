using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentToneGridModelTests
{
	[Test]
	public void DefaultTwelveDivisionsUsesC11IndexAndDescendingVisualRows()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");

		InstrumentToneGridRow[] rows =
			new InstrumentToneGridModel(workspace, instrument).Rows.ToArray();
		rows.Should().HaveCount(86); // blank + 0..84
		rows[0].IsEntry.Should().BeTrue();
		rows[0].Index.Should().BeNull();
		rows[1].Index.Should().Be(84); // C11 is C4 + seven octaves
		rows[^1].Index.Should().Be(0);
		rows.Skip(1).Should().OnlyContain(row => !row.IsOutOfRange);
	}

	[Test]
	public void TenOctaveBoundaryIsExclusiveEvenWhenC11FallsBeyondIt()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(
				workspace, "Wide", divisions: 48, offset: 144);

		InstrumentToneGridProjection.RegularExclusiveEnd(48, 144)
			.Should().Be(480); // C11 at index 480, 10*48 excludes it
		var rows = new InstrumentToneGridModel(workspace, instrument).Rows;
		rows.Should().HaveCount(481);
		rows[1].Index.Should().Be(479);
		rows[^1].Index.Should().Be(0);
	}

	[Test]
	public void NonintegralDivisionsUseRendererRoundingWithoutChangingDivisions()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(
				workspace, "Microtonal", divisions: 2.5);

		// C11 is continuous index 17.5 -> AwayFromZero rounds to 18.
		// 10*2.5 allows indices 0..24; C11 caps this at 18.
		InstrumentToneGridProjection.RegularExclusiveEnd(2.5, 0)
			.Should().Be(19);
		instrument.Divisions.Should().Be(2.5);
		new InstrumentToneGridModel(workspace, instrument).Rows[1]
			.Index.Should().Be(18);
	}

	[Test]
	public void NegativeOffsetCanLeaveNoRegularRowsWithoutLosingExtraMappings()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(
				workspace, "Below C11", divisions: 12, offset: -100);
		ObjectId sample = AddSample(workspace);
		InstrumentDocumentEditor.AddToneSpecification(workspace, instrument, sample);
		InstrumentDocumentEditor.ResizeToneTable(workspace, instrument, 4);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 3, 0);

		var rows = new InstrumentToneGridModel(workspace, instrument).Rows;
		rows.Should().HaveCount(2);
		rows[0].IsEntry.Should().BeTrue();
		rows[1].Index.Should().Be(3);
		rows[1].IsOutOfRange.Should().BeTrue();
	}

	[Test]
	public void AssignedExtraRowsSortDescendingAndUseSpecifiedArgb()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId sample = AddSample(workspace);
		InstrumentDocumentEditor.AddToneSpecification(workspace, instrument, sample);
		InstrumentDocumentEditor.ResizeToneTable(workspace, instrument, 126);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 100, 0);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 125, 0);

		InstrumentToneGridRow[] rows =
			new InstrumentToneGridModel(workspace, instrument).Rows.ToArray();
		rows.Select(r => r.Index).Take(5)
			.Should().Equal(null, 125, 100, 84, 83);
		rows[1].BackgroundArgb.Should().Be(0x40FF0000U);
		rows[2].BackgroundArgb.Should().Be(0x40FF0000U);
		rows[3].BackgroundArgb.Should().BeNull();
		rows[1].Cells.SourceId.Should().Be(sample);
	}

	[Test]
	public void OutOfRangeBlankInsertionCanBeFilledLaterWithoutPrematureSave()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		var model = new InstrumentToneGridModel(workspace, instrument);
		uint audioRevision = workspace.Document.AudioRevision;

		model.SetEntryDraft(null, new(PitchMultiplier: 1.25));
		model.CommitEntry().Should().BeFalse();
		model.SetEntryDraft(-1, new(PitchMultiplier: 1.25));
		model.CommitEntry().Should().BeFalse();
		model.SetEntryDraft(105, new(PitchMultiplier: 1.25));
		model.CommitEntry().Should().BeTrue();

		model.EntryIndex.Should().BeNull();
		model.EntryCells.Should().Be(new InstrumentToneGridCells());
		model.Rows[1].Index.Should().Be(105);
		model.Rows[1].IsOutOfRange.Should().BeTrue();
		model.Rows[1].Cells.PitchMultiplier.Should().Be(1.25);
		instrument.ToneSpecifications.Should().BeEmpty();
		instrument.ToneTable.Should().BeEmpty();
		workspace.Document.AudioRevision.Should().Be(audioRevision);

		ObjectId sample = AddSample(workspace);
		model.SetRow(105, new(SourceId: sample, PitchMultiplier: 1.25));
		instrument.ToneTable[105].Should().Be(0);
		instrument.ToneSpecifications[0].PitchMultiplier.Should().Be(1.25);
	}

	[Test]
	public void EmptySourceFieldsRemainEditorOnlyAcrossRefreshAndLookupChanges()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId envelope = AddEnvelope(workspace);
		var model = new InstrumentToneGridModel(workspace, instrument);
		model.SetRow(2, new(PitchMultiplier: 0.4, FilterEnvelopeId: envelope));
		model.SetRow(120, new(PitchMultiplier: 1.8, VolumeEnvelopeId: envelope));
		uint revision = workspace.Document.AudioRevision;

		// Reading rows/refreshing does not discard the draft.
		model.Rows.Single(row => row.Index == 2)
			.Cells.PitchMultiplier.Should().Be(0.4);
		InstrumentDocumentEditor.UpdateLookup(
			workspace, instrument, 24, 9);
		revision++;
		model.OnDivisionsChanged();
		model.Rows.Single(row => row.Index == 120)
			.Cells.VolumeEnvelopeId.Should().Be(envelope);
		model.Rows.Single(row => row.Index == 2)
			.Cells.FilterEnvelopeId.Should().Be(envelope);
		workspace.Document.AudioRevision.Should().Be(revision);
		instrument.ToneSpecifications.Should().BeEmpty();
		new InstrumentToneGridModel(workspace, instrument).Rows
			.Single(row => row.Index == 120).Cells
			.Should().Be(new InstrumentToneGridCells());
	}

	[Test]
	public void SharedToneIsCopiedOnWriteAndEquivalentCellsAreInterned()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId a = AddSample(workspace, "A");
		ObjectId b = AddSample(workspace, "B");
		InstrumentDocumentEditor.AddToneSpecification(workspace, instrument, a);
		InstrumentDocumentEditor.ResizeToneTable(workspace, instrument, 4);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 1, 0);
		InstrumentDocumentEditor.SetToneMapping(workspace, instrument, 2, 0);
		var model = new InstrumentToneGridModel(workspace, instrument);
		uint initial = workspace.Document.AudioRevision;

		model.SetRow(1, new(SourceId: b, PitchMultiplier: 0.75));
		instrument.ToneSpecifications.Should().HaveCount(2);
		ToneSpecification originalTone =
			instrument.ToneSpecifications[instrument.ToneTable[2]];
		originalTone.SourceId.Should().Be(a);
		instrument.ToneSpecifications[instrument.ToneTable[1]]
			.SourceId.Should().Be(b);
		workspace.Document.AudioRevision.Should().Be(initial + 1);

		model.SetRow(3, new(SourceId: b, PitchMultiplier: 0.75));
		instrument.ToneSpecifications.Should().HaveCount(2);
		instrument.ToneTable[3].Should().Be(instrument.ToneTable[1]);
	}

	[Test]
	public void ClearingSourceRetainsDraftButRemovesMappingAndCompactsSpecs()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId a = AddSample(workspace, "A");
		ObjectId b = AddSample(workspace, "B");
		var model = new InstrumentToneGridModel(workspace, instrument);
		model.SetRow(1, new(SourceId: a, PitchMultiplier: 1.2));
		model.SetRow(2, new(SourceId: b, PitchMultiplier: 0.6));
		uint before = workspace.Document.AudioRevision;

		model.SetRow(1, new(PitchMultiplier: 1.2));
		workspace.Document.AudioRevision.Should().Be(before + 1);
		instrument.ToneTable[1].Should().Be(-1);
		instrument.ToneSpecifications.Should().ContainSingle()
			.Which.SourceId.Should().Be(b);
		instrument.ToneTable[2].Should().Be(0);
		model.Rows.Single(row => row.Index == 1)
			.Cells.PitchMultiplier.Should().Be(1.2);

		// Closing and reopening this editor drops the draft, but
		// leaves its silent -1 Core mapping intact.
		new InstrumentToneGridModel(workspace, instrument).Rows
			.Single(row => row.Index == 1)
			.Cells.PitchMultiplier.Should().Be(1.0);
	}

	[Test]
	public void DeleteRemovesMappingAndEditorDraftWithoutTouchingOtherNotes()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId sample = AddSample(workspace);
		var model = new InstrumentToneGridModel(workspace, instrument);
		model.SetRow(150, new(SourceId: sample));
		model.SetRow(3, new(SourceId: sample));
		model.DeleteRow(150);
		model.Rows.Skip(1).Select(row => row.Index)
			.Should().NotContain(150);
		instrument.ToneTable[3].Should().Be(0);
		model.DeleteRow(3);
		instrument.ToneSpecifications.Should().BeEmpty();
		model.DeleteRow(3); // no-op
	}

	[Test]
	public void EntryOverwriteReplacesOnlyNamedIndex()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId a = AddSample(workspace, "A");
		ObjectId b = AddSample(workspace, "B");
		var model = new InstrumentToneGridModel(workspace, instrument);
		model.SetRow(3, new(SourceId: a));
		model.SetRow(4, new(SourceId: a));
		model.SetEntryDraft(3, new(SourceId: b, PitchMultiplier: 2));
		model.CommitEntry().Should().BeTrue();

		model.Rows.Single(row => row.Index == 3)
			.Cells.SourceId.Should().Be(b);
		model.Rows.Single(row => row.Index == 4)
			.Cells.SourceId.Should().Be(a);
		instrument.ToneSpecifications.Should().HaveCount(2);
	}

	[Test]
	public void InvalidAssignedEditNeverChangesCoreOrDraft()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		var model = new InstrumentToneGridModel(workspace, instrument);
		model.SetRow(6, new(PitchMultiplier: 1.5));
		uint before = workspace.Document.AudioRevision;

		Action edit = () => model.SetRow(
			6, new(SourceId: (ObjectId)999U, PitchMultiplier: 0.5));
		edit.Should().Throw<InvalidOperationException>();
		workspace.Document.AudioRevision.Should().Be(before);
		model.Rows.Single(row => row.Index == 6)
			.Cells.PitchMultiplier.Should().Be(1.5);
		instrument.ToneTable.Should().BeEmpty();
	}

	[Test]
	public void EquivalentEffectiveMappingIsNoOp()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId sample = AddSample(workspace);
		var model = new InstrumentToneGridModel(workspace, instrument);
		model.SetRow(2, new(SourceId: sample, PitchMultiplier: 0.75));
		uint revision = workspace.Document.AudioRevision;
		model.SetRow(2, new(SourceId: sample, PitchMultiplier: 0.75));
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[Test]
	public void GridSizeLimitIsExplicitRatherThanSilentlyDiscardingRows()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(
				workspace, "Huge", divisions: 100_000);
		var model = new InstrumentToneGridModel(workspace, instrument);
		Action project = () => { _ = model.Rows; };
		project.Should().Throw<InvalidOperationException>()
			.WithMessage("*exceed*");
	}

	private static ObjectId AddSample(DocumentWorkspace workspace,
		string name = "A")
	{
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(new SampleDefinition(
			id, name, new Heresy.Core.Assets.ExternalAssetReference(
				System.IO.Path.GetFullPath(name + ".wav"))));
		return id;
	}

	private static ObjectId AddEnvelope(DocumentWorkspace workspace)
	{
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(new AdsrEnvelopeDefinition(id, "Envelope"));
		return id;
	}
}

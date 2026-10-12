using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Persistence;
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
    public void ToneNnaSurvivesUnrelatedGridChangesAndDraftPublication()
    {
        DocumentWorkspace workspace = new();
        InstrumentDefinition instrument =
            InstrumentDocumentEditor.CreateInstrument(workspace, "NNA tones");
        ObjectId sample = AddSample(workspace);
        InstrumentToneGridModel model = new(workspace, instrument);
        InstrumentNnaSetting policy = InstrumentNnaSetting.Fade(
            TimeSpan.FromMilliseconds(23));
        model.SetRow(0, new InstrumentToneGridCells(
            PitchMultiplier: 1.5, Nna: policy));
        instrument.ToneSpecifications.Should().BeEmpty();
        model.Rows.Single(row => row.Index == 0).Cells.Nna.Should().Be(policy);

        model.SetRow(0, model.Rows.Single(row => row.Index == 0).Cells
            with { SourceId = sample });
        instrument.ToneSpecifications.Should().ContainSingle();
        instrument.ToneSpecifications[0].Nna.Should().Be(policy);
        model.SetRow(0, model.Rows.Single(row => row.Index == 0).Cells
            with { PitchMultiplier = 1.25 });
        instrument.ToneSpecifications[0].Nna.Should().Be(policy);

        InstrumentDocumentEditor.UpdateToneSpecification(
            workspace, instrument, 0, sample, pitchMultiplier: 1.75);
        instrument.ToneSpecifications[0].Nna.Should().Be(policy);
    }

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

	[Test]
	public void AudioSnapshotRemainsIsolatedAfterEditingMappedRow()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId first = AddSample(workspace, "First");
		ObjectId second = AddSample(workspace, "Second");
		var model = new InstrumentToneGridModel(workspace, instrument);
		model.SetRow(5, new(SourceId: first, PitchMultiplier: 1.0));
		SongDocumentSnapshot frozen = SongDocumentSnapshot.Create(
			workspace.Document);
		uint frozenAudioRevision = frozen.AudioRevision;

		model.SetRow(5, new(SourceId: second, PitchMultiplier: 1.75));
		InstrumentDefinition snapshotInstrument =
			(InstrumentDefinition)frozen.Document.Objects[instrument.Id];
		ToneSpecification oldTone = snapshotInstrument.ToneSpecifications[
			snapshotInstrument.ToneTable[5]];
		oldTone.SourceId.Should().Be(first);
		oldTone.PitchMultiplier.Should().Be(1.0);
		instrument.ToneSpecifications[instrument.ToneTable[5]]
			.SourceId.Should().Be(second);
		workspace.Document.AudioRevision.Should().Be(frozenAudioRevision + 1);
	}

	[Test]
	public void RejectedHugeRowAssignmentDoesNotLoseItsPreviousEditorDraft()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId sample = AddSample(workspace);
		var model = new InstrumentToneGridModel(workspace, instrument);
		const int oversizedIndex = 100_001;
		model.SetRow(oversizedIndex, new(PitchMultiplier: 0.6));
		uint revision = workspace.Document.AudioRevision;
		Action assignment = () => model.SetRow(oversizedIndex,
			new(SourceId: sample, PitchMultiplier: 1.75));
		assignment.Should().Throw<InvalidOperationException>();
		workspace.Document.AudioRevision.Should().Be(revision);
		instrument.ToneTable.Should().BeEmpty();
		model.Rows.Single(row => row.Index == oversizedIndex)
			.Cells.PitchMultiplier.Should().Be(0.6);
	}

	[Test]
	public void InvalidPitchAndEnvelopeRejectAssignedEditButAllowUnassignedDraft()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Lead");
		ObjectId sample = AddSample(workspace);
		var model = new InstrumentToneGridModel(workspace, instrument);
		uint revision = workspace.Document.AudioRevision;
		model.SetRow(2, new(PitchMultiplier: -1.0));
		model.Rows.Single(row => row.Index == 2)
			.Cells.PitchMultiplier.Should().Be(-1.0);
		Action invalidPitch = () => model.SetRow(2,
			new(SourceId: sample, PitchMultiplier: -1.0));
		invalidPitch.Should().Throw<ArgumentOutOfRangeException>();
		Action invalidEnvelope = () => model.SetRow(2,
			new(SourceId: sample, VolumeEnvelopeId: (ObjectId)1234U));
		invalidEnvelope.Should().Throw<InvalidOperationException>();
		instrument.ToneTable.Should().BeEmpty();
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[Test]
	public void FilterOverridesParticipateInToneMappingCopyOnWriteAndInterning()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Filtered");
		ObjectId source = AddSample(workspace);
		ObjectId filterId = workspace.Document.AllocateObjectId();
		FilterDefinition definition = new(filterId, "One Pole");
		workspace.Document.Add(definition);
		FilterInstance selected = new(definition);
		selected.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(2300));
		InstrumentToneGridModel model = new(workspace, instrument);
		model.SetRow(1, new(SourceId: source, FilterConfiguration: selected));
		model.SetRow(2, new(SourceId: source, FilterConfiguration: selected.Clone()));
		instrument.ToneSpecifications.Should().ContainSingle();
		instrument.ToneTable[1].Should().Be(instrument.ToneTable[2]);
		uint revision = workspace.Document.AudioRevision;

		// Equivalent independent instances do not spuriously dirty the song.
		model.SetRow(1, new(SourceId: source,
			FilterConfiguration: selected.Clone()));
		workspace.Document.AudioRevision.Should().Be(revision);

		FilterInstance changed = selected.Clone();
		changed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(4400));
		model.SetRow(1, new(SourceId: source, FilterConfiguration: changed));
		instrument.ToneSpecifications.Should().HaveCount(2);
		instrument.ToneSpecifications[instrument.ToneTable[1]]
			.FilterConfiguration!.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue.Should().Be(4400);
		instrument.ToneSpecifications[instrument.ToneTable[2]]
			.FilterConfiguration!.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue.Should().Be(2300);
		workspace.Document.AudioRevision.Should().Be(revision + 1);

		// The newly edited specification is not just a reference to caller
		// state: after publish, caller changes cannot mutate the document.
		changed.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(7000));
		instrument.ToneSpecifications[instrument.ToneTable[1]]
			.FilterConfiguration!.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue.Should().Be(4400);

		FilterInstance foreign = new(new FilterDefinition(filterId, "Foreign"));
		Action invalid = () => model.SetRow(1, new(
			SourceId: source, FilterConfiguration: foreign));
		invalid.Should().Throw<InvalidOperationException>();
		workspace.Document.AudioRevision.Should().Be(revision + 1);
	}

	[Test]
	public void ToneFilterPersistsForBothInstrumentKindsAndPlaybackSnapshotIsIndependent()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument =
			InstrumentDocumentEditor.CreateInstrument(workspace, "Mapped");
		ScriptInstrumentDefinition scripted =
			ScriptInstrumentDocumentEditor.Create(workspace, "Scripted", "0");
		// Use a valid nested sound source without requiring an external
		// sample asset to exist during the JSON/snapshot round trip.
		ObjectId sample = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Nested").Id;
		ObjectId filterId = workspace.Document.AllocateObjectId();
		FilterDefinition definition = new(filterId, "Tone Filter");
		workspace.Document.Add(definition);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(4100));
		new InstrumentToneGridModel(workspace, instrument).SetRow(5,
			new(SourceId: sample, FilterConfiguration: use));
		ScriptInstrumentDocumentEditor.AddTone(workspace, scripted, sample,
			filterConfiguration: use);
		string json = SongDocumentJson.Serialize(workspace.Document,
			System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tone-filters.hm.json"),
			JsonAssetPathMode.Absolute);
		SongDocument restored = SongDocumentJson.Deserialize(json,
			System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tone-filters.hm.json"));
		InstrumentDefinition mappedCopy =
			(InstrumentDefinition)restored.Objects[instrument.Id];
		ScriptInstrumentDefinition scriptCopy =
			(ScriptInstrumentDefinition)restored.Objects[scripted.Id];
		FilterInstance first = mappedCopy.ToneSpecifications[0].FilterConfiguration!;
		FilterInstance second = scriptCopy.ToneSpecifications[0].FilterConfiguration!;
		first.Definition.Should().BeSameAs(restored.Objects[filterId]);
		second.Definition.Should().BeSameAs(restored.Objects[filterId]);
		first.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue.Should().Be(4100);
		second.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue.Should().Be(4100);
		first.Should().NotBeSameAs(second);

		SongDocumentSnapshot snapshot = SongDocumentSnapshot.Create(workspace.Document);
		uint oldRevision = snapshot.AudioRevision;
		FilterInstance edited = use.Clone();
		edited.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(4700));
		new InstrumentToneGridModel(workspace, instrument).SetRow(5,
			new(SourceId: sample, FilterConfiguration: edited));
		InstrumentDefinition frozen =
			(InstrumentDefinition)snapshot.Document.Objects[instrument.Id];
		frozen.ToneSpecifications[0].FilterConfiguration!.GetStageParameters(1)
			.GetParameter("CutoffHz").NumericValue.Should().Be(4100);
		workspace.Document.AudioRevision.Should().Be(oldRevision + 1);
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
		workspace.Document.Add(new NodeEnvelopeDefinition(id, "Envelope"));
		return id;
	}
}

using System;
using System.Globalization;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class InstrumentToneGridBatchTests
{
	[Test]
	public void SelectedSourceAssignmentPublishesAllRowsOnceAndLeavesEntryDraftUntouched()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Drafts");
		ObjectId source = AddSource(workspace);
		InstrumentToneGridModel model = new(workspace, instrument);
		model.SetEntryDraft(47, new InstrumentToneGridCells(PitchMultiplier: 2.5));
		model.SetRow(2, new InstrumentToneGridCells(PitchMultiplier: 1.25));
		model.SetRow(3, new InstrumentToneGridCells(PitchMultiplier: 1.75));
		uint before = workspace.Document.AudioRevision;
		uint docBefore = workspace.Document.DocumentRevision;

		model.EditRows([2, 3], (_, old) => old with { SourceId = source })
			.Should().BeTrue();
		workspace.Document.AudioRevision.Should().Be(before + 1);
		workspace.Document.DocumentRevision.Should().Be(docBefore + 1);
		model.Rows.Single(row => row.Index == 2).Cells.PitchMultiplier.Should().Be(1.25);
		model.Rows.Single(row => row.Index == 3).Cells.PitchMultiplier.Should().Be(1.75);
		instrument.ToneSpecifications.Should().HaveCount(2);
		model.EntryIndex.Should().Be(47);
		model.EntryCells.PitchMultiplier.Should().Be(2.5);
	}

	[Test]
	public void BatchIsOneRevisionDespiteSharedSpecificationsAndUnselectedRows()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Shared");
		ObjectId source = AddSource(workspace);
		InstrumentToneGridModel model = new(workspace, instrument);
		foreach (int i in new[] { 1, 2, 3 })
			model.SetRow(i, new(SourceId: source));
		instrument.ToneSpecifications.Should().ContainSingle();
		uint old = workspace.Document.AudioRevision;
		model.EditRows([1, 2], (_, c) => c with
		{
			PitchMultiplier = Math.Pow(2, 5.0 / 12),
		}).Should().BeTrue();

		workspace.Document.AudioRevision.Should().Be(old + 1);
		model.Rows.Single(r => r.Index == 1).Cells.PitchMultiplier
			.Should().BeApproximately(Math.Pow(2, 5.0 / 12), 1e-12);
		model.Rows.Single(r => r.Index == 2).Cells.PitchMultiplier
			.Should().BeApproximately(Math.Pow(2, 5.0 / 12), 1e-12);
		model.Rows.Single(r => r.Index == 3).Cells.PitchMultiplier.Should().Be(1);
		instrument.ToneSpecifications.Should().HaveCount(2);
		model.EditRows([1, 2], (_, c) => c with
		{
			PitchMultiplier = Math.Pow(2, 5.0 / 12),
		}).Should().BeFalse();
		workspace.Document.AudioRevision.Should().Be(old + 1);
	}

	[Test]
	public void InvalidMiddleRowRollsBackAllCoreStateAndUnpublishedDrafts()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Atomic");
		ObjectId source = AddSource(workspace);
		InstrumentToneGridModel model = new(workspace, instrument);
		model.SetRow(1, new(PitchMultiplier: 1.5));
		model.SetRow(2, new(SourceId: source, PitchMultiplier: 0.5));
		model.SetRow(3, new(PitchMultiplier: -1)); // editable invalid draft
		uint before = workspace.Document.AudioRevision;
		uint docBefore = workspace.Document.DocumentRevision;
		int[] previous = instrument.ToneTable.ToArray();
		ToneSpecification[] specs = instrument.ToneSpecifications.ToArray();

		Action rejected = () => model.EditRows([1, 2, 3],
			(_, c) => c with { SourceId = source });
		rejected.Should().Throw<ArgumentOutOfRangeException>();
		workspace.Document.AudioRevision.Should().Be(before);
		workspace.Document.DocumentRevision.Should().Be(docBefore);
		instrument.ToneTable.Should().Equal(previous);
		instrument.ToneSpecifications.Should().Equal(specs);
		model.Rows.Single(row => row.Index == 1).Cells.SourceId.Should().BeNull();
		model.Rows.Single(row => row.Index == 1).Cells.PitchMultiplier.Should().Be(1.5);
		model.Rows.Single(row => row.Index == 2).Cells.PitchMultiplier.Should().Be(0.5);
		model.Rows.Single(row => row.Index == 3).Cells.PitchMultiplier.Should().Be(-1);
	}

	[Test]
	public void BadSourceOrEnvelopeInAnyTargetRollsBackPreviouslyVisitedTargets()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Invalid references");
		ObjectId sound = AddSource(workspace);
		InstrumentToneGridModel model = new(workspace, instrument);
		model.SetRow(0, new(SourceId: sound));
		model.SetRow(1, new(PitchMultiplier: 1.5));
		uint before = workspace.Document.AudioRevision;
		Action invalidSource = () => model.EditRows([0, 1],
			(index, c) => c with { SourceId =
				index == 0 ? sound : (ObjectId)987654U });
		invalidSource.Should().Throw<InvalidOperationException>();
		model.Rows.Single(r => r.Index == 0).Cells.SourceId.Should().Be(sound);
		model.Rows.Single(r => r.Index == 1).Cells.SourceId.Should().BeNull();
		workspace.Document.AudioRevision.Should().Be(before);

		Action invalidEnvelope = () => model.EditRows([0, 1],
			(index, c) => c with { VolumeEnvelopeId =
				index == 1 ? (ObjectId)987654U : null });
		invalidEnvelope.Should().Throw<InvalidOperationException>();
		workspace.Document.AudioRevision.Should().Be(before);
	}

	[Test]
	public void ClearingSelectedSourceDoesNotDeleteRowsOrLoseTheirOtherDraftCells()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Clear source");
		ObjectId sound = AddSource(workspace);
		InstrumentToneGridModel model = new(workspace, instrument);
		model.SetRow(0, new(SourceId: sound, PitchMultiplier: 1.25));
		model.SetRow(1, new(SourceId: sound, PitchMultiplier: 1.5));
		uint before = workspace.Document.AudioRevision;
		model.EditRows([0, 1], (_, c) => c with { SourceId = null });
		workspace.Document.AudioRevision.Should().Be(before + 1);
		instrument.ToneSpecifications.Should().BeEmpty();
		model.Rows.Single(r => r.Index == 0).Cells.PitchMultiplier.Should().Be(1.25);
		model.Rows.Single(r => r.Index == 1).Cells.PitchMultiplier.Should().Be(1.5);
		model.Rows.Single(r => r.Index == 0).Cells.SourceId.Should().BeNull();
		model.Rows.Single(r => r.Index == 1).Cells.SourceId.Should().BeNull();
	}

	[Test]
	public void DraftOnlyBatchDoesNotChangeDocumentRevisionUntilPublication()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Unpublished");
		ObjectId source = AddSource(workspace);
		InstrumentToneGridModel model = new(workspace, instrument);
		uint revision = workspace.Document.DocumentRevision;
		model.EditRows([0, 1, 2], (_, c) => c with
		{
			PitchMultiplier = Math.Pow(2, 0.25),
		}).Should().BeTrue();
		workspace.Document.DocumentRevision.Should().Be(revision);
		instrument.ToneSpecifications.Should().BeEmpty();
		model.Rows.Single(r => r.Index == 2).Cells.PitchMultiplier
			.Should().BeApproximately(Math.Pow(2, 0.25), 1e-12);
		model.EditRows([0, 1, 2], (_, c) => c with { SourceId = source });
		workspace.Document.DocumentRevision.Should().Be(revision + 1);
		instrument.ToneSpecifications.Should().ContainSingle();
	}

	[Test]
	public void IndependentFilterCopiesDoNotAliasCallerAndAreCopyOnWritePerTone()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Filter batch");
		ObjectId source = AddSource(workspace);
		ObjectId id = workspace.Document.AllocateObjectId();
		FilterDefinition definition = new(id, "Filter");
		workspace.Document.Add(definition);
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(2300));
		InstrumentToneGridModel model = new(workspace, instrument);
		model.SetRow(0, new(SourceId: source));
		model.SetRow(1, new(SourceId: source));
		uint before = workspace.Document.AudioRevision;
		model.EditRows([0, 1], (_, c) => c with { FilterConfiguration = use });
		workspace.Document.AudioRevision.Should().Be(before + 1);
		use.SetStageParameter(1, "CutoffHz", FilterParameterValue.FromNumber(4200));
		FilterInstance firstProjection = model.Rows.Single(r => r.Index == 0)
			.Cells.FilterConfiguration!;
		FilterInstance secondProjection = model.Rows.Single(r => r.Index == 1)
			.Cells.FilterConfiguration!;
		firstProjection.Should().NotBeSameAs(secondProjection);
		firstProjection.Should().NotBeSameAs(
			instrument.ToneSpecifications[instrument.ToneTable[0]].FilterConfiguration);
		firstProjection.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(9700));
		model.Rows.Single(r => r.Index == 0).Cells.FilterConfiguration!
			.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue.Should().Be(2300);
		foreach (int i in new[] { 0, 1 })
			model.Rows.Single(r => r.Index == i).Cells
				.FilterConfiguration!.GetStageParameters(1)
				.GetParameter("CutoffHz").NumericValue.Should().Be(2300);

		// The input passed to the transform must itself be isolated,
		// even if a future Filter editor mutates that input in-place.
		before = workspace.Document.AudioRevision;
		Action failed = () => model.EditRows([0, 1], (i, c) =>
		{
			c.FilterConfiguration!.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(6500));
			if (i == 1)
				throw new InvalidOperationException("Reject second row");
			return c;
		});
		failed.Should().Throw<InvalidOperationException>();
		workspace.Document.AudioRevision.Should().Be(before);
		foreach (int i in new[] { 0, 1 })
			model.Rows.Single(r => r.Index == i).Cells
				.FilterConfiguration!.GetStageParameters(1)
				.GetParameter("CutoffHz").NumericValue.Should().Be(2300);

		model.EditRows([0], (_, c) =>
		{
			c.FilterConfiguration!.SetStageParameter(1, "CutoffHz",
				FilterParameterValue.FromNumber(3400));
			return c;
		});
		model.Rows.Single(r => r.Index == 0).Cells.FilterConfiguration!
			.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue.Should().Be(3400);
		model.Rows.Single(r => r.Index == 1).Cells.FilterConfiguration!
			.GetStageParameters(1).GetParameter("CutoffHz")
			.NumericValue.Should().Be(2300);
	}

	[Test]
	public void InvalidBatchSizeAndInvalidNnaCannotLeakPartialChanges()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Reject");
		ObjectId source = AddSource(workspace);
		InstrumentToneGridModel model = new(workspace, instrument);
		model.SetRow(0, new(SourceId: source));
		uint revision = workspace.Document.AudioRevision;
		Action tooLarge = () => model.EditRows([0, 100_001],
			(_, c) => c with { SourceId = source });
		tooLarge.Should().Throw<InvalidOperationException>();
		workspace.Document.AudioRevision.Should().Be(revision);
		model.Rows.Single(row => row.Index == 0).Cells.PitchMultiplier.Should().Be(1);

		Action invalidNna = () => model.EditRows([0, 1],
			(_, c) => c with { Nna = new InstrumentNnaSetting(
				(InstrumentNnaAction)999, null) });
		invalidNna.Should().Throw<ArgumentOutOfRangeException>();
		workspace.Document.AudioRevision.Should().Be(revision);
	}

	[Test]
	public void OnlyEditedColumnInsideSelectedRectangleTriggersItsRows()
	{
		DocumentWorkspace workspace = new();
		InstrumentDefinition instrument = InstrumentDocumentEditor.CreateInstrument(
			workspace, "Selection");
		InstrumentToneGridRow[] rows =
			new InstrumentToneGridModel(workspace, instrument).Rows.ToArray();
		InstrumentToneGridSelection selection = new();
		selection.BeginAt(new(3, 2), rows);
		selection.Extend(new(0, 6), rows);
		selection.EditTargetRows(new(1, 2), rows).Should().Equal(3, 2, 1, 0);
		selection.EditTargetRows(new(1, 6), rows).Should().Equal(3, 2, 1, 0);
		selection.EditTargetRows(new(1, 7), rows).Should().Equal(1);
		selection.EditTargetRows(new(1, 0), rows).Should().Equal(1);
		selection.EditTargetRows(new(0, 2), rows).Should().Equal(3, 2, 1, 0);
		selection.BeginAt(new(null, 2), rows);
		selection.Extend(new(0, 6), rows);
		selection.EditTargetRows(new(0, 2), rows).Should().Equal(84, 83, 82,
			81, 80, 79, 78, 77, 76, 75, 74, 73, 72, 71, 70, 69, 68, 67,
			66, 65, 64, 63, 62, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52,
			51, 50, 49, 48, 47, 46, 45, 44, 43, 42, 41, 40, 39, 38, 37,
			36, 35, 34, 33, 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22,
			21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5,
			4, 3, 2, 1, 0);
	}

	[Test]
	public void DivisionsPitchFormatterSuppressesRoundoffWithoutQuantizingStorage()
	{
		double multiplier = Math.Pow(2.0, 5.0 / 48.0);
		double display = InstrumentToneNoteNotation.DisplayPitchOffset(multiplier, 48);
		display.Should().BeApproximately(5, 1e-12);
		InstrumentToneNoteNotation.FormatDisplayPitch(
			multiplier, 48, CultureInfo.InvariantCulture)
			.Should().Be("5");
		double precise = Math.Pow(2.0, 1.234567 / 48);
		string label = InstrumentToneNoteNotation.FormatDisplayPitch(
			precise, 48, CultureInfo.InvariantCulture);
		double.Parse(label, CultureInfo.InvariantCulture)
			.Should().BeApproximately(1.234567, 1e-12);
		InstrumentToneNoteNotation.MultiplierFromDisplayPitch(5, 48)
			.Should().Be(multiplier);
		double saved = multiplier;
		_ = InstrumentToneNoteNotation.FormatDisplayPitch(saved, 48);
		saved.Should().Be(multiplier);
	}

	private static ObjectId AddSource(DocumentWorkspace workspace)
	{
		ObjectId id = workspace.Document.AllocateObjectId();
		workspace.Document.Add(new DataPatternDefinition(id, "Source"));
		return id;
	}
}

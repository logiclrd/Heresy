using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternDocumentEditorTests
{
	[Test]
	public void CreatePatternAddsCanonicalPatternPlacementAndMarksAudio()
	{
		DocumentWorkspace workspace = new();
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Verse",
				rowCount: 32,
				channelCount: 4);

		pattern.Name.Should().Be("Verse");
		pattern.RowCount.Should().Be(32);
		pattern.ChannelCount.Should().Be(4);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		SongTreeObject node =
			workspace.Document.GetSectionRoot(SongTreeSection.Patterns)
				.Children.Cast<SongTreeObject>()
				.Single();
		node.ObjectId.Should().Be(pattern.Id);
	}

	[Test]
	public void SetNoteCreatesSparseCellAndMarksOneAudioRevision()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		ObjectId sourceId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new SampleDefinition(
				sourceId,
				"Kick",
				new Heresy.Core.Assets.ExternalAssetReference(
					System.IO.Path.GetFullPath("kick.wav"))));
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;
		StartPatternNote note =
			new(
				sourceId,
				pitchMultiplier: 1.5,
				playbackSpeedMultiplier: 0.75,
				mixdown: true);

		PatternDocumentEditor.SetNote(workspace, pattern, 7, 2, note);

		pattern.Grid[7, 2]!.Note.Should().Be(note);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void SettingSameNoteIsNoOp()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternNoteOff note = new();
		PatternDocumentEditor.SetNote(workspace, pattern, 1, 0, note);
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		PatternDocumentEditor.SetNote(workspace, pattern, 1, 0, note);

		workspace.Document.DocumentRevision.Should().Be(documentRevision);
		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}

	[Test]
	public void ClearingLastValueRemovesSparseCell()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternDocumentEditor.SetNote(
			workspace,
			pattern,
			3,
			1,
			new PatternNoteCut());

		PatternDocumentEditor.SetNote(workspace, pattern, 3, 1, null);

		pattern.Grid[3, 1].Should().BeNull();
	}

	[Test]
	public void ClearingNotePreservesEffectsAndAllocatedCell()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(3, 1);
		cell.Note = new PatternNoteCut();
		cell.Effects.Add(new SetNoteVolumePatternEffect(0.5));

		PatternDocumentEditor.SetNote(workspace, pattern, 3, 1, null);

		pattern.Grid[3, 1].Should().BeSameAs(cell);
		cell.Note.Should().BeNull();
		cell.Effects.Should().ContainSingle();
	}

	[Test]
	public void LayoutChangeMarksOneAudioRevision()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		uint documentRevision = workspace.Document.DocumentRevision;
		uint audioRevision = workspace.Document.AudioRevision;

		PatternDocumentEditor.UpdateLayout(
			workspace,
			pattern,
			rowCount: 96,
			channelCount: 12,
			minorHighlightRows: 3,
			majorHighlightRows: 12);

		pattern.RowCount.Should().Be(96);
		pattern.ChannelCount.Should().Be(12);
		pattern.MinorHighlightRows.Should().Be(3);
		pattern.MajorHighlightRows.Should().Be(12);
		workspace.Document.DocumentRevision.Should().Be(documentRevision + 1);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void ShrinkReportsWhetherPopulatedCellsWouldBeDiscarded()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		pattern.Grid.GetOrCreateCell(20, 6).Note = new PatternNoteOff();

		PatternDocumentEditor.WouldDiscardCells(
			pattern,
			rowCount: 16,
			channelCount: 8).Should().BeTrue();
		PatternDocumentEditor.WouldDiscardCells(
			pattern,
			rowCount: 64,
			channelCount: 6).Should().BeTrue();
		PatternDocumentEditor.WouldDiscardCells(
			pattern,
			rowCount: 64,
			channelCount: 8).Should().BeFalse();
	}

	[Test]
	public void CellProjectionResolvesLiveSourceNameAndShowsEffectCount()
	{
		DocumentWorkspace workspace = new();
		ObjectId sourceId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new SampleDefinition(
				sourceId,
				"Snare",
				new Heresy.Core.Assets.ExternalAssetReference(
					System.IO.Path.GetFullPath("snare.wav"))));
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = sourceId;
		cell.Note = new StartPatternNote();
		cell.Effects.Add(new SetNoteVolumePatternEffect(0.5));
		cell.Effects.Add(new PatternNoteDelayPatternEffectForTest());

		PatternCellViewModel view =
			PatternCellViewModel.Create(workspace.Document, pattern, 0, 0);

		view.NoteText.Should().Be("C-4");
		view.SourceText.Should().Contain("Snare");
		view.SourceText.Should().Contain($"<{sourceId.Value}>");
		view.EffectCount.Should().Be(2);
		view.DisplayText.Should().Contain("+2 fx");
	}

	private sealed record PatternNoteDelayPatternEffectForTest : PatternEffect;
	[Test]
	public void SetSourceCreatesSparseCellAndClearingLastValueRemovesIt()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		ObjectId sourceId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new SampleDefinition(
				sourceId,
				"Kick",
				new Heresy.Core.Assets.ExternalAssetReference(
					System.IO.Path.GetFullPath("kick.wav"))));
		uint audioRevision = workspace.Document.AudioRevision;

		PatternDocumentEditor.SetSource(
			workspace,
			pattern,
			3,
			1,
			sourceId);

		pattern.Grid[3, 1]!.SourceId.Should().Be(sourceId);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);

		PatternDocumentEditor.SetSource(
			workspace,
			pattern,
			3,
			1,
			ObjectId.None);

		pattern.Grid[3, 1].Should().BeNull();
	}

	[Test]
	public void SetSourceRejectsNonSoundObject()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		ObjectId envelopeId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new Heresy.Core.Envelopes.AdsrEnvelopeDefinition(
				envelopeId,
				"Envelope"));

		var action = () =>
			PatternDocumentEditor.SetSource(
				workspace,
				pattern,
				0,
				0,
				envelopeId);

		action.Should().Throw<InvalidOperationException>();
		pattern.Grid[0, 0].Should().BeNull();
	}

}

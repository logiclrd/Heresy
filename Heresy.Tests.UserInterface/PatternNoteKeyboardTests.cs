using System;

using AwesomeAssertions;

using Avalonia.Input;

using Heresy.Core.Assets;
using Heresy.Core.Envelopes;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternNoteKeyboardTests
{
	[TestCase('Z', 0)]
	[TestCase('S', 1)]
	[TestCase('X', 2)]
	[TestCase('D', 3)]
	[TestCase('C', 4)]
	[TestCase('V', 5)]
	[TestCase('G', 6)]
	[TestCase('B', 7)]
	[TestCase('H', 8)]
	[TestCase('N', 9)]
	[TestCase('J', 10)]
	[TestCase('M', 11)]
	[TestCase('Q', 12)]
	[TestCase('2', 13)]
	[TestCase('W', 14)]
	[TestCase('3', 15)]
	[TestCase('E', 16)]
	[TestCase('R', 17)]
	[TestCase('5', 18)]
	[TestCase('T', 19)]
	[TestCase('6', 20)]
	[TestCase('Y', 21)]
	[TestCase('7', 22)]
	[TestCase('U', 23)]
	[TestCase('I', 24)]
	[TestCase('9', 25)]
	[TestCase('O', 26)]
	[TestCase('0', 27)]
	[TestCase('P', 28)]
	public void TrackerKeyboardMapReturnsChromaticSemitoneOffset(
		char key,
		int expectedSemitone)
	{
		PatternNoteKeyboard.TryGetSemitoneOffset(
			key,
			out int actual).Should().BeTrue();
		actual.Should().Be(expectedSemitone);
	}

	[Test]
	public void TypingNoteDoesNotImplicitlyCopyToolbarSourceAndAdvancesOneRow()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Piano");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(sample.Id, baseOctave: 4);
		uint audioRevision = workspace.Document.AudioRevision;

		PatternNoteInputResult result =
			PatternNoteKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				state,
				'Z');

		result.Changed.Should().BeTrue();
		result.Rejected.Should().BeFalse();
		pattern.Grid[0, 0]!.Note.Should().Be(
			new StartPatternNote());
		pattern.Grid[0, 0]!.SourceId.Should().Be(ObjectId.None);
		cursor.Row.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.Note);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void UpperRowsUseSelectedBaseOctave()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Piano");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(sample.Id, baseOctave: 3);

		PatternNoteKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			state,
			'Q');
		PatternNoteKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			state,
			'I');

		StartPatternNote c4 =
			(StartPatternNote)pattern.Grid[0, 0]!.Note!;
		StartPatternNote c5 =
			(StartPatternNote)pattern.Grid[1, 0]!.Note!;
		c4.PitchMultiplier.Should().BeApproximately(1.0, 1e-12);
		c5.PitchMultiplier.Should().BeApproximately(2.0, 1e-12);
	}

	[Test]
	public void ReplacingStartNotePreservesSpeedMixdownAndInlineSource()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition oldSource = AddSample(workspace, "Old");
		SampleDefinition newToolbarSource = AddSample(workspace, "New");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		pattern.Grid.GetOrCreateCell(0, 0).Note =
			new StartPatternNote(
				oldSource.Id,
				pitchMultiplier: 1.0,
				playbackSpeedMultiplier: 0.75,
				mixdown: true);
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(newToolbarSource.Id, baseOctave: 4);

		PatternNoteKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			state,
			'X');

		StartPatternNote note =
			(StartPatternNote)pattern.Grid[0, 0]!.Note!;
		note.SourceId.Should().Be(oldSource.Id);
		note.PitchMultiplier.Should().BeApproximately(
			Math.Pow(2.0, 2.0 / 12.0),
			1e-12);
		note.PlaybackSpeedMultiplier.Should().Be(0.75);
		note.Mixdown.Should().BeTrue();
	}

	[Test]
	public void OneEntersCutAndBacktickEntersOff()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(ObjectId.None, baseOctave: 4);

		PatternNoteKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			state,
			'1');
		PatternNoteKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			state,
			'`');

		pattern.Grid[0, 0]!.Note.Should().Be(new PatternNoteCut());
		pattern.Grid[1, 0]!.Note.Should().Be(new PatternNoteOff());
		cursor.Row.Should().Be(2);
	}

	[Test]
	public void NoteTypingWithoutSelectedSourceCreatesSourceLessNoteAndAdvances()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(ObjectId.None, baseOctave: 4);

		PatternNoteInputResult result =
			PatternNoteKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				state,
				'Z');

		result.Changed.Should().BeTrue();
		result.Rejected.Should().BeFalse();
		pattern.Grid[0, 0]!.Note.Should().Be(new StartPatternNote());
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void DeletedToolbarSourceDoesNotBlockNoteEntry()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Piano");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternNoteInputState state =
			new(sample.Id, baseOctave: 4);
		workspace.Document.Remove(sample.Id);
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);

		PatternNoteInputResult result =
			PatternNoteKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				state,
				'Z');

		result.Rejected.Should().BeFalse();
		pattern.Grid[0, 0]!.Note.Should().Be(new StartPatternNote());
	}

	[Test]
	public void RepeatedNoteEntryPaintsDownRows()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Piano");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(sample.Id, baseOctave: 4);

		foreach (char key in "ZXCV")
			PatternNoteKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				state,
				key);

		((StartPatternNote)pattern.Grid[0, 0]!.Note!)
			.PitchMultiplier.Should().BeApproximately(1.0, 1e-12);
		((StartPatternNote)pattern.Grid[1, 0]!.Note!)
			.PitchMultiplier.Should().BeApproximately(
				Math.Pow(2.0, 2.0 / 12.0),
				1e-12);
		((StartPatternNote)pattern.Grid[2, 0]!.Note!)
			.PitchMultiplier.Should().BeApproximately(
				Math.Pow(2.0, 4.0 / 12.0),
				1e-12);
		((StartPatternNote)pattern.Grid[3, 0]!.Note!)
			.PitchMultiplier.Should().BeApproximately(
				Math.Pow(2.0, 5.0 / 12.0),
				1e-12);
		cursor.Row.Should().Be(4);
	}

	[Test]
	public void CellProjectionUsesTrackerNoteNameForSemitonePitch()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Snare");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell sharpCell = pattern.Grid.GetOrCreateCell(0, 0);
		sharpCell.SourceId = sample.Id;
		sharpCell.Note =
			new StartPatternNote(
				Math.Pow(2.0, 1.0 / 12.0));

		PatternCellViewModel view =
			PatternCellViewModel.Create(
				workspace.Document,
				pattern,
				0,
				0);

		view.NoteText.Should().Be("C#4");
		view.SourceText.Should().Contain("Snare");
		view.SourceText.Should().Contain($"<{sample.Id.Value}>");
	}

	[Test]
	public void CellProjectionUsesDashForNaturalTrackerNote()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Piano");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell naturalCell = pattern.Grid.GetOrCreateCell(0, 0);
		naturalCell.SourceId = sample.Id;
		naturalCell.Note = new StartPatternNote();

		PatternCellViewModel view =
			PatternCellViewModel.Create(
				workspace.Document,
				pattern,
				0,
				0);

		view.NoteText.Should().Be("C-4");
	}

	[Test]
	public void ArbitraryPitchStillDisplaysMultiplier()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Piano");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell arbitraryCell = pattern.Grid.GetOrCreateCell(0, 0);
		arbitraryCell.SourceId = sample.Id;
		arbitraryCell.Note = new StartPatternNote(1.001);

		PatternCellViewModel view =
			PatternCellViewModel.Create(
				workspace.Document,
				pattern,
				0,
				0);

		view.NoteText.Should().Contain("p×1.001");
	}

	[Test]
	public void SourceCatalogContainsEverySoundProducingKindButNotEnvelope()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Sample");
		ObjectId instrumentId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new InstrumentDefinition(instrumentId, "Instrument"));
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		ObjectId sequenceId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new DataSequenceDefinition(sequenceId, "Sequence"));
		ObjectId envelopeId = workspace.Document.AllocateObjectId();
		workspace.Document.Add(
			new AdsrEnvelopeDefinition(envelopeId, "Envelope"));

		PatternSourceOption[] sources =
			PatternSourceCatalog.GetSources(workspace.Document);

		sources.Should().Contain(option => option.Id == sample.Id);
		sources.Should().Contain(option => option.Id == instrumentId);
		sources.Should().Contain(option => option.Id == pattern.Id);
		sources.Should().Contain(option => option.Id == sequenceId);
		sources.Should().NotContain(option => option.Id == envelopeId);
	}

	private static SampleDefinition AddSample(
		DocumentWorkspace workspace,
		string name)
	{
		ObjectId id = workspace.Document.AllocateObjectId();
		SampleDefinition sample =
			new(
				id,
				name,
				new ExternalAssetReference(
					System.IO.Path.GetFullPath($"{name}.wav")));
		workspace.Document.Add(sample);
		return sample;
	}
	[TestCase(PhysicalKey.Z, 0)]
	[TestCase(PhysicalKey.S, 1)]
	[TestCase(PhysicalKey.X, 2)]
	[TestCase(PhysicalKey.D, 3)]
	[TestCase(PhysicalKey.C, 4)]
	[TestCase(PhysicalKey.V, 5)]
	[TestCase(PhysicalKey.G, 6)]
	[TestCase(PhysicalKey.B, 7)]
	[TestCase(PhysicalKey.H, 8)]
	[TestCase(PhysicalKey.N, 9)]
	[TestCase(PhysicalKey.J, 10)]
	[TestCase(PhysicalKey.M, 11)]
	[TestCase(PhysicalKey.Q, 12)]
	[TestCase(PhysicalKey.Digit2, 13)]
	[TestCase(PhysicalKey.W, 14)]
	[TestCase(PhysicalKey.Digit3, 15)]
	[TestCase(PhysicalKey.E, 16)]
	[TestCase(PhysicalKey.R, 17)]
	[TestCase(PhysicalKey.Digit5, 18)]
	[TestCase(PhysicalKey.T, 19)]
	[TestCase(PhysicalKey.Digit6, 20)]
	[TestCase(PhysicalKey.Y, 21)]
	[TestCase(PhysicalKey.Digit7, 22)]
	[TestCase(PhysicalKey.U, 23)]
	[TestCase(PhysicalKey.I, 24)]
	[TestCase(PhysicalKey.Digit9, 25)]
	[TestCase(PhysicalKey.O, 26)]
	[TestCase(PhysicalKey.Digit0, 27)]
	[TestCase(PhysicalKey.P, 28)]
	public void PhysicalTrackerKeyboardMapIsLayoutIndependent(
		PhysicalKey key,
		int expectedSemitone)
	{
		PatternNoteKeyboard.TryGetSemitoneOffset(
			key,
			out int actual).Should().BeTrue();
		actual.Should().Be(expectedSemitone);
	}

	[Test]
	public void PhysicalZEntersCWithoutDependingOnProducedText()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Piano");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(sample.Id, baseOctave: 4);

		PatternNoteInputResult result =
			PatternNoteKeyboardEditor.TypePhysical(
				workspace,
				pattern,
				cursor,
				state,
				PhysicalKey.Z);

		result.Changed.Should().BeTrue();
		pattern.Grid[0, 0]!.Note.Should().Be(
			new StartPatternNote());
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void PhysicalCutAndOffKeysUseQwertyPositions()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(ObjectId.None, baseOctave: 4);

		PatternNoteKeyboardEditor.TypePhysical(
			workspace,
			pattern,
			cursor,
			state,
			PhysicalKey.Digit1);
		PatternNoteKeyboardEditor.TypePhysical(
			workspace,
			pattern,
			cursor,
			state,
			PhysicalKey.Backquote);

		pattern.Grid[0, 0]!.Note.Should().Be(new PatternNoteCut());
		pattern.Grid[1, 0]!.Note.Should().Be(new PatternNoteOff());
	}

	[Test]
	public void NoteTypingNeedsNoEditorSourceSelection()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.Note);
		PatternNoteInputState state =
			new(ObjectId.None, baseOctave: 4);

		PatternNoteInputResult result =
			PatternNoteKeyboardEditor.TypePhysical(
				workspace,
				pattern,
				cursor,
				state,
				PhysicalKey.Z);

		result.Changed.Should().BeTrue();
		result.Rejected.Should().BeFalse();
		pattern.Grid[0, 0]!.Note.Should().Be(
			new StartPatternNote());
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void SourceIsProjectedInItsOwnField()
	{
		DocumentWorkspace workspace = new();
		SampleDefinition sample = AddSample(workspace, "Very Long Piano Name");
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.SourceId = sample.Id;
		cell.Note = new StartPatternNote();

		PatternCellViewModel view =
			PatternCellViewModel.Create(
				workspace.Document,
				pattern,
				0,
				0);

		view.NoteText.Should().Be("C-4");
		view.SourceText.Should().Contain("Very Long Piano Name");
		view.SourceText.Should().Contain($"<{sample.Id.Value}>");
	}

	[Test]
	public void CursorTraversesNoteSourceVolumeThenEffect()
	{
		DataPatternDefinition pattern =
			new((ObjectId)1U, "Pattern");
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.Note);

		cursor.MoveRight(pattern);
		cursor.Field.Should().Be(PatternCellField.Source);
		cursor.MoveRight(pattern);
		cursor.Field.Should().Be(PatternCellField.Volume);
		cursor.MoveRight(pattern);
		cursor.Field.Should().Be(PatternCellField.EffectCommand);
	}

}

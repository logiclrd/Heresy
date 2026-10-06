using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class NativePatternEffectInsertionTests
{
	[Test]
	public void CreationCatalogCoversEveryNativeEffectExactlyOnce()
	{
		NativePatternEffectChoice[] choices =
			NativePatternEffectEditor.GetCreationChoices();

		choices.Should().HaveCount(11);
		choices.Select(choice => choice.Kind)
			.Should().OnlyHaveUniqueItems();
		choices.Select(choice => choice.DisplayName)
			.Should().OnlyHaveUniqueItems();

		foreach (NativePatternEffectChoice choice in choices)
		{
			PatternEffect effect =
				NativePatternEffectEditor.CreateDefault(choice.Kind);

			NativePatternEffectEditor.CanEdit(effect).Should().BeTrue();
			NativePatternEffectEditor.Describe(
				effect,
				CultureInfo.InvariantCulture)
				.Fields.Should().NotBeEmpty();
		}
	}

	[Test]
	public void DefaultsAreValidAndMusicallyNeutralWherePossible()
	{
		NativePatternEffectEditor.CreateDefault(
			NativePatternEffectKind.PitchSlide)
			.Should().Be(new PitchSlidePatternEffect(0.0));
		NativePatternEffectEditor.CreateDefault(
			NativePatternEffectKind.NoteVolumeSlide)
			.Should().Be(new NoteVolumeSlidePatternEffect(0.0));
		NativePatternEffectEditor.CreateDefault(
			NativePatternEffectKind.PlaybackOffset)
			.Should().Be(
				new SetPlaybackOffsetPatternEffect(TimeSpan.Zero));
		NativePatternEffectEditor.CreateDefault(
			NativePatternEffectKind.ResonantFilter)
			.Should().Be(
				new SetResonantFilterPatternEffect(1.0, 0.0));
	}

	[Test]
	public void InsertNativeIntoEmptyCellSelectsInsertedNativeEffect()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		uint audioRevision = workspace.Document.AudioRevision;

		bool changed =
			PatternEffectStackEditor.InsertBefore(
				workspace,
				pattern,
				cursor,
				new SetPlaybackFrequencyPatternEffect(440.0));

		changed.Should().BeTrue();
		PatternCell cell = pattern.Grid[0, 0]!;
		cell.Effects.Should().ContainSingle()
			.Which.Should().Be(
				new SetPlaybackFrequencyPatternEffect(440.0));
		cursor.IsExpanded.Should().BeTrue();
		cursor.ExpandedEffectIndex.Should().Be(0);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Native);
		workspace.Document.AudioRevision.Should().Be(audioRevision + 1);
	}

	[Test]
	public void InsertNativeBeforeSelectedMixedStackPreservesOrder()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TonePortamentoPatternEffect(0x15));
		cell.Effects.Add(new VibratoPatternEffect(0x22));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		cursor.Expand(cell);
		cursor.MoveRight(pattern.RowCount, pattern.ChannelCount, cell);
		cursor.MoveRight(pattern.RowCount, pattern.ChannelCount, cell);
		cursor.ExpandedEffectIndex.Should().Be(1);

		PatternEffectStackEditor.InsertBefore(
			workspace,
			pattern,
			cursor,
			new SetSpeedPatternEffect(6));

		cell.Effects.Should().Equal(
			new TonePortamentoPatternEffect(0x15),
			new SetSpeedPatternEffect(6),
			new VibratoPatternEffect(0x22));
		cursor.IsExpanded.Should().BeTrue();
		cursor.ExpandedEffectIndex.Should().Be(1);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Native);
	}

	[Test]
	public void InsertNativeAfterSelectedMixedStackPreservesOrder()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TonePortamentoPatternEffect(0x15));
		cell.Effects.Add(new VibratoPatternEffect(0x22));
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		cursor.Expand(cell);

		PatternEffectStackEditor.InsertAfter(
			workspace,
			pattern,
			cursor,
			new SetTempoPatternEffect(24.0));

		cell.Effects.Should().Equal(
			new TonePortamentoPatternEffect(0x15),
			new SetTempoPatternEffect(24.0),
			new VibratoPatternEffect(0x22));
		cursor.ExpandedEffectIndex.Should().Be(1);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Native);
	}

	[Test]
	public void ExistingTrackerInsertionStillCreatesInertTrackerSlotWithoutAudioRevision()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(0, 0, PatternCellField.EffectCommand);
		uint audioRevision = workspace.Document.AudioRevision;

		PatternEffectStackEditor.InsertBefore(
			workspace,
			pattern,
			cursor);

		pattern.Grid[0, 0]!.Effects.Should().ContainSingle()
			.Which.Should().Be(new EmptyTrackerPatternEffect());
		workspace.Document.AudioRevision.Should().Be(audioRevision);
	}
	[Test]
	public void SequenceMappedNativeInsertionEditsUnderlyingSharedPattern()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition first =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"First",
				rowCount: 1,
				channelCount: 1);
		DataPatternDefinition second =
			PatternDocumentEditor.CreateDataPattern(
				workspace,
				"Second",
				rowCount: 1,
				channelCount: 1);
		DataSequenceDefinition sequence =
			SequenceDocumentEditor.CreateDataSequence(
				workspace,
				"Arrangement");
		sequence.Entries.Add(new SequenceEntry(first.Id));
		sequence.Entries.Add(new SequenceEntry(second.Id));
		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				workspace.Document,
				sequence,
				initialEntryIndex: 1);
		PatternEffectCursor cursor =
			new(
				context.InitialDisplayRow,
				0,
				PatternCellField.EffectCommand);

		bool changed =
			PatternEditorContextCursor.EditCurrent(
				context,
				cursor,
				row =>
					PatternEffectStackEditor.InsertBefore(
						workspace,
						row.Pattern,
						cursor,
						new SetSpeedPatternEffect(6)));

		changed.Should().BeTrue();
		first.Grid[0, 0].Should().BeNull();
		second.Grid[0, 0]!.Effects.Should().ContainSingle()
			.Which.Should().Be(new SetSpeedPatternEffect(6));
		cursor.Row.Should().Be(context.InitialDisplayRow);
		cursor.IsExpanded.Should().BeTrue();
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Native);
	}

}

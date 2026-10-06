using System;

using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.ViewModels;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternEffectInteractionTests
{
	[Test]
	public void TrackerPortamentoProjectsAsGAndParameterByte()
	{
		PatternEffectViewModel effect =
			PatternEffectViewModel.Create(
				new TonePortamentoPatternEffect(0x15));

		effect.IsTrackerStyle.Should().BeTrue();
		effect.Command.Should().Be('G');
		effect.Parameter.Should().Be(0x15);
		effect.CompactText.Should().Be("G15");
	}

	[Test]
	public void NativeEffectProjectsAsOneFlatKeyboardStop()
	{
		PatternEffectViewModel effect =
			PatternEffectViewModel.Create(
				new SetPlaybackFrequencyPatternEffect(440.0));

		effect.IsTrackerStyle.Should().BeFalse();
		effect.Command.Should().BeNull();
		effect.Parameter.Should().BeNull();
		effect.CompactText.Should().Contain("Frequency");
	}

	[Test]
	public void CompactTabsStackAtRightEdgeWithFivePixelReveals()
	{
		EffectStripLayoutItem[] items =
			EffectStripLayout.Compact(
				effectCount: 3,
				viewportWidth: 100,
				tabWidth: 36,
				revealWidth: 5);

		items.Should().HaveCount(3);
		items[0].X.Should().Be(54);
		items[1].X.Should().Be(59);
		items[2].X.Should().Be(64);
		items[2].X + items[2].Width.Should().Be(100);
		items.Should().OnlyContain(item =>
			item.X >= 0 && item.X + item.Width <= 100);
	}

	[Test]
	public void CompactTabsNeverBleedPastCellWhenNarrow()
	{
		EffectStripLayoutItem[] items =
			EffectStripLayout.Compact(
				effectCount: 20,
				viewportWidth: 40,
				tabWidth: 36,
				revealWidth: 5);

		items.Should().OnlyContain(item =>
			item.X >= 0 && item.X + item.Width <= 40);
	}

	[Test]
	public void ExpandedLayoutUsesSideBySideTabsAndClampedScrolling()
	{
		double maxScroll =
			EffectStripLayout.GetMaximumExpandedScroll(
				effectCount: 5,
				viewportWidth: 100,
				tabWidth: 36,
				edgeControlWidth: 16);

		maxScroll.Should().BeGreaterThan(0);

		EffectStripLayoutItem[] items =
			EffectStripLayout.Expanded(
				effectCount: 5,
				viewportWidth: 100,
				tabWidth: 36,
				edgeControlWidth: 16,
				scrollOffset: maxScroll + 100);

		items[0].X.Should().BeLessThan(16);
		items[^1].X.Should().BeLessThanOrEqualTo(100 - 16);
	}

	[Test]
	public void ExpandedViewCollapsesOnlyBeyondFiveRowHeights()
	{
		EffectStripRect rowBounds =
			new(10, 20, 400, 28);

		EffectStripLayout.ShouldCollapseForPointer(
			rowBounds,
			new EffectStripPoint(100, 20 + 28 + (5 * 28)),
			rowHeight: 28,
			distanceInRowHeights: 5).Should().BeFalse();

		EffectStripLayout.ShouldCollapseForPointer(
			rowBounds,
			new EffectStripPoint(100, 20 + 28 + (5 * 28) + 0.1),
			rowHeight: 28,
			distanceInRowHeights: 5).Should().BeTrue();
	}

	[Test]
	public void CollapsedCursorMovesThroughCommandAndParameterColumns()
	{
		PatternEffectCursor cursor =
			new(row: 3, channel: 2, PatternCellField.Note);

		cursor.MoveRight(rowCount: 64, channelCount: 8);
		cursor.Field.Should().Be(PatternCellField.EffectCommand);

		cursor.MoveRight(rowCount: 64, channelCount: 8);
		cursor.Field.Should().Be(PatternCellField.EffectParameter);

		cursor.MoveRight(rowCount: 64, channelCount: 8);
		cursor.Row.Should().Be(3);
		cursor.Channel.Should().Be(3);
		cursor.Field.Should().Be(PatternCellField.Note);
	}

	[Test]
	public void CommandTypingOnEmptyCellCreatesG00AndAdvancesDown()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.EffectCommand);

		PatternEffectInputResult result =
			PatternEffectKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				'G');

		result.Changed.Should().BeTrue();
		result.Rejected.Should().BeFalse();
		pattern.Grid[0, 0]!.Effects.Should().ContainSingle()
			.Which.Should().Be(new TonePortamentoPatternEffect(0x00));
		cursor.Row.Should().Be(1);
		cursor.Field.Should().Be(PatternCellField.EffectCommand);
	}

	[Test]
	public void CommandTypingReplacesSingleTrackerEffectButPreservesParameter()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new VibratoPatternEffect(0x37));
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.EffectCommand);

		PatternEffectKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			'G');

		pattern.Grid[0, 0]!.Effects.Should().ContainSingle()
			.Which.Should().Be(new TonePortamentoPatternEffect(0x37));
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void ParameterDotWritesZeroWithoutChangingEffectTypeAndAdvancesDown()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TonePortamentoPatternEffect(0x7F));
		pattern.Grid.GetOrCreateCell(1, 0).Effects.Add(
			new VibratoPatternEffect(0x7F));
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.EffectParameter);

		PatternEffectKeyboardEditor.Type(workspace, pattern, cursor, '.');
		PatternEffectKeyboardEditor.Type(workspace, pattern, cursor, '.');

		pattern.Grid[0, 0]!.Effects[0]
			.Should().Be(new TonePortamentoPatternEffect(0x00));
		pattern.Grid[1, 0]!.Effects[0]
			.Should().Be(new VibratoPatternEffect(0x00));
		cursor.Row.Should().Be(2);
	}

	[Test]
	public void TwoHexDigitsWriteParameterThenAdvanceDown()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		pattern.Grid.GetOrCreateCell(0, 0).Effects.Add(
			new TonePortamentoPatternEffect(0xAA));
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.EffectParameter);

		PatternEffectKeyboardEditor.Type(workspace, pattern, cursor, '1');
		cursor.Row.Should().Be(0);
		((TonePortamentoPatternEffect)pattern.Grid[0, 0]!.Effects[0])
			.Parameter.Should().Be(0x1A);

		PatternEffectKeyboardEditor.Type(workspace, pattern, cursor, '5');
		((TonePortamentoPatternEffect)pattern.Grid[0, 0]!.Effects[0])
			.Parameter.Should().Be(0x15);
		cursor.Row.Should().Be(1);
	}

	[Test]
	public void CollapsedMultipleEffectsRejectTypingAndEnterExpands()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TonePortamentoPatternEffect(0x11));
		cell.Effects.Add(new VibratoPatternEffect(0x22));
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.EffectCommand);

		PatternEffectInputResult result =
			PatternEffectKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				'G');

		result.Changed.Should().BeFalse();
		result.Rejected.Should().BeTrue();
		cell.Effects.Should().Equal(
			new TonePortamentoPatternEffect(0x11),
			new VibratoPatternEffect(0x22));

		cursor.HandleEnter(cell);
		cursor.IsExpanded.Should().BeTrue();
		cursor.ExpandedEffectIndex.Should().Be(0);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Command);
	}

	[Test]
	public void ExpandedMixedStackHasTwoStopsForTrackerAndOneForNative()
	{
		PatternCell cell = new();
		cell.Effects.Add(new TonePortamentoPatternEffect(0x11));
		cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
		cell.Effects.Add(new VibratoPatternEffect(0x22));
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.EffectCommand);
		cursor.Expand(cell);

		cursor.ExpandedEffectIndex.Should().Be(0);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Command);

		cursor.MoveRight(64, 8, cell);
		cursor.ExpandedEffectIndex.Should().Be(0);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Parameter);

		cursor.MoveRight(64, 8, cell);
		cursor.ExpandedEffectIndex.Should().Be(1);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Native);

		cursor.MoveRight(64, 8, cell);
		cursor.ExpandedEffectIndex.Should().Be(2);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Command);

		cursor.MoveRight(64, 8, cell);
		cursor.ExpandedEffectIndex.Should().Be(2);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Parameter);

		cursor.MoveRight(64, 8, cell);
		cursor.ExpandedEffectIndex.Should().Be(2);
		cursor.ExpandedField.Should().Be(ExpandedEffectField.Parameter);
		cursor.IsExpanded.Should().BeTrue();
	}

	[Test]
	public void ExpandedNativeEffectRejectsTyping()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new SetPlaybackFrequencyPatternEffect(440));
		cell.Effects.Add(new TonePortamentoPatternEffect(0x11));
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.EffectCommand);
		cursor.Expand(cell);

		PatternEffectInputResult result =
			PatternEffectKeyboardEditor.Type(
				workspace,
				pattern,
				cursor,
				'G');

		result.Changed.Should().BeFalse();
		result.Rejected.Should().BeTrue();
		cell.Effects[0].Should().Be(
			new SetPlaybackFrequencyPatternEffect(440));
	}

	[Test]
	public void ExpandedUpDownAndEnterCollapseWhileLeftRightRemainInside()
	{
		PatternCell cell = new();
		cell.Effects.Add(new TonePortamentoPatternEffect(0x11));
		cell.Effects.Add(new VibratoPatternEffect(0x22));
		PatternEffectCursor cursor =
			new(row: 5, channel: 2, PatternCellField.EffectCommand);
		cursor.Expand(cell);

		cursor.MoveLeft(64, 8, cell);
		cursor.IsExpanded.Should().BeTrue();

		cursor.MoveDown(64);
		cursor.IsExpanded.Should().BeFalse();
		cursor.Row.Should().Be(6);

		cursor.Expand(cell);
		cursor.MoveUp(64);
		cursor.IsExpanded.Should().BeFalse();
		cursor.Row.Should().Be(5);

		cursor.Expand(cell);
		cursor.HandleEnter(cell);
		cursor.IsExpanded.Should().BeFalse();
		cursor.Row.Should().Be(5);
	}

	[Test]
	public void ExpandedTrackerEffectCanBeEditedWithoutLeavingExpandedCell()
	{
		DocumentWorkspace workspace = new();
		DataPatternDefinition pattern =
			PatternDocumentEditor.CreateDataPattern(workspace, "Pattern");
		PatternCell cell = pattern.Grid.GetOrCreateCell(0, 0);
		cell.Effects.Add(new TonePortamentoPatternEffect(0x11));
		cell.Effects.Add(new VibratoPatternEffect(0x22));
		PatternEffectCursor cursor =
			new(row: 0, channel: 0, PatternCellField.EffectCommand);
		cursor.Expand(cell);

		PatternEffectKeyboardEditor.Type(
			workspace,
			pattern,
			cursor,
			'H');

		cell.Effects[0].Should().Be(new VibratoPatternEffect(0x11));
		cursor.Row.Should().Be(0);
		cursor.IsExpanded.Should().BeTrue();
	}
}

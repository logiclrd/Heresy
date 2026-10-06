using AwesomeAssertions;

using Heresy.Core.Patterns;
using Heresy.UserInterface.PatternEditing;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class PatternGridInteractionTests
{
	[Test]
	public void ExpandedEffectsRightAlignWhenTheyFit()
	{
		EffectStripLayoutItem[] layout =
			EffectStripLayout.Expanded(
				effectCount: 2,
				viewportWidth: 188,
				tabWidth: 52,
				edgeControlWidth: 16,
				scrollOffset: 0);

		layout.Should().HaveCount(2);
		layout[0].X.Should().Be(84);
		layout[1].X.Should().Be(136);
	}

	[Test]
	public void ExpandedEffectsStillStartAtScrollableEdgeWhenTheyOverflow()
	{
		EffectStripLayoutItem[] layout =
			EffectStripLayout.Expanded(
				effectCount: 5,
				viewportWidth: 188,
				tabWidth: 52,
				edgeControlWidth: 16,
				scrollOffset: 0);

		layout[0].X.Should().Be(16);
	}

	[TestCase(0, PatternCellField.Note)]
	[TestCase(50, PatternCellField.Note)]
	[TestCase(95, PatternCellField.Note)]
	[TestCase(96, PatternCellField.Source)]
	[TestCase(191, PatternCellField.Source)]
	[TestCase(192, PatternCellField.Volume)]
	[TestCase(225, PatternCellField.Volume)]
	[TestCase(226, PatternCellField.EffectCommand)]
	[TestCase(247, PatternCellField.EffectCommand)]
	[TestCase(248, PatternCellField.EffectParameter)]
	[TestCase(279, PatternCellField.EffectParameter)]
	public void ClickGeometrySelectsCollapsedTrackerFields(
		double x,
		PatternCellField expected)
	{
		PatternCellFieldGeometry.HitTest(
			x,
			cellWidth: 280,
			sourceWidth: 96,
			volumeWidth: 34,
			effectWidth: 54,
			effectCount: 1,
			singleEffectIsTrackerStyle: true)
			.Should().Be(expected);
	}

	[Test]
	public void MultipleEffectsExposeOneCollapsedEffectKeyboardStop()
	{
		PatternCellFieldGeometry.HitTest(
			x: 270,
			cellWidth: 280,
			sourceWidth: 96,
			volumeWidth: 34,
			effectWidth: 54,
			effectCount: 2,
			singleEffectIsTrackerStyle: true)
			.Should().Be(PatternCellField.EffectCommand);
	}

	[Test]
	public void NativeEffectExposesOneCollapsedEffectKeyboardStop()
	{
		PatternCellFieldGeometry.HitTest(
			x: 270,
			cellWidth: 280,
			sourceWidth: 96,
			volumeWidth: 34,
			effectWidth: 54,
			effectCount: 1,
			singleEffectIsTrackerStyle: false)
			.Should().Be(PatternCellField.EffectCommand);
	}

	[Test]
	public void EmptyEffectAreaStillHasCommandAndParameterClickTargets()
	{
		PatternCellFieldGeometry.HitTest(
			x: 235,
			cellWidth: 280,
			sourceWidth: 96,
			volumeWidth: 34,
			effectWidth: 54,
			effectCount: 0,
			singleEffectIsTrackerStyle: true)
			.Should().Be(PatternCellField.EffectCommand);
		PatternCellFieldGeometry.HitTest(
			x: 180,
			cellWidth: 280,
			sourceWidth: 96,
			volumeWidth: 34,
			effectWidth: 54,
			effectCount: 0,
			singleEffectIsTrackerStyle: true)
			.Should().Be(PatternCellField.EffectParameter);
	}
}

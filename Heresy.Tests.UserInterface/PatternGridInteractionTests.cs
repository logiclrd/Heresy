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
	[TestCase(101, PatternCellField.Note)]
	[TestCase(102, PatternCellField.Volume)]
	[TestCase(135, PatternCellField.Volume)]
	[TestCase(136, PatternCellField.EffectCommand)]
	[TestCase(157, PatternCellField.EffectCommand)]
	[TestCase(158, PatternCellField.EffectParameter)]
	[TestCase(189, PatternCellField.EffectParameter)]
	public void ClickGeometrySelectsCollapsedTrackerFields(
		double x,
		PatternCellField expected)
	{
		PatternCellFieldGeometry.HitTest(
			x,
			cellWidth: 190,
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
			x: 180,
			cellWidth: 190,
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
			x: 180,
			cellWidth: 190,
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
			x: 145,
			cellWidth: 190,
			volumeWidth: 34,
			effectWidth: 54,
			effectCount: 0,
			singleEffectIsTrackerStyle: true)
			.Should().Be(PatternCellField.EffectCommand);
		PatternCellFieldGeometry.HitTest(
			x: 180,
			cellWidth: 190,
			volumeWidth: 34,
			effectWidth: 54,
			effectCount: 0,
			singleEffectIsTrackerStyle: true)
			.Should().Be(PatternCellField.EffectParameter);
	}
}

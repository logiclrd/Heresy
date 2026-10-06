using System;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Horizontal hit testing for the collapsed tracker cell. Keeping this separate
/// from Avalonia pointer routing makes mouse/touch cursor placement deterministic.
/// </summary>
public static class PatternCellFieldGeometry
{
	public static PatternCellField HitTest(
		double x,
		double cellWidth,
		double volumeWidth,
		double effectWidth,
		int effectCount,
		bool singleEffectIsTrackerStyle)
	{
		if (!(cellWidth > 0))
			throw new ArgumentOutOfRangeException(nameof(cellWidth));
		if (volumeWidth < 0 || effectWidth < 0
			|| volumeWidth + effectWidth > cellWidth)
		{
			throw new ArgumentOutOfRangeException(
				nameof(volumeWidth));
		}
		if (effectCount < 0)
			throw new ArgumentOutOfRangeException(nameof(effectCount));

		double clampedX = Math.Clamp(x, 0, cellWidth);
		double effectLeft = cellWidth - effectWidth;
		double volumeLeft = effectLeft - volumeWidth;

		if (clampedX < volumeLeft)
			return PatternCellField.Note;
		if (clampedX < effectLeft)
			return PatternCellField.Volume;

		if (effectCount > 1
			|| (effectCount == 1 && !singleEffectIsTrackerStyle))
		{
			return PatternCellField.EffectCommand;
		}

		// Match the visual tracker tab: 20 px command field in a 52 px tab.
		// The 54 px collapsed effect region leaves a 2 px leading inset.
		double tabWidth = Math.Min(52, effectWidth);
		double tabLeft = cellWidth - tabWidth;
		double commandWidth = Math.Min(20, tabWidth * 0.4);
		return clampedX < tabLeft + commandWidth
			? PatternCellField.EffectCommand
			: PatternCellField.EffectParameter;
	}
}

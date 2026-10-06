using System;
using System.Collections.Generic;

namespace Heresy.UserInterface.PatternEditing;

public readonly record struct EffectStripLayoutItem(
	int Index,
	double X,
	double Width);

public readonly record struct EffectStripPoint(double X, double Y);

public readonly record struct EffectStripRect(
	double X,
	double Y,
	double Width,
	double Height);

public static class EffectStripLayout
{
	public static EffectStripLayoutItem[] Compact(
		int effectCount,
		double viewportWidth,
		double tabWidth,
		double revealWidth)
	{
		Validate(effectCount, viewportWidth, tabWidth);
		if (revealWidth < 0)
			throw new ArgumentOutOfRangeException(nameof(revealWidth));
		if (effectCount == 0)
			return [];

		double width = Math.Min(tabWidth, viewportWidth);
		double step = effectCount <= 1 ? 0 : revealWidth;
		double firstX = viewportWidth - width - (step * (effectCount - 1));

		EffectStripLayoutItem[] result =
			new EffectStripLayoutItem[effectCount];
		for (int index = 0; index < effectCount; index++)
		{
			result[index] =
				new EffectStripLayoutItem(
					index,
					firstX + (index * step),
					width);
		}
		return result;
	}

	public static EffectStripLayoutItem[] Expanded(
		int effectCount,
		double viewportWidth,
		double tabWidth,
		double edgeControlWidth,
		double scrollOffset)
	{
		Validate(effectCount, viewportWidth, tabWidth);
		if (edgeControlWidth < 0)
			throw new ArgumentOutOfRangeException(nameof(edgeControlWidth));
		if (effectCount == 0)
			return [];

		double maxScroll =
			GetMaximumExpandedScroll(
				effectCount,
				viewportWidth,
				tabWidth,
				edgeControlWidth);
		double clampedScroll = Math.Clamp(scrollOffset, 0, maxScroll);
		bool overflows = maxScroll > 0;
		double edge = overflows
			? Math.Min(edgeControlWidth, viewportWidth / 2)
			: 0;
		double contentWidth = effectCount * tabWidth;
		double firstX = overflows
			? edge
			: Math.Max(0, viewportWidth - contentWidth);

		EffectStripLayoutItem[] result =
			new EffectStripLayoutItem[effectCount];
		for (int index = 0; index < effectCount; index++)
		{
			result[index] =
				new EffectStripLayoutItem(
					index,
					firstX + (index * tabWidth) - clampedScroll,
					tabWidth);
		}
		return result;
	}

	public static double GetMaximumExpandedScroll(
		int effectCount,
		double viewportWidth,
		double tabWidth,
		double edgeControlWidth)
	{
		Validate(effectCount, viewportWidth, tabWidth);
		if (edgeControlWidth < 0)
			throw new ArgumentOutOfRangeException(nameof(edgeControlWidth));
		if (effectCount == 0)
			return 0;

		double total = effectCount * tabWidth;
		if (total <= viewportWidth)
			return 0;

		double innerWidth =
			Math.Max(0, viewportWidth - (2 * edgeControlWidth));
		return Math.Max(0, total - innerWidth);
	}

	public static bool ShouldCollapseForPointer(
		EffectStripRect rowBounds,
		EffectStripPoint pointer,
		double rowHeight,
		double distanceInRowHeights)
	{
		if (rowHeight <= 0)
			throw new ArgumentOutOfRangeException(nameof(rowHeight));
		if (distanceInRowHeights < 0)
			throw new ArgumentOutOfRangeException(nameof(distanceInRowHeights));

		double margin = rowHeight * distanceInRowHeights;
		double left = rowBounds.X - margin;
		double right = rowBounds.X + rowBounds.Width + margin;
		double top = rowBounds.Y - margin;
		double bottom = rowBounds.Y + rowBounds.Height + margin;
		return pointer.X < left
			|| pointer.X > right
			|| pointer.Y < top
			|| pointer.Y > bottom;
	}

	private static void Validate(
		int effectCount,
		double viewportWidth,
		double tabWidth)
	{
		if (effectCount < 0)
			throw new ArgumentOutOfRangeException(nameof(effectCount));
		if (!(viewportWidth > 0))
			throw new ArgumentOutOfRangeException(nameof(viewportWidth));
		if (!(tabWidth > 0))
			throw new ArgumentOutOfRangeException(nameof(tabWidth));
	}
}

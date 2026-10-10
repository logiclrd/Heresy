using System;
using System.Collections.Generic;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Framework-independent scroll math for playback-follow. Coordinates are
/// obtained from laid-out row-header visuals so sequence separators of
/// arbitrary height do not distort the centered playback position.
/// </summary>
public static class PatternPlaybackFollowScroll
{
	public static double CenteredOffset(
		double rowCenter,
		double viewportHeight,
		double extentHeight)
	{
		if (!double.IsFinite(rowCenter)
			|| !double.IsFinite(viewportHeight)
			|| !double.IsFinite(extentHeight)
			|| viewportHeight <= 0
			|| extentHeight <= 0)
		{
			return 0;
		}

		return Math.Clamp(
			rowCenter - viewportHeight / 2,
			0,
			Math.Max(0, extentHeight - viewportHeight));
	}

	/// <summary>
	/// A standalone Pattern can occur more than once in a Sequence view.
	/// Highlight all matching rows, but follow the one nearest the current
	/// viewport center so playback does not leap between occurrences.
	/// </summary>
	public static int? NearestDisplayRow(
		IEnumerable<(int DisplayRow, double CenterY)> candidates,
		double viewportCenter)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		int? nearest = null;
		double distance = double.PositiveInfinity;
		foreach ((int displayRow, double centerY) in candidates)
		{
			if (!double.IsFinite(centerY))
				continue;
			double candidateDistance = Math.Abs(centerY - viewportCenter);
			if (candidateDistance < distance)
			{
				distance = candidateDistance;
				nearest = displayRow;
			}
		}
		return nearest;
	}
}

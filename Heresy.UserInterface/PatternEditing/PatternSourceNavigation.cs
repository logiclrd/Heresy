using System;
using System.Collections.Generic;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Moves the current pattern-editor Source selection through the same ordered
/// catalog presented by the toolbar.
/// </summary>
public static class PatternSourceNavigation
{
	public static PatternSourceOption? Move(
		IReadOnlyList<PatternSourceOption> sources,
		PatternSourceOption? current,
		int delta)
	{
		ArgumentNullException.ThrowIfNull(sources);
		if (delta is not -1 and not 1)
			throw new ArgumentOutOfRangeException(nameof(delta));

		if (sources.Count == 0)
			return null;

		if (current is null)
			return delta > 0 ? sources[0] : sources[^1];

		int currentIndex = -1;
		for (int i = 0; i < sources.Count; i++)
		{
			if (sources[i].Id == current.Id)
			{
				currentIndex = i;
				break;
			}
		}

		if (currentIndex < 0)
			return delta > 0 ? sources[0] : sources[^1];

		int nextIndex =
			Math.Clamp(
				currentIndex + delta,
				0,
				sources.Count - 1);
		return sources[nextIndex];
	}
}

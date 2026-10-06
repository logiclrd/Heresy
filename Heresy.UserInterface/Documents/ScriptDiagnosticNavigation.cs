using System;

namespace Heresy.UserInterface.Documents;

public readonly record struct ScriptDiagnosticNavigationTarget(
	int SelectionStart,
	int SelectionLength,
	int CaretOffset);

public static class ScriptDiagnosticNavigation
{
	public static ScriptDiagnosticNavigationTarget GetTarget(
		int sourceLength,
		ScriptDiagnosticVisualMarker marker)
	{
		if (sourceLength < 0)
			throw new ArgumentOutOfRangeException(nameof(sourceLength));
		ArgumentNullException.ThrowIfNull(marker);

		int start =
			Math.Clamp(
				marker.OriginalSpan.Start,
				0,
				sourceLength);

		long requestedEnd =
			(long)marker.OriginalSpan.Start
				+ Math.Max(
					0,
					marker.OriginalSpan.Length);
		int end =
			(int)Math.Clamp(
				requestedEnd,
				start,
				sourceLength);

		return new ScriptDiagnosticNavigationTarget(
			start,
			end - start,
			start);
	}
}

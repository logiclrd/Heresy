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

		return new(0, 0, 0);
	}
}

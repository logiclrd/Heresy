using System;
using System.Collections.Generic;

namespace Heresy.UserInterface.Documents;

public sealed record ScriptDiagnosticHoverInfo(
	IReadOnlyList<ScriptDiagnosticVisualMarker> Diagnostics)
{
	public string Text => string.Empty;
}

public static class ScriptDiagnosticHoverCatalog
{
	public static ScriptDiagnosticHoverInfo? FindAtOffset(
		IReadOnlyList<ScriptDiagnosticVisualMarker> markers,
		int offset)
	{
		ArgumentNullException.ThrowIfNull(markers);
		if (offset < 0)
			throw new ArgumentOutOfRangeException(nameof(offset));
		return null;
	}
}

using System;
using System.Collections.Generic;

using Heresy.Scripting.Analysis;

namespace Heresy.UserInterface.Documents;

public sealed record ScriptDiagnosticVisualMarker(
	ScriptSourceSpan Span,
	ScriptSourceSpan OriginalSpan,
	ScriptDiagnosticSeverity Severity,
	string Code,
	string Message);

public static class ScriptDiagnosticVisualMarkerCatalog
{
	public static IReadOnlyList<ScriptDiagnosticVisualMarker> Create(
		int sourceLength,
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
	{
		if (sourceLength < 0)
			throw new ArgumentOutOfRangeException(nameof(sourceLength));
		ArgumentNullException.ThrowIfNull(diagnostics);
		return [];
	}
}

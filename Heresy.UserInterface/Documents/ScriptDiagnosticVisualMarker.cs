using System;
using System.Collections.Generic;
using System.Linq;

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

		return diagnostics
			.Select(diagnostic =>
				CreateMarker(
					sourceLength,
					diagnostic))
			.OrderBy(marker => marker.Span.Start)
			.ThenBy(marker => marker.Severity)
			.ToArray();
	}

	private static ScriptDiagnosticVisualMarker CreateMarker(
		int sourceLength,
		ScriptAnalysisDiagnostic diagnostic)
	{
		int start =
			Math.Clamp(
				diagnostic.Span.Start,
				0,
				sourceLength);

		long requestedEnd =
			(long)diagnostic.Span.Start
				+ Math.Max(
					0,
					diagnostic.Span.Length);
		int end =
			(int)Math.Clamp(
				requestedEnd,
				start,
				sourceLength);

		ScriptSourceSpan visibleSpan;
		if (end > start)
		{
			visibleSpan =
				new ScriptSourceSpan(
					start,
					end - start);
		}
		else if (sourceLength == 0)
		{
			visibleSpan =
				new ScriptSourceSpan(0, 0);
		}
		else if (start < sourceLength)
		{
			visibleSpan =
				new ScriptSourceSpan(start, 1);
		}
		else
		{
			visibleSpan =
				new ScriptSourceSpan(
					sourceLength - 1,
					1);
		}

		return new ScriptDiagnosticVisualMarker(
			visibleSpan,
			diagnostic.Span,
			diagnostic.Severity,
			diagnostic.Code,
			diagnostic.Message);
	}
}

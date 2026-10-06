using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Scripting.Analysis;

namespace Heresy.UserInterface.Documents;

public sealed record ScriptDiagnosticHoverInfo(
	IReadOnlyList<ScriptDiagnosticVisualMarker> Diagnostics)
{
	public string Text =>
		string.Join(
			Environment.NewLine,
			Diagnostics.Select(
				diagnostic =>
					$"{diagnostic.Severity} {diagnostic.Code}: "
						+ diagnostic.Message));
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

		ScriptDiagnosticVisualMarker[] matches =
			markers
				.Where(marker =>
					Matches(
						marker.Span,
						offset))
				.OrderByDescending(marker => marker.Severity)
				.ThenBy(marker => marker.Span.Start)
				.ThenBy(marker => marker.Code, StringComparer.Ordinal)
				.ToArray();

		return matches.Length == 0
			? null
			: new ScriptDiagnosticHoverInfo(matches);
	}

	private static bool Matches(
		ScriptSourceSpan span,
		int offset)
	{
		if (span.Length == 0)
			return offset == span.Start;

		return offset >= span.Start
			&& offset <= span.End;
	}
}

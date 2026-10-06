using System;
using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Scripting.Analysis;

public enum ScriptDiagnosticSeverity
{
	Info,
	Warning,
	Error,
}

public readonly record struct ScriptSourceSpan(int Start, int Length)
{
	public int End => Start + Length;
}

public sealed record ScriptObjectReference(
	ObjectId Id,
	ScriptSourceSpan Span);

public sealed record ScriptAnalysisDiagnostic(
	string Code,
	ScriptDiagnosticSeverity Severity,
	string Message,
	ScriptSourceSpan Span);

public sealed record ScriptReferenceAnalysis(
	IReadOnlyList<ScriptObjectReference> References,
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics)
{
	public bool IsReliable
	{
		get
		{
			foreach (ScriptAnalysisDiagnostic diagnostic in Diagnostics)
			{
				if (diagnostic.Severity == ScriptDiagnosticSeverity.Error)
					return false;
			}

			return true;
		}
	}
}

public static class ScriptReferenceAnalyzer
{
	public static ScriptReferenceAnalysis Analyze(string source)
	{
		ArgumentNullException.ThrowIfNull(source);

		return new ScriptReferenceAnalysis(
			Array.Empty<ScriptObjectReference>(),
			Array.Empty<ScriptAnalysisDiagnostic>());
	}
}

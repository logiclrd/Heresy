using System;
using System.Collections.Generic;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent projection of Roslyn script analysis for authoring
/// surfaces. Persisted source remains unchanged; references are resolved only
/// for presentation.
/// </summary>
public sealed record ScriptSourceDocumentAnalysis(
	ScriptReferenceAnalysis Syntax,
	IReadOnlyList<ProjectedScriptObjectReference> References,
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

public static class ScriptSourceDocumentAnalyzer
{
	public static ScriptSourceDocumentAnalysis Analyze(
		DocumentWorkspace workspace,
		string source)
		=> AnalyzeReferences(workspace, source);

	public static ScriptSourceDocumentAnalysis Analyze(
		DocumentWorkspace workspace,
		ScriptPatternDefinition pattern,
		string source)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		return AnalyzeReferences(
			workspace,
			source,
			ScriptCompiler.AnalyzePatternSource(source));
	}

	public static ScriptSourceDocumentAnalysis Analyze(
		DocumentWorkspace workspace,
		ScriptSequenceDefinition sequence,
		string source)
	{
		ArgumentNullException.ThrowIfNull(sequence);
		return AnalyzeReferences(
			workspace,
			source,
			ScriptCompiler.AnalyzeSequenceSource(source));
	}

	private static ScriptSourceDocumentAnalysis AnalyzeReferences(
		DocumentWorkspace workspace,
		string source)
		=> AnalyzeReferences(
			workspace,
			source,
			ScriptReferenceAnalyzer.Analyze(source).Diagnostics);

	private static ScriptSourceDocumentAnalysis AnalyzeReferences(
		DocumentWorkspace workspace,
		string source,
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(source);

		ScriptReferenceAnalysis syntax =
			ScriptReferenceAnalyzer.Analyze(source);

		return new ScriptSourceDocumentAnalysis(
			syntax,
			ScriptObjectReferenceProjector.Project(
				workspace.Document,
				syntax),
			diagnostics);
	}
}

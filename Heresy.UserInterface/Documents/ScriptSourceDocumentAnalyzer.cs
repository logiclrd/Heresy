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
	ScriptReferenceAnalysisSnapshot SyntaxSnapshot,
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
		string source,
		ScriptReferenceAnalysisSnapshot? previousSnapshot = null)
		=> AnalyzeReferences(
			workspace,
			source,
			ScriptReferenceAnalyzer.CreateSnapshot(
				source,
				previousSnapshot).Analysis.Diagnostics,
			previousSnapshot);

	public static ScriptSourceDocumentAnalysis Analyze(
		DocumentWorkspace workspace,
		ScriptPatternDefinition pattern,
		string source,
		ScriptReferenceAnalysisSnapshot? previousSnapshot = null)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		return AnalyzeReferences(
			workspace,
			source,
			ScriptCompiler.AnalyzePatternSource(source),
			previousSnapshot);
	}

	public static ScriptSourceDocumentAnalysis Analyze(
		DocumentWorkspace workspace,
		ScriptSequenceDefinition sequence,
		string source,
		ScriptReferenceAnalysisSnapshot? previousSnapshot = null)
	{
		ArgumentNullException.ThrowIfNull(sequence);
		return AnalyzeReferences(
			workspace,
			source,
			ScriptCompiler.AnalyzeSequenceSource(source),
			previousSnapshot);
	}

	private static ScriptSourceDocumentAnalysis AnalyzeReferences(
		DocumentWorkspace workspace,
		string source,
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics,
		ScriptReferenceAnalysisSnapshot? previousSnapshot)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(source);

		ScriptReferenceAnalysisSnapshot snapshot =
			ScriptReferenceAnalyzer.CreateSnapshot(
				source,
				previousSnapshot);
		ScriptReferenceAnalysis syntax =
			snapshot.Analysis;

		return new ScriptSourceDocumentAnalysis(
			snapshot,
			syntax,
			ScriptObjectReferenceProjector.Project(
				workspace.Document,
				syntax),
			diagnostics);
	}
}

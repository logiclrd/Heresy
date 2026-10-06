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
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(source);

		ScriptReferenceAnalysisSnapshot snapshot =
			ScriptReferenceAnalyzer.CreateSnapshot(
				source,
				previousSnapshot);
		return Project(
			workspace,
			snapshot,
			snapshot.Analysis.Diagnostics);
	}

	public static ScriptSourceDocumentAnalysis Analyze(
		DocumentWorkspace workspace,
		ScriptPatternDefinition pattern,
		string source,
		ScriptReferenceAnalysisSnapshot? previousSnapshot = null)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(source);

		ScriptReferenceAnalysisSnapshot snapshot =
			ScriptReferenceAnalyzer.CreateSnapshot(
				source,
				previousSnapshot);
		return Project(
			workspace,
			snapshot,
			ScriptCompiler.AnalyzePatternSource(source));
	}

	public static ScriptSourceDocumentAnalysis Analyze(
		DocumentWorkspace workspace,
		ScriptSequenceDefinition sequence,
		string source,
		ScriptReferenceAnalysisSnapshot? previousSnapshot = null)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(sequence);
		ArgumentNullException.ThrowIfNull(source);

		ScriptReferenceAnalysisSnapshot snapshot =
			ScriptReferenceAnalyzer.CreateSnapshot(
				source,
				previousSnapshot);
		return Project(
			workspace,
			snapshot,
			ScriptCompiler.AnalyzeSequenceSource(source));
	}

	private static ScriptSourceDocumentAnalysis Project(
		DocumentWorkspace workspace,
		ScriptReferenceAnalysisSnapshot snapshot,
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
	{
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

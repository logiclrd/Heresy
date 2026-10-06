using System;
using System.Collections.Generic;

using Heresy.Scripting.Analysis;

namespace Heresy.UserInterface.Documents;

/// <summary>
/// Framework-independent projection of Roslyn script analysis for authoring
/// surfaces. Persisted source remains unchanged; references are resolved only
/// for presentation.
/// </summary>
public sealed record ScriptSourceDocumentAnalysis(
	ScriptReferenceAnalysis Syntax,
	IReadOnlyList<ProjectedScriptObjectReference> References)
{
	public bool IsReliable => Syntax.IsReliable;
}

public static class ScriptSourceDocumentAnalyzer
{
	public static ScriptSourceDocumentAnalysis Analyze(
		DocumentWorkspace workspace,
		string source)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(source);

		ScriptReferenceAnalysis syntax =
			ScriptReferenceAnalyzer.Analyze(source);

		return new ScriptSourceDocumentAnalysis(
			syntax,
			ScriptObjectReferenceProjector.Project(
				workspace.Document,
				syntax));
	}
}

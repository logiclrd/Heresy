using System;
using System.Linq;

using Heresy.Core.Scripting;

namespace Heresy.Scripting.Analysis;

/// <summary>
/// Adapts Roslyn reference analysis to Core's parser-independent graph-analysis
/// boundary.
/// </summary>
public sealed class RoslynScriptObjectReferenceAnalyzer
	: IScriptObjectReferenceAnalyzer
{
	public static RoslynScriptObjectReferenceAnalyzer Instance { get; } =
		new();

	private RoslynScriptObjectReferenceAnalyzer() { }

	public ScriptObjectReferenceSet AnalyzeObjectReferences(string source)
	{
		ArgumentNullException.ThrowIfNull(source);

		ScriptReferenceAnalysis analysis =
			ScriptReferenceAnalyzer.Analyze(source);

		return new ScriptObjectReferenceSet(
			analysis.References
				.Select(reference => reference.Id)
				.ToArray(),
			analysis.IsReliable);
	}
}

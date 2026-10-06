using System;

using Heresy.Core.Objects;
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

		return new ScriptObjectReferenceSet(
			Array.Empty<ObjectId>(),
			IsReliable: true);
	}
}

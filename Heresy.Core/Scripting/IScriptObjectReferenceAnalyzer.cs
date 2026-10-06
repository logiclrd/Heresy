using System.Collections.Generic;

using Heresy.Core.Objects;

namespace Heresy.Core.Scripting;

/// <summary>
/// Roslyn-neutral script-reference result consumed by Core graph analysis.
/// Compiler assemblies may provide richer diagnostics/spans separately.
/// </summary>
public sealed record ScriptObjectReferenceSet(
	IReadOnlyList<ObjectId> ObjectIds,
	bool IsReliable);

/// <summary>
/// Boundary through which Core can consume exact script references without
/// depending on the parser/compiler implementation.
/// </summary>
public interface IScriptObjectReferenceAnalyzer
{
	ScriptObjectReferenceSet AnalyzeObjectReferences(string source);
}

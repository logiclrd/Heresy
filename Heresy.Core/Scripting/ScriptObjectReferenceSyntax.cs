using Heresy.Core.Objects;

namespace Heresy.Core.Scripting;

/// <summary>
/// Documents the persisted restricted-C# spelling for an object reference.
/// The editor projects expressions such as _O(137) as atomic named tokens.
/// Roslyn rewriting lives in a later scripting/compiler assembly.
/// </summary>
public static class ScriptObjectReferenceSyntax
{
	public const string LookupFunctionName = "_O";

	public static string Format(ObjectId id) => $"{LookupFunctionName}({id.Value})";
}

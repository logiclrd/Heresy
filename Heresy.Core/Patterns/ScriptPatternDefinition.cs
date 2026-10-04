using Heresy.Core.Objects;

namespace Heresy.Core.Patterns;

/// <summary>
/// Persistent source for a scripted pattern. Heresy scripting is intentionally
/// compiled outside Heresy.Core so Core has no Roslyn dependency.
/// </summary>
public sealed class ScriptPatternDefinition : PatternDefinition
{
	public ScriptPatternDefinition(ObjectId id, string name) : base(id, name) { }

	public string Source { get; set; } = string.Empty;
}

using Heresy.Core.Objects;

namespace Heresy.Core.Sequences;

public sealed class ScriptSequenceDefinition : SequenceDefinition
{
	public ScriptSequenceDefinition(ObjectId id, string name) : base(id, name) { }

	public string Source { get; set; } = string.Empty;
}

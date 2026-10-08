using Heresy.Core.Patterns;
using Heresy.Core.Sequences;

namespace Heresy.Core.Sequencing;

/// <summary>
/// Optional producer factory for experimental recursive scripted sources.
/// Core does not reference Roslyn: callers explicitly supply a compiler
/// that returns invocation-local resumable generators. A compilation failure
/// must throw, not silently fall back to eager source expansion.
/// </summary>
public interface IIncrementalScriptSourceCompiler
{
	IIncrementalRawPatternNoteGenerator CompilePattern(
		ScriptPatternDefinition source);

	IIncrementalRawSequenceEntryGenerator CompileSequence(
		ScriptSequenceDefinition source);
}

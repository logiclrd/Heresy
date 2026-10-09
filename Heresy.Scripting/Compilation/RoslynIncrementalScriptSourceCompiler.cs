using System;
using System.Linq;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;

namespace Heresy.Scripting.Compilation;

/// <summary>
/// Production bridge between the shared-clock recursive sequencer and
/// restricted Roslyn streaming Pattern/Sequence compilers. No eager song
/// compilation or playback-specific compatibility branch remains.
/// </summary>
public sealed class RoslynIncrementalScriptSourceCompiler
	: IIncrementalScriptSourceCompiler
{
	public IIncrementalRawPatternNoteGenerator CompilePattern(
		ScriptPatternDefinition source)
	{
		ArgumentNullException.ThrowIfNull(source);
		ScriptCompilationResult<IIncrementalRawPatternNoteGenerator> result =
			ScriptCompiler.CompileIncrementalPattern(source);
		if (!result.Success || result.Program is null)
			throw CompilationFailure("Pattern", result.Diagnostics);
		return result.Program;
	}

	public ISequenceEntrySourceFactory CompileSequence(
		ScriptSequenceDefinition source)
	{
		ArgumentNullException.ThrowIfNull(source);
		ScriptCompilationResult<ISequenceEntrySourceFactory> result =
			ScriptCompiler.CompileIncrementalSequence(source);
		if (!result.Success || result.Program is null)
			throw CompilationFailure("Sequence", result.Diagnostics);
		return result.Program;
	}

	private static NotSupportedException CompilationFailure(
		string kind,
		System.Collections.Generic.IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
		=> new(
			$"Incremental {kind} script compilation failed: "
			+ string.Join("; ", diagnostics
				.Where(d => d.Severity == ScriptDiagnosticSeverity.Error)
				.Select(d => $"{d.Code}: {d.Message}")));
}

using System;
using System.Collections.Generic;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;

namespace Heresy.Scripting.Compilation;

public sealed record ScriptCompilationResult<T>(
	T? Program,
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics)
	where T : class
{
	public bool Success
	{
		get
		{
			if (Program is null)
				return false;

			foreach (ScriptAnalysisDiagnostic diagnostic in Diagnostics)
			{
				if (diagnostic.Severity == ScriptDiagnosticSeverity.Error)
					return false;
			}

			return true;
		}
	}
}

/// <summary>
/// Compiles persisted restricted-C# source into Heresy's existing sequencing
/// abstractions. The first implementation is intentionally test-first.
/// </summary>
public static class ScriptCompiler
{
	public static ScriptCompilationResult<IRawPatternNoteGenerator> CompilePattern(
		ScriptPatternDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		return NotImplemented<IRawPatternNoteGenerator>();
	}

	public static ScriptCompilationResult<INoteSequencer> CompileSequence(
		ScriptSequenceDefinition definition,
		ISequencePatternResolver resolver)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(resolver);
		return NotImplemented<INoteSequencer>();
	}

	private static ScriptCompilationResult<T> NotImplemented<T>()
		where T : class
		=> new(
			null,
			[
				new ScriptAnalysisDiagnostic(
					"HRS2999",
					ScriptDiagnosticSeverity.Error,
					"Executable script compilation has not been implemented.",
					new ScriptSourceSpan(0, 0)),
			]);
}

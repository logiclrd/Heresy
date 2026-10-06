using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;

namespace Heresy.Scripting.Compilation;

public sealed record SongScheduleCompilationResult(
	NoteSchedule? Schedule,
	TimeSpan Duration,
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics)
{
	public bool Success
	{
		get
		{
			if (Schedule is null)
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
/// Compiles the current root sequence into an immutable note schedule.
/// The implementation is introduced test-first.
/// </summary>
public static class SongScheduleCompiler
{
	public static SongScheduleCompilationResult CompileRoot(
		SongDocument document,
		SequencingContext? context = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		return new(
			null,
			TimeSpan.Zero,
			[
				new ScriptAnalysisDiagnostic(
					"HRS3999",
					ScriptDiagnosticSeverity.Error,
					"Song schedule compilation has not been implemented.",
					new ScriptSourceSpan(0, 0)),
			]);
	}
}

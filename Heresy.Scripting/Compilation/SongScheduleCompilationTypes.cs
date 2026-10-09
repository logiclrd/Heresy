using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;

namespace Heresy.Scripting.Compilation;

/// <summary>Legacy authoring compiler result metadata retained for
/// standalone script diagnostics; the production song scheduler no longer
/// builds an eager NoteSchedule.</summary>
public sealed record CompiledPatternPlaybackPosition(
	TimeSpan Offset, ObjectId PatternId, int PatternRow,
	int? SequenceEntryIndex);

internal interface IPlaybackPositionSequencer : INoteSequencer
{
	IReadOnlyList<CompiledPatternPlaybackPosition> PlaybackPositions { get; }
}

public sealed record SongScheduleCompilationResult(
	NoteSchedule? Schedule, TimeSpan Duration,
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics)
{
	public IReadOnlyList<CompiledPatternPlaybackPosition> PlaybackPositions
		{ get; init; } = Array.Empty<CompiledPatternPlaybackPosition>();
	public bool Success
	{
		get
		{
			if (Schedule is null) return false;
			foreach (ScriptAnalysisDiagnostic item in Diagnostics)
				if (item.Severity == ScriptDiagnosticSeverity.Error)
					return false;
			return true;
		}
	}
}

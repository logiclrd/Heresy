using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;

namespace Heresy.Scripting.Compilation;

public sealed record CompiledPatternPlaybackPosition(
	TimeSpan Offset,
	ObjectId PatternId,
	int PatternRow,
	int? SequenceEntryIndex);

internal interface IPlaybackPositionSequencer
	: INoteSequencer
{
	IReadOnlyList<CompiledPatternPlaybackPosition>
		PlaybackPositions { get; }
}

public sealed record SongScheduleCompilationResult(
	NoteSchedule? Schedule,
	TimeSpan Duration,
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics)
{
	public IReadOnlyList<CompiledPatternPlaybackPosition> PlaybackPositions
		{ get; init; } =
			Array.Empty<CompiledPatternPlaybackPosition>();

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
/// Executes data-driven and scripted song structures into immutable note
/// schedules through the same Core sequencing processors.
/// </summary>
public static class SongScheduleCompiler
{
	private const string InvalidTargetCode = "HRS3001";

	public static SongScheduleCompilationResult CompileRoot(
		SongDocument document,
		SequencingContext? context = null,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (document.RootSequenceId.IsNone)
		{
			return Failure(
				"Root sequence 0 is missing or is not a sequence.");
		}

		return CompileSequence(
			document,
			document.RootSequenceId,
			startOrder: 0,
			startRow: null,
			context,
			shouldFollowOrderJump);
	}

	public static SongScheduleCompilationResult CompileSequence(
		SongDocument document,
		ObjectId sequenceId,
		int startOrder = 0,
		int? startRow = null,
		SequencingContext? context = null,
		Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (sequenceId.IsNone)
			return Failure("Sequence 0 is missing or is not a sequence.");
		if (startOrder < 0)
			throw new ArgumentOutOfRangeException(nameof(startOrder));
		if (startRow.HasValue && startRow.Value < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		if (!document.TryGet(
				sequenceId,
				out SongObject? songObject)
			|| songObject is not SequenceDefinition sequence)
		{
			return Failure(
				$"Sequence {sequenceId.Value} is missing or is not a sequence.");
		}

		DocumentPatternResolver resolver =
			new(document);
		List<ScriptAnalysisDiagnostic> diagnostics = [];
		INoteSequencer? sequencer;

		if (sequence is DataSequenceDefinition dataSequence)
		{
			sequencer =
				new DataSequenceSequencer(
					dataSequence,
					resolver,
					startOrder,
					startRow,
					shouldFollowOrderJump);
		}
		else if (sequence is ScriptSequenceDefinition scriptSequence)
		{
			ScriptCompilationResult<INoteSequencer> compilation =
				ScriptCompiler.CompileSequence(
					scriptSequence,
					resolver,
					startOrder,
					startRow,
					shouldFollowOrderJump);
			diagnostics.AddRange(compilation.Diagnostics);
			sequencer = compilation.Program;
		}
		else
		{
			return Failure(
				$"Sequence {sequence.Id.Value} has an unsupported definition type.");
		}

		SequencingContext activeContext = context ?? new SequencingContext();
		bool rootInvocation = activeContext.FlattenedSourceExpander is null;
		activeContext.FlattenedSourceExpander ??=
			new DocumentFlattenedNoteSourceExpander(document);
		SongScheduleCompilationResult result = Generate(
			sequencer,
			resolver,
			diagnostics,
			activeContext);
		if (rootInvocation && result.Success
			&& activeContext.FlattenedSourceExpander.MaximumAbsoluteEnd > result.Duration)
		{
			return result with
			{
				Duration = activeContext.FlattenedSourceExpander.MaximumAbsoluteEnd,
			};
		}
		return result;
	}

	public static SongScheduleCompilationResult CompilePattern(
		SongDocument document,
		ObjectId patternId,
		int startRow = 0,
		SequencingContext? context = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (patternId.IsNone)
			return Failure("Pattern 0 is missing or is not a pattern.");
		if (startRow < 0)
			throw new ArgumentOutOfRangeException(nameof(startRow));

		DocumentPatternResolver resolver =
			new(document);
		if (!resolver.TryResolve(
				patternId,
				out IRawPatternNoteGenerator? pattern)
			|| pattern is null)
		{
			if (resolver.Diagnostics.Count != 0)
			{
				return new(
					null,
					TimeSpan.Zero,
					resolver.Diagnostics);
			}

			return Failure(
				$"Pattern {patternId.Value} is missing or is not a pattern.");
		}

		SequencingContext activeContext = context ?? new SequencingContext();
		bool rootInvocation = activeContext.FlattenedSourceExpander is null;
		activeContext.FlattenedSourceExpander ??=
			new DocumentFlattenedNoteSourceExpander(document);

		NoteScheduleBuilder output = new();
		List<CompiledPatternPlaybackPosition> playbackPositions = [];
		PatternNoteProcessor.GenerateNotes(
			pattern,
			activeContext,
			output,
			startRow,
			out TimeSpan duration,
			out _,
			(patternRow, offset) =>
				playbackPositions.Add(
					new CompiledPatternPlaybackPosition(
						offset,
						patternId,
						patternRow,
						null)));

		if (HasErrors(resolver.Diagnostics))
		{
			return new(
				null,
				TimeSpan.Zero,
				resolver.Diagnostics);
		}

		return new(
			output.Freeze(),
			rootInvocation && activeContext.FlattenedSourceExpander.MaximumAbsoluteEnd > duration
				? activeContext.FlattenedSourceExpander.MaximumAbsoluteEnd
				: duration,
			resolver.Diagnostics)
		{
			PlaybackPositions = playbackPositions,
		};
	}

	private static SongScheduleCompilationResult Generate(
		INoteSequencer? sequencer,
		DocumentPatternResolver resolver,
		List<ScriptAnalysisDiagnostic> diagnostics,
		SequencingContext? context)
	{
		if (sequencer is null || HasErrors(diagnostics))
		{
			return new(
				null,
				TimeSpan.Zero,
				diagnostics);
		}

		NoteScheduleBuilder output = new();
		sequencer.GenerateNotes(
			context ?? new SequencingContext(),
			output,
			out TimeSpan duration);

		diagnostics.AddRange(resolver.Diagnostics);
		if (HasErrors(diagnostics))
		{
			return new(
				null,
				TimeSpan.Zero,
				diagnostics);
		}

		return new(
			output.Freeze(),
			duration,
			diagnostics)
		{
			PlaybackPositions =
				sequencer is IPlaybackPositionSequencer positioned
					? positioned.PlaybackPositions
					: Array.Empty<CompiledPatternPlaybackPosition>(),
		};
	}

	private static SongScheduleCompilationResult Failure(
		string message)
		=> new(
			null,
			TimeSpan.Zero,
			[
				new ScriptAnalysisDiagnostic(
					InvalidTargetCode,
					ScriptDiagnosticSeverity.Error,
					message,
					new ScriptSourceSpan(0, 0)),
			]);

	private static bool HasErrors(
		IReadOnlyList<ScriptAnalysisDiagnostic> diagnostics)
	{
		foreach (ScriptAnalysisDiagnostic diagnostic in diagnostics)
		{
			if (diagnostic.Severity == ScriptDiagnosticSeverity.Error)
				return true;
		}

		return false;
	}

	private sealed class DocumentPatternResolver
		: ISequencePatternResolver
	{
		private readonly SongDocument _document;
		private readonly Dictionary<ObjectId, IRawPatternNoteGenerator?> _cache = [];
		private readonly HashSet<ObjectId> _resolved = [];
		private readonly List<ScriptAnalysisDiagnostic> _diagnostics = [];

		public DocumentPatternResolver(
			SongDocument document)
		{
			_document = document;
		}

		public IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics =>
			_diagnostics;

		public bool TryResolve(
			ObjectId patternId,
			out IRawPatternNoteGenerator? pattern)
		{
			if (_resolved.Contains(patternId))
			{
				_cache.TryGetValue(
					patternId,
					out pattern);
				return pattern is not null;
			}

			_resolved.Add(patternId);

			if (!_document.TryGet(
					patternId,
					out SongObject? songObject)
				|| songObject is null)
			{
				_cache[patternId] = null;
				pattern = null;
				return false;
			}

			if (songObject is DataPatternDefinition dataPattern)
			{
				pattern = dataPattern;
				_cache[patternId] = pattern;
				return true;
			}

			if (songObject is ScriptPatternDefinition scriptPattern)
			{
				ScriptCompilationResult<IRawPatternNoteGenerator> compilation =
					ScriptCompiler.CompilePattern(
						scriptPattern);
				_diagnostics.AddRange(
					compilation.Diagnostics);
				pattern = compilation.Program;
				_cache[patternId] = pattern;
				return pattern is not null
					&& !HasErrors(
						compilation.Diagnostics);
			}

			_cache[patternId] = null;
			pattern = null;
			return false;
		}
	}

	private sealed class DataSequenceSequencer
		: IPlaybackPositionSequencer
	{
		private readonly DataSequenceDefinition _sequence;
		private readonly ISequencePatternResolver _resolver;
		private readonly int _startOrder;
		private readonly int? _startRow;
		private readonly Func<SequenceOrderJumpEncounter, bool>?
			_shouldFollowOrderJump;
		private readonly List<CompiledPatternPlaybackPosition>
			_playbackPositions = [];

		public IReadOnlyList<CompiledPatternPlaybackPosition>
			PlaybackPositions => _playbackPositions;

		public DataSequenceSequencer(
			DataSequenceDefinition sequence,
			ISequencePatternResolver resolver,
			int startOrder,
			int? startRow,
			Func<SequenceOrderJumpEncounter, bool>? shouldFollowOrderJump)
		{
			_sequence = sequence;
			_resolver = resolver;
			_startOrder = startOrder;
			_startRow = startRow;
			_shouldFollowOrderJump = shouldFollowOrderJump;
		}

		public void GenerateNotes(
			SequencingContext context,
			INoteReceiver output,
			out TimeSpan duration)
		{
			_playbackPositions.Clear();
			SequenceNoteProcessor.GenerateNotes(
				_sequence.Entries,
				_resolver,
				context,
				output,
				_startOrder,
				_startRow,
				out duration,
				(order, patternId, patternRow, offset) =>
					_playbackPositions.Add(
						new CompiledPatternPlaybackPosition(
							offset,
							patternId,
							patternRow,
							order)),
				_shouldFollowOrderJump);
		}
	}
}

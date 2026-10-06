using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
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
/// Executes the current root sequence into an immutable note schedule. Script
/// objects are compiled on demand through the same sequencing interfaces used
/// by data-driven definitions, so the resulting schedule is a stable playback
/// boundary rather than a parallel script-only runtime.
/// </summary>
public static class SongScheduleCompiler
{
	private const string InvalidRootCode = "HRS3001";

	public static SongScheduleCompilationResult CompileRoot(
		SongDocument document,
		SequencingContext? context = null)
	{
		ArgumentNullException.ThrowIfNull(document);

		if (document.RootSequenceId.IsNone
			|| !document.TryGet(
				document.RootSequenceId,
				out SongObject? rootObject)
			|| rootObject is not SequenceDefinition rootSequence)
		{
			return Failure(
				$"Root sequence {document.RootSequenceId.Value} is missing or is not a sequence.");
		}

		DocumentPatternResolver resolver =
			new(document);
		List<ScriptAnalysisDiagnostic> diagnostics = [];

		INoteSequencer? sequencer;
		if (rootSequence is DataSequenceDefinition dataSequence)
		{
			sequencer =
				new DataSequenceSequencer(
					dataSequence,
					resolver);
		}
		else if (rootSequence is ScriptSequenceDefinition scriptSequence)
		{
			ScriptCompilationResult<INoteSequencer> compilation =
				ScriptCompiler.CompileSequence(
					scriptSequence,
					resolver);
			diagnostics.AddRange(compilation.Diagnostics);
			sequencer = compilation.Program;
		}
		else
		{
			return Failure(
				$"Root sequence {rootSequence.Id.Value} has an unsupported definition type.");
		}

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
			diagnostics);
	}

	private static SongScheduleCompilationResult Failure(
		string message)
		=> new(
			null,
			TimeSpan.Zero,
			[
				new ScriptAnalysisDiagnostic(
					InvalidRootCode,
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
		: INoteSequencer
	{
		private readonly DataSequenceDefinition _sequence;
		private readonly ISequencePatternResolver _resolver;

		public DataSequenceSequencer(
			DataSequenceDefinition sequence,
			ISequencePatternResolver resolver)
		{
			_sequence = sequence;
			_resolver = resolver;
		}

		public void GenerateNotes(
			SequencingContext context,
			INoteReceiver output,
			out TimeSpan duration)
		{
			SequenceNoteProcessor.GenerateNotes(
				_sequence,
				_resolver,
				context,
				output,
				out duration);
		}
	}
}

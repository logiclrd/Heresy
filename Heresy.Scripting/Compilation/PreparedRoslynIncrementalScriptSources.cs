using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;

namespace Heresy.Scripting.Compilation;

/// <summary>
/// A snapshot-owned source resolver and streaming script compiler
/// for production recursive playback. All restricted Roslyn scripts are
/// compiled once before creating a timeline. Compiled program factories
/// are immutable and reused; their invocation-local script state and RNG
/// are never shared between executions.
/// </summary>
/// <remarks>
/// Preparation takes a private copy of the supplied snapshot. Neither
/// subsequent authoring edits nor mutations to the caller's snapshot can
/// affect the prepared source graph or the compiled Pattern dimensions.
/// Scripts are compiled eagerly, before the playback timeline is created,
/// but a failure is retained against its own source ID. An unreferenced
/// broken script cannot prevent unrelated audition/preview; requesting the
/// broken source rethrows its original diagnostic before that source runs.
/// </remarks>
public sealed class PreparedRoslynIncrementalScriptSources :
	IIncrementalInvocationResolver, IIncrementalScriptSourceCompiler
{
	private readonly SongDocument _document;
	private readonly Dictionary<ObjectId, IIncrementalRawPatternNoteGenerator>
		_patterns = [];
	private readonly Dictionary<ObjectId, ISequenceEntrySourceFactory>
		_sequences = [];
	private readonly Dictionary<ObjectId, ExceptionDispatchInfo>
		_failedPatterns = [];
	private readonly Dictionary<ObjectId, ExceptionDispatchInfo>
		_failedSequences = [];

	public PreparedRoslynIncrementalScriptSources(
		SongDocumentSnapshot snapshot, ObjectId? editorSequenceCallsites = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// A SongDocumentSnapshot owns a private clone but exposes its document
		// for reading; take another clone so that the caller cannot mutate
		// the definitions after we prepare them. PCM remains shared/immutable.
		_document = SongDocumentSnapshot.Create(snapshot.Document).Document;
		RoslynIncrementalScriptSourceCompiler compiler = new();
		foreach (SongObject source in _document.Objects.Values)
		{
			switch (source)
			{
				case ScriptPatternDefinition pattern:
					try
					{
						_patterns.Add(pattern.Id, compiler.CompilePattern(pattern));
					}
					catch (NotSupportedException error)
					{
						_failedPatterns.Add(pattern.Id, ExceptionDispatchInfo.Capture(error));
					}
					break;
				case ScriptSequenceDefinition sequence:
					try
					{
						_sequences.Add(sequence.Id, compiler.CompileSequence(
							sequence, instrumentPlayCallsites:
								editorSequenceCallsites == sequence.Id));
					}
					catch (NotSupportedException error)
					{
						_failedSequences.Add(sequence.Id, ExceptionDispatchInfo.Capture(error));
					}
					break;
			}
		}
	}

	public bool TryResolve(ObjectId id, out SongObject? source)
		=> _document.TryGet(id, out source);

	public IIncrementalRawPatternNoteGenerator CompilePattern(
		ScriptPatternDefinition source)
	{
		RequirePreparedSource(source);
		if (_failedPatterns.TryGetValue(source.Id, out ExceptionDispatchInfo? failure))
			failure.Throw();
		return _patterns[source.Id];
	}

	public ISequenceEntrySourceFactory CompileSequence(
		ScriptSequenceDefinition source)
	{
		RequirePreparedSource(source);
		if (_failedSequences.TryGetValue(source.Id, out ExceptionDispatchInfo? failure))
			failure.Throw();
		return _sequences[source.Id];
	}

	/// <summary>
	/// Create a fresh shared-clock coordinator from this immutable song
	/// snapshot, for realtime, offline or nested private playback.
	/// </summary>
	public IncrementalRecursiveTimeline CreateTimeline(SequencingContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		return new IncrementalRecursiveTimeline(context, this, this);
	}

	private void RequirePreparedSource(SongObject source)
	{
		ArgumentNullException.ThrowIfNull(source);
		if (!_document.TryGet(source.Id, out SongObject? prepared)
			|| !ReferenceEquals(prepared, source))
		{
			throw new ArgumentException(
				"Script source is not a definition owned by this prepared snapshot.",
				nameof(source));
		}
	}
}

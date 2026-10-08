using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;

namespace Heresy.Scripting.Compilation;

/// <summary>
/// An explicitly prepared, snapshot-owned source resolver and script compiler
/// for experimental recursive playback. All restricted Roslyn scripts are
/// compiled once before creating a timeline. Compiled program factories
/// are immutable and reused; their invocation-local script state and RNG
/// are never shared between executions.
/// </summary>
/// <remarks>
/// Preparation takes a private copy of the supplied snapshot. Neither
/// subsequent authoring edits nor mutations to the caller's snapshot can
/// affect the prepared source graph or the compiled Pattern dimensions.
/// Compilation errors in any scripted object fail preparation immediately,
/// before a playback timeline is created.
/// </remarks>
public sealed class PreparedRoslynIncrementalScriptSources :
	IIncrementalInvocationResolver, IIncrementalScriptSourceCompiler
{
	private readonly SongDocument _document;
	private readonly Dictionary<ObjectId, IIncrementalRawPatternNoteGenerator>
		_patterns = [];
	private readonly Dictionary<ObjectId, ISequenceEntrySourceFactory>
		_sequences = [];

	public PreparedRoslynIncrementalScriptSources(SongDocumentSnapshot snapshot)
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
					_patterns.Add(pattern.Id, compiler.CompilePattern(pattern));
					break;
				case ScriptSequenceDefinition sequence:
					_sequences.Add(sequence.Id, compiler.CompileSequence(sequence));
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
		return _patterns[source.Id];
	}

	public ISequenceEntrySourceFactory CompileSequence(
		ScriptSequenceDefinition source)
	{
		RequirePreparedSource(source);
		return _sequences[source.Id];
	}

	/// <summary>
	/// Create a fresh shared-clock coordinator, resolving its entire source
	/// graph from the private preparation snapshot. Production playback is
	/// not switched to this experimental coordinator.
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

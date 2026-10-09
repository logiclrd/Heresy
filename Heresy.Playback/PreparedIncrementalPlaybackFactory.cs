using System;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Scripting.Compilation;

namespace Heresy.Playback;

/// <summary>
/// Owns the experimental recursive timeline and adapter. The playback
/// session's tail/end-of-input policy stays explicit; disposing this plan
/// does not switch or affect production realtime/offline scheduling.
/// </summary>
public sealed class PreparedIncrementalPlaybackPlan : IDisposable
{
	private bool _disposed;

	internal PreparedIncrementalPlaybackPlan(
		SongDocumentSnapshot snapshot, IncrementalRecursiveTimeline timeline,
		PlaybackSession session, PreparedIncrementalAudioSource source,
		long rootInvocationId)
	{
		Snapshot = snapshot;
		Timeline = timeline;
		Session = session;
		Source = source;
		RootInvocationId = rootInvocationId;
	}

	public SongDocumentSnapshot Snapshot { get; }
	public IncrementalRecursiveTimeline Timeline { get; }
	public PlaybackSession Session { get; }
	public PreparedIncrementalAudioSource Source { get; }
	public long RootInvocationId { get; }

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Source.Dispose();
		Timeline.Dispose();
	}
}

/// <summary>
/// Opt-in preparation of a recursive Pattern/Sequence as a sample-accurate
/// PCM source, using stable song definitions and already decoded sample data.
/// This does not replace the production playback/export source factories.
/// </summary>
public sealed class PreparedIncrementalPlaybackFactory
{
	private readonly RenderConfiguration _configuration;
	private readonly ISampleDataProvider _samples;

	public PreparedIncrementalPlaybackFactory(RenderConfiguration configuration)
		: this(configuration, new InMemorySampleDataProvider()) { }

	public PreparedIncrementalPlaybackFactory(
		RenderConfiguration configuration, ISampleDataProvider samples)
	{
		_configuration = configuration
			?? throw new ArgumentNullException(nameof(configuration));
		_samples = samples ?? throw new ArgumentNullException(nameof(samples));
	}

	public PreparedIncrementalPlaybackPlan Create(
		SongDocument document, ObjectId? rootSourceId = null)
	{
		ArgumentNullException.ThrowIfNull(document);
		return Create(SongDocumentSnapshot.Create(document), rootSourceId);
	}

	public PreparedIncrementalPlaybackPlan Create(
		SongDocumentSnapshot snapshot, ObjectId? rootSourceId = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		// Never give the producer or renderer the caller's mutable snapshot.
		// Cloning shares immutable PCM but captures editable source parameters.
		SongDocumentSnapshot frozen = SongDocumentSnapshot.Create(snapshot.Document);
		ObjectId root = rootSourceId ?? frozen.Document.RootSequenceId;
		if (root.IsNone)
			throw new InvalidOperationException(
				"The prepared recursive source needs a root sequence or Pattern ID.");

		// Restricted Roslyn compilation happens here, before any playback.
		// The script resolver separately owns the same frozen definitions.
		PreparedRoslynIncrementalScriptSources scripts = new(frozen);
		if (!scripts.TryResolve(root, out SongObject? selected)
			|| selected is not (PatternDefinition or SequenceDefinition))
			throw new ArgumentException(
				"Root source must resolve to a prepared Pattern or Sequence.",
				nameof(rootSourceId));

		// Keep the renderer's object graph private too: the plan exposes its
		// descriptive snapshot, but callers may mutate that document. The
		// additional clone shares immutable PCM and does not decode assets.
		SongDocumentSnapshot rendererSnapshot =
			SongDocumentSnapshot.Create(frozen.Document);
		PlaybackSnapshotSoundResolver sounds =
			new(rendererSnapshot.Document, _samples);
		// Resolve every direct sample/synth/instrument and envelope now so
		// injected providers and codec-dependent work cannot run in Render.
		sounds.PrepareDirectSources();

		IncrementalRecursiveTimeline timeline =
			scripts.CreateTimeline(new SequencingContext());
		try
		{
			long invocationId = timeline.AddRoot(root);
			PlaybackSession session = new(
				new RenderContext(_configuration),
				new NoteScheduleBuilder().Freeze(), sounds);
			PreparedIncrementalAudioSource source = new(
				timeline, session, note => ValidatePreparedNote(scripts, note));
			return new PreparedIncrementalPlaybackPlan(
				frozen, timeline, session, source, invocationId);
		}
		catch
		{
			timeline.Dispose();
			throw;
		}
	}

	private static void ValidatePreparedNote(
		PreparedRoslynIncrementalScriptSources scripts, NoteEvent note)
	{
		foreach (NoteCommand command in note.Commands)
		{
			if (command is StartNoteCommand { Mixdown: true } start
				&& scripts.TryResolve(start.SourceId, out SongObject? source)
				&& source is PatternDefinition or SequenceDefinition)
			{
				throw new NotSupportedException(
					"Prepared recursive playback does not yet support nested Pattern/Sequence mixdown clocks.");
			}
		}
	}
}

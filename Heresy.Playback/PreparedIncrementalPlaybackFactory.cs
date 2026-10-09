using System;
using System.Collections.Generic;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;
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
	private AsyncPreparedIncrementalAudioSource? _lookahead;
	private readonly Action _disposePrivateMixdowns;

	internal PreparedIncrementalPlaybackPlan(
		SongDocumentSnapshot snapshot, IncrementalRecursiveTimeline timeline,
		PlaybackSession session, PreparedIncrementalAudioSource source,
		long rootInvocationId, Action disposePrivateMixdowns)
	{
		Snapshot = snapshot;
		Timeline = timeline;
		Session = session;
		Source = source;
		RootInvocationId = rootInvocationId;
		_disposePrivateMixdowns = disposePrivateMixdowns;
	}

	public SongDocumentSnapshot Snapshot { get; }
	public IncrementalRecursiveTimeline Timeline { get; }
	public PlaybackSession Session { get; }
	public PreparedIncrementalAudioSource Source { get; }
	public long RootInvocationId { get; }

	/// <summary>
	/// Start the opt-in background producer. One worker per plan; its
	/// lifetime is tied to the plan. Stop consuming audio before disposal.
	/// </summary>
	public AsyncPreparedIncrementalAudioSource StartLookahead(
		int lookaheadFrames)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_lookahead is not null)
			throw new InvalidOperationException(
				"A prepared playback plan already has a lookahead worker.");
		return _lookahead = new AsyncPreparedIncrementalAudioSource(
			Source, lookaheadFrames);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_lookahead?.Dispose();
		Source.Dispose();
		Timeline.Dispose();
		_disposePrivateMixdowns();
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
			PreparedIncrementalAudioSource source = CreatePrivateMixdownAwareSource(
				timeline, session, scripts, sounds, [root],
				out Action disposePrivateMixdowns);
			return new PreparedIncrementalPlaybackPlan(
				frozen, timeline, session, source, invocationId,
				disposePrivateMixdowns);
		}
		catch
		{
			timeline.Dispose();
			throw;
		}
	}


	/// <summary>
	/// Each physical mixdown start gets a new private recursive clock and
	/// renderer. The parent receives an invocation-unique rendered sound ID
	/// while the producer renders that child's speaker PCM before publishing
	/// each parent horizon. Neither a child script nor child PCM session runs
	/// on the parent's callback.
	/// </summary>
	private PreparedIncrementalAudioSource CreatePrivateMixdownAwareSource(
		IncrementalRecursiveTimeline timeline,
		PlaybackSession session,
		PreparedRoslynIncrementalScriptSources scripts,
		PlaybackSnapshotSoundResolver sounds,
		IReadOnlyList<ObjectId> ancestry,
		out Action disposePrivateMixdowns)
	{
		List<PreparedRecursiveMixdownSound> privateVoices = [];
		disposePrivateMixdowns = () =>
		{
			foreach (PreparedRecursiveMixdownSound voice in privateVoices)
				voice.Dispose();
		};

		NoteEvent Transform(NoteEvent note, long parentFrame)
		{
			NoteCommand[] commands = new NoteCommand[note.Commands.Count];
			for (int i = 0; i < commands.Length; i++)
			{
				NoteCommand command = note.Commands[i];
				if (command is not StartNoteCommand { Mixdown: true } start
					|| !scripts.TryResolve(start.SourceId, out SongObject? definition)
					|| definition is not (PatternDefinition or SequenceDefinition))
				{
					commands[i] = command;
					continue;
				}
				if (start.PitchMultiplier != 1.0
					|| start.PlaybackSpeedMultiplier != 1.0)
					throw new NotSupportedException(
						"Recursive mixdown transforms require private-clock remapping.");
				foreach (ObjectId ancestor in ancestry)
					if (ancestor == start.SourceId)
						throw new InvalidOperationException(
							$"Recursive mixdown source cycle includes object {start.SourceId.Value}.");

				ObjectId[] childAncestry = new ObjectId[ancestry.Count + 1];
				for (int a = 0; a < ancestry.Count; a++)
					childAncestry[a] = ancestry[a];
				childAncestry[^1] = start.SourceId;

				IncrementalRecursiveTimeline childTimeline =
					scripts.CreateTimeline(new SequencingContext());
				try
				{
					childTimeline.AddRoot(start.SourceId);
					PlaybackSession childSession = new(
						new RenderContext(_configuration),
						new NoteScheduleBuilder().Freeze(), sounds);
					PreparedIncrementalAudioSource childSource =
						CreatePrivateMixdownAwareSource(childTimeline, childSession,
							scripts, sounds, childAncestry,
							out Action disposeDescendants);
					PreparedRecursiveMixdownSound privateVoice = new(
						childTimeline, childSession, childSource, parentFrame,
						disposeDescendants);
					ObjectId preparedId = sounds.RegisterPreparedMixdown(privateVoice);
					privateVoices.Add(privateVoice);
					commands[i] = start with
					{
						SourceId = preparedId,
						Mixdown = false,
					};
				}
				catch
				{
					childTimeline.Dispose();
					throw;
				}
			}
			return note with { Commands = commands };
		}

		void PrepareNested(TimeSpan exclusiveEnd)
		{
			long parentEnd = FrameTime.Ceiling(exclusiveEnd, _configuration.SampleRate);
			foreach (PreparedRecursiveMixdownSound voice in privateVoices)
				voice.PrepareThrough(Math.Max(0, parentEnd - voice.ParentStartFrame));
		}

		return new PreparedIncrementalAudioSource(
			timeline, session,
			prepareEvent: Transform,
			prepareNested: PrepareNested);
	}
}

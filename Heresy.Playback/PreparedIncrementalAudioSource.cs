using System;
using System.Collections.Generic;

using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// Single-owner incremental recursive sequencing and PCM rendering.
/// The dedicated PCM worker executes the generator and immediately applies
/// its events to the same PlaybackSession. The only lookahead is one cursor
/// step, never an event queue, a replay journal or a second producer.
/// This source MUST NOT be called by a device audio callback.
/// </summary>
public sealed class PreparedIncrementalAudioSource : IAudioOutputSource, IDisposable
{
	private const int MaximumStepsPerRender = 1_000_000;
	private sealed record PendingEvent(long Frame, long Owner, NoteEvent Note);

	private readonly IncrementalRecursiveTimeline _timeline;
	private readonly PlaybackSession _session;
	private readonly Action<NoteEvent>? _validateNote;
	private readonly bool _endInputAtNaturalCompletion;
	private readonly Func<NoteEvent, long, long, NoteEvent>? _transform;
	private readonly HashSet<long> _canceledOwners = [];
	private IncrementalPatternTimelineStep? _deferredStep;
	private PendingEvent? _pendingEvent;
	private long _lastEventFrame = -1;
	private bool _complete;
	private bool _inputStopped;
	private bool _disposed;

	public PreparedIncrementalAudioSource(
		IncrementalRecursiveTimeline timeline, PlaybackSession session,
		Action<NoteEvent>? validatePreparedNote = null,
		Func<NoteEvent, long, long, NoteEvent>? prepareEvent = null,
		bool endInputAtNaturalCompletion = false)
	{
		_timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_validateNote = validatePreparedNote;
		_endInputAtNaturalCompletion = endInputAtNaturalCompletion;
		_transform = prepareEvent;
		if (session.NextFrame != 0)
			throw new ArgumentException(
				"The incremental renderer requires a fresh playback session.",
				nameof(session));
		Format = new AudioOutputFormat(
			session.SampleRate, session.OutputChannelCount);
	}

	public AudioOutputFormat Format { get; }
	public long NextFrame => _session.NextFrame;
	public PlaybackSession Session => _session;
	public bool IsComplete => _complete || _inputStopped;

	/// <summary>
	/// End private child sequencing now, retaining its existing sounding
	/// voices and their normal end-input release/tail behavior. Called by
	/// the parent on this same PCM worker, at the exact musical boundary.
	/// </summary>
	public void EndInput()
	{
		if (_inputStopped)
			return;
		_inputStopped = true;
		_session.EndInput();
		_session.CutIndefiniteActiveVoicesAfterEndInput();
	}

	public bool Cancel(long invocationId)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_timeline.IsInvocationActive(invocationId))
			return false;
		List<long> removed = [];
		if (!_timeline.Cancel(invocationId, removed))
			return false;
		foreach (long owner in removed)
		{
			_canceledOwners.Add(owner);
			_session.CancelScopedVoices(owner);
		}
		return true;
	}

	private void FindNextEvent(long exclusiveFrame)
	{
		if (_pendingEvent is not null || _complete || _inputStopped)
			return;

		for (int i = 0; i < MaximumStepsPerRender; i++)
		{
			IncrementalPatternTimelineStep? step = _deferredStep;
			_deferredStep = null;
			if (step is null && !_timeline.TryStep(out step))
			{
				_complete = true;
				return;
			}
			if (step is null)
				throw new InvalidOperationException(
					"Recursive timeline returned a null step.");

			if (step is IncrementalPatternTimelineStep.Emit emit)
			{
				long frame = FrameTime.Ceiling(emit.Time, Format.SampleRate);
				if (frame < _lastEventFrame)
					throw new InvalidOperationException(
						"Incremental events must have nondecreasing timestamps.");
				if (frame < _session.NextFrame)
					throw new InvalidOperationException(
						"The recursive event fell behind the PCM render head.");
				_lastEventFrame = frame;
				_pendingEvent = new PendingEvent(
					frame, emit.InvocationId, emit.Note);
				return;
			}

			// A cooperative/no-op step in the future is one bounded cursor
			// lookahead. Do not execute an unbounded silent script to reach
			// an event that lies past this output block.
			if (FrameTime.Ceiling(step.Time, Format.SampleRate) >= exclusiveFrame)
			{
				_deferredStep = step;
				return;
			}
		}
		throw new InvalidOperationException(
			"Recursive generation exceeded the per-render step budget.");
	}

	public void Render(int frameCount, Span<float> destination)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));
		int channels = Format.ChannelCount;
		if (destination.Length != checked(frameCount * channels))
			throw new ArgumentException(
				"Destination must match the requested interleaved frames.",
				nameof(destination));
		long end = checked(_session.NextFrame + frameCount);
		int written = 0;
		while (_session.NextFrame < end)
		{
			long now = _session.NextFrame;
			FindNextEvent(end);

			if (_pendingEvent is { } next && next.Frame == now)
			{
				_pendingEvent = null;
				if (!_canceledOwners.Contains(next.Owner))
				{
					_validateNote?.Invoke(next.Note);
					NoteEvent note = _transform?.Invoke(next.Note, now, next.Owner)
						?? next.Note;
					_session.ApplyScopedEvent(
						next.Owner, note.Target, note.Commands);
				}
				continue;
			}

			if (_endInputAtNaturalCompletion && _complete && !_inputStopped && !_session.InputEnded
				&& FrameTime.Ceiling(_timeline.Elapsed, Format.SampleRate) <= now)
				EndInput();

			long boundary = end;
			if (_pendingEvent is { } future)
				boundary = Math.Min(boundary, future.Frame);
			if (_endInputAtNaturalCompletion && _complete && !_session.InputEnded && !_inputStopped)
				boundary = Math.Min(boundary,
					FrameTime.Ceiling(_timeline.Elapsed, Format.SampleRate));
			if (boundary <= now)
				throw new InvalidOperationException(
					"Incremental rendering could not advance its musical clock.");
			int count = checked((int)(boundary - now));
			_session.Render(now, count,
				destination.Slice(written * channels, count * channels));
			written += count;
		}
	}

	/// <summary>The enclosing playback plan owns the timeline and session.</summary>
	public void Dispose() => _disposed = true;
}

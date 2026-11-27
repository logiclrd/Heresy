using System;
using System.Buffers;

using Heresy.Core.Sequences;
using Heresy.Render.Playback;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Playback;

/// <summary>
/// Fully owned private recursive invocation. Reconstructed on a backward
/// seek/retrigger by rerunning its deterministic generators on the PCM worker,
/// not by keeping prepared-event history or any PCM recording.
/// </summary>
internal sealed record PrivateRecursivePlayback(
	IncrementalRecursiveTimeline Timeline,
	PlaybackSession Session,
	PreparedIncrementalAudioSource Source,
	Action DisposeChildren) : IDisposable
{
	public void Dispose()
	{
		Source.Dispose();
		Timeline.Dispose();
		DisposeChildren();
	}
}

/// <summary>
/// A mixdown is another live incremental sequencer/renderer, with a private
/// tracker clock. The parent asks for PCM synchronously. No event queues,
/// lifecycle queues, replay journals or cooked PCM buffers are retained.
/// </summary>
internal sealed class PreparedRecursiveMixdownSound :
	IStreamingFiniteSound, ISourceFrameSeekableSound, IPlaybackSeekableSound, IFirPreparationSound, IInheritedLifecycleSound, IDisposable
{
	private const int ScratchFrames = 256;
	private readonly record struct SeekAnchor(long OuterFrame, long ChildFrame, double PrerollPitch);
	private sealed class NestedState : SoundState
	{
		private long _sourceFrameOffset;
		public long SourceFrameOffset
		{
			get => _sourceFrameOffset;
			set { _sourceFrameOffset = value; Anchor = null; }
		}
		public SeekAnchor? Anchor { get; set; }
		protected override void OnPlaybackOffsetChanged() => Anchor = null;
	}

	private readonly Func<double, double, PrivateRecursivePlayback> _reconstruct;
	private double _pitchMultiplier = 1.0;
	private double _playbackSpeedMultiplier = 1.0;
	private NestedState? _boundState;
	private PrivateRecursivePlayback _playback;
	private readonly int _sampleRate;
	private readonly int _channels;
	private long _renderOriginFrame;
	private long _observedEndFrame = -1;
	private long _cutFrame = -1;
	private bool _disposed;
	// Lifecycle ages are in the uninterrupted outer invocation clock.
	// A seek replaces the child timeline, never these clocks.
	private long _releaseFrame = -1;
	private long _releaseElapsedAtStart;
	private long _fadeFrame = -1;
	private long _fadeElapsedAtStart;
	private bool _newNoteDisplacement;
	private long? _lifecycleEndFrame;
	private bool IsProducerStopped => _releaseFrame >= 0 || _fadeFrame >= 0 || _cutFrame >= 0;

	public PreparedRecursiveMixdownSound(
		PrivateRecursivePlayback playback,
		long parentStartFrame,
		Func<double, double, PrivateRecursivePlayback> reconstruct)
	{
		_playback = playback ?? throw new ArgumentNullException(nameof(playback));
		_reconstruct = reconstruct ?? throw new ArgumentNullException(nameof(reconstruct));
		if (parentStartFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(parentStartFrame));
		ParentStartFrame = parentStartFrame;
		_sampleRate = playback.Session.SampleRate;
		_channels = playback.Session.OutputChannelCount;
	}

    /// <summary>Private recursive sessions preserve independent DSP and
    /// FIR mailboxes; their current playback is re-bound after seek/rebuild.</summary>
    public void AdoptPreparedFirControls(SoundState state)
        => _playback.Session.AdoptPreparedFirControls();

    public void QueuePendingFirControls(SoundState state,
        FirPreparationScheduler scheduler)
        => _playback.Session.QueuePendingFirControls(scheduler);

	public long ParentStartFrame { get; }
	/// <summary>Current private renderer; replaced on deterministic rewind.</summary>
	public PlaybackSession Session => _playback.Session;
	public SourceFrameSeekCost SeekCost => SourceFrameSeekCost.ReplayRequired;
	public NoteConfigurationSnapshot SnapshotNoteConfiguration()
		=> NoteConfigurationSnapshot.Default;
	public SoundState CreateState() => new NestedState();

	public SoundInvocation? CreateInvocation(
		double pitchMultiplier, double playbackSpeedMultiplier)
	{
		if (!(pitchMultiplier > 0.0) || !double.IsFinite(pitchMultiplier))
			throw new ArgumentOutOfRangeException(nameof(pitchMultiplier));
		if (!(playbackSpeedMultiplier > 0.0)
			|| !double.IsFinite(playbackSpeedMultiplier))
			throw new ArgumentOutOfRangeException(nameof(playbackSpeedMultiplier));
		// Pitch transposes child note frequencies; speed independently
		// scales the child's tracker clock, never its PCM sample rate.
		if (_pitchMultiplier != pitchMultiplier
			|| _playbackSpeedMultiplier != playbackSpeedMultiplier)
		{
			PrivateRecursivePlayback fresh =
				_reconstruct(pitchMultiplier, playbackSpeedMultiplier);
			_playback.Dispose();
			_playback = fresh;
			_pitchMultiplier = pitchMultiplier;
			_playbackSpeedMultiplier = playbackSpeedMultiplier;
			_observedEndFrame = -1;
		}
		NestedState state = new();
		_boundState = state;
		BindParentPitch();
		return new SoundInvocation(this, state, SnapshotNoteConfiguration());
	}

	/// <summary>Pass the parent's live pitch trajectory to each child voice,
	/// not to the completed mixdown or the child's tracker Tempo. The
	/// native source-frame map subtracts Oxx and resets its playback
	/// origin at Qxy; skipped frames precede note time zero.</summary>
	private void BindParentPitch()
	{
		_playback.Session.InheritedPitchAtFrame = _boundState is { } state
			? sourceFrame => state.Anchor is { } anchor
				? sourceFrame < anchor.ChildFrame
					? anchor.PrerollPitch
					: state.PitchTrajectory.GetMultiplier(ClampFrame(
						(Int128)anchor.OuterFrame + sourceFrame - anchor.ChildFrame))
				: state.PitchTrajectory.GetMultiplier(
					Math.Max(0, checked(state.PlaybackOriginFrame
						+ sourceFrame - state.SourceFrameOffset
						- FrameTime.Ceiling(state.PlaybackOffset, _sampleRate))))
			: null;
	}

	public void SetSourceFrameOffset(SoundState state, long sourceFrameOffset)
	{
		if (sourceFrameOffset < 0)
			throw new ArgumentOutOfRangeException(nameof(sourceFrameOffset));
		((NestedState)state).SourceFrameOffset = sourceFrameOffset;
		_observedEndFrame = -1;
	}

	// Use wide arithmetic so signed relative offsets cannot wrap around.
	private static long ClampFrame(Int128 frame)
		=> frame <= 0 ? 0 : frame >= long.MaxValue ? long.MaxValue : (long)frame;

	private long ChildFrameAt(NestedState state, long outerFrame)
		=> state.Anchor is { } anchor
			? ClampFrame((Int128)anchor.ChildFrame + outerFrame - anchor.OuterFrame)
			: checked(outerFrame - state.PlaybackOriginFrame + state.SourceFrameOffset
				+ FrameTime.Ceiling(state.PlaybackOffset, _sampleRate));

	public void SeekTime(RenderContext context, SoundState state, long invocationFrame,
		TimeSpan time, bool relative)
	{
		if (!relative && time < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(time));
		if (relative && time == TimeSpan.Zero) return;
		// The instantiated child clock already incorporates speed; pitch
		// transposes its voices, not the timeline. Round on its native grid.
		Int128 numerator = (Int128)time.Ticks * _sampleRate;
		Int128 frames = numerator / TimeSpan.TicksPerSecond;
		if (numerator % TimeSpan.TicksPerSecond > 0) frames++;
		SeekCore((NestedState)state, invocationFrame, frames, relative);
	}

	public void SeekFrame(RenderContext context, SoundState state, long invocationFrame,
		long frame, bool relative)
	{
		if (!relative && frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));
		if (relative && frame == 0) return;
		SeekCore((NestedState)state, invocationFrame, frame, relative);
	}

	private void SeekCore(NestedState state, long invocationFrame, Int128 amount, bool relative)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (invocationFrame < 0) throw new ArgumentOutOfRangeException(nameof(invocationFrame));
		// Cut/retirement cannot be undone. Release and Fade can still
		// address content, but their already elapsed ages survive the seek.
		if (_cutFrame >= 0 || (IsProducerStopped && _playback.Session.IsAudioQuiescent)
			|| (_lifecycleEndFrame.HasValue && invocationFrame >= _lifecycleEndFrame.Value))
			return;
		long current = ChildFrameAt(state, invocationFrame);
		long target = ClampFrame(relative ? (Int128)current + amount : amount);
		if (target == current) return;
		_playback.Source.CheckCancellation();
		if (IsProducerStopped && _playback.Session.GetKnownStoppedAudioEndFrameExclusive() is long oldEnd)
		{
			long bound = ClampFrame((Int128)invocationFrame
				+ Math.Max(0, oldEnd - _playback.Source.NextFrame));
			_lifecycleEndFrame = _lifecycleEndFrame.HasValue
				? Math.Min(_lifecycleEndFrame.Value, bound) : bound;
			if (invocationFrame >= _lifecycleEndFrame.Value) return;
		}
		double prerollPitch = state.PitchTrajectory.GetMultiplier(invocationFrame);
		PrivateRecursivePlayback fresh = _reconstruct(_pitchMultiplier, _playbackSpeedMultiplier);
		_playback.Dispose();
		_playback = fresh;
		state.PlaybackOffset = TimeSpan.Zero;
		state.SourceFrameOffset = 0;
		state.Anchor = new SeekAnchor(invocationFrame, target, prerollPitch);
		_renderOriginFrame = state.PlaybackOriginFrame;
		_observedEndFrame = -1;
		BindParentPitch();
		_playback.Session.ReplayRequiredSeekObserved?.Invoke("Script playhead seek", target);

		// Reconstruct immediately at the ordered event boundary: a
		// following Off/Cut at the same frame must see the sought state.
		// No retained PCM, event history or obsolete descendant registry.
		float[] scratch = ArrayPool<float>.Shared.Rent(ScratchFrames * _channels);
		try
		{
			RenderUntil(target, Span<float>.Empty, target, scratch, checkCancellation: true,
				reconstructing: true);
			if (IsProducerStopped && _playback.Source.NextFrame == target)
				RestoreLifecycle(invocationFrame);
		}
		finally { ArrayPool<float>.Shared.Return(scratch); }
	}

	public long? GetEndFrameExclusive(RenderContext context, SoundState state)
	{
		NestedState nested = (NestedState)state;
		long origin = nested.PlaybackOriginFrame;
		if (nested.Anchor is { } anchor)
		{
			long? mappedEnd = _observedEndFrame < 0 ? null
				: ClampFrame((Int128)anchor.OuterFrame
					+ Math.Max(0, _observedEndFrame - anchor.ChildFrame));
			if (_cutFrame >= 0)
				mappedEnd = mappedEnd.HasValue ? Math.Min(mappedEnd.Value, _cutFrame) : _cutFrame;
			if (_lifecycleEndFrame is long lifecycleEnd)
				mappedEnd = mappedEnd.HasValue ? Math.Min(mappedEnd.Value, lifecycleEnd) : lifecycleEnd;
			return mappedEnd;
		}
		long cut = _cutFrame >= origin ? _cutFrame : -1;
		long end = _observedEndFrame >= 0 && _renderOriginFrame == origin
			? checked(origin + _observedEndFrame) : -1;
		if (cut >= 0)
			end = end < 0 ? cut : Math.Min(end, cut);
		return end < 0 ? null
			: Math.Max(0, end - nested.SourceFrameOffset
				- FrameTime.Ceiling(nested.PlaybackOffset, _sampleRate));
	}

	/// <summary>
	/// Parent lifecycle commands arrive in chronological order while the
	/// same worker is at the affected parent frame. No extra control queue.
	/// </summary>
	public void ScheduleRelease(long parentFrame)
	{
		AdvanceToParentFrame(parentFrame);
		long outerFrame = parentFrame - ParentStartFrame;
		if (_releaseFrame < 0) _releaseFrame = outerFrame;
		_playback.Source.EndInput(0, reconstructing: true, includeTargetEvents: false);
	}

	public void ScheduleCut(long parentFrame)
	{
		AdvanceToParentFrame(parentFrame);
		_cutFrame = parentFrame - ParentStartFrame;
		_observedEndFrame = _playback.Source.NextFrame;
	}

	public void ScheduleFade(long parentFrame,
		bool newNoteDisplacement = false)
	{
		AdvanceToParentFrame(parentFrame);
		_fadeFrame = parentFrame - ParentStartFrame;
		_fadeElapsedAtStart = 0;
		_newNoteDisplacement = newNoteDisplacement;
		_playback.Source.StopProducing();
		_playback.Source.Session.RequestFadeOfActiveVoices(newNoteDisplacement, 0,
			reconstructing: true, includeTargetEvents: false);
	}

	// A rebuilt nested voice can be younger than the ancestor's release.
	// Anchor its transition at its own selected source boundary and carry
	// the independent elapsed age, rather than backdating its onset.
	public void InheritRelease(long invocationFrame, long elapsedReleaseFrames, bool includeTargetEvents)
	{
		if (_cutFrame >= 0 || _releaseFrame >= 0) return;
		PrepareInheritedBoundary(invocationFrame, includeTargetEvents);
		_releaseFrame = invocationFrame;
		_releaseElapsedAtStart = elapsedReleaseFrames;
		_playback.Source.EndInput(elapsedReleaseFrames, reconstructing: true, includeTargetEvents: includeTargetEvents);
	}

	public void InheritFade(long invocationFrame, long elapsedFadeFrames, bool newNoteDisplacement,
		bool includeTargetEvents)
	{
		if (_cutFrame >= 0 || _fadeFrame >= 0) return;
		PrepareInheritedBoundary(invocationFrame, includeTargetEvents);
		_fadeFrame = invocationFrame;
		_fadeElapsedAtStart = elapsedFadeFrames;
		_newNoteDisplacement = newNoteDisplacement;
		_playback.Source.StopProducing();
		_playback.Session.RequestFadeOfActiveVoices(newNoteDisplacement, elapsedFadeFrames,
			reconstructing: true, includeTargetEvents: includeTargetEvents);
	}

	private void PrepareInheritedBoundary(long invocationFrame, bool includeTargetEvents)
	{
		if (_boundState is not { } state) return;
		long target = ChildFrameAt(state, invocationFrame);
		if (target > _playback.Source.NextFrame)
		{
			float[] scratch = ArrayPool<float>.Shared.Rent(ScratchFrames * _channels);
			try { RenderUntil(target, Span<float>.Empty, target, scratch, checkCancellation: true); }
			finally { ArrayPool<float>.Shared.Return(scratch); }
		}
		if (includeTargetEvents && _playback.Source.NextFrame == target)
			_playback.Source.ApplyEventsAtCurrentFrame();
	}

	private void RestoreLifecycle(long invocationFrame)
	{
		_playback.Source.ApplyEventsAtCurrentFrame();
		long releaseAge = _releaseFrame < 0 ? 0
			: ClampFrame((Int128)_releaseElapsedAtStart + invocationFrame - _releaseFrame);
		long fadeAge = _fadeFrame < 0 ? 0
			: ClampFrame((Int128)_fadeElapsedAtStart + invocationFrame - _fadeFrame);
		void Release() => _playback.Source.EndInput(releaseAge, reconstructing: true);
		void Fade()
		{
			_playback.Source.StopProducing();
			_playback.Session.RequestFadeOfActiveVoices(_newNoteDisplacement, fadeAge,
				reconstructing: true);
		}
		// Preserve the ordering of independent Off and Fade transitions.
		if (_fadeFrame >= 0 && (_releaseFrame < 0 || _fadeFrame < _releaseFrame)) Fade();
		if (_releaseFrame >= 0) Release();
		if (_fadeFrame >= 0 && _releaseFrame >= 0 && _fadeFrame >= _releaseFrame) Fade();
	}

	private void AdvanceToParentFrame(long parentFrame)
	{
		if (parentFrame < ParentStartFrame)
			throw new ArgumentOutOfRangeException(nameof(parentFrame));
		long outerFrame = parentFrame - ParentStartFrame;
		long requested = _boundState is { Anchor: not null } state
			? ChildFrameAt(state, outerFrame)
			: outerFrame - _renderOriginFrame;
		if (requested < 0)
			return;
		if (requested > _playback.Source.NextFrame)
		{
			float[] scratch = ArrayPool<float>.Shared.Rent(ScratchFrames * _channels);
			try { RenderUntil(requested, Span<float>.Empty, requested, scratch); }
			finally { ArrayPool<float>.Shared.Return(scratch); }
		}
	}

	public void Render(RenderContext context, SoundState state,
		long startFrame, int frameCount, Span<float> destination)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (startFrame < 0 || frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (destination.Length != checked(frameCount * _channels))
			throw new ArgumentException(
				"Destination must match interleaved speaker frame count.",
				nameof(destination));
		if (frameCount == 0)
			return;
		NestedState nested = (NestedState)state;
		long origin = nested.PlaybackOriginFrame;
		long first = ChildFrameAt(nested, startFrame);
		if (first < 0)
			throw new InvalidOperationException(
				"Private mixdown playback cannot precede its retrigger origin.");
		long end = nested.Anchor.HasValue ? ClampFrame((Int128)first + frameCount)
			: checked(first + frameCount);

		// Restarts create fresh generator AND rendering state, including
		// independent nested clocks. They execute on the PCM worker.
		if (origin != _renderOriginFrame || first < _playback.Source.NextFrame)
		{
			PrivateRecursivePlayback fresh = _reconstruct(
				_pitchMultiplier, _playbackSpeedMultiplier);
			_playback.Dispose();
			_playback = fresh;
			BindParentPitch();
			_renderOriginFrame = origin;
			_observedEndFrame = -1;
		}
		long cut = ChildCutFrame();
		if (cut >= 0 && first >= cut)
			return;

		float[] scratch = ArrayPool<float>.Shared.Rent(ScratchFrames * _channels);
		try
		{
			RenderUntil(first, Span<float>.Empty, first, scratch);
			RenderUntil(end, destination, first, scratch);
		}
		finally { ArrayPool<float>.Shared.Return(scratch); }
	}

	private long ChildCutFrame()
	{
		long cut = _cutFrame;
		if (_lifecycleEndFrame is long end)
			cut = cut < 0 ? end : Math.Min(cut, end);
		return _boundState is { Anchor: not null } state
			? cut < 0 ? -1 : ChildFrameAt(state, cut)
			: cut >= _renderOriginFrame ? cut - _renderOriginFrame : -1;
	}

	private void RenderUntil(long exclusiveEnd,
		Span<float> destination, long outputStart, float[] scratch, bool checkCancellation = false,
		bool reconstructing = false)
	{
		while (_playback.Source.NextFrame < exclusiveEnd)
		{
			if (checkCancellation) _playback.Source.CheckCancellation();
			long now = _playback.Source.NextFrame;
			long cut = reconstructing ? -1 : ChildCutFrame();
			if (cut >= 0 && now >= cut)
			{
				_observedEndFrame = now;
				return;
			}
			if (_playback.Source.Session.IsQuiescent
				|| (!reconstructing && IsProducerStopped && _playback.Session.IsAudioQuiescent))
			{
				_observedEndFrame = now;
				return;
			}
			long until = now + Math.Min(exclusiveEnd - now, ScratchFrames);
			if (cut >= 0)
				until = Math.Min(until, cut);
			if (until <= now)
				break;
			int count = checked((int)(until - now));
			Span<float> scratchFrames = scratch.AsSpan(0, count * _channels);
			_playback.Source.Render(count, scratchFrames);
			if (!destination.IsEmpty && until > outputStart)
			{
				long from = Math.Max(now, outputStart);
				int input = checked((int)(from - now) * _channels);
				int output = checked((int)(from - outputStart) * _channels);
				int samples = checked((int)(until - from) * _channels);
				for (int i = 0; i < samples; i++)
					destination[output + i] += scratchFrames[input + i];
			}
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_playback.Dispose();
	}
}

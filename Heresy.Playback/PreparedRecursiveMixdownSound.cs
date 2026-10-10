using System;
using System.Buffers;

using Heresy.Core.Sequences;
using Heresy.Render.Playback;
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
	IStreamingFiniteSound, ISourceFrameSeekableSound, IDisposable
{
	private const int ScratchFrames = 256;
	private sealed class NestedState : SoundState
	{
		public long SourceFrameOffset { get; set; }
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
			? sourceFrame => state.PitchTrajectory.GetMultiplier(
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

	public long? GetEndFrameExclusive(RenderContext context, SoundState state)
	{
		NestedState nested = (NestedState)state;
		long origin = nested.PlaybackOriginFrame;
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
		_playback.Source.EndInput();
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
		// Unlike Off, Fade ends future note production without forcing a
		// release/cut of already sounding private voices. A source-level
		// S76 or NNA Fade uses the new-note fade duration; S72 past-note
		// Fade uses the ordinary note-fade duration.
		_playback.Source.StopProducing();
		_playback.Source.Session.RequestFadeOfActiveVoices(
			newNoteDisplacement);
	}

	private void AdvanceToParentFrame(long parentFrame)
	{
		if (parentFrame < ParentStartFrame)
			throw new ArgumentOutOfRangeException(nameof(parentFrame));
		long requested = parentFrame - ParentStartFrame - _renderOriginFrame;
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
		long offset = checked(nested.SourceFrameOffset
			+ FrameTime.Ceiling(nested.PlaybackOffset, _sampleRate));
		long first = checked(startFrame - origin + offset);
		if (first < 0)
			throw new InvalidOperationException(
				"Private mixdown playback cannot precede its retrigger origin.");
		long end = checked(first + frameCount);

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
		long cut = _cutFrame >= origin ? _cutFrame - origin : -1;
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

	private void RenderUntil(long exclusiveEnd,
		Span<float> destination, long outputStart, float[] scratch)
	{
		while (_playback.Source.NextFrame < exclusiveEnd)
		{
			long now = _playback.Source.NextFrame;
			long cut = _cutFrame >= _renderOriginFrame
				? _cutFrame - _renderOriginFrame : -1;
			if (cut >= 0 && now >= cut)
			{
				_observedEndFrame = now;
				return;
			}
			if (_playback.Source.Session.IsQuiescent)
			{
				_observedEndFrame = now;
				return;
			}
			long until = Math.Min(exclusiveEnd, checked(now + ScratchFrames));
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

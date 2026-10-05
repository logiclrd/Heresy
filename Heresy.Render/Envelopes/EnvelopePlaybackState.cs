using System;
using System.Collections.Generic;

namespace Heresy.Render.Envelopes;

/// <summary>
/// Mutable per-voice envelope timeline. Enable/disable and note-off mutations
/// are chronological, while value queries may revisit historical frames. This
/// is required by pitch-trajectory integration of already-closed segments.
/// </summary>
public sealed class EnvelopePlaybackState
{
	private readonly record struct TimelineSegment(
		long AbsoluteStartFrame,
		long ActiveStartFrame,
		bool Enabled);

	private readonly IEnvelopeCurve _curve;
	private readonly int _sampleRate;
	private readonly long _startFrame;
	private readonly List<TimelineSegment> _segments = [];
	private long _latestAbsoluteFrame;
	private long _activeFrame;
	private long? _noteOffAbsoluteFrame;
	private long? _noteOffActiveFrame;

	public EnvelopePlaybackState(
		IEnvelopeCurve curve,
		long startFrame,
		int sampleRate)
	{
		_curve = curve ?? throw new ArgumentNullException(nameof(curve));
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		_startFrame = startFrame;
		_latestAbsoluteFrame = startFrame;
		_sampleRate = sampleRate;
		_segments.Add(new TimelineSegment(startFrame, 0, true));
	}

	public bool Enabled => _segments[^1].Enabled;
	public long ActiveFrame => _activeFrame;
	public long? NoteOffActiveFrame => _noteOffActiveFrame;

	public double GetValue(long absoluteFrame)
	{
		long activeFrame = GetActiveFrame(absoluteFrame);
		Observe(absoluteFrame, activeFrame);

		long? noteOffActiveFrame =
			_noteOffAbsoluteFrame.HasValue
				&& absoluteFrame >= _noteOffAbsoluteFrame.Value
				? _noteOffActiveFrame
				: null;

		double value = _curve.GetValue(
			activeFrame,
			_sampleRate,
			noteOffActiveFrame);
		if (double.IsNaN(value) || double.IsInfinity(value))
			throw new InvalidOperationException("Envelope returned a non-finite value.");
		return value;
	}

	public void SetEnabled(long absoluteFrame, bool enabled)
	{
		EnsureChronologicalMutation(absoluteFrame);
		long activeFrame = GetActiveFrame(absoluteFrame);
		Observe(absoluteFrame, activeFrame);

		if (Enabled == enabled)
			return;

		_segments.Add(
			new TimelineSegment(
				absoluteFrame,
				activeFrame,
				enabled));
	}

	public void NoteOff(long absoluteFrame)
	{
		EnsureChronologicalMutation(absoluteFrame);
		long activeFrame = GetActiveFrame(absoluteFrame);
		Observe(absoluteFrame, activeFrame);

		if (_noteOffActiveFrame.HasValue)
			return;

		_noteOffAbsoluteFrame = absoluteFrame;
		_noteOffActiveFrame = activeFrame;
	}

	private long GetActiveFrame(long absoluteFrame)
	{
		if (absoluteFrame < _startFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		int low = 0;
		int high = _segments.Count - 1;
		while (low < high)
		{
			int middle = low + (high - low + 1) / 2;
			if (_segments[middle].AbsoluteStartFrame <= absoluteFrame)
				low = middle;
			else
				high = middle - 1;
		}

		TimelineSegment segment = _segments[low];
		return segment.Enabled
			? checked(
				segment.ActiveStartFrame
					+ absoluteFrame
					- segment.AbsoluteStartFrame)
			: segment.ActiveStartFrame;
	}

	private void Observe(long absoluteFrame, long activeFrame)
	{
		if (absoluteFrame < _latestAbsoluteFrame)
			return;

		_latestAbsoluteFrame = absoluteFrame;
		_activeFrame = activeFrame;
	}

	private void EnsureChronologicalMutation(long absoluteFrame)
	{
		if (absoluteFrame < _latestAbsoluteFrame)
		{
			throw new InvalidOperationException(
				"Envelope timeline mutations must be chronological.");
		}
	}
}

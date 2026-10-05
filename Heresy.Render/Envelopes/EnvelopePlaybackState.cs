using System;

namespace Heresy.Render.Envelopes;

/// <summary>
/// Mutable per-voice envelope timeline. Disabling pauses active-frame
/// progression and holds the current value; re-enabling resumes from that
/// position. This matches the behavior needed by IT S77-S7C controls.
/// </summary>
public sealed class EnvelopePlaybackState
{
	private readonly IEnvelopeCurve _curve;
	private readonly int _sampleRate;
	private long _lastAbsoluteFrame;
	private long _activeFrame;
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

		_lastAbsoluteFrame = startFrame;
		_sampleRate = sampleRate;
	}

	public bool Enabled { get; private set; } = true;
	public long ActiveFrame => _activeFrame;
	public long? NoteOffActiveFrame => _noteOffActiveFrame;

	public double GetValue(long absoluteFrame)
	{
		Synchronize(absoluteFrame);
		double value = _curve.GetValue(
			_activeFrame,
			_sampleRate,
			_noteOffActiveFrame);
		if (double.IsNaN(value) || double.IsInfinity(value))
			throw new InvalidOperationException("Envelope returned a non-finite value.");
		return value;
	}

	public void SetEnabled(long absoluteFrame, bool enabled)
	{
		Synchronize(absoluteFrame);
		Enabled = enabled;
	}

	public void NoteOff(long absoluteFrame)
	{
		Synchronize(absoluteFrame);
		_noteOffActiveFrame ??= _activeFrame;
	}

	private void Synchronize(long absoluteFrame)
	{
		if (absoluteFrame < _lastAbsoluteFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		if (Enabled)
		{
			_activeFrame = checked(
				_activeFrame + absoluteFrame - _lastAbsoluteFrame);
		}

		_lastAbsoluteFrame = absoluteFrame;
	}
}

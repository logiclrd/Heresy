using System;

namespace Heresy.Render.Playback;

/// <summary>
/// Residue/inertia anti-click tail belonging to one playback channel after
/// spatialization. One residue/inertia pair is maintained per output stream.
/// </summary>
public sealed class AntiClickTail
{
	public const double ReferenceDecay = 0.93;
	public const int ReferenceSampleRate = 44100;

	private readonly double[] _residue;
	private readonly double[] _inertia;
	private readonly double _decay;

	public AntiClickTail(int outputChannelCount, int sampleRate)
	{
		if (outputChannelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(outputChannelCount));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		_residue = new double[outputChannelCount];
		_inertia = new double[outputChannelCount];
		_decay = CalculateDecay(sampleRate);
	}

	public int OutputChannelCount => _residue.Length;

	public bool IsActive
	{
		get
		{
			for (int i = 0; i < _residue.Length; i++)
			{
				if (_residue[i] != 0.0 || _inertia[i] != 0.0)
					return true;
			}
			return false;
		}
	}

	public static double CalculateDecay(int sampleRate)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		return Math.Pow(
			ReferenceDecay,
			(double)ReferenceSampleRate / sampleRate);
	}

	/// <summary>
	/// Adds the just-detached source waveform to any residue already present.
	/// If two source frames are known, inertia continues the most recent slope.
	/// </summary>
	public void AddCut(ReadOnlySpan<float> previous, ReadOnlySpan<float> last, bool havePrevious)
	{
		if (previous.Length != _residue.Length)
			throw new ArgumentException("Previous sample width does not match output channel count.", nameof(previous));
		if (last.Length != _residue.Length)
			throw new ArgumentException("Last sample width does not match output channel count.", nameof(last));

		for (int channel = 0; channel < _residue.Length; channel++)
		{
			_residue[channel] += last[channel];
			if (havePrevious)
				_inertia[channel] += last[channel] - previous[channel];
		}
	}

	/// <summary>
	/// Adds one tail frame to destination. The first frame after a cut continues
	/// the waveform slope before decay is applied for the following frame.
	/// </summary>
	public void RenderFrame(Span<float> destination)
	{
		if (destination.Length != _residue.Length)
			throw new ArgumentException("Destination width does not match output channel count.", nameof(destination));

		for (int channel = 0; channel < _residue.Length; channel++)
		{
			_residue[channel] += _inertia[channel];
			destination[channel] += (float)_residue[channel];

			_residue[channel] *= _decay;
			_inertia[channel] *= _decay;

			if (Math.Abs(_residue[channel]) < 1e-12)
				_residue[channel] = 0.0;
			if (Math.Abs(_inertia[channel]) < 1e-12)
				_inertia[channel] = 0.0;
		}
	}
}

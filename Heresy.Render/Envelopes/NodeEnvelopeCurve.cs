using System;

using Heresy.Core.Envelopes;

namespace Heresy.Render.Envelopes;

/// <summary>
/// Immutable, sample-addressable piecewise-linear envelope. During sustain
/// the effective time wraps or reflects between node indices; on Note Off
/// traversal continues from its *actual* position and current direction.
/// Both values and finite retirement bounds are pure functions of active
/// frame, rate and (optional) note-off active frame.
/// </summary>
public sealed class NodeEnvelopeCurve : IEnvelopeCurve
{
	private readonly double[] _ticks;
	private readonly double[] _values;
	private readonly double _start;
	private readonly double _end;
	private readonly double _last;
	private readonly EnvelopeSustainLoopMode _mode;

	private readonly record struct Position(double Ticks, int Direction);

	public bool HasSustainLoop => _mode != EnvelopeSustainLoopMode.None;
	public bool IsZeroAfterCompletion => _values[^1] == 0.0;

	public NodeEnvelopeCurve(NodeEnvelopeDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		NodeEnvelopeDefinition.Validate(definition.Nodes,
			definition.SustainStartIndex,
			definition.SustainEndIndex,
			definition.SustainLoopMode);
		_ticks = new double[definition.Nodes.Count];
		_values = new double[definition.Nodes.Count];
		for (int i = 0; i < _ticks.Length; i++)
		{
			_ticks[i] = definition.Nodes[i].Time.Ticks;
			_values[i] = definition.Nodes[i].Value;
		}
		_start = _ticks[definition.SustainStartIndex];
		_end = _ticks[definition.SustainEndIndex];
		_last = _ticks[^1];
		_mode = definition.SustainLoopMode;
	}

	/// <summary>
	/// Two separately prepared frozen node curves can describe the exact
	/// same envelope even when their objects have different identities.
	/// Compare the complete captured sample-addressable shape without
	/// hashing, allocating, or consulting mutable authoring definitions.
	/// Only node curves can prove this equivalence; arbitrary scripted
	/// envelopes remain identity-only.
	/// </summary>
	internal bool HasSameFrozenShapeAs(NodeEnvelopeCurve other)
	{
		if (ReferenceEquals(this, other))
			return true;
		if (_mode != other._mode || _ticks.Length != other._ticks.Length
			|| _values.Length != other._values.Length
			|| BitConverter.DoubleToInt64Bits(_start)
				!= BitConverter.DoubleToInt64Bits(other._start)
			|| BitConverter.DoubleToInt64Bits(_end)
				!= BitConverter.DoubleToInt64Bits(other._end)
			|| BitConverter.DoubleToInt64Bits(_last)
				!= BitConverter.DoubleToInt64Bits(other._last))
			return false;
		for (int i = 0; i < _ticks.Length; i++)
			if (BitConverter.DoubleToInt64Bits(_ticks[i])
					!= BitConverter.DoubleToInt64Bits(other._ticks[i])
				|| BitConverter.DoubleToInt64Bits(_values[i])
					!= BitConverter.DoubleToInt64Bits(other._values[i]))
				return false;
		return true;
	}

	public double GetValue(
		long activeFrame, int sampleRate, long? noteOffActiveFrame)
	{
		ValidateFrame(activeFrame, sampleRate);
		if (noteOffActiveFrame is long off
			&& (off < 0 || off > activeFrame))
			throw new ArgumentOutOfRangeException(nameof(noteOffActiveFrame));

		double now = ToTicks(activeFrame, sampleRate);
		Position position;
		if (noteOffActiveFrame is long noteOff)
		{
			double atOff = ToTicks(noteOff, sampleRate);
			position = AdvanceAfterNoteOff(
				HeldPosition(atOff), now - atOff);
		}
		else
			position = HeldPosition(now);

		return Interpolate(position.Ticks);
	}

	public long? GetEndActiveFrameExclusiveWithoutNoteOff(int sampleRate)
	{
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (_mode != EnvelopeSustainLoopMode.None)
			return null;
		// Include the final node's own sample even when nonzero.
		double frames = Math.Floor(
			_last * sampleRate / TimeSpan.TicksPerSecond) + 1.0;
		if (!double.IsFinite(frames) || frames >= long.MaxValue)
			throw new OverflowException("Envelope end exceeds the frame timeline.");
		return (long)frames;
	}

	public long? GetEndActiveFrameExclusiveAfterNoteOff(
		long noteOffActiveFrame, int sampleRate)
	{
		ValidateFrame(noteOffActiveFrame, sampleRate);
		Position position = HeldPosition(
			ToTicks(noteOffActiveFrame, sampleRate));

		// A backward release finishes its trip to sustain start before
		// turning forward and traversing the remaining nodes. The end
		// frame is exclusive and includes the exact final node sample.
		double travel = position.Direction < 0
			? Math.Max(0.0, position.Ticks - _start)
				+ (_last - _start)
			: Math.Max(0.0, _last - position.Ticks);
		double frames = Math.Floor(
			travel * sampleRate / TimeSpan.TicksPerSecond) + 1.0;
		if (!double.IsFinite(frames) || frames >= long.MaxValue
			|| frames > long.MaxValue - (double)noteOffActiveFrame)
			throw new OverflowException("Envelope tail exceeds the frame timeline.");
		return checked(noteOffActiveFrame + (long)frames);
	}

	private Position HeldPosition(double ticks)
	{
		if (_mode == EnvelopeSustainLoopMode.None)
			return new Position(ticks, +1);
		if (ticks < _end)
			return new Position(ticks, +1);
		if (_start == _end)
			return new Position(_start, 0);

		double length = _end - _start;
		double passed = ticks - _end;
		if (_mode == EnvelopeSustainLoopMode.Forward)
			return new Position(_start + (passed % length), +1);

		double cycle = 2.0 * length;
		double part = passed % cycle;
		return part < length
			? new Position(_end - part, -1)
			: new Position(_start + (part - length), +1);
	}

	private Position AdvanceAfterNoteOff(
		Position position, double elapsedTicks)
	{
		if (position.Direction >= 0)
			return new Position(position.Ticks + elapsedTicks, +1);

		double backward = position.Ticks - _start;
		if (elapsedTicks <= backward)
			return new Position(position.Ticks - elapsedTicks, -1);
		return new Position(
			_start + elapsedTicks - backward, +1);
	}

	private double Interpolate(double ticks)
	{
		// A completed envelope holds its final value. Ending a release
		// trajectory does not imply zero gain; the enclosing voice owns
		// retirement, including the permanent-zero Volume special case.
		if (ticks > _last)
			return _values[^1];
		if (ticks <= _ticks[0])
			return _values[0];

		int found = Array.BinarySearch(_ticks, ticks);
		if (found >= 0)
			return _values[found];
		int right = ~found;
		int left = right - 1;
		double fraction =
			(ticks - _ticks[left]) / (_ticks[right] - _ticks[left]);
		return _values[left]
			+ (_values[right] - _values[left]) * fraction;
	}

	private static double ToTicks(long frame, int rate)
		=> frame * (double)TimeSpan.TicksPerSecond / rate;

	private static void ValidateFrame(long frame, int rate)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));
		if (rate <= 0)
			throw new ArgumentOutOfRangeException(nameof(rate));
	}
}

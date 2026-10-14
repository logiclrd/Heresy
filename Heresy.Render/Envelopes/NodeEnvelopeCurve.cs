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
		// The curve has no defined value after the final node, so it
		// becomes silent. Do not normalize/clamp the last *node* value:
		// it may deliberately be nonzero, negative, or above unity.
		if (ticks > _last)
			return 0.0;
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

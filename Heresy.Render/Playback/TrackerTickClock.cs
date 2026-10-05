using System;
using System.Collections.Generic;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;
using Heresy.Render.Timing;

namespace Heresy.Render.Playback;

/// <summary>
/// Piecewise tracker tick clock derived from tempo commands in a resolved
/// schedule. Tick position is continuous across tempo changes.
/// </summary>
public sealed class TrackerTickClock
{
	private readonly record struct Anchor(
		long Frame,
		double TickPosition,
		double Tempo);

	private readonly List<Anchor> _anchors = [];
	private readonly int _sampleRate;

	public TrackerTickClock(
		NoteSchedule schedule,
		int sampleRate,
		double initialTempo = SequencingConstants.DefaultTempo)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (!(initialTempo > 0.0)
			|| double.IsNaN(initialTempo)
			|| double.IsInfinity(initialTempo))
		{
			throw new ArgumentOutOfRangeException(nameof(initialTempo));
		}

		_sampleRate = sampleRate;

		long currentFrame = 0;
		double currentTicks = 0.0;
		double currentTempo = initialTempo;
		_anchors.Add(new Anchor(
			currentFrame,
			currentTicks,
			currentTempo));

		foreach (NoteEvent noteEvent in schedule)
		{
			long eventFrame = Math.Max(
				0,
				FrameTime.Ceiling(
					noteEvent.Offset.TimeOffset,
					sampleRate));

			foreach (NoteCommand command in noteEvent.Commands)
			{
				if (command is not SetTempoCommand tempo)
					continue;

				if (!(tempo.TicksPerDiachron > 0.0)
					|| double.IsNaN(tempo.TicksPerDiachron)
					|| double.IsInfinity(tempo.TicksPerDiachron))
				{
					throw new ArgumentOutOfRangeException(
						nameof(schedule),
						"Schedule contains an invalid tempo.");
				}

				if (eventFrame < currentFrame)
				throw new ArgumentException(
					"Schedule events must be ordered by time.",
					nameof(schedule));

				if (eventFrame > currentFrame)
				{
					currentTicks +=
						(eventFrame - currentFrame)
						/ (double)_sampleRate
						* currentTempo
						/ SequencingConstants.Diachron.TotalSeconds;
					currentFrame = eventFrame;
				}

				currentTempo = tempo.TicksPerDiachron;

				Anchor anchor = new(
					currentFrame,
					currentTicks,
					currentTempo);

				if (_anchors[^1].Frame == currentFrame)
					_anchors[^1] = anchor;
				else
					_anchors.Add(anchor);
			}
		}
	}

	public double GetElapsedTicks(
		long startFrame,
		long endFrame)
	{
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (endFrame < startFrame)
			throw new ArgumentOutOfRangeException(nameof(endFrame));

		return GetTickPosition(endFrame)
			- GetTickPosition(startFrame);
	}

	public double GetTickPosition(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		int low = 0;
		int high = _anchors.Count - 1;

		while (low < high)
		{
			int mid = low + (high - low + 1) / 2;
			if (_anchors[mid].Frame <= absoluteFrame)
				low = mid;
			else
				high = mid - 1;
		}

		Anchor anchor = _anchors[low];
		return anchor.TickPosition
			+ (absoluteFrame - anchor.Frame)
				/ (double)_sampleRate
				* anchor.Tempo
				/ SequencingConstants.Diachron.TotalSeconds;
	}
}

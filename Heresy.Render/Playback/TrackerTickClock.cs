using System;

using Heresy.Core.Sequencing;
using Heresy.Core.Timing;

namespace Heresy.Render.Playback;

/// <summary>
/// Playback-facing adapter over the shared continuous tracker-time map. It
/// exposes tracker row/tick coordinates to the IT compatibility layer; native
/// effects continue to use wall-clock time directly.
/// </summary>
public sealed class TrackerTickClock
{
	private readonly TrackerTimeMap _timeMap;
	private readonly int _sampleRate;

	public TrackerTickClock(
		NoteSchedule schedule,
		int sampleRate,
		double initialTempo = SequencingConstants.DefaultTempo)
	{
		ArgumentNullException.ThrowIfNull(schedule);
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		_sampleRate = sampleRate;
		_timeMap = new TrackerTimeMap(initialTempo);

		foreach (NoteEvent noteEvent in schedule)
		{
			double eventTimeSeconds =
				noteEvent.Offset.TimeOffset.TotalSeconds;

			foreach (NoteCommand command
				in noteEvent.Commands)
			{
				if (command is not SetTempoCommand
					&& command is not SetTempoRampCommand)
				{
					continue;
				}

				double delta =
					eventTimeSeconds
						- _timeMap.CurrentTimeSeconds;

				if (delta > 1e-7)
				{
					_timeMap.AppendConstantTime(delta);
				}
				else if (delta < -1e-7)
				{
					throw new ArgumentException(
						"Tempo commands overlap or are not ordered in time.",
						nameof(schedule));
				}

				switch (command)
				{
					case SetTempoCommand tempo:
						_timeMap.SetTempo(
							tempo.TicksPerDiachron);
						break;

					case SetTempoRampCommand ramp:
						_timeMap.AppendTempoRamp(
							ramp.EndingTempo,
							ramp.TrackerTicks);
						break;
				}
			}
		}
	}

	public TrackerTimeMap TimeMap => _timeMap;

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

		return _timeMap.GetTickAtTime(
			absoluteFrame / (double)_sampleRate);
	}

	public double GetTempoAtFrame(long absoluteFrame)
	{
		if (absoluteFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		return _timeMap.GetTempoAtTime(
			absoluteFrame / (double)_sampleRate);
	}

}

using System;

using Heresy.Core.Timing;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

namespace Heresy.Render.Playback;

/// <summary>
/// One live note voice. The same object may be attached to a physical playback
/// channel or migrated intact into the session's virtual-voice collection.
/// </summary>
public sealed class PlaybackVoice
{
	private sealed class ActiveVibrato
	{
		public required byte Speed { get; init; }
		public required TimeSpan TickDuration { get; init; }
		public required TimeSpan NextLegacyTickTime { get; set; }
	}

	private readonly float[] _previousOutputFrame;
	private readonly float[] _lastOutputFrame;
	private int _outputHistoryFrames;

	private long? _fadeStartFrame;
	private long? _fadeEndFrameExclusive;
	private TimeSpan? _fadeDuration;

	private byte _vibratoPhase;
	private ActiveVibrato? _activeVibrato;

	internal PlaybackVoice(
		ISound sound,
		SoundState soundState,
		NoteConfigurationSnapshot configuration,
		long startFrame,
		int outputChannelCount,
		int sampleRate,
		ResonantFilterParameters filterParameters,
		double noteVolume,
		double overallVolume)
	{
		Sound = sound ?? throw new ArgumentNullException(nameof(sound));
		SoundState = soundState ?? throw new ArgumentNullException(nameof(soundState));
		Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (outputChannelCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(outputChannelCount));

		StartFrame = startFrame;
		NoteVolume = noteVolume;
		OverallVolume = overallVolume;

		_previousOutputFrame = new float[outputChannelCount];
		_lastOutputFrame = new float[outputChannelCount];
		FilterState = new ResonantFilterState(
			outputChannelCount,
			sampleRate,
			filterParameters);
	}

	public ISound Sound { get; }

	public SoundState SoundState { get; }

	public NoteConfigurationSnapshot Configuration { get; }

	public ResonantFilterState FilterState { get; }

	public long StartFrame { get; }

	public double NoteVolume { get; internal set; }

	public double OverallVolume { get; internal set; }

	public bool IsFading => _fadeStartFrame.HasValue;

	public long? FadeStartFrame => _fadeStartFrame;

	public long? FadeEndFrameExclusive => _fadeEndFrameExclusive;

	public TimeSpan? FadeDuration => _fadeDuration;

	internal void ApplyNoteOff(long absoluteFrame, int sampleRate)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));

		long relativeFrame = absoluteFrame - StartFrame;
		SoundState.NoteOffTime = FrameTime.FrameStartTime(
			relativeFrame,
			sampleRate);
	}

	internal void BeginFade(long absoluteFrame, TimeSpan duration, int sampleRate)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (duration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(duration));

		_fadeStartFrame = absoluteFrame;
		_fadeDuration = duration;
		_fadeEndFrameExclusive = checked(
			absoluteFrame + FrameTime.Ceiling(duration, sampleRate));
	}

	internal double GetFadeGain(long absoluteFrame, int sampleRate)
	{
		if (!_fadeStartFrame.HasValue || !_fadeDuration.HasValue)
			return 1.0;

		long fadeStart = _fadeStartFrame.Value;
		if (absoluteFrame <= fadeStart)
			return 1.0;

		Int128 elapsed = (Int128)(absoluteFrame - fadeStart) * TimeSpan.TicksPerSecond;
		Int128 duration = (Int128)_fadeDuration.Value.Ticks * sampleRate;

		if (elapsed >= duration)
			return 0.0;

		return 1.0 - (double)elapsed / (double)duration;
	}

	internal void SetVibrato(
		long absoluteFrame,
		TimeSpan eventTime,
		double tempo,
		int sampleRate,
		byte speed,
		byte depth)
	{
		if (absoluteFrame < StartFrame)
			throw new ArgumentOutOfRangeException(nameof(absoluteFrame));
		if (!(tempo > 0.0) || double.IsNaN(tempo) || double.IsInfinity(tempo))
			throw new ArgumentOutOfRangeException(nameof(tempo));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));

		CommitVibratoPhaseThrough(eventTime);

		TimeSpan tickDuration = TimeSpan.FromSeconds(
			SequencingConstants.Diachron.TotalSeconds / tempo);
		if (tickDuration <= TimeSpan.Zero)
			throw new InvalidOperationException("Tempo produces a zero-length tracker tick.");

		_vibratoPhase = TrackerVibrato.AdvancePhase(
			_vibratoPhase,
			speed);

		_activeVibrato = new ActiveVibrato
		{
			Speed = speed,
			TickDuration = tickDuration,
			NextLegacyTickTime = eventTime + tickDuration,
		};

		SoundState.PitchTrajectory.SetCurve(
			absoluteFrame - StartFrame,
			new TrackerVibratoPitchCurve(
				_vibratoPhase,
				speed,
				depth,
				tickDuration,
				sampleRate));
	}

	internal void ClearPitchModulation(
		long absoluteFrame,
		TimeSpan eventTime)
	{
		if (absoluteFrame < StartFrame)
			return;

		CommitVibratoPhaseThrough(eventTime);
		_activeVibrato = null;

		SoundState.PitchTrajectory.SetMultiplier(
			absoluteFrame - StartFrame,
			1.0);
	}

	private void CommitVibratoPhaseThrough(TimeSpan eventTime)
	{
		ActiveVibrato? vibrato = _activeVibrato;
		if (vibrato is null)
			return;

		// A tracker tick exactly coincident with the command replacing/clearing
		// this effect belongs to the new command, not the old one.
		while (vibrato.NextLegacyTickTime < eventTime)
		{
			_vibratoPhase = TrackerVibrato.AdvancePhase(
				_vibratoPhase,
				vibrato.Speed);
			vibrato.NextLegacyTickTime += vibrato.TickDuration;
		}
	}

	internal void ObserveOutputFrame(ReadOnlySpan<float> outputFrame)
	{
		if (outputFrame.Length != _lastOutputFrame.Length)
			throw new ArgumentException("Output frame width does not match the voice.", nameof(outputFrame));

		if (_outputHistoryFrames != 0)
			_lastOutputFrame.CopyTo(_previousOutputFrame);

		outputFrame.CopyTo(_lastOutputFrame);
		if (_outputHistoryFrames < 2)
			_outputHistoryFrames++;
	}

	internal void AddCutTo(AntiClickTail tail)
	{
		ArgumentNullException.ThrowIfNull(tail);

		if (_outputHistoryFrames == 0)
			return;

		tail.AddCut(
			_previousOutputFrame,
			_lastOutputFrame,
			havePrevious: _outputHistoryFrames >= 2);
	}
}

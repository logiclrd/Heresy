using System;

using Heresy.Core.Filters;
using Heresy.Render.Envelopes;
using Heresy.Render.Sounds;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Sequencing;

namespace Heresy.Render.Playback;

/// <summary>
/// A flattened Pattern/Sequence acts as one logical instigating note for
/// note-volume and pitch automation, despite producing many independent audible voices.
/// Descendant voices retain this object by reference after the instigator's
/// logical channel has moved on to another note. A separate instance exists
/// for every invocation scope, so overlapping/nested starts never share
/// volume state merely because they use the same physical host.
/// </summary>
internal sealed class FlattenedSourceVolume
{
	private readonly TrackerTickClock _clock;
	private readonly int _sampleRate;
	// Every pitch segment is immutable once published. A descendant's
	// PitchTrajectory may query earlier frames when reconstructing its sample
	// position; replacing a mutable current-pitch value would retroactively
	// change previously rendered audio.
	private readonly List<(long StartFrame, PitchCurve Curve)> _pitchHistory =
	[
		(0, new ConstantPitchCurve(1.0)),
	];
	private readonly double _initialPitchMultiplier;
	private double? _tonePitchTargetRatio;
	private TrackerTonePortamentoCurve? _activeTonePitchCurve;
	private long _activeTonePitchStartFrame;
	private int _activeTonePitchTicksPerRow;
	private double? _continuousTonePitchAtBoundary;
	private readonly PlaybackOperatorCollection _operators = new();
	private LinearRowPlaybackOperator? _slide;
	private long _slideStartFrame;
	private int _slideTicksPerRow;
	private double _volume;

	public FlattenedSourceVolume(long scopeId, double volume,
		TrackerTickClock clock, int sampleRate,
		FilterInstance? eventFilter = null,
		double initialPitchMultiplier = 1.0)
	{
		if (scopeId <= 0)
			throw new ArgumentOutOfRangeException(nameof(scopeId));
		if (volume < 0 || volume > 1 || !double.IsFinite(volume))
			throw new ArgumentOutOfRangeException(nameof(volume));
		if (sampleRate <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleRate));
		if (!(initialPitchMultiplier > 0.0) || !double.IsFinite(initialPitchMultiplier))
			throw new ArgumentOutOfRangeException(nameof(initialPitchMultiplier));
		_initialPitchMultiplier = initialPitchMultiplier;
		ScopeId = scopeId;
		EventFilterConfiguration = eventFilter?.Clone();
		_volume = volume;
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
		_sampleRate = sampleRate;
	}

	public long ScopeId { get; }

	/// <summary>Optional authored event-local Filter on the flattened
	/// instigating note. Descendants clone it into their own DSP chains.</summary>
	public FilterInstance? EventFilterConfiguration { get; private set; }

	/// <summary>Only publish after all current descendant DSP chains have
	/// successfully prepared the same proposed controller configuration.</summary>
	internal void PublishEventFilterConfiguration(FilterInstance? configuration)
		=> EventFilterConfiguration = configuration?.Clone();

	private readonly Dictionary<(int Id, string Parameter), (IEnvelopeCurve Curve, long Frame)>
		_parameterEnvelopes = [];

	internal IReadOnlyDictionary<(int Id, string Parameter), (IEnvelopeCurve Curve, long Frame)>
		ParameterEnvelopes => _parameterEnvelopes;

	internal long? ParameterEnvelopeReleaseFrame { get; private set; }

	// A flattened instigator owns these action followers independently of
	// descendant DSP Envelopes. Its identity survives controlling-channel
	// replacement, voice NNA displacement and producer retirement.
	private readonly Dictionary<(int Id, string Parameter),
		(EnvelopePlaybackState State, FilterEnvelopeCompletionAction Action)>
		_completionActions = [];
	internal bool HasCompletionActions => _completionActions.Count != 0;
	internal bool IsCut { get; private set; }

	internal long NextCompletionFrame(long frame)
	{
		long next = long.MaxValue;
		foreach (var binding in _completionActions.Values)
			if (binding.State.GetCompletionFrameExclusive(frame) is long end)
				next = Math.Min(next, end);
		return next;
	}

	/// <summary>Consume all due actions once. Cut wins over Off, and an
	/// existing external Off makes another Off inert. The follower is not
	/// a descendant's individual DSP timeline.</summary>
	internal FilterEnvelopeCompletionAction ConsumeCompletion(long frame)
	{
		FilterEnvelopeCompletionAction result = FilterEnvelopeCompletionAction.None;
		List<(int Id, string Parameter)>? due = null;
		foreach (var pair in _completionActions)
		{
			if (pair.Value.State.GetCompletionFrameExclusive(frame) is not long end
				|| end > frame)
				continue;
			(due ??= []).Add(pair.Key);
			if (pair.Value.Action == FilterEnvelopeCompletionAction.Cut)
				result = FilterEnvelopeCompletionAction.Cut;
			else if (pair.Value.Action == FilterEnvelopeCompletionAction.Off
				&& !ParameterEnvelopeReleaseFrame.HasValue
				&& result == FilterEnvelopeCompletionAction.None)
				result = FilterEnvelopeCompletionAction.Off;
		}
		if (due is not null)
			foreach (var key in due)
				_completionActions.Remove(key);
		return result;
	}

	internal void MarkCut()
	{
		IsCut = true;
		_completionActions.Clear();
	}


	internal string ValidateParameterEnvelope(int stageId, string parameterName)
	{
		if (EventFilterConfiguration is null)
			throw new InvalidOperationException("No flattened note-event Filter is selected.");
		FilterStageDefinition stage = EventFilterConfiguration.Definition.GetStage(stageId);
		FilterParameterDescriptor descriptor = FilterCatalogue.GetParameters(stage.Kind)
			.FirstOrDefault(p => string.Equals(p.Name, parameterName,
				StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException($"Unknown note Filter parameter '{parameterName}'.");
		if (!descriptor.IsModulatable)
			throw new ArgumentException($"Parameter {stage.Name}.{descriptor.Name} is structural or not numeric.");
		return descriptor.Name.ToUpperInvariant();
	}

	internal void PublishParameterEnvelope(int stageId, string parameter,
		IEnvelopeCurve? curve, long frame,
		FilterEnvelopeCompletionAction completionAction = FilterEnvelopeCompletionAction.None)
	{
		var key = (stageId, parameter);
		if (curve is null) _parameterEnvelopes.Remove(key);
		else _parameterEnvelopes[key] = (curve, frame);
		_completionActions.Remove(key);
		if (curve is not null && completionAction != FilterEnvelopeCompletionAction.None)
		{
			EnvelopePlaybackState state = new(curve, frame, _sampleRate);
			if (ParameterEnvelopeReleaseFrame.HasValue)
				state.NoteOff(Math.Max(frame, ParameterEnvelopeReleaseFrame.Value));
			_completionActions[key] = (state, completionAction);
		}
	}

	internal void ClearParameterEnvelopes()
	{
		_parameterEnvelopes.Clear();
		_completionActions.Clear();
	}

	internal void ReleaseParameterEnvelopes(long frame)
	{
		if (ParameterEnvelopeReleaseFrame.HasValue) return;
		ParameterEnvelopeReleaseFrame = frame;
		// A second Off from a completion action would restart nothing and
		// must not act on later notes, so discard its pending follower now.
		List<(int Id, string Parameter)>? discard = null;
		foreach (var pair in _completionActions)
		{
			if (pair.Value.Action == FilterEnvelopeCompletionAction.Off)
				(discard ??= []).Add(pair.Key);
			else
				pair.Value.State.NoteOff(frame);
		}
		if (discard is not null)
			foreach (var key in discard) _completionActions.Remove(key);
	}

	/// <summary>
	/// Current transposition relative to the pitch baked into this flattened
	/// invocation's SequencingContext. This changes descendant sound pitch,
	/// not the tempo or the musical deadlines of its internal Pattern.
	/// History is retained because voices may reconstruct earlier source frames.
	/// </summary>
	internal double ReadPitchRatio(long frame)
	{
		if (frame < 0)
			throw new ArgumentOutOfRangeException(nameof(frame));
		int low = 0, high = _pitchHistory.Count - 1;
		while (low < high)
		{
			int middle = low + (high - low + 1) / 2;
			if (_pitchHistory[middle].StartFrame <= frame)
				low = middle;
			else
				high = middle - 1;
		}
		var segment = _pitchHistory[low];
		return segment.Curve.GetMultiplier(frame - segment.StartFrame);
	}

	private void PublishPitchCurve(long frame, PitchCurve curve)
	{
		if (frame < _pitchHistory[^1].StartFrame)
			throw new InvalidOperationException(
				"Flattened source pitch changes must be chronological.");
		if (frame == _pitchHistory[^1].StartFrame)
			_pitchHistory[^1] = (frame, curve);
		else
			_pitchHistory.Add((frame, curve));
	}

	private double ReadContinuousTonePitch(long frame)
	{
		if (_activeTonePitchCurve is null)
			return _continuousTonePitchAtBoundary ?? ReadPitchRatio(frame);
		return _activeTonePitchCurve.GetContinuousMultiplierForRowTime(
			ToneRowTime(frame));
	}

	private double ToneRowTime(long frame)
		=> Math.Clamp(_clock.GetElapsedTicks(
			_activeTonePitchStartFrame,
			Math.Max(frame, _activeTonePitchStartFrame)),
			0, _activeTonePitchTicksPerRow);

	/// <summary>
	/// A note on the caller's Gxx row is a new logical target, not a restart.
	/// FF is Heresy's explicit instantaneous-target mode. G00 and Lxx
	/// recall its byte from the same existing tracker memory.
	/// </summary>
	internal void SetTonePortamento(long frame, int ticksPerRow,
		double linearUnitsPerTick, double? targetPitchMultiplier,
		bool glissando, bool instant)
	{
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (frame < 0 || !double.IsFinite(linearUnitsPerTick)
			|| linearUnitsPerTick < 0)
			throw new ArgumentOutOfRangeException(nameof(linearUnitsPerTick));
		if (targetPitchMultiplier.HasValue)
		{
			if (!(targetPitchMultiplier.Value > 0)
				|| !double.IsFinite(targetPitchMultiplier.Value))
				throw new ArgumentOutOfRangeException(nameof(targetPitchMultiplier));
			_tonePitchTargetRatio = targetPitchMultiplier.Value
				/ _initialPitchMultiplier;
		}
		if (!_tonePitchTargetRatio.HasValue)
			return;
		double continuous = ReadContinuousTonePitch(frame);
		_activeTonePitchCurve = null;
		_continuousTonePitchAtBoundary = null;
		if (instant)
		{
			PublishPitchCurve(frame,
				new ConstantPitchCurve(_tonePitchTargetRatio.Value));
			return;
		}
		TrackerTonePortamentoCurve curve = new(
			continuous, _tonePitchTargetRatio.Value, linearUnitsPerTick,
			_clock, frame, ticksPerRow, glissando);
		_activeTonePitchCurve = curve;
		_activeTonePitchStartFrame = frame;
		_activeTonePitchTicksPerRow = ticksPerRow;
		PublishPitchCurve(frame, curve);
	}

	internal void ClearTonePortamento(long frame)
	{
		if (_activeTonePitchCurve is null)
			return;
		double audible = ReadPitchRatio(frame);
		double continuous = ReadContinuousTonePitch(frame);
		_activeTonePitchCurve = null;
		_continuousTonePitchAtBoundary = continuous;
		PublishPitchCurve(frame, new ConstantPitchCurve(audible));
	}

	public double Read(long frame)
	{
		if (_slide is not null)
			_slide.Update(frame / (double)_sampleRate, RowTime(frame));
		return Math.Clamp(_volume +
			_operators.GetPersistentTotalDelta(PlaybackParameter.NoteVolume),
			0, 1);
	}

	public void Set(long frame, double volume)
	{
		if (volume < 0 || volume > 1 || !double.IsFinite(volume))
			throw new ArgumentOutOfRangeException(nameof(volume));
		Cancel();
		_volume = volume;
	}

	public double Adjust(long frame, double trackerUnits)
	{
		if (!double.IsFinite(trackerUnits))
			throw new ArgumentOutOfRangeException(nameof(trackerUnits));
		double volume = Math.Clamp(Read(frame) + trackerUnits / 64.0, 0, 1);
		Set(frame, volume);
		return volume;
	}

	public void Slide(long frame, int ticksPerRow, double trackerUnitsPerTick)
	{
		if (ticksPerRow <= 0)
			throw new ArgumentOutOfRangeException(nameof(ticksPerRow));
		if (!double.IsFinite(trackerUnitsPerTick))
			throw new ArgumentOutOfRangeException(nameof(trackerUnitsPerTick));
		Commit(frame);
		_slideStartFrame = frame;
		_slideTicksPerRow = ticksPerRow;
		_slide = new LinearRowPlaybackOperator(
			PlaybackParameter.NoteVolume,
			trackerUnitsPerTick * Math.Max(0, ticksPerRow - 1) / 64.0,
			ticksPerRow,
			commitOnExpire: true);
		_operators.Add(_slide);
	}

	public double Clear(long frame)
	{
		Commit(frame);
		return _volume;
	}

	private double RowTime(long frame)
		=> Math.Clamp(_clock.GetElapsedTicks(
			_slideStartFrame, Math.Max(frame, _slideStartFrame)),
			0, _slideTicksPerRow);

	private void Commit(long frame)
	{
		if (_slide is null)
			return;
		var delta = _operators.Expire(_slide,
			frame / (double)_sampleRate, RowTime(frame));
		_volume = Math.Clamp(_volume +
			delta[PlaybackParameter.NoteVolume], 0, 1);
		_slide = null;
	}

	private void Cancel()
	{
		if (_slide is null)
			return;
		_operators.Remove(_slide);
		_slide = null;
	}
}

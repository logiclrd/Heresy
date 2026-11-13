using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Instruments;
using Heresy.Render.Samples;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using Heresy.Render.Spatial;
using Heresy.Render.Timing;

namespace Heresy.Render.FmSynthesis;

public sealed class FmSynthSound : IStreamingFiniteSound, IFirPreparationSound
{
	private sealed class SourceVoice
	{
		public SourceVoice(SoundInvocation invocation, double originalOuterFactor,
			double originalFrequencyHz, int? boundInstrumentToneSlot)
		{
			Invocation = invocation;
			// The invocation retains its original state/configuration. A
			// compatible referenced Synth revision can replace its compiled
			// sound without rewinding the child or its pitch trajectory.
			Sound = invocation.Sound;
			OriginalOuterFactor = originalOuterFactor;
			OriginalFrequencyHz = originalFrequencyHz;
			BoundInstrumentToneSlot = boundInstrumentToneSlot;
			Pitch = new FmSourcePitchCurve();
			ActiveVolumeEnvelope = invocation.Configuration.Envelopes.Volume;
			invocation.State.PitchTrajectory.SetCurve(0, Pitch);
		}
		public SoundInvocation Invocation { get; }
		public ISound Sound { get; set; }
		/// <summary>Instrument tone-table index bound when this voice was
		/// created, not recomputed from a retuned pitch on the PCM thread.</summary>
		public int? BoundInstrumentToneSlot { get; }
		public double OriginalOuterFactor { get; }
		// Child pitch and speed are *never* retroactively rescaled. Edits
		// alter only the forward-integrated FmSourcePitchCurve.
		// The frequency baseline must survive an authored retune and a
		// worker-prepared Multi onset replacement.
		public double OriginalFrequencyHz { get; private set; }
		public void RetainOriginalFrequencyBasis(SourceVoice original)
			=> OriginalFrequencyHz = original.OriginalFrequencyHz;
		// Prepared Multi oscillators may change their local phase clock.
		// Instrument Volume envelopes belong to the continuing Sample
		// and must instead follow its original invocation frame clock.
		public long EnvelopeFrameOffset { get; private set; }
		public void RetainEnvelopeClock(SourceVoice original,
			long oldFrame, long newFrame)
			=> EnvelopeFrameOffset = checked(
				original.EnvelopeFrameOffset + oldFrame - newFrame);
		public FmSourcePitchCurve Pitch { get; }
		public StreamingFilterChain? Filters { get; set; }
		public ResonantFilterState? FilterEnvelopeState { get; set; }
		public bool InputFinished { get; set; }
		public long? EndFrame { get; set; }
		// Gain is applied after tone Filters; a completed permanent-zero
		// Volume envelope may silence this branch before other releases end.
		public long? PostGainSilenceEnd { get; set; }

		// The Instrument invocation's Sample/PitchTrajectory remain physically
		// owned by the original note. Only an independently frozen node Volume
		// function can be replaced in the first bounded live-edit case.
		public IEnvelopeCurve? ActiveVolumeEnvelope { get; private set; }
		private IEnvelopeCurve? _outgoingVolumeEnvelope;
		private double? _outgoingFrozenVolumeLevel;
		private double _lastVolumeLevel = 1.0;
		private long _volumeFadeStart = -1;

		public void AdoptPreparedNodeVolume(NodeEnvelopeCurve next, long frame,
			int sampleRate)
		{
			if (_volumeFadeStart >= 0
				&& frame - _volumeFadeStart < LiveTransitionFrames(sampleRate))
			{
				// A second edit in flight begins at the last audible gain;
				// don't build an unbounded chain of stale envelope objects.
				_outgoingVolumeEnvelope = null;
				_outgoingFrozenVolumeLevel = _lastVolumeLevel;
			}
			else
			{
				_outgoingVolumeEnvelope = ActiveVolumeEnvelope;
				_outgoingFrozenVolumeLevel = null;
			}
			ActiveVolumeEnvelope = next;
			_volumeFadeStart = frame;
		}

		public double EvaluateVolume(
			long frame, long envelopeFrame, int sampleRate, long? released)
		{
			double gain = ActiveVolumeEnvelope?.GetValue(
				envelopeFrame, sampleRate, released) ?? 1.0;
			if (_volumeFadeStart >= 0)
			{
				long elapsed = frame - _volumeFadeStart;
				if (elapsed >= LiveTransitionFrames(sampleRate))
				{
					_outgoingVolumeEnvelope = null;
					_outgoingFrozenVolumeLevel = null;
					_volumeFadeStart = -1;
				}
				else if (elapsed >= 0)
				{
					double oldGain = _outgoingFrozenVolumeLevel
						?? _outgoingVolumeEnvelope?.GetValue(
							envelopeFrame, sampleRate, released) ?? gain;
					double weight = MultiTransitionWeight(_volumeFadeStart, frame, sampleRate);
					gain = oldGain * (1.0 - weight) + gain * weight;
				}
			}
			_lastVolumeLevel = gain;
			return gain;
		}

		/// <summary>A shorter edited release cannot erase the retiring
		/// curve's audible contribution during its bounded gain fade.</summary>
		public long? GetOutgoingVolumeReleaseEnd(
			long envelopeNoteOffFrame, int sampleRate)
		{
			if (_volumeFadeStart < 0)
				return null;
			int frames = LiveTransitionFrames(sampleRate);
			long fadeEnd = _volumeFadeStart >= long.MaxValue - frames
				? long.MaxValue : _volumeFadeStart + frames;
			if (_outgoingFrozenVolumeLevel is double held)
				return held != 0.0 ? fadeEnd : null;
			if (_outgoingVolumeEnvelope is not { } outgoing)
				return null;
			// An ended curve with a nonzero last node keeps providing
			// that gain until the crossfade ends, not just its release EOF.
			if (!outgoing.IsZeroAfterCompletion)
				return fadeEnd;
			long? end = outgoing.GetEndActiveFrameExclusiveAfterNoteOff(
				envelopeNoteOffFrame, sampleRate);
			return end.HasValue
				? Math.Min(fadeEnd, checked(end.Value - EnvelopeFrameOffset))
				: fadeEnd;
		}
	}


	// One sample-rate-scaled transition policy for compatible live edits.
	private const int MultiGrowthReservePerNode = 4;
	private static int LiveTransitionFrames(int sampleRate)
		=> FmLiveTransitionClock.Frames(sampleRate);
	private static double MultiTransitionWeight(long start, long frame,
		int sampleRate)
		=> FmLiveTransitionClock.Weight(start, frame, sampleRate);
	private static double AdditiveOutputTransitionWeight(long start,
		long frame, int durationFrames)
		=> FmLiveTransitionClock.WeightFrames(start, frame, durationFrames);

	private sealed class MultiVoice
	{
		public MultiVoice(SoundInvocation invocation, double ratio,
			FmMultiNode definition, int index, double initialMixGain,
			bool started)
		{
			Invocation = invocation;
			Sound = (FmSynthSound)invocation.Sound;
			RatioFrom = RatioTo = ratio;
			MixFrom = MixTo = initialMixGain;
			Definition = definition;
			Index = index;
			HasStarted = started;
		}
		public SoundInvocation Invocation { get; }
		// A compatible child revision changes compiled code, not the child state.
		public FmSynthSound Sound { get; set; }
		public FmMultiNode Definition { get; }
		public int Index { get; }
		// A dormant, preallocated child is first audible at StartFrame. Its
		// local playback clock starts at zero then; existing children stay
		// at StartFrame=0 and are never rewound or reconstructed.
		public long StartFrame { get; private set; }
		public bool HasStarted { get; private set; }
		private double RatioFrom { get; set; }
		private double RatioTo { get; set; }
		private long RatioEditFrame { get; set; }
		private int _sampleRate = FmMultiNode.ReferenceSampleRate;
		public void UseSampleRate(int sampleRate)
		{
			if (sampleRate > 0) _sampleRate = sampleRate;
		}
		private double MixFrom { get; set; }
		private double MixTo { get; set; }
		private long MixEditFrame { get; set; }
		public int SkipFrames { get; set; }
		public int FadeFrames { get; set; }
		public bool Initialized { get; set; }
		public long? SilenceEnd { get; set; }
		// Only the prepared replacement holds the outgoing voice, so
		// overlapping phase edits cannot accumulate unbounded fade chains.
		public MultiVoice? OnsetFadeFrom { get; private set; }
		public long OnsetFadeStartFrame { get; private set; }

		public void BeginPreparedOnsetFade(MultiVoice outgoing, long frame)
		{
			if (outgoing.OnsetFadeFrom is not null)
				throw new InvalidOperationException(
					"An overlapping Multi onset fade must finish before replacement.");
			StartFrame = outgoing.StartFrame;
			HasStarted = true;
			OnsetFadeFrom = outgoing;
			_sampleRate = outgoing._sampleRate;
			OnsetFadeStartFrame = frame;
		}
		public void FinishOnsetFade() => OnsetFadeFrom = null;

		/// <summary>The outgoing branch is structurally incapable of
		/// contributing after the last crossfade frame, even if its
		/// own Sample or Filter would otherwise play indefinitely.</summary>
		public long OnsetFadeEndFrame => OnsetFadeStartFrame >=
			long.MaxValue - LiveTransitionFrames(_sampleRate)
			? long.MaxValue : OnsetFadeStartFrame + LiveTransitionFrames(_sampleRate);

		/// <summary>Keep the proved last audible contribution of the
		/// retired branch. The incoming Sample can have reached EOF before
		/// the fade ended; adopting only its EOF would retroactively
		/// truncate the note's actual mixed audio.</summary>
		public void CompleteOnsetFade()
		{
			if (OnsetFadeFrom is not { } outgoing)
				return;
			if (SilenceEnd is long incomingEnd)
			{
				long outgoingBound = outgoing.SilenceEnd is long knownEnd
					? Math.Min(knownEnd, OnsetFadeEndFrame)
					: OnsetFadeEndFrame;
				SilenceEnd = Math.Max(incomingEnd, outgoingBound);
			}
			OnsetFadeFrom = null;
		}

		private double Interpolate(double from, double to,
			long startFrame, long frame, bool pitch)
		{
			if (from == to) return to;
			double w = MultiTransitionWeight(startFrame, frame, _sampleRate);
			return pitch
				? FmLiveTransitionClock.Frequency(from, to, w)
				: from + (to - from) * w;
		}

		public double RatioAt(long frame)
			=> Interpolate(RatioFrom, RatioTo, RatioEditFrame, frame, true);
		public double MixAt(long frame)
			=> Interpolate(MixFrom, MixTo, MixEditFrame, frame, false);
		public bool CanAffectOutput(long frame)
			=> HasStarted && (MixTo != 0.0 || MixAt(frame) != 0.0);

		public void Retune(double ratio, double mix, long frame)
		{
			double currentRatio = RatioAt(frame);
			double currentMix = MixAt(frame);
			if (!HasStarted && mix > 0.0)
			{
				HasStarted = true;
				StartFrame = frame;
				// This child has never run. Its first sample is local 0,
				// faded in alongside the already-running constituents.
				Initialized = false;
				SilenceEnd = null;
				currentMix = 0;
			}
			if (ratio != RatioTo)
			{
				RatioFrom = currentRatio;
				RatioTo = ratio;
				RatioEditFrame = frame;
			}
			if (mix != MixTo)
			{
				MixFrom = currentMix;
				MixTo = mix;
				MixEditFrame = frame;
			}
		}
	}

	/// <summary>Fresh Multi children and replacement arrays constructed off
	/// the PCM thread. Existing histories are inserted only when the entire
	/// voice has passed atomic preflight, not read by this worker.</summary>
	private sealed class PreparedMultiExpansion(
		FmSynthSound revision, MultiVoice[]?[] slots, int[] capacities,
		ulong seed, double pitch, double speed, int sampleRate)
	{
		public MultiVoice[]?[] Slots { get; } = slots;
		public bool Matches(FmSynthSound next, FmSynthState state)
		{
			if (!ReferenceEquals(revision, next) || seed != state.NoiseSeed
				|| pitch != state.BoundPitch || speed != state.BoundSpeed
				|| sampleRate != state.SampleRate)
				return false;
			for (int i = 0; i < capacities.Length; i++)
				if (capacities[i] != state.Multis[i].Length)
					return false;
			return true;
		}
	}

	/// <summary>Latest-wins per-invocation mailbox. The producer captures
	/// only immutable definitions and scalar state at its own boundary;
	/// the existing off-PCM FIR scheduler also prepares these new children.
	/// Superseded results cannot be published by an older worker request.</summary>
	private sealed class MultiExpansionMailbox(int nodeCount)
		: IRealtimePreparationWorkItem
	{
		private readonly object _gate = new();
		private readonly int[] _capacities = new int[nodeCount];
		private readonly int[] _workCapacities = new int[nodeCount];
		// Captured from the PCM owner; the worker never visits live voices.
		private readonly bool[] _rephase = new bool[nodeCount];
		private readonly bool[] _workRephase = new bool[nodeCount];
		private FmSynthSound? _revision;
		private ulong _seed;
		private int _sampleRate;
		private double _pitch, _speed;
		private long _version, _workingVersion;
		private bool _pending, _queued;
		private PreparedMultiExpansion? _ready;

		public void Offer(FmSynthSound original, FmSynthSound next,
			FmSynthState state)
		{
			lock (_gate)
			{
				bool identical = ReferenceEquals(_revision, next)
					&& _seed == state.NoiseSeed && _pitch == state.BoundPitch
					&& _speed == state.BoundSpeed && _sampleRate == state.SampleRate;
				for (int i = 0; identical && i < _capacities.Length; i++)
					identical = _capacities[i] == state.Multis[i].Length;
				if (identical && (_pending || _queued || _ready is not null))
					return;
				_revision = next;
				_seed = state.NoiseSeed;
				_sampleRate = state.SampleRate;
				_pitch = state.BoundPitch;
				_speed = state.BoundSpeed;
				for (int i = 0; i < _capacities.Length; i++)
				{
					_capacities[i] = state.Multis[i].Length;
					_rephase[i] = original.NeedsMultiOnsetReplacement(next, state, i);
				}
				// Invalidate work from an earlier generation. A running
				// worker checks this version after every new child.
				_version++;
				_pending = true;
				_ready = null;
			}
		}

		/// <summary>Invalidate pending/ready work and release the frozen
		/// revision immediately when the invocation is retired, superseded
		/// or accepted. A running worker finishes its current child only,
		/// then detects cancellation at the next checkpoint.</summary>
		public void Cancel()
		{
			lock (_gate)
			{
				_version++;
				_revision = null;
				_pending = false;
				_ready = null;
				// _queued remains true until the queued worker slot has
				// actually been consumed, avoiding duplicate admissions.
			}
		}

		public void Queue(FirPreparationScheduler scheduler)
		{
			lock (_gate)
			{
				if (!_pending || _queued || _revision is null)
					return;
				// Bounded admission, nonblocking on the PCM owner. If the
				// worker queue is full, keep the request in this mailbox
				// and retry at the next audio-block boundary.
				if (!scheduler.TryEnqueueMultiExpansion(this))
					return;
				_pending = false;
				_queued = true;
			}
		}

		public PreparedMultiExpansion? TakeReady()
		{
			lock (_gate)
			{
				PreparedMultiExpansion? ready = _ready;
				_ready = null;
				return ready;
			}
		}

		private bool IsCurrent(long version)
		{
			lock (_gate)
				return _revision is not null && _version == version;
		}

		/// <summary>Report cancellation without retaining partial arrays
		/// or enqueueing an obsolete follow-up. A newer offered revision is
		/// retried through the same capped admission path.</summary>
		private void DiscardStale(long version, FirPreparationScheduler scheduler,
			int builtChildren, long startingAllocation)
		{
			lock (_gate)
			{
				if (_version == version)
				{
					// Only an explicit Cancel() can make an otherwise
					// unchanged version invalid; keep the guard defensive.
					_revision = null;
					_pending = false;
				}
				_queued = false;
				_pending = _revision is not null;
				_ready = null;
			}
			scheduler.RecordCancelledMultiExpansion(builtChildren,
				Math.Max(0, GC.GetAllocatedBytesForCurrentThread()
					- startingAllocation));
			Queue(scheduler);
		}

		public void Prepare(FirPreparationScheduler scheduler)
		{
			FmSynthSound next;
			double pitch, speed;
			ulong seed;
			int sampleRate;
			long version;
			lock (_gate)
			{
				if (_revision is null)
				{
					_queued = false;
					scheduler.RecordCancelledMultiExpansion();
					return;
				}
				version = _version;
				_workingVersion = version;
				_pending = false;
				next = _revision;
				seed = _seed;
				sampleRate = _sampleRate;
				pitch = _pitch;
				speed = _speed;
				Array.Copy(_capacities, _workCapacities, _capacities.Length);
				Array.Copy(_rephase, _workRephase, _rephase.Length);
			}
			long beforeTicks = Stopwatch.GetTimestamp();
			long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
			int addedChildren = 0;
			// One cancellation delegate per worker batch, shared by every
			// nested child constructor; never one allocation per voice.
			Func<bool> cancelRequested = () => !IsCurrent(version);
			MultiVoice[]?[] slots = new MultiVoice[]?[next._nodes.Length];
			for (int i = 0; i < next._nodes.Length; i++)
			{
				if (!IsCurrent(version))
				{
					DiscardStale(version, scheduler, addedChildren, beforeBytes);
					return;
				}
				int target = next._multiRatios[i].Length;
				int retained = _workCapacities[i];
				bool rephase = _workRephase[i];
				if (target <= retained && !rephase)
					continue;
				FmSynthSound input = next._nodes[i].MultiInput
					?? throw new InvalidOperationException(
						"Multi expansion requires a prepared child sound.");
				FmMultiNode multi = (FmMultiNode)next._nodes[i].Definition;
				MultiVoice[] voices = new MultiVoice[Math.Max(target, retained)];
				for (int j = 0; j < target; j++)
				{
					if (j < retained && !rephase)
						continue;
					if (!IsCurrent(version))
					{
						DiscardStale(version, scheduler, addedChildren, beforeBytes);
						return;
					}
					double ratio = next._multiRatios[i][j];
					// A descendant may have a large nested Multi tree.
					// Check the same immutable revision token throughout
					// child construction rather than only *between* the
					// outer children. No active DSP state crosses threads.
					SoundInvocation invocation;
					try
					{
						invocation = input.CreateInvocationForLiveExpansion(
							pitch * ratio, speed,
							cancelRequested,
							scheduler.AfterNestedMultiChildStateCreated);
					}
					catch (OperationCanceledException) when (!IsCurrent(version))
					{
						DiscardStale(version, scheduler, addedChildren, beforeBytes);
						return;
					}
					// Only the detached replacement receives fresh OnePole
					// channel histories. The active voice is never touched by
					// the worker. PCM publication will copy its live integrators
					// into these already allocated states after preflight.
					if (rephase && j < retained)
						input.PrepareOnePoleOnsetStates(invocation.State, sampleRate);
					MultiVoice child = new(invocation, ratio, multi, j,
						initialMixGain: rephase && j < retained
							? (multi.GainCompensation ? 1.0 / target : 1.0) : 0.0,
						started: rephase && j < retained);
					child.Invocation.State.SetNoiseSeed(MultiHash(seed
						^ ((ulong)(uint)multi.Id << 32)
						^ (ulong)(uint)(j + 1)));
					voices[j] = child;
					addedChildren++;
					scheduler.AfterMultiConstituentPrepared?.Invoke();
				}
				slots[i] = voices;
			}
			if (!IsCurrent(version))
			{
				DiscardStale(version, scheduler, addedChildren, beforeBytes);
				return;
			}
			PreparedMultiExpansion candidate = new(next, slots,
				(int[])_workCapacities.Clone(), seed, pitch, speed, sampleRate);
			bool stale;
			lock (_gate)
			{
				stale = version != _version || _revision is null;
				if (!stale)
				{
					_ready = candidate;
					_queued = false;
				}
			}
			if (stale)
			{
				DiscardStale(version, scheduler, addedChildren, beforeBytes);
				return;
			}
			scheduler.RecordMultiExpansion(
				addedChildren,
				Stopwatch.GetTimestamp() - beforeTicks,
				Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - beforeBytes));
		}

		public void MarkFailed(FirPreparationScheduler scheduler)
		{
			lock (_gate)
			{
				_ready = null;
				_pending = _revision is not null
					&& _version != _workingVersion;
				_queued = false;
			}
			Queue(scheduler);
		}
	}

	private sealed class FmSynthState : SoundState
	{
		public FmSynthState(
			int nodeCount,
			int oscillatorCount,
			SourceVoice?[] sources, MultiVoice[][] multis,
			double boundPitch, double boundSpeed,
			bool allowMultiExpansion)
		{
			MultiExpansion = allowMultiExpansion
				? new MultiExpansionMailbox(nodeCount) : null;
			BoundPitch = boundPitch;
			BoundSpeed = boundSpeed;
			Values = new double[nodeCount];
			AudioValues = new double[nodeCount];
			SilentFrom = new long?[nodeCount];
			Phases = new double[oscillatorCount];
			Filters = new FmFilterNodeProcessor.State?[nodeCount];
			Sources = sources;
			Multis = multis;
		}

		public double[] Values { get; }
		public double[] AudioValues { get; }
		public long?[] SilentFrom { get; }
		public double[] Phases { get; }
		public FmFilterNodeProcessor.State?[] Filters { get; }
		public SourceVoice?[] Sources { get; private set; }
		public MultiVoice[][] Multis { get; private set; }
		public MultiExpansionMailbox? MultiExpansion { get; }
		public PreparedMultiExpansion? PreparedExpansion { get; set; }
		public void ReplaceMultiVoices(int index, MultiVoice[] voices)
			=> Multis[index] = voices;
		public double BoundPitch { get; }
		public double BoundSpeed { get; }
		public RenderContext? MonoContext { get; set; }
		public RenderContext? StereoChildContext { get; set; }
		public long NextFrame { get; set; }
		public int SampleRate { get; set; }

		// An audition-only stateless graph parameter change uses one
		// scalar output hold-and-crossfade; no second graph, integrator or
		// PCM buffer. Repeated edits use the last already-blended output.
		public double? StatelessOutputFadeFrom { get; private set; }
		public long StatelessOutputFadeStartFrame { get; private set; }
		public void StartStatelessOutputFade(int outputIndex)
		{
			if (NextFrame == 0 || SampleRate == 0)
				return;
			// Capture the *audible graph output*, not a child control:
			// stateless Operators can combine independently phased voices.
			// Mid-fade edits start from the last blended output.
			StatelessOutputFadeFrom = AudioValues[outputIndex];
			StatelessOutputFadeStartFrame = NextFrame;
		}
		public void ClearStatelessOutputFade() => StatelessOutputFadeFrom = null;

		// The frozen, continuing Multi's whole signal is evaluated once per
		// frame. A final Multiply maps it to (constant * signal), while Add
		// maps it to (signal + constant). Morph these two affine mappings,
		// never holding the old waveform or recreating its DSP history.
		public bool HasAdditiveOutputTransition { get; private set; }
		public int AdditiveSignalIndex { get; private set; }
		private double _additiveFromScale, _additiveFromOffset;
		private double _additiveToScale, _additiveToOffset;
		private long _additiveStartFrame;
		private int _additiveDurationFrames;

		public void StartAdditiveOutputTransition(int signalIndex, int outputIndex,
			double fromScale, double fromOffset, double toScale, double toOffset)
		{
			if (NextFrame == 0 || SampleRate == 0)
				return;
			if (HasAdditiveOutputTransition && AdditiveSignalIndex == signalIndex)
			{
				// A second edit during the first transition inherits the
				// mapping applied to the *last rendered* moving signal.
				double w = AdditiveOutputTransitionWeight(
					_additiveStartFrame, NextFrame - 1, _additiveDurationFrames);
				fromScale = _additiveFromScale * (1 - w) + _additiveToScale * w;
				fromOffset = _additiveFromOffset * (1 - w) + _additiveToOffset * w;
			}
			else if (StatelessOutputFadeFrom is not null)
			{
				// A prior 64-frame waveform hold may still be in progress.
				// Join its last audible value without freezing the Multi for
				// the next, longer DC-offset smoothing interval.
				fromOffset = AudioValues[outputIndex]
					- fromScale * AudioValues[signalIndex];
			}
			AdditiveSignalIndex = signalIndex;
			_additiveFromScale = fromScale;
			_additiveFromOffset = fromOffset;
			_additiveToScale = toScale;
			_additiveToOffset = toOffset;
			_additiveStartFrame = NextFrame;
			_additiveDurationFrames = LiveTransitionFrames(SampleRate);
			HasAdditiveOutputTransition = true;
			ClearStatelessOutputFade();
		}

		public double ApplyAdditiveOutputTransition(long frame, double signal)
		{
			long elapsed = frame - _additiveStartFrame;
			if (elapsed >= _additiveDurationFrames)
			{
				ClearAdditiveOutputTransition();
				return signal * _additiveToScale + _additiveToOffset;
			}
			if (elapsed < 0)
				return signal * _additiveFromScale + _additiveFromOffset;
			double w = AdditiveOutputTransitionWeight(
				_additiveStartFrame, frame, _additiveDurationFrames);
			return signal * (_additiveFromScale * (1 - w) + _additiveToScale * w)
				+ (_additiveFromOffset * (1 - w) + _additiveToOffset * w);
		}

		public void ClearAdditiveOutputTransition()
			=> HasAdditiveOutputTransition = false;

		public override void SetNoiseSeed(ulong seed)
		{
			base.SetNoiseSeed(seed);
			for (int i = 0; i < Sources.Length; i++)
				if (Sources[i] is { } child)
					child.Invocation.State.SetNoiseSeed(unchecked(
						seed ^ (0x9E3779B97F4A7C15UL * (ulong)(i + 1))));
			foreach (MultiVoice[] voices in Multis)
				foreach (MultiVoice voice in voices)
				{
					voice.Invocation.State.SetNoiseSeed(MultiHash(seed
						^ ((ulong)(uint)voice.Definition.Id << 32)
						^ (ulong)(uint)(voice.Index + 1)));
					voice.Initialized = false;
					voice.SilenceEnd = null;
					voice.FinishOnsetFade();
				}
		}
		public bool ChildEndChanged { get; set; }
		// Preserve an external Multiply-envelope release through a Multi
		// boundary without changing the inner subgraph's note-off event.
		public long? InheritedAudioReleaseEnd { get; set; }
		public bool HasReleasingSubtree { get; set; }
		public long? ReleasingSubtreeEnd { get; set; }

		public void Reset(int sampleRate)
		{
			if ((Sources.Any(source => source is not null)
				|| Multis.Any(voices => voices.Length != 0)) && SampleRate != 0)
				throw new InvalidOperationException(
					"Seek or format change requires PrepareReplay at a producer/control boundary.");
			ClearRenderHistory(sampleRate);
		}

		public void ReplaceForReplay(FmSynthState rebuilt, int sampleRate)
		{
			// Rebind rather than rewinding the old PitchTrajectory: child
			// integrated positions, script-selected tones and filter histories
			// are invocation-local and must start from fresh state.
			MultiExpansion?.Cancel();
			Sources = rebuilt.Sources;
			Multis = rebuilt.Multis;
			PreparedExpansion = null;
			MonoContext = rebuilt.MonoContext;
			StereoChildContext = rebuilt.StereoChildContext;
			SetNoiseSeed(NoiseSeed); // propagate original invocation identity
			ClearRenderHistory(sampleRate);
		}

		private void ClearRenderHistory(int sampleRate)
		{
			Array.Clear(Values);
			Array.Clear(AudioValues);
			ClearStatelessOutputFade();
			ClearAdditiveOutputTransition();
			Array.Clear(SilentFrom);
			Array.Clear(Phases);
			Array.Clear(Filters); // never reuse histories after rewind/rate change
			MultiExpansion?.Cancel();
			PreparedExpansion = null;
			foreach (MultiVoice[] voices in Multis)
				foreach (MultiVoice voice in voices)
				{
					voice.Initialized = false;
					voice.SilenceEnd = null;
					voice.FinishOnsetFade();
				}
			NextFrame = 0;
			ChildEndChanged = false;
			InheritedAudioReleaseEnd = null;
			HasReleasingSubtree = false;
			ReleasingSubtreeEnd = null;
			SampleRate = sampleRate;
		}
	}

	private sealed record CompiledNode(
		FmSynthNode Definition,
		int[] InputIndices,
		int OscillatorIndex,
		FmOscillatorCurvatureShaper? Curvature,
		IEnvelopeCurve? Envelope,
		FmFilterNodeProcessor? Filter,
		ISound? Source,
		FmSynthSound? MultiInput);

	private readonly FmSynthGraph _graph;
	private readonly CompiledNode[] _nodes;
	// Single output oscillators are common in Multi child subgraphs. Their
	// stateful DSP still uses EvaluateOscillator, without the generic graph
	// dispatch and redundant audible Operator routing on each sample.
	private readonly CompiledNode? _singleOutputOscillator;
	// Audition-only smoothing of changes on an audible graph path uses a
	// bounded output fade, not a second voice or reconstructed DSP state.
	// Eligible nodes are stateless controls, compatible direct Sample
	// leaves, continuing Filters, and stable single-Oscillator Multi
	// wrappers. Oscillator/Source FM inputs are not audio paths. Multi
	// distribution, constituent shape, growth and onset revisions keep
	// their separate history and crossfade transactions.
	private readonly bool _canFadeBoundedOutput;
	private readonly bool[] _statelessAudioPathNodes;
	// All target ratios are calculated by the preparation worker, never
	// inside PCM publication or per-frame child rendering.
	private readonly double[][] _multiRatios;
	private readonly int[] _multiReserve;
	private readonly bool _hasMulti;
	private readonly bool _allowMultiExpansion;
	// A Multi constituent has a separate onset-rebase transaction. Do not
	// broaden changed Volume adoption inside that transaction as a side effect.
	private readonly bool _isMultiInputSubgraph;
	private readonly int _outputIndex;
	private readonly int _oscillatorCount;
	private readonly NewNotePolicy _newNotePolicy;
	private readonly PreparedToneFilterStageFactory? _preparedChildFilterFactory;
	private readonly bool _hasSources;
	private readonly double _maximumAudioPathTailSeconds;
	private const double C4FrequencyHz = 440.0 * 0.59460355750136053336; // 2^(-9/12)

	public FmSynthSound(
		FmSynthGraph graph,
		IEnvelopeCurveResolver envelopeResolver,
		NewNotePolicy? newNotePolicy = null,
		ISoundResolver? soundResolver = null,
		PreparedToneFilterStageFactory? preparedChildFilterFactory = null,
		bool reserveMultiHeadroom = false,
		bool enableLiveMultiExpansion = false)
		: this(graph, envelopeResolver, newNotePolicy, soundResolver,
			preparedChildFilterFactory, reserveMultiHeadroom,
			enableLiveMultiExpansion, isMultiInputSubgraph: false)
	{
	}

	private FmSynthSound(
		FmSynthGraph graph,
		IEnvelopeCurveResolver envelopeResolver,
		NewNotePolicy? newNotePolicy,
		ISoundResolver? soundResolver,
		PreparedToneFilterStageFactory? preparedChildFilterFactory,
		bool reserveMultiHeadroom,
		bool enableLiveMultiExpansion,
		bool isMultiInputSubgraph)
	{
		ArgumentNullException.ThrowIfNull(graph);
		ArgumentNullException.ThrowIfNull(envelopeResolver);
		_graph = graph;
		_allowMultiExpansion = reserveMultiHeadroom || enableLiveMultiExpansion;
		_isMultiInputSubgraph = isMultiInputSubgraph;

		_newNotePolicy =
			newNotePolicy
				?? NewNotePolicy.Cut;
		_preparedChildFilterFactory = preparedChildFilterFactory;

		Dictionary<int, FmSynthNode> byId =
			graph.Nodes.ToDictionary(
				node => node.Id);
		List<FmSynthNode> ordered = [];
		HashSet<int> visited = [];

		Visit(graph.OutputNodeId);

		_hasSources = ordered.Any(node => node is FmSoundSourceNode or FmMultiNode);
		_hasMulti = ordered.Any(node => node is FmMultiNode);
		if (ordered.Any(node => node is FmSoundSourceNode) && soundResolver is null)
			throw new NotSupportedException(
				"ISound-fed Synth nodes require the prepared child-invocation resolver.");
		// Input modulation is evaluated in graph order and drives the
		// invocation-local child pitch trajectory once per rendered frame.

		Dictionary<int, int> indexById = [];
		for (int index = 0; index < ordered.Count; index++)
			indexById.Add(ordered[index].Id, index);

		List<CompiledNode> compiled = [];
		// Preparation cache only: per-frame DSP never computes exponentials.
		Dictionary<double, FmOscillatorCurvatureShaper> curvatureByK = [];
		int oscillatorIndex = 0;
		foreach (FmSynthNode node in ordered)
		{
			int[] inputs = node is FmMultiNode
				? [] : node.InputNodeIds.Select(id => indexById[id]).ToArray();

			IEnvelopeCurve? curve = null;
			if (node is FmEnvelopeNode envelope)
			{
				envelopeResolver.TryResolve(
					envelope.EnvelopeId,
					out curve);
			}

			int phaseIndex =
				node is FmOscillatorNode
					? oscillatorIndex++
					: -1;

			FmOscillatorCurvatureShaper? curvature = null;
			if (node is FmOscillatorNode oscillator && oscillator.Curvature != 0.0)
			{
				if (!curvatureByK.TryGetValue(oscillator.Curvature, out curvature))
				{
					curvature = new FmOscillatorCurvatureShaper(oscillator.Curvature);
					curvatureByK.Add(oscillator.Curvature, curvature);
				}
			}

			ISound? sourceSound = null;
			if (node is FmSoundSourceNode sourceNode
				&& !sourceNode.SourceId.IsNone)
			{
				// Resolving is done on the frozen-song preparation thread,
				// never in CreateInvocation or EvaluateFrame.
				soundResolver!.TryResolve(sourceNode.SourceId,
					mixdown: false, out sourceSound);
				if (_allowMultiExpansion
					&& sourceSound is InstrumentSound directInstrument)
					directInstrument.PrepareDirectSampleVolumesForLiveAudition();
			}
			FmSynthSound? multiInput = node is FmMultiNode { InputNodeId: int multiId }
				? new FmSynthSound(new FmSynthGraph(graph.Nodes, multiId),
					envelopeResolver, newNotePolicy, soundResolver,
					preparedChildFilterFactory, reserveMultiHeadroom: false,
					enableLiveMultiExpansion: _allowMultiExpansion,
					isMultiInputSubgraph: true)
				: null;
			compiled.Add(
				new CompiledNode(
					node,
					inputs,
					phaseIndex,
					curvature,
					curve,
					node is FmFilterNode filter
						? new FmFilterNodeProcessor(filter, indexById,
							preparedChildFilterFactory?.SampleRate) : null,
					sourceSound, multiInput));
		}

		_nodes = [.. compiled];
		_singleOutputOscillator = _nodes.Length == 1
			&& _nodes[0].Definition is FmOscillatorNode
			&& _nodes[0].InputIndices.Length == 0
			? _nodes[0] : null;
		// Counter-based Noise has no mutable history. Filter nodes do:
		// a continuing configured Filter may take the same scalar
		// *final-output* fade only if its existing DSP history remains
		// compatible and active. Source PCM has a separate bound-identity
		// check. All of these decisions use prepared graph metadata.
		_canFadeBoundedOutput = _nodes.All(node => node.Definition is
			FmOscillatorNode or FmConstantNode or FmOperatorNode
			or FmEnvelopeNode or FmNoiseNode
			|| node.Definition is FmSoundSourceNode
				&& node.Source is SampleSound
			|| node.Definition is FmFilterNode
					{ AudioInputNodeId: not null, Configuration: not null }
				&& node.Filter is not null
			|| node.Definition is FmMultiNode { InputNodeId: not null }
				&& node.MultiInput is FmSynthSound multiChild
				&& multiChild._oscillatorCount > 0
				&& multiChild._nodes.All(child => child.Definition is
					FmOscillatorNode or FmConstantNode or FmOperatorNode
					or FmNoiseNode or FmEnvelopeNode
					|| child.Definition is FmSoundSourceNode
						&& child.Source is SampleSound
					|| child.Definition is FmFilterNode
						{ AudioInputNodeId: not null, Configuration: not null }
						&& child.Filter is not null));
		_multiRatios = _nodes.Select(node => node.Definition is FmMultiNode multi
			? multi.GetFrequencyRatios() : Array.Empty<double>()).ToArray();
		_multiReserve = new int[_nodes.Length];
		_outputIndex = indexById[graph.OutputNodeId];
		_statelessAudioPathNodes = new bool[_nodes.Length];
		if (_canFadeBoundedOutput)
			MarkAudibleNode(_outputIndex);
		_oscillatorCount = oscillatorIndex;
		int actualConstituents = CountMultiVoices(graph.OutputNodeId);
		if (actualConstituents > FmMultiNode.MaximumVoices)
			throw new NotSupportedException("Multi graph exceeds the 128-voice realtime limit.");
		// Reserve dormant children ONLY for an explicitly live-editable
		// audition. Normal pattern/sequence playback, ordinary ad-hoc
		// rendering and offline export use exactly the authored voice count.
		// Optional reserve: at most four per top-level, non-nested Multi,
		// bounded by the remaining 128-voice budget. Nested Multi child
		// graphs never recursively reserve growth headroom.
		if (reserveMultiHeadroom)
		{
			int free = Math.Min(16, FmMultiNode.MaximumVoices - actualConstituents);
			for (int i = 0; i < _nodes.Length && free > 0; i++)
				if (_nodes[i].Definition is FmMultiNode
					&& _nodes[i].MultiInput is { _hasMulti: false }
					&& _multiRatios[i].Length < FmMultiNode.MaximumVoices)
				{
					_multiReserve[i] = Math.Min(free, Math.Min(
						MultiGrowthReservePerNode,
						FmMultiNode.MaximumVoices - _multiRatios[i].Length));
					free -= _multiReserve[i];
				}
		}


		// Follow actual audible inputs through Operators and the Filter
		// audio input (always the first compiled Filter input). Do NOT
		// traverse Filter numeric-control ports or Oscillator/Source pitch
		// multiplier inputs: those change DSP controls, not the audible
		// signal being multiplied or mixed at the final output.
		void MarkAudibleNode(int index)
		{
			if (_statelessAudioPathNodes[index])
				return;
			_statelessAudioPathNodes[index] = true;
			if (_nodes[index].Definition is FmOperatorNode)
				foreach (int input in _nodes[index].InputIndices)
					MarkAudibleNode(input);
			else if (_nodes[index].Definition is FmFilterNode
					{ AudioInputNodeId: not null })
				MarkAudibleNode(_nodes[index].InputIndices[0]);
		}

		int CountMultiVoices(int id)
		{
			FmSynthNode node = byId[id];
			if (node is FmMultiNode multi)
				return multi.InputNodeId is int input
					? checked(multi.GetFrequencyRatios().Length * CountMultiVoices(input)) : 0;
			if (node is FmOperatorNode operation)
				return operation.InputNodeIds.Sum(CountMultiVoices);
			if (node is FmFilterNode filter && filter.AudioInputNodeId is int audio)
				return CountMultiVoices(audio);
			return 1;
		}
		_maximumAudioPathTailSeconds = GetMaximumAudioPathTailSeconds();

		void Visit(int id)
		{
			if (!visited.Add(id))
				return;

			FmSynthNode node = byId[id];
			// Multi's input is compiled as a distinct, independently-instantiated
			// subgraph, never evaluated in the parent's voice state.
			if (node is not FmMultiNode)
				foreach (int inputId in node.InputNodeIds)
					Visit(inputId);
			ordered.Add(node);
		}
	}

	/// <summary>Worker-only preparation for a detached replacement
	/// that will inherit live OnePole integrators at publication. Building
	/// FilterInstance/StreamingFilterChain histories can allocate and MUST
	/// never happen in the realtime audio callback.</summary>
	private void PrepareOnePoleOnsetStates(SoundState state, int sampleRate)
	{
		FmSynthState prepared = ValidateState(state);
		if (sampleRate <= 0)
			throw new InvalidOperationException(
					"Multi onset history preparation requires a valid sample rate.");
		for (int i = 0; i < _nodes.Length; i++)
			if (_nodes[i].Filter is { } filter)
				prepared.Filters[i] = filter.CreateState(sampleRate);
	}

	/// <summary>Preflight both independently owned child graphs and all
	/// preconstructed filter histories. The following PCM publication
	/// copies scalar integrators only after this whole-voice admission.</summary>
	private bool CanTransferPreparedOnsetHistoriesTo(
		FmSynthSound next, SoundState outgoing, SoundState incoming,
		long oldFrame, long newFrame)
	{
		FmSynthState before = ValidateState(outgoing);
		FmSynthState after = ValidateState(incoming);
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Filter is { } original
				&& (next._nodes[i].Filter is not { } revised
					|| !original.CanTransferPreparedOnePoleHistoryTo(
						revised, before.Filters[i], after.Filters[i])))
				return false;
			if (_nodes[i].Definition is FmSoundSourceNode
				&& !CanTransferPreparedDirectSample(
					before.Sources[i], after.Sources[i], oldFrame, newFrame))
				return false;
		}
		return true;
	}

	/// <summary>A directly Sample-backed Source may have a connected
	/// modulation input, provided its connection and modulation semantics
	/// are unchanged. The whole-tree graph admission checks every upstream
	/// oscillator/operator/filter separately; its newly phased modulation
	/// controls only subsequent Sample rate, never its historical playhead.
	/// The original Sample asset and frequency basis survive the edit.</summary>
	private static bool CanRephaseDirectSampleSource(
		CompiledNode old, CompiledNode revised)
	{
		return old.Definition is FmSoundSourceNode source
			&& revised.Definition is FmSoundSourceNode next
			&& source.SourceId == next.SourceId
			&& source.MultiplierNodeId == next.MultiplierNodeId
			&& source.FrequencyHz > 0.0
			&& next.FrequencyHz > 0.0
			&& double.IsFinite(next.FrequencyHz / source.FrequencyHz)
			&& source.Constant == next.Constant
			&& source.ExponentialMultiplier == next.ExponentialMultiplier
			&& (old.Source is SampleSound sample
				&& revised.Source is SampleSound prepared
				&& sample.CanSharePreparedOnsetTimelineWith(prepared)
				&& sample.SourceChannelCount is >= 1 and <= 2
				// Compare just the frozen pitch-to-table-slot geometry
				// initially. Each existing note has already snapshotted
				// its selected tone slot at binding; before enqueueing any
				// worker we compare that specific tone, and after worker
				// completion the terminal Sample/Envelope proof still runs.
				|| old.Source is InstrumentSound instrument
					&& revised.Source is InstrumentSound preparedInstrument
					&& instrument.CanShareFrozenSelectionGeometryWith(
						preparedInstrument));
	}

	/// <summary>The same immutable Volume curve can be recompiled into
	/// separate objects by successive frozen-song Synth publications.
	/// Permit that one stateless-equivalent shape while keeping every
	/// other authored note policy and selected envelope binding exactly
	/// unchanged. Script envelopes still require shared object identity.
	/// This runs on pre-captured render data without allocations or
	/// authoring-model lookups.</summary>
	private static bool CanSharePreparedVolumeConfiguration(
		NoteConfigurationSnapshot previous, NoteConfigurationSnapshot revised)
	{
		if (previous == revised)
			return true;
		if (previous.NewNotePolicy != revised.NewNotePolicy
			|| previous.NoteFadeDuration != revised.NoteFadeDuration
			|| previous.NewNoteFadeDuration != revised.NewNoteFadeDuration)
			return false;
		EnvelopeConfigurationSnapshot a = previous.Envelopes;
		EnvelopeConfigurationSnapshot b = revised.Envelopes;
		if (!ReferenceEquals(a.Pitch, b.Pitch)
			|| !ReferenceEquals(a.Panning, b.Panning)
			|| !ReferenceEquals(a.Filter, b.Filter))
			return false;
		return ReferenceEquals(a.Volume, b.Volume)
			|| a.Volume is NodeEnvelopeCurve oldNode
				&& b.Volume is NodeEnvelopeCurve newNode
				&& oldNode.HasSameFrozenShapeAs(newNode);
	}

	private static bool CanTransferPreparedDirectSample(
		SourceVoice? original, SourceVoice? replacement,
		long oldFrame, long nextFrame)
	{
		if (original?.Sound is not SampleSound sourceSample
			|| replacement?.Sound is not SampleSound preparedSample
			|| !sourceSample.CanSharePreparedOnsetTimelineWith(preparedSample)
			|| original.Invocation.State is not SampleSoundState previous
			|| replacement.Invocation.State is not SampleSoundState destination
			|| original.EndFrame.HasValue || original.InputFinished
			|| original.Filters is not null || replacement.Filters is not null
			|| original.FilterEnvelopeState is not null
			|| replacement.FilterEnvelopeState is not null
			|| previous.NaturalEndFrameExclusive.HasValue
			|| previous.HasNoteOffTime
			|| !(original.OriginalFrequencyHz > 0.0)
			|| !(replacement.OriginalFrequencyHz > 0.0)
			|| !double.IsFinite(replacement.OriginalFrequencyHz
				/ original.OriginalFrequencyHz)
			|| original.OriginalOuterFactor != replacement.OriginalOuterFactor
			|| original.Pitch.LastFrame != oldFrame - 1
			|| oldFrame < 0 || nextFrame < 0
			|| !previous.PitchTrajectory.CanSeedPreparedStreamingPosition
			|| !destination.PitchTrajectory.CanSeedPreparedStreamingPosition)
			return false;
		var envelopes = original.Invocation.Configuration.Envelopes;
		if (envelopes.Pitch is not null
			|| envelopes.Filter is not null || envelopes.Panning is not null
			|| !CanSharePreparedVolumeConfiguration(
				original.Invocation.Configuration,
				replacement.Invocation.Configuration))
			return false;
		// The new Source must inherit the old Instrument's envelope clock
		// even if its Multi phase offset has changed. Reject overflow
		// before the allocation-free publication/transfer boundary.
		long delta = oldFrame - nextFrame;
		long oldOffset = original.EnvelopeFrameOffset;
		if ((delta > 0 && oldOffset > long.MaxValue - delta)
			|| (delta < 0 && oldOffset < long.MinValue - delta))
			return false;
		// The active Sample position, not a hypothetical new onset
		// position, is the source of truth. No history window, rate
		// projection, or skip-dependent preflight is required.
		return true;
	}

	/// <summary>Allow a new onset only when histories are independent
	/// unmodulated oscillator phases and, optionally, unbound OnePole IIR
	/// integrators that can be copied into worker-prepared states.
	/// Constants/operators are stateless; other DSP families remain
	/// ineligible until their history and timeline semantics are defined.</summary>
	private static bool CanRephaseOscillatorGraph(
		FmSynthSound original, FmSynthSound revised)
	{
		if (original._oscillatorCount != revised._oscillatorCount
			|| original._outputIndex != revised._outputIndex
			|| original._nodes.Length != revised._nodes.Length)
			return false;
		bool hasTimeDependentInput = false;
		for (int i = 0; i < original._nodes.Length; i++)
		{
			CompiledNode a = original._nodes[i];
			CompiledNode b = revised._nodes[i];
			if (a.Definition.Id != b.Definition.Id
				|| a.Definition.GetType() != b.Definition.GetType()
				|| a.OscillatorIndex != b.OscillatorIndex
				|| !a.InputIndices.SequenceEqual(b.InputIndices))
				return false;
			switch (a.Definition)
			{
				case FmOscillatorNode oscillator
					when b.Definition is FmOscillatorNode updated
						&& oscillator.MultiplierNodeId is null
						&& updated.MultiplierNodeId is null
						&& oscillator.Waveform == updated.Waveform
						&& oscillator.FrequencyHz == updated.FrequencyHz
						&& oscillator.Minimum == updated.Minimum
						&& oscillator.Maximum == updated.Maximum
						&& oscillator.Curvature == updated.Curvature
						&& oscillator.Constant == updated.Constant
						&& oscillator.ExponentialMultiplier
							== updated.ExponentialMultiplier:
					hasTimeDependentInput = true;
					break;
				case FmConstantNode constant
					when b.Definition is FmConstantNode updated
						&& constant.Value == updated.Value:
					break;
				case FmOperatorNode operation
					when b.Definition is FmOperatorNode updated
						&& operation.Operation == updated.Operation:
					break;
				case FmSoundSourceNode
					when CanRephaseDirectSampleSource(a, b):
					// Samples preserve their running playhead. They do
					// not by themselves justify a new Multi onset;
					// oscillators elsewhere in this graph may still
					// be rephased through a prepared replacement.
					break;
				case FmFilterNode filterNode
					when b.Definition is FmFilterNode revisedNode
						&& filterNode.TailSeconds == revisedNode.TailSeconds
						&& a.Filter is { } filter
						&& b.Filter is { } revisedFilter
						&& filter.CanRephasePreparedOnePoleHistoryTo(revisedFilter):
					break;
				default:
					return false;
			}
		}
		return hasTimeDependentInput;
	}

	/// <summary>Only direct Sample voices with a current continuing
	/// playhead can join a worker-prepared oscillator onset replacement.
	/// This is independent of old rate history and the new phase skip:
	/// Sample playback itself will never be rephased.</summary>
	private bool HasContinuingDirectSampleState(
		SoundState activeState, long oldLocalFrame)
	{
		FmSynthState active = ValidateState(activeState);
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmSoundSourceNode)
				continue;
			SourceVoice? source = active.Sources[i];
			if (oldLocalFrame <= 0
				|| source?.Sound is not SampleSound
				|| source.Pitch.LastFrame != oldLocalFrame - 1
				|| source.InputFinished || source.EndFrame.HasValue
				|| source.Invocation.State.HasNoteOffTime
				|| source.Invocation.State.NaturalEndFrameExclusive.HasValue)
				return false;
		}
		return true;
	}

	/// <summary>Every *running* Instrument Source has already selected
	/// a tone when its invocation was created outside PCM. Only that
	/// selected tone must match the revision's frozen table. Other notes
	/// and their tone definitions may have changed. No selection, log2,
	/// resolver call or allocation occurs on the realtime path.</summary>
	private bool HasCompatibleBoundInstrumentTones(
		FmSynthSound revised, SoundState activeState)
	{
		FmSynthState active = ValidateState(activeState);
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmSoundSourceNode
				|| _nodes[i].Source is not InstrumentSound original)
				continue;
			if (revised._nodes[i].Source is not InstrumentSound updated
				|| active.Sources[i] is not SourceVoice voice
				|| !original.CanShareFrozenBoundToneWith(
					updated, voice.BoundInstrumentToneSlot))
				return false;
		}
		return true;
	}

	/// <summary>A frozen tone-map comparison proves which Source IDs the
	/// data-driven Instrument will select, not what a separate song
	/// resolver supplies for those IDs. Once the worker has constructed
	/// replacement invocations, reject an incompatible terminal PCM or
	/// captured release configuration *without retrying* that phase edit.
	/// Independent scalar edits can then publish on the original voice.
	/// All checks are nonallocating, and none select or resolve a tone.</summary>
	private bool CanSharePreparedInstrumentBindingsWith(
		SoundState outgoing, SoundState replacement)
	{
		FmSynthState before = ValidateState(outgoing);
		FmSynthState after = ValidateState(replacement);
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmSoundSourceNode
				|| _nodes[i].Source is not InstrumentSound)
				continue;
			SourceVoice? old = before.Sources[i];
			SourceVoice? prepared = after.Sources[i];
			if (old?.BoundInstrumentToneSlot != prepared?.BoundInstrumentToneSlot
				|| old?.Sound is not SampleSound sourceSample
				|| prepared?.Sound is not SampleSound preparedSample
				|| !sourceSample.CanSharePreparedOnsetTimelineWith(preparedSample)
				|| !CanSharePreparedVolumeConfiguration(
					old.Invocation.Configuration, prepared.Invocation.Configuration)
				|| old.Invocation.ToneFilters is not null
				|| prepared.Invocation.ToneFilters is not null
				|| old.Invocation.State is not SampleSoundState oldSample
				|| prepared.Invocation.State is not SampleSoundState newSample)
				return false;
		}
		return true;
	}

	/// <summary>Held audition: compatible oscillator phases may rephase
	/// through worker-prepared states. Direct Samples, including connected
	/// rate modulation and matching flattened Instrument Volume-release
	/// envelopes, retain their physical playheads and release clocks.
	/// Source reassignment, incompatible Instrument selection, tone Filters,
	/// other Envelope histories and nested Multi remain excluded.</summary>
	private bool NeedsMultiOnsetReplacement(
		FmSynthSound next, FmSynthState live, int index)
	{
		if (live.MultiExpansion is null || live.HasNoteOffTime
			|| _nodes[index].Definition is not FmMultiNode original
			|| next._nodes[index].Definition is not FmMultiNode updated
			|| (!original.RandomizePhase && !updated.RandomizePhase)
			|| _multiRatios[index].Length != next._multiRatios[index].Length
			|| !_multiRatios[index].SequenceEqual(next._multiRatios[index])
			|| _nodes[index].MultiInput is not FmSynthSound originalChild
			|| next._nodes[index].MultiInput is not FmSynthSound updatedChild
			|| !CanRephaseOscillatorGraph(originalChild, updatedChild))
			return false;

		// The frozen map alone cannot prove that the new resolver yields
		// the same decoded Sample or immutable envelope. After the worker
		// resolves the actual selected tone for each prepared voice, refuse
		// an incompatible new onset and allow independent scalar changes
		// to adopt without stalling behind an impossible worker transfer.
		if (live.PreparedExpansion?.Matches(next, live) == true
			&& live.PreparedExpansion.Slots[index] is MultiVoice[] preparedSlots)
		{
			for (int j = 0; j < _multiRatios[index].Length; j++)
			{
				MultiVoice outgoing = live.Multis[index][j];
				if (!outgoing.HasStarted || !outgoing.Initialized
					|| (outgoing.Definition.RandomizePhase
						== updated.RandomizePhase
						&& (!(outgoing.Definition.RandomizePhase
							|| updated.RandomizePhase)
							|| outgoing.Definition.CycleLength
								== updated.CycleLength)))
					continue;
				if (preparedSlots[j] is MultiVoice replacement
					&& !originalChild.CanSharePreparedInstrumentBindingsWith(
						outgoing.Invocation.State, replacement.Invocation.State))
					return false;
			}
		}
		foreach (MultiVoice voice in live.Multis[index])
		{
			if (!voice.HasStarted || !voice.Initialized
				|| (voice.Definition.RandomizePhase == updated.RandomizePhase
					&& (!(voice.Definition.RandomizePhase || updated.RandomizePhase)
						|| voice.Definition.CycleLength == updated.CycleLength)))
				continue;
			// Only the oscillator phase changes. Direct Sample children
			// retain their current accumulated position regardless of the
			// new Multi skip, or any prior parent/Source pitch automation.
			// A noncontinuing Sample state remains ineligible for this
			// mixed-graph replacement, without starving scalar edits.
			foreach (MultiVoice active in live.Multis[index])
			{
				if (!active.HasStarted || !active.Initialized)
					continue;
				long local = live.NextFrame - active.StartFrame;
				if (local < 0 || local > long.MaxValue - active.SkipFrames
					|| !originalChild.HasContinuingDirectSampleState(
						active.Invocation.State, local + active.SkipFrames)
					|| !originalChild.HasCompatibleBoundInstrumentTones(
						updatedChild, active.Invocation.State))
					return false;
			}
			return true;
		}
		return false;
	}

	private bool CanCommitMultiOnsetReplacement(
		FmSynthSound next, FmSynthState live, int index)
	{
		if (!NeedsMultiOnsetReplacement(next, live, index))
			return true;
		if (live.PreparedExpansion?.Matches(next, live) != true
			|| live.PreparedExpansion.Slots[index] is not MultiVoice[] preparedSlots)
			return false;
		// Do not chain crossfades or interrupt an unfinished ratio/mix
		// transition. The held voice keeps the latest pending edit and
		// retries at a later block boundary; the worker result is reused.
		double[] ratios = next._multiRatios[index];
		FmMultiNode layout = (FmMultiNode)next._nodes[index].Definition;
		double mix = layout.GainCompensation ? 1.0 / ratios.Length : 1.0;
		for (int j = 0; j < ratios.Length; j++)
		{
			MultiVoice voice = live.Multis[index][j];
			if (voice.OnsetFadeFrom is not null
				|| voice.RatioAt(live.NextFrame) != ratios[j]
				|| voice.MixAt(live.NextFrame) != mix)
				return false;
			bool changedOnset = voice.HasStarted && voice.Initialized
				&& (voice.Definition.RandomizePhase != layout.RandomizePhase
					|| ((voice.Definition.RandomizePhase || layout.RandomizePhase)
						&& voice.Definition.CycleLength != layout.CycleLength));
			if (changedOnset)
			{
				if (preparedSlots[j] is not MultiVoice replacement)
					return false;
				// Compute the exact authored new skip using the same pure
				// arithmetic as normal note onset before checking each
				// preallocated Source/Filter destination.
				double pitch = live.PitchMultiplier * voice.RatioAt(live.NextFrame)
					* live.PitchTrajectory.GetMultiplier(live.NextFrame);
				next.InitializeMultiOnset(layout, live.NoiseSeed, index,
					replacement, pitch, live.PlaybackSpeedMultiplier, live.SampleRate);
				long local = live.NextFrame - voice.StartFrame;
				if (local < 0 || local > long.MaxValue -
					Math.Max(voice.SkipFrames, replacement.SkipFrames)
					|| !voice.Sound.CanTransferPreparedOnsetHistoriesTo(
						replacement.Sound, voice.Invocation.State,
						replacement.Invocation.State,
						local + voice.SkipFrames, local + replacement.SkipFrames))
					return false;
			}
		}
		return true;
	}

	/// <summary>Test the frozen audible Envelope, not authoring object
	/// identity: unrelated revisions may prepare an equivalent curve
	/// into a distinct NodeEnvelopeCurve wrapper. Script curves cannot
	/// prove equivalent frozen shapes and remain identity-only.</summary>
	private static bool HasSameFrozenEnvelope(
		CompiledNode original, CompiledNode revised)
		=> ReferenceEquals(original.Envelope, revised.Envelope)
			|| original.Envelope is NodeEnvelopeCurve a
				&& revised.Envelope is NodeEnvelopeCurve b
				&& a.HasSameFrozenShapeAs(b);

	/// <summary>Preflight the entire running voice before changing its sound.
	/// Stateful nodes must prove that their existing invocation histories
	/// remain valid. A declined edit leaves the old graph generation intact.</summary>
	public bool CanAdoptRunningState(FmSynthSound next, SoundState state)
		=> CanAdoptRunningState(next, state, allowUnpreparedExpansion: false);

	/// <summary>Check structure without requiring an already prepared
	/// expansion. Used only to admit an off-thread worker request; never
	/// authorizes a partial or allocation-heavy PCM commit.</summary>
	public bool CanPrepareMultiExpansion(FmSynthSound next, SoundState state)
		=> CanAdoptRunningState(next, state, allowUnpreparedExpansion: true);

	private bool CanAdoptRunningState(FmSynthSound next, SoundState state,
		bool allowUnpreparedExpansion)
	{
		ArgumentNullException.ThrowIfNull(next);
		if (state is not FmSynthState live
			|| !FmSynthLiveGraphShape.HasSameWiring(_graph, next._graph)
			|| _outputIndex != next._outputIndex
			|| _oscillatorCount != next._oscillatorCount
			|| _nodes.Length != next._nodes.Length
			|| live.Values.Length != _nodes.Length
			|| live.Phases.Length != _oscillatorCount)
			return false;

		for (int i = 0; i < _nodes.Length; i++)
		{
			CompiledNode old = _nodes[i];
			CompiledNode revised = next._nodes[i];
			if (old.Definition.Id != revised.Definition.Id
				|| old.Definition.GetType() != revised.Definition.GetType()
				|| old.OscillatorIndex != revised.OscillatorIndex
				|| !old.InputIndices.SequenceEqual(revised.InputIndices))
				return false;
			switch (old.Definition)
			{
				case FmConstantNode:
				case FmOscillatorNode:
				case FmOperatorNode:
				case FmNoiseNode:
					break;

				case FmEnvelopeNode:
					// The held note can adopt a revised stateless curve at
					// its original clock. Once release has begun, however,
					// replacing an *audible* curve can change its retirement
					// deadline or erase a still-audible outgoing release.
					// Preserve the old generation until a stateful release
					// migration policy can prove both lifetimes.
					if (live.HasNoteOffTime
						&& _canFadeBoundedOutput
						&& next._canFadeBoundedOutput
						&& _statelessAudioPathNodes[i]
						&& !HasSameFrozenEnvelope(old, revised))
						return false;
					break;

				case FmFilterNode:
					// An unchanged Filter stage plan can keep its existing
					// IIR/FIR histories. TailSeconds and upstream edits
					// require no history rebuild.
					if (old.Filter is null || revised.Filter is null
						|| !old.Filter.CanAdoptStateConfiguration(
							revised.Filter, live.Filters[i]))
						return false;
					break;

				case FmSoundSourceNode source:
					FmSoundSourceNode newSource =
						(FmSoundSourceNode)revised.Definition;
					// The child invocation retains the *original* pitch
					// scale and integrated position. The new authored
					// frequency, Constant flag and modulation mode only
					// affect its subsequent trajectory frames.
					// Reassignment also changes the selected Source's
					// note configuration. Keep the already-captured envelope,
					// fade and past-note semantics only when the newly
					// selected Synth advertises an equivalent snapshot.
					if (source.SourceId != newSource.SourceId
						&& (!CanMigrateSourceChild(live.Sources[i], revised.Source,
								allowUnpreparedExpansion)
							|| live.Sources[i]!.Invocation.Configuration
								!= revised.Source!.SnapshotNoteConfiguration()))
						return false;
					if (live.Sources[i] is { } boundSource
						&& !double.IsFinite(
							newSource.FrequencyHz / boundSource.OriginalFrequencyHz))
						return false;
					// An otherwise compatible child Synth with an unprepared
					// Multi expansion must defer the enclosing parent too.
					// Truly structural child changes still leave the parent
					// free to adopt its independent scalar edits.
					if (!allowUnpreparedExpansion
						&& source.SourceId == newSource.SourceId
						&& live.Sources[i] is { } childSource
						&& childSource.Sound is FmSynthSound oldChild
						&& revised.Source is FmSynthSound newChild
						&& oldChild.CanAdoptRunningState(newChild,
							childSource.Invocation.State, true)
						&& !oldChild.CanAdoptRunningState(newChild,
							childSource.Invocation.State))
						return false;
					break;

				case FmMultiNode multi:
					FmMultiNode nextMulti = (FmMultiNode)revised.Definition;
					// Each constituent's original phase, local time and
					// Source/Filter histories survive. New detune/distribution
					// ratios are precompiled and glide forward, while a
					// change to the active count fades constituents in/out.
					// No child invocation is constructed during publication.
					// RandomizePhase and CycleLength are onset settings:
					// initialized children retain their captured offset.
					if ((old.MultiInput is null) != (revised.MultiInput is null))
						return false;
					if (old.MultiInput is not null)
					{
						if (!allowUnpreparedExpansion
							&& !CanCommitMultiOnsetReplacement(next, live, i))
							return false;
						if (next._multiRatios[i].Length > live.Multis[i].Length
							&& (live.MultiExpansion is null
								|| (!allowUnpreparedExpansion
									&& live.PreparedExpansion?.Matches(next, live)
										!= true)))
							return false;
						foreach (MultiVoice child in live.Multis[i])
							if (!child.Sound.CanAdoptRunningState(
								revised.MultiInput!, child.Invocation.State,
								allowUnpreparedExpansion))
								return false;
					}
					else if (live.Multis[i].Length != 0
						|| next._multiRatios[i].Length != 0)
						return false;
					break;

				default:
					return false;
			}
		}
		return true;
	}

	/// <summary>Only a compatible referenced Synth can inherit a bound
	/// child invocation's original SoundState. In particular, an arbitrary
	/// Sample/Instrument definition cannot be substituted into a different
	/// implementation's state. A structural edit to the SAME referenced
	/// Synth stays on its old child generation while the parent may adopt
	/// otherwise compatible changes.</summary>
	private static bool CanMigrateSourceChild(
		SourceVoice? voice, ISound? revised,
		bool allowUnpreparedExpansion = false)
		=> voice?.Sound is FmSynthSound original
			&& revised is FmSynthSound next
			&& original.CanAdoptRunningState(
				next, voice.Invocation.State, allowUnpreparedExpansion);

	/// <summary>Transactional preflight over all Filters and nested Multi
	/// constituents. Every chain validates its targets into owned scratch
	/// before the voice can commit any DSP state or child sound binding.</summary>
	public bool TryPrepareRunningState(FmSynthSound next, SoundState state)
	{
		if (!CanAdoptRunningState(next, state))
			return false;
		FmSynthState live = ValidateState(state);
		for (int i = 0; i < _nodes.Length; i++)
			if (_nodes[i].Filter is { } oldFilter
				&& next._nodes[i].Filter is { } revisedFilter
				&& !oldFilter.TryPrepareLiveStateConfiguration(
					revisedFilter, live.Filters[i]))
				return false;
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmMultiNode
				|| next._nodes[i].MultiInput is not FmSynthSound revised)
				continue;
			foreach (MultiVoice child in live.Multis[i])
				if (!child.Sound.TryPrepareRunningState(
					revised, child.Invocation.State))
					return false;
		}
		for (int i = 0; i < _nodes.Length; i++)
		{
			SourceVoice? child = live.Sources[i];
			if (!CanMigrateSourceChild(child, next._nodes[i].Source))
				continue;
			FmSynthSound previous = (FmSynthSound)child!.Sound;
			FmSynthSound revised = (FmSynthSound)next._nodes[i].Source!;
			if (!previous.TryPrepareRunningState(revised, child.Invocation.State))
				return false;
		}
		return true;
	}

	/// <summary>Release any obsolete off-thread Multi expansion payloads
	/// when a running voice is retired, rewound or its edited revision is
	/// superseded. Each nested invocation owns a separate mailbox, so walk
	/// the same live Source/Multi tree as audio preparation. This does not
	/// cancel unrelated sound generations or release DSP state.</summary>
	public void CancelPendingMultiExpansion(SoundState state)
	{
		FmSynthState live = ValidateState(state);
		live.MultiExpansion?.Cancel();
		live.PreparedExpansion = null;
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (live.Sources[i] is { Sound: FmSynthSound nestedSource } source)
				nestedSource.CancelPendingMultiExpansion(source.Invocation.State);
			foreach (MultiVoice child in live.Multis[i])
				child.Sound.CancelPendingMultiExpansion(child.Invocation.State);
		}
	}

	/// <summary>Schedule only the missing Multi children, retaining every
	/// old child invocation. A single invocation-private mailbox prepares
	/// all newly needed slots outside the PCM loop. Nested Multi and
	/// referenced Synths use their own independent mailboxes.</summary>
	public bool RequestMultiExpansionPreparation(FmSynthSound next, SoundState state)
	{
		if (!CanPrepareMultiExpansion(next, state))
			return false;
		FmSynthState live = ValidateState(state);
		bool requested = false;
		for (int i = 0; i < _nodes.Length; i++)
			if (next._multiRatios[i].Length > live.Multis[i].Length
				|| NeedsMultiOnsetReplacement(next, live, i))
			{
				requested = true;
				if (live.PreparedExpansion?.Matches(next, live) != true)
					live.MultiExpansion!.Offer(this, next, live);
				break; // one worker request prepares every changed slot
			}
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmMultiNode
				|| next._nodes[i].MultiInput is not FmSynthSound revised)
				continue;
			foreach (MultiVoice child in live.Multis[i])
				requested = child.Sound.RequestMultiExpansionPreparation(
					revised, child.Invocation.State) || requested;
		}
		for (int i = 0; i < _nodes.Length; i++)
		{
			SourceVoice? child = live.Sources[i];
			if (!CanMigrateSourceChild(
				child, next._nodes[i].Source, allowUnpreparedExpansion: true))
				continue;
			requested = ((FmSynthSound)child!.Sound)
				.RequestMultiExpansionPreparation(
					(FmSynthSound)next._nodes[i].Source!,
					child.Invocation.State) || requested;
		}
		return requested;
	}

	/// <summary>After structural compatibility was established but a
	/// voice-specific FIR plan was not yet prepared, capture each affected
	/// Filter's most recently accepted absolute modulation values. The
	/// PCM owner queues these bounded snapshots to the existing off-loop
	/// FIR worker; the whole Synth tree stays on the old definition until
	/// every requested plan and non-FIR update passes atomic preflight.</summary>
	public bool RequestBoundLiveFirPreparation(FmSynthSound next, SoundState state)
	{
		FmSynthState live = ValidateState(state);
		bool requested = false;
		for (int i = 0; i < _nodes.Length; i++)
			if (_nodes[i].Filter is { } oldFilter
				&& next._nodes[i].Filter is { } revisedFilter
				&& oldFilter.RequestBoundLiveFirPreparation(
					revisedFilter, live.Filters[i]))
				requested = true;
		for (int i = 0; i < _nodes.Length; i++)
		{
			SourceVoice? child = live.Sources[i];
			if (CanMigrateSourceChild(child, next._nodes[i].Source))
				requested = ((FmSynthSound)child!.Sound)
					.RequestBoundLiveFirPreparation(
						(FmSynthSound)next._nodes[i].Source!,
						child.Invocation.State) || requested;
		}
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmMultiNode
				|| next._nodes[i].MultiInput is not FmSynthSound revised)
				continue;
			foreach (MultiVoice child in live.Multis[i])
				requested = child.Sound.RequestBoundLiveFirPreparation(
					revised, child.Invocation.State) || requested;
		}
		return requested;
	}

	/// <summary>Copy the actual, already accumulated Sample playhead.
	/// A Multi phase edit may rephase sibling oscillators, but a running
	/// Sample keeps its physical fractional playback position regardless
	/// of the new randomized skip or earlier rate automation. Its new
	/// Source controls affect only subsequent integration. No history
	/// replay, projection, or callback-side allocation is necessary.</summary>
	private static void TransferPreparedDirectSample(
		SourceVoice original, SourceVoice replacement,
		long oldFrame, long newFrame)
	{
		SampleSoundState oldState = (SampleSoundState)original.Invocation.State;
		SampleSoundState newState = (SampleSoundState)replacement.Invocation.State;
		// Old Source pitch was integrated through oldFrame - 1. The
		// cached position at oldFrame is the next Sample interpolation
		// position, even after arbitrary previous pitch-rate changes.
		double position = oldState.PitchTrajectory.GetPosition(oldFrame);
		double rate = original.Pitch.LastRate;
		newState.PitchMultiplier = oldState.PitchMultiplier;
		newState.PlaybackSpeedMultiplier = oldState.PlaybackSpeedMultiplier;
		newState.PlaybackOffset = oldState.PlaybackOffset;
		newState.SourceFrameOffset = oldState.SourceFrameOffset;
		newState.Position = oldState.Position;
		newState.NoteOffTime = oldState.NoteOffTime;
		newState.PitchTrajectory.SeedPreparedStreamingPosition(newFrame, position);
		replacement.RetainOriginalFrequencyBasis(original);
		replacement.RetainEnvelopeClock(original, oldFrame, newFrame);
		replacement.Pitch.SeedBeforeFrame(newFrame, rate);
	}

	/// <summary>Adopt already worker-constructed oscillator and OnePole
	/// histories. Shift each independent phase by the onset-offset delta,
	/// then copy the still-sounding IIR integrators into separately prepared
	/// Filter chains. No child construction, clones or replay in PCM.</summary>
	private void AttachPreparedOscillatorOnset(
		FmSynthState parent, int index,
		MultiVoice outgoing, MultiVoice replacement, long frame)
	{
		FmSynthState previous = ValidateState(outgoing.Invocation.State);
		FmSynthState created = ValidateState(replacement.Invocation.State);
		FmMultiNode multi = (FmMultiNode)_nodes[index].Definition;
		FmSynthSound childGraph = _nodes[index].MultiInput!;

		created.PitchMultiplier = parent.PitchMultiplier
			* outgoing.RatioAt(frame)
			* parent.PitchTrajectory.GetMultiplier(frame);
		created.PlaybackSpeedMultiplier = parent.PlaybackSpeedMultiplier;
		created.Position = previous.Position;
		// Reuse the parent's immutable preprepared mono bus. Generic
		// oscillator/OnePole descendants must not lazily allocate a
		// RenderContext on the replacement's first PCM sample.
		created.MonoContext = parent.MonoContext
			?? throw new InvalidOperationException(
					"Multi onset migration requires a prepared mono child bus.");
		created.NoteOffTime = previous.NoteOffTime;
		created.InheritedAudioReleaseEnd = previous.InheritedAudioReleaseEnd;
		InitializeMultiOnset(multi, parent.NoiseSeed, index, replacement,
			created.PitchMultiplier, created.PlaybackSpeedMultiplier,
			parent.SampleRate);

		// Each independent oscillator has its own phase history.
		// Stateless constants/operators need no migration; unbound OnePole
		// integrators are copied below. Other stateful nodes are excluded.
		int offset = replacement.SkipFrames - outgoing.SkipFrames;
		foreach (CompiledNode node in childGraph._nodes)
		{
			if (node.Definition is not FmOscillatorNode oscillator)
				continue;
			int phaseIndex = node.OscillatorIndex;
			double frequency = oscillator.FrequencyHz
				* (oscillator.Constant ? 1.0
					: created.PitchMultiplier * created.PlaybackSpeedMultiplier);
			double phase = previous.Phases[phaseIndex]
				+ offset * frequency / parent.SampleRate;
			phase -= Math.Floor(phase);
			created.Phases[phaseIndex] = phase;
		}
		// Each destination Filter chain was built and its channel states
		// allocated by the worker. Copy the exact current OnePole integrator
		// values at the publication boundary; old/new chains then evolve
		// independently during the 64-frame crossfade.
		FmSynthSound previousGraph = outgoing.Sound;
		for (int i = 0; i < childGraph._nodes.Length; i++)
		{
			if (previousGraph._nodes[i].Filter is { } oldFilter)
				oldFilter.TransferPreparedOnePoleHistoryTo(
					previous.Filters[i]!, created.Filters[i]!);
			if (previousGraph._nodes[i].Definition is FmSoundSourceNode)
				TransferPreparedDirectSample(
					previous.Sources[i]!, created.Sources[i]!,
					frame - outgoing.StartFrame + outgoing.SkipFrames,
					frame - outgoing.StartFrame + replacement.SkipFrames);
		}
		created.SampleRate = parent.SampleRate;
		created.NextFrame = checked(frame - outgoing.StartFrame
			+ replacement.SkipFrames);
		replacement.BeginPreparedOnsetFade(outgoing, frame);
	}

	/// <summary>Preflight one continuing Instrument-backed Source using only
	/// immutable, off-PCM prepared selected-tone and terminal Sample data.
	/// A compound graph can contain several independent Instrument voices;
	/// none of their Volume curves may be changed until all participants
	/// pass the same selected-tone, PCM, envelope and release proof.
	/// Unrecognized or filtered tone histories stay on the old curves.</summary>
	private bool TryGetLiveInstrumentVolume(
		FmSynthSound next, FmSynthState live, int index,
		out SourceVoice? continuing, out NodeEnvelopeCurve? edited)
	{
		continuing = null;
		edited = null;
		if (!_allowMultiExpansion || !next._allowMultiExpansion
			|| _nodes[index].Definition is not FmSoundSourceNode oldSource
			|| next._nodes[index].Definition is not FmSoundSourceNode newSource
			|| oldSource.SourceId != newSource.SourceId
			|| _nodes[index].Source is not InstrumentSound oldInstrument
			|| next._nodes[index].Source is not InstrumentSound newInstrument
			|| live.Sources[index] is not { } voice
			|| voice.Sound is not SampleSound currentSample
			|| voice.Invocation.State is not SampleSoundState sampleState
			|| voice.InputFinished
			|| voice.EndFrame is long end && end <= live.NextFrame
			|| sampleState.NaturalEndFrameExclusive is long natural
				&& natural <= live.NextFrame
			|| voice.Filters is not null
			|| voice.Invocation.ToneFilters is not null
			|| voice.FilterEnvelopeState is not null
			|| voice.Invocation.Configuration.Envelopes.Pitch is not null
			|| voice.Invocation.Configuration.Envelopes.Panning is not null
			|| voice.Invocation.Configuration.Envelopes.Filter is not null
			|| voice.ActiveVolumeEnvelope is not NodeEnvelopeCurve
			|| !oldInstrument.CanShareFrozenBoundToneWith(
				newInstrument, voice.BoundInstrumentToneSlot)
			|| !newInstrument.TryGetPreparedDirectSampleVolume(
				voice.BoundInstrumentToneSlot, out SampleSound? newSample,
				out NodeEnvelopeCurve? newCurve)
			|| newSample is null || newCurve is null
			|| !currentSample.CanSharePreparedOnsetTimelineWith(newSample)
			|| currentSample.NewNotePolicy != newSample.NewNotePolicy)
			return false;
		continuing = voice;
		edited = newCurve;
		return true;
	}

	/// <summary>The Volume migration is its own all-or-none transaction:
	/// a rejected Instrument child does not undo otherwise compatible
	/// parent scalar/Filter adoption, but no other Instrument Source in
	/// this graph receives half of the edited shared-Volume generation.
	/// A Source with no active node Volume curve has nothing to migrate.
	/// This gate never selects a tone, resolves an asset, or allocates.</summary>
	private bool CanAdoptLiveInstrumentVolumes(
		FmSynthSound next, FmSynthState live)
	{
		if (!_allowMultiExpansion || !next._allowMultiExpansion)
			return false;
		// Multi onset crossfades have their own compatibility transaction.
		// Keep their historical one-direct-Source allowance, but do not
		// accidentally admit a changed Instrument release in a compound
		// constituent whose prepared phase rebase was refused.
		if ((_isMultiInputSubgraph || next._isMultiInputSubgraph)
			&& (_nodes.Length != 1 || next._nodes.Length != 1
				|| _outputIndex != 0 || next._outputIndex != 0))
			return false;
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Source is not InstrumentSound
				|| live.Sources[i]?.ActiveVolumeEnvelope is not NodeEnvelopeCurve)
				continue;
			if (!TryGetLiveInstrumentVolume(next, live, i,
				out _, out _))
				return false;
		}
		return true;
	}

	/// <summary>Safe audible output smoothing around direct Sample leaves:
	/// every running Source keeps its exact bound SampleSoundState, phase/rate
	/// trajectory, natural EOF and Note Off handling. Never let a changed
	/// SourceId, different immutable PCM, tone Filter, already-ended Source
	/// or active release enter a final-output fade. Structural and arbitrary
	/// child-sound replacement remain governed by separate preflights.
	/// This scans existing immutable compiled arrays; no PCM allocations.</summary>
	private bool CanFadeBoundSampleOutputs(FmSynthSound next, FmSynthState live)
	{
		if (!_hasSources)
			return true;
		if (!next._hasSources || live.HasNoteOffTime)
			return false;
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmSoundSourceNode source)
				continue;
			if (next._nodes[i].Definition is not FmSoundSourceNode revised
				|| source.SourceId != revised.SourceId
				|| _nodes[i].Source is not SampleSound oldSample
				|| next._nodes[i].Source is not SampleSound newSample
				|| live.Sources[i] is not { Sound: SampleSound bound } voice
				|| voice.Invocation.State is not SampleSoundState sampleState
				|| !bound.CanSharePreparedOnsetTimelineWith(oldSample)
				|| !oldSample.CanSharePreparedOnsetTimelineWith(newSample)
				|| voice.EndFrame is long end && end <= live.NextFrame
				|| voice.InputFinished
				|| sampleState.HasNoteOffTime
				|| voice.Filters is not null
				|| voice.FilterEnvelopeState is not null)
				return false;
		}
		return true;
	}

	/// <summary>Additional bounded-audio-fade guard for continuing Filter
	/// histories. The full voice preflight has already admitted each
	/// Filter's immutable stage/wiring compatibility; this check only
	/// excludes completed tails and Note-Off lifecycles that cannot be
	/// represented by a scalar held-output transition. No Filter chain
	/// or prepared coefficient state is created inside PCM.</summary>
	private bool CanFadeBoundFilterOutputs(FmSynthSound next, FmSynthState live)
	{
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmFilterNode)
				continue;
			if (live.HasNoteOffTime
				|| next._nodes[i].Definition is not FmFilterNode
				|| _nodes[i].Filter is null || next._nodes[i].Filter is null
				|| live.Filters[i]?.Chain is not { IsInputFinished: false })
				return false;
		}
		return true;
	}

	/// <summary>A Multi child has one independent phase integrator per
	/// Oscillator. A parent output fade is safe only if its authored child
	/// graph is frozen-identical, including *every* oscillator phase/rate
	/// control and all stateless gain/mixing nodes, deterministic Noise
	/// IDs, selected equivalent Node Envelope curves, and unchanged
	/// direct Sample geometry/Source pitch parameters, and an unchanged
	/// single unmodulated OnePole Filter configuration.
	/// Compare immutable snapshots rather than authoring object identity;
	/// never accept edited or scripted Envelope curves in this outer fade.
	/// Continuing Sample readheads are checked against the bound state.
	/// Checking compiled IDs/types/inputs also rejects any rewire.
	/// No allocations, hashes or authoring lookups.</summary>
	private bool HasSameFrozenStatelessMultiGraphAs(FmSynthSound next)
	{
		if (_oscillatorCount < 1
			|| _oscillatorCount != next._oscillatorCount
			|| _nodes.Length != next._nodes.Length
			|| _outputIndex != next._outputIndex
			|| _hasMulti || next._hasMulti
			|| _newNotePolicy != next._newNotePolicy)
			return false;
		for (int i = 0; i < _nodes.Length; i++)
		{
			CompiledNode old = _nodes[i], revised = next._nodes[i];
			if (old.Definition.Id != revised.Definition.Id
				|| old.Definition.GetType() != revised.Definition.GetType()
				|| old.OscillatorIndex != revised.OscillatorIndex
				|| !old.InputIndices.AsSpan().SequenceEqual(revised.InputIndices))
				return false;
			switch (old.Definition)
			{
				case FmOscillatorNode oscillator
					when revised.Definition is FmOscillatorNode updatedOscillator
						&& oscillator.Waveform == updatedOscillator.Waveform
						&& oscillator.FrequencyHz == updatedOscillator.FrequencyHz
						&& oscillator.Minimum == updatedOscillator.Minimum
						&& oscillator.Maximum == updatedOscillator.Maximum
						&& oscillator.Curvature == updatedOscillator.Curvature
						&& oscillator.Constant == updatedOscillator.Constant
						&& oscillator.ExponentialMultiplier
							== updatedOscillator.ExponentialMultiplier
						&& oscillator.MultiplierNodeId == updatedOscillator.MultiplierNodeId:
					break;
				case FmConstantNode constant
					when revised.Definition is FmConstantNode updatedConstant
						&& constant.Value == updatedConstant.Value:
					break;
				case FmOperatorNode operation
					when revised.Definition is FmOperatorNode updatedOperator
						&& operation.Operation == updatedOperator.Operation:
					break;
				// Noise uses the continuing voice's seed, node ID and frame:
				// it has no mutable RNG history to migrate. The compiled
				// node ID comparison above covers its entire configuration.
				case FmNoiseNode when revised.Definition is FmNoiseNode:
					break;
				// The Sample is a frozen audible leaf, not a new invocation.
				// Rebound PCM and changing pitch controls are ineligible.
				case FmSoundSourceNode source
					when revised.Definition is FmSoundSourceNode updatedSource
						&& source.SourceId == updatedSource.SourceId
						&& source.FrequencyHz == updatedSource.FrequencyHz
						&& source.Constant == updatedSource.Constant
						&& source.ExponentialMultiplier
							== updatedSource.ExponentialMultiplier
						&& source.MultiplierNodeId == updatedSource.MultiplierNodeId
						&& old.Source is SampleSound oldSample
						&& revised.Source is SampleSound updatedSample
						&& oldSample.NewNotePolicy == updatedSample.NewNotePolicy
						&& oldSample.CanSharePreparedOnsetTimelineWith(updatedSample):
					break;
				// Exact frozen OnePole parameters, stage identity, and
				// tail are required. A retuned Filter is not an unchanged
				// audible leaf, even if normal migration can admit it.
				case FmFilterNode filter
					when revised.Definition is FmFilterNode updatedFilter
						&& filter.TailSeconds == updatedFilter.TailSeconds
						&& filter.AudioInputNodeId == updatedFilter.AudioInputNodeId
						&& old.Filter is FmFilterNodeProcessor oldProcessor
						&& revised.Filter is FmFilterNodeProcessor newProcessor
						&& oldProcessor.HasSameFrozenUnmodulatedOnePoleAs(
							newProcessor):
					break;
				// An unchanged node-based Envelope evaluates on the
				// continuing child note clock. Newly frozen wrappers are
				// equivalent only when all captured curve data agrees.
				// Reject different IDs, changed points and scripted curves.
				case FmEnvelopeNode envelope
					when revised.Definition is FmEnvelopeNode updatedEnvelope
						&& envelope.EnvelopeId == updatedEnvelope.EnvelopeId
						&& old.Envelope is NodeEnvelopeCurve oldCurve
						&& revised.Envelope is NodeEnvelopeCurve updatedCurve
						&& oldCurve.HasSameFrozenShapeAs(updatedCurve):
					break;
				default:
					return false;
			}
		}
		return true;
	}

	/// <summary>Allow only downstream audible controls to fade around
	/// a stable, already-running Multi whose Oscillator/Noise/Node-Envelope/
	/// Constant/Operator/direct-Sample child graph is frozen-identical.
	/// All constituent phases, bound Sample readheads, continuing OnePole
	/// histories, deterministic Noise streams and Envelope clocks survive.
	/// Any child edit, voice-count, distribution or onset change has a
	/// separate transaction; this guard is bounded and allocation-free.</summary>
	private bool CanFadeStableMultiOutputs(FmSynthSound next, FmSynthState live)
	{
		if (!_hasMulti)
			return true;
		if (!next._hasMulti || live.HasNoteOffTime)
			return false;
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmMultiNode previous)
				continue;
			if (next._nodes[i].Definition is not FmMultiNode revised
				|| previous.InputNodeId != revised.InputNodeId
				|| previous.Distribution != revised.Distribution
				|| previous.Detune != revised.Detune
				|| previous.VoiceCount != revised.VoiceCount
				|| previous.HarmonicComponents != revised.HarmonicComponents
				|| previous.ChordComponents != revised.ChordComponents
				|| previous.RandomizePhase != revised.RandomizePhase
				|| previous.GainCompensation != revised.GainCompensation
				|| previous.CycleLength != revised.CycleLength
				|| _nodes[i].MultiInput is not FmSynthSound oldChild
				|| next._nodes[i].MultiInput is not FmSynthSound nextChild
				|| !oldChild.HasSameFrozenStatelessMultiGraphAs(nextChild)
				|| _multiRatios[i].Length != next._multiRatios[i].Length
				|| live.Multis[i].Length < _multiRatios[i].Length)
				return false;
			double targetMix = previous.GainCompensation
				? 1.0 / _multiRatios[i].Length : 1.0;
			for (int voiceIndex = 0; voiceIndex < live.Multis[i].Length; voiceIndex++)
			{
				MultiVoice voice = live.Multis[i][voiceIndex];
				if (voiceIndex >= _multiRatios[i].Length)
				{
					if (voice.HasStarted || voice.OnsetFadeFrom is not null)
						return false;
					continue; // dormant preallocated headroom is inert
				}
				if (voice.Invocation.State is not FmSynthState childState
					|| !voice.Sound.HasSameFrozenStatelessMultiGraphAs(oldChild)
					|| !voice.Sound.CanFadeBoundSampleOutputs(oldChild, childState)
					|| !oldChild.CanFadeBoundSampleOutputs(nextChild, childState)
					|| !voice.Sound.CanFadeBoundFilterOutputs(oldChild, childState)
					|| !oldChild.CanFadeBoundFilterOutputs(nextChild, childState)
					|| !voice.HasStarted || !voice.Initialized
					|| voice.OnsetFadeFrom is not null
					|| voice.SilenceEnd.HasValue
					|| voice.Invocation.State.HasNoteOffTime
					|| voice.RatioAt(live.NextFrame) != _multiRatios[i][voiceIndex]
					|| voice.MixAt(live.NextFrame) != targetMix)
					return false;
			}
		}
		return true;
	}

	/// <summary>Use a moving, phase-coherent output mapping for one exact
	/// outer Multi + Constant -> Add/Multiply shape. A sample-rate-scaled
	/// 100 ms audition-only window softens gain and DC offset changes
	/// without holding Sample, Filter, oscillator or constituent audio.
	/// All stable-Multi and bound-Source/Filter proofs precede mutation.
	/// No per-frame DSP state, additional voice or allocation is needed.
	/// A different graph shape retains the original 64-frame policy.</summary>
	private bool TryStartAdditiveMultiOutputTransition(
		FmSynthSound next, FmSynthState live)
	{
		if (!_allowMultiExpansion || !next._allowMultiExpansion
			|| _isMultiInputSubgraph || next._isMultiInputSubgraph
			|| _nodes.Length != 3 || next._nodes.Length != 3
			|| _outputIndex != next._outputIndex
			|| _nodes[_outputIndex].Definition is not FmOperatorNode original
			|| next._nodes[_outputIndex].Definition is not FmOperatorNode updated
			|| original.Operation is not (FmOperatorKind.Add or FmOperatorKind.Multiply)
			|| updated.Operation is not (FmOperatorKind.Add or FmOperatorKind.Multiply)
			|| _nodes[_outputIndex].InputIndices.Length != 2)
			return false;
		int left = _nodes[_outputIndex].InputIndices[0];
		int right = _nodes[_outputIndex].InputIndices[1];
		int constantIndex, signalIndex;
		if (_nodes[left].Definition is FmMultiNode
			&& _nodes[right].Definition is FmConstantNode)
		{
			signalIndex = left;
			constantIndex = right;
		}
		else if (_nodes[right].Definition is FmMultiNode
			&& _nodes[left].Definition is FmConstantNode)
		{
			signalIndex = right;
			constantIndex = left;
		}
		else
			return false;
		if (_nodes[constantIndex].Definition is not FmConstantNode oldConstant
			|| next._nodes[constantIndex].Definition is not FmConstantNode newConstant
			|| (oldConstant.Value == newConstant.Value
				&& original.Operation == updated.Operation)
			|| !_canFadeBoundedOutput || !next._canFadeBoundedOutput
			|| !CanFadeBoundSampleOutputs(next, live)
			|| !CanFadeBoundFilterOutputs(next, live)
			|| !CanFadeStableMultiOutputs(next, live))
			return false;
		double fromScale = original.Operation == FmOperatorKind.Add
			? 1.0 : oldConstant.Value;
		double fromOffset = original.Operation == FmOperatorKind.Add
			? oldConstant.Value : 0.0;
		double toScale = updated.Operation == FmOperatorKind.Add
			? 1.0 : newConstant.Value;
		double toOffset = updated.Operation == FmOperatorKind.Add
			? newConstant.Value : 0.0;
		live.StartAdditiveOutputTransition(signalIndex, _outputIndex,
			fromScale, fromOffset, toScale, toOffset);
		return true;
	}

	/// <summary>Commit only an entirely preflighted state tree. No validation
	/// after this boundary can leave multiple Filters partially updated.</summary>
	public void CommitPreparedRunningState(FmSynthSound next, SoundState state)
	{
		FmSynthState live = ValidateState(state);
		// Probe all active Instrument Volume children before changing any.
		// This remains entirely frozen PCM-owner work: no Source resolution,
		// tone selection, Sample state construction, or managed allocation.
		// Incompatible branches retain their old gain without starving the
		// independently safe graph parameter changes below.
		if (CanAdoptLiveInstrumentVolumes(next, live))
			for (int i = 0; i < _nodes.Length; i++)
			{
				if (live.Sources[i]?.ActiveVolumeEnvelope is not NodeEnvelopeCurve oldCurve
					|| _nodes[i].Source is not InstrumentSound)
					continue;
				// All participating children passed the preflight above.
				TryGetLiveInstrumentVolume(next, live, i,
					out SourceVoice? volumeVoice, out NodeEnvelopeCurve? volumeCurve);
				if (!oldCurve.HasSameFrozenShapeAs(volumeCurve!))
					volumeVoice!.AdoptPreparedNodeVolume(
						volumeCurve!, live.NextFrame, live.SampleRate);
			}
		// Stateless output changes are click-prone even when no waveform
		// changes: a Constant can scale gain and an Operator can switch
		// from Add to Multiply. Fade *only* a changed node that lies on
		// the audible value path; changed modulation controls still retune
		// their Oscillator immediately. Raw Values and phases are untouched.
		// The direct single-oscillator exception remains supported inside
		// Multi; compound Multi constituents have separate onset semantics.
		bool additiveTransition = TryStartAdditiveMultiOutputTransition(next, live);
		// When leaving Add/Multiply (e.g. for Maximum), fall back to the
		// existing 64-frame full-output transition from the last audible
		// sample instead of retaining a stale additive transform.
		if (!additiveTransition
			&& live.HasAdditiveOutputTransition
			&& next._nodes[_outputIndex].Definition is FmOperatorNode nextOutput
			&& nextOutput.Operation is not (FmOperatorKind.Add or FmOperatorKind.Multiply))
			live.ClearAdditiveOutputTransition();
		if (!additiveTransition
			&& _allowMultiExpansion && next._allowMultiExpansion
			&& ((_singleOutputOscillator is not null
					&& next._singleOutputOscillator is not null)
				|| (_canFadeBoundedOutput
					&& next._canFadeBoundedOutput
					&& !_isMultiInputSubgraph && !next._isMultiInputSubgraph
					&& CanFadeBoundSampleOutputs(next, live)
					&& CanFadeBoundFilterOutputs(next, live)
					&& CanFadeStableMultiOutputs(next, live))))
			for (int i = 0; i < _nodes.Length; i++)
			{
				if (!_statelessAudioPathNodes[i])
					continue;
				bool shapeChanged = _nodes[i].Definition is FmOscillatorNode oldOsc
					&& next._nodes[i].Definition is FmOscillatorNode newOsc
					&& (oldOsc.Waveform != newOsc.Waveform
						|| oldOsc.Curvature != newOsc.Curvature
						|| oldOsc.Minimum != newOsc.Minimum
						|| oldOsc.Maximum != newOsc.Maximum);
				bool scalarChanged = _nodes[i].Definition is FmConstantNode oldConstant
					&& next._nodes[i].Definition is FmConstantNode newConstant
					&& oldConstant.Value != newConstant.Value;
				bool operatorChanged = _nodes[i].Definition is FmOperatorNode oldOperator
					&& next._nodes[i].Definition is FmOperatorNode newOperator
					&& oldOperator.Operation != newOperator.Operation;
				// Shared Node Envelope revisions are stateless but can
				// abruptly change an already sounding gain. Fade when
				// an audible node curve truly changes, not when the
				// frozen song recompiles an equivalent wrapper. Never
				// treat a pitch-only Envelope as an audio value path.
				bool envelopeChanged = _nodes[i].Definition is FmEnvelopeNode
					&& next._nodes[i].Definition is FmEnvelopeNode
					&& _nodes[i].Envelope is NodeEnvelopeCurve oldCurve
					&& next._nodes[i].Envelope is NodeEnvelopeCurve newCurve
					&& !oldCurve.HasSameFrozenShapeAs(newCurve);
				if (shapeChanged || scalarChanged || operatorChanged || envelopeChanged)
				{
					live.StartStatelessOutputFade(_outputIndex);
					break;
				}
			}
		// All expansion arrays and child SoundInvocations were allocated and
		// seeded on the control worker. Splice in old references before
		// committing any Filter, Source, or Multi parameter updates.
		PreparedMultiExpansion? expansion = live.PreparedExpansion;
		bool expansionMatches = expansion?.Matches(next, live) == true;
		for (int i = 0; i < _nodes.Length; i++)
		{
			MultiVoice[] oldVoices = live.Multis[i];
			bool rephase = NeedsMultiOnsetReplacement(next, live, i);
			if (next._multiRatios[i].Length <= oldVoices.Length && !rephase)
				continue;
			if (!expansionMatches
				|| expansion!.Slots[i] is not MultiVoice[] grown)
				throw new InvalidOperationException(
					"Multi growth/onset preparation must be completed off-thread.");
			FmMultiNode newMulti = (FmMultiNode)next._nodes[i].Definition;
			for (int j = 0; j < oldVoices.Length; j++)
			{
				MultiVoice previous = oldVoices[j];
				bool changedOnset = rephase && j < next._multiRatios[i].Length
					&& previous.HasStarted && previous.Initialized
					&& (previous.Definition.RandomizePhase != newMulti.RandomizePhase
						|| ((previous.Definition.RandomizePhase
							|| newMulti.RandomizePhase)
							&& previous.Definition.CycleLength != newMulti.CycleLength));
				if (changedOnset)
				{
					MultiVoice replacement = grown[j];
					next.AttachPreparedOscillatorOnset(live, i, previous,
						replacement, live.NextFrame);
				}
				else
					grown[j] = previous;
			}
			live.ReplaceMultiVoices(i, grown);
		}
		live.PreparedExpansion = null;
		for (int i = 0; i < _nodes.Length; i++)
			if (_nodes[i].Filter is { } oldFilter)
				oldFilter.CommitPreparedLiveStateConfiguration(live.Filters[i]);
		for (int i = 0; i < _nodes.Length; i++)
		{
			SourceVoice? child = live.Sources[i];
			if (!CanMigrateSourceChild(child, next._nodes[i].Source))
				continue;
			FmSynthSound revised = (FmSynthSound)next._nodes[i].Source!;
			((FmSynthSound)child!.Sound).CommitPreparedRunningState(
				revised, child.Invocation.State);
			child.Sound = revised;
		}
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmMultiNode
				|| next._nodes[i].MultiInput is not FmSynthSound revised)
				continue;
			foreach (MultiVoice child in live.Multis[i])
			{
				child.Sound.CommitPreparedRunningState(
					revised, child.Invocation.State);
				child.Sound = revised;
			}
			// Every compatible child was fully admitted before we change
			// the mix. Retuning only preallocated entries is allocation-free.
			MultiVoice[] voices = live.Multis[i];
			double[] ratios = next._multiRatios[i];
			FmMultiNode layout = (FmMultiNode)next._nodes[i].Definition;
			double mix = layout.GainCompensation ? 1.0 / ratios.Length : 1.0;
			for (int j = 0; j < voices.Length; j++)
			{
				voices[j].UseSampleRate(live.SampleRate);
				voices[j].Retune(
					j < ratios.Length ? ratios[j] : voices[j].RatioAt(live.NextFrame),
					j < ratios.Length ? mix : 0.0, live.NextFrame);
			}
		}
	}

	/// <summary>Release is a property of the selected active graph, not
	/// the mere presence of an envelope and not just the output gain.
	/// Source-bound Instrument tones contribute only their selected curves;
	/// dormant Multi reserve children cannot make a note sustained.</summary>
	public bool HasSustainRelease(SoundState state)
	{
		FmSynthState active = ValidateState(state);
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Envelope?.HasSustainLoop == true)
				return true;
			if (active.Sources[i] is { } source
				&& (HasSourceSustainRelease(source)
					|| source.Sound.HasSustainRelease(source.Invocation.State)))
				return true;
			foreach (MultiVoice voice in active.Multis[i])
				if (voice.HasStarted
					&& voice.Sound.HasSustainRelease(voice.Invocation.State))
					return true;
		}
		return false;
	}

	private static bool HasSourceSustainRelease(SourceVoice source)
	{
		EnvelopeConfigurationSnapshot curves = source.Invocation.Configuration.Envelopes;
		return source.ActiveVolumeEnvelope?.HasSustainLoop == true
			|| curves.Pitch?.HasSustainLoop == true
			|| curves.Panning?.HasSustainLoop == true
			|| curves.Filter?.HasSustainLoop == true;
	}

	/// <summary>Latest known completion of all active sustain loops in
	/// this subtree. Null is an unknown/unbounded release, not silence.
	/// The child states and EOFs were prepared before this query.</summary>
	private long? GetReleasingSubtreeEnd(
		FmSynthState state, long offFrame, int sampleRate)
	{
		long latest = checked(offFrame + 1);
		bool unknown = false;
		void Include(long? end)
		{
			if (!end.HasValue) unknown = true;
			else latest = Math.Max(latest, end.Value);
		}
		for (int i = 0; i < _nodes.Length; i++)
		{
			IEnvelopeCurve? nodeCurve = _nodes[i].Envelope;
			if (nodeCurve?.HasSustainLoop == true)
				Include(nodeCurve.GetEndActiveFrameExclusiveAfterNoteOff(
					offFrame, sampleRate));
			if (state.Sources[i] is { } source)
			{
				EnvelopeConfigurationSnapshot curves =
					source.Invocation.Configuration.Envelopes;
				long envelopeOff = checked(offFrame + source.EnvelopeFrameOffset);
				void IncludeSource(IEnvelopeCurve? curve)
				{
					if (curve?.HasSustainLoop != true) return;
					long? end = curve.GetEndActiveFrameExclusiveAfterNoteOff(
						envelopeOff, sampleRate);
					Include(end.HasValue
						? checked(end.Value - source.EnvelopeFrameOffset) : null);
				}
				IncludeSource(source.ActiveVolumeEnvelope);
				// An edited shorter Volume release cannot erase the previous
				// audible gain during its in-flight 64-frame crossfade.
				if (source.GetOutgoingVolumeReleaseEnd(envelopeOff, sampleRate)
					is long outgoingVolumeEnd)
					latest = Math.Max(latest, outgoingVolumeEnd);
				IncludeSource(curves.Pitch);
				IncludeSource(curves.Panning);
				IncludeSource(curves.Filter);
				if (source.Sound.HasSustainRelease(source.Invocation.State))
					Include(source.EndFrame);
			}
			foreach (MultiVoice voice in state.Multis[i])
				if (voice.HasStarted
					&& voice.Sound.HasSustainRelease(voice.Invocation.State))
					Include(voice.SilenceEnd);
		}
		return unknown ? null : latest;
	}

	public NoteConfigurationSnapshot SnapshotNoteConfiguration()
		=> new(_newNotePolicy);

	public SoundState CreateState() => CreateBoundState(1.0, 1.0);

	public SoundInvocation CreateInvocation(
		double pitchMultiplier, double playbackSpeedMultiplier)
	{
		SoundState state = CreateBoundState(pitchMultiplier,
			playbackSpeedMultiplier);
		return new SoundInvocation(this, state, SnapshotNoteConfiguration());
	}

	/// <summary>Worker-only cancellable construction of a fresh child
	/// invocation. Existing voices never use this path; the callback is
	/// checked between expensive nested operations, and cancellation
	/// discards the new state before it can become audible.</summary>
	internal SoundInvocation CreateInvocationForLiveExpansion(
		double pitchMultiplier, double playbackSpeedMultiplier,
		Func<bool> cancellationRequested,
		Action? afterNestedChild = null)
	{
		ArgumentNullException.ThrowIfNull(cancellationRequested);
		SoundState state = CreateBoundState(pitchMultiplier,
			playbackSpeedMultiplier, cancellationRequested, afterNestedChild);
		return new SoundInvocation(this, state, SnapshotNoteConfiguration());
	}

	private FmSynthState CreateBoundState(
		double pitchMultiplier, double playbackSpeedMultiplier,
		Func<bool>? cancellationRequested = null,
		Action? afterNestedChild = null)
	{
		void CheckCancellation()
		{
			if (cancellationRequested?.Invoke() == true)
				throw new OperationCanceledException(
					"Obsolete live Multi expansion child construction.");
		}

		CheckCancellation();
		if (!(pitchMultiplier > 0.0) || !double.IsFinite(pitchMultiplier))
			throw new ArgumentOutOfRangeException(nameof(pitchMultiplier));
		if (!(playbackSpeedMultiplier > 0.0) || !double.IsFinite(playbackSpeedMultiplier))
			throw new ArgumentOutOfRangeException(nameof(playbackSpeedMultiplier));
		SourceVoice?[] sources = new SourceVoice?[_nodes.Length];
		MultiVoice[][] multis = new MultiVoice[_nodes.Length][];
		for (int i = 0; i < _nodes.Length; i++)
		{
			CheckCancellation();
			CompiledNode compiled = _nodes[i];
			if (compiled.Definition is not FmMultiNode multi
				|| compiled.MultiInput is not FmSynthSound input)
			{
				multis[i] = [];
				continue;
			}
			double[] ratios = _multiRatios[i];
			MultiVoice[] voices = new MultiVoice[ratios.Length + _multiReserve[i]];
			double initialMix = multi.GainCompensation ? 1.0 / ratios.Length : 1.0;
			for (int j = 0; j < voices.Length; j++)
			{
				CheckCancellation();
				bool started = j < ratios.Length;
				double ratio = started ? ratios[j] : 1.0;
				SoundInvocation invocation = cancellationRequested is null
					? input.CreateInvocation(
						pitchMultiplier * ratio, playbackSpeedMultiplier)
					: input.CreateInvocationForLiveExpansion(
						pitchMultiplier * ratio, playbackSpeedMultiplier,
						cancellationRequested, afterNestedChild);
				voices[j] = new MultiVoice(invocation, ratio, multi, j,
					started ? initialMix : 0.0, started);
				afterNestedChild?.Invoke(); // off-worker test gate only
				CheckCancellation();
			}
			multis[i] = voices;
		}

		for (int i = 0; i < _nodes.Length; i++)
		{
			CheckCancellation();
			if (_nodes[i].Definition is not FmSoundSourceNode node
				|| _nodes[i].Source is not ISound sound)
				continue;
			double childPitch = (node.FrequencyHz / C4FrequencyHz)
				* (node.Constant ? 1.0 : pitchMultiplier);
			double childSpeed = node.Constant ? 1.0 : playbackSpeedMultiplier;
			SoundInvocation? invocation = sound is FmSynthSound nested
				&& cancellationRequested is not null
					? nested.CreateInvocationForLiveExpansion(
						childPitch, childSpeed, cancellationRequested,
						afterNestedChild)
					: sound.CreateInvocation(childPitch, childSpeed);
			CheckCancellation();
			if (invocation is not null)
			{
				// One-time selected tone capture belongs to note/worker
				// construction. Later PCM adoption compares this exact
				// bound table slot, never re-selects against live pitch.
				int? boundTone = sound is InstrumentSound instrument
					? instrument.CaptureFrozenBoundToneIndex(childPitch) : null;
				SourceVoice voice = new(invocation,
					node.Constant ? 1.0 : pitchMultiplier * playbackSpeedMultiplier,
					node.FrequencyHz, boundTone);
				// For a frozen live plan, even expensive maximum/minimum-phase
				// FIR designs are already immutable: this only instantiates
				// per-voice histories from prepared plans at the note boundary.
				if (_preparedChildFilterFactory is { } prepared)
				{
					if (invocation.ToneFilters is { } filters)
						voice.Filters = new StreamingFilterChain(filters,
							prepared.SampleRate, 1, prepared);
					if (invocation.Configuration.Envelopes.Filter is not null)
						voice.FilterEnvelopeState = new ResonantFilterState(1,
							prepared.SampleRate);
				}
				sources[i] = voice;
			}
			CheckCancellation();
		}
		CheckCancellation();
		FmSynthState state = new(_nodes.Length, _oscillatorCount, sources, multis,
			pitchMultiplier, playbackSpeedMultiplier, _allowMultiExpansion)
		{
			PitchMultiplier = pitchMultiplier,
			PlaybackSpeedMultiplier = playbackSpeedMultiplier,
		};
		// Direct CreateState also initializes nested deterministic streams.
		// PlaybackVoice may rebind these to its unique producer voice seed.
		state.SetNoiseSeed(state.NoiseSeed);
		CheckCancellation();
		if (_preparedChildFilterFactory is { } ready && _hasSources)
		{
			// All sample-rate-specific contexts are constructed at the
			// invocation boundary, before the first PCM sample is processed.
			state.MonoContext = new RenderContext(new RenderConfiguration(
				ready.SampleRate, [
				new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0)]));
			if (sources.Any(source =>
				source?.Invocation.Configuration.Envelopes.Panning is not null))
				state.StereoChildContext = new RenderContext(
					RenderConfiguration.Stereo(ready.SampleRate));
		}
		CheckCancellation();
		return state;
	}

	/// <summary>
	/// Rebuild a child-source Synth invocation for deterministic random-access
	/// replay. Call only at an explicit producer/control boundary, never from
	/// an audio callback. Source definitions and FIR plans are already compiled,
	/// but Instrument tone selection and the creation of per-voice histories
	/// can allocate and resolve a prepared child. The parent's authored pitch
	/// trajectory, note-off, position and playback controls are preserved.
	/// Replays the exact source-frame timeline from zero to targetFrame, so
	/// there are no approximate Sample offsets or stale Filter histories.
	/// Supply the *original accepted* synchronous FIR preparation boundaries
	/// for graph automation that commits off-thread kernel updates. This
	/// reconstructs the same off-PCM publication schedule; asynchronous
	/// publications need their actual acceptance frames recorded by the producer.
	/// </summary>
	public void PrepareReplay(RenderContext context, SoundState state,
		long targetFrame = 0,
		IReadOnlyList<long>? firPreparationFrames = null)
	{
		ArgumentNullException.ThrowIfNull(context);
		if (targetFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(targetFrame));
		if (firPreparationFrames is not null)
		{
			long previous = -1;
			foreach (long boundary in firPreparationFrames)
			{
				if (boundary <= previous || boundary > targetFrame)
					throw new ArgumentOutOfRangeException(nameof(firPreparationFrames),
						"Control publication boundaries must be unique, ascending and at or before targetFrame.");
				previous = boundary;
			}
		}
		FmSynthState current = ValidateState(state);
		int sampleRate = context.Configuration.SampleRate;
		if (_preparedChildFilterFactory is { } prepared
			&& prepared.SampleRate != sampleRate)
			throw new InvalidOperationException(
				"Frozen source voices cannot change sample rate after preparation.");

		if (_hasSources)
		{
			FmSynthState rebuilt = CreateBoundState(
				current.BoundPitch, current.BoundSpeed);
			current.ReplaceForReplay(rebuilt, sampleRate);
		}
		else
			current.Reset(sampleRate);
		PrepareChildRender(context, current);
		// Reconstruct accepted FIR candidates at their original off-PCM
		// boundaries. This may design kernels and must never run in a callback.
		if (firPreparationFrames is not null)
			foreach (long boundary in firPreparationFrames)
			{
				EnsureFrame(context, current, boundary);
				PreparePendingFilterControls(context, current);
			}
		// Deliberately do all potentially expensive replay/warm-up work at
		// the boundary. The next Render(targetFrame, ...) stays sequential.
		EnsureFrame(context, current, targetFrame);
	}

	/// <summary>
	/// Explicit off-PCM control boundary for FIR modulation. The caller runs
	/// this *between* Render blocks on the invocation-owning rendering
	/// thread, never inside the audio-device callback or sample loop.
	/// Prepares at most one bounded latest-wins FIR snapshot per Filter node.
	/// A later sample consumes it only if its complete control request still
	/// matches; otherwise the old kernel is retained. Passing the same
	/// absolute preparation boundaries when replaying produces identical PCM.
	/// </summary>
	public int PreparePendingFilterControls(RenderContext context, SoundState state)
	{
		ArgumentNullException.ThrowIfNull(context);
		FmSynthState synthState = ValidateState(state);
		if (synthState.SampleRate != context.Configuration.SampleRate)
			return 0;
		int prepared = 0;
		for (int index = 0; index < _nodes.Length; index++)
		{
			FmFilterNodeProcessor? processor = _nodes[index].Filter;
			FmFilterNodeProcessor.State? filterState = synthState.Filters[index];
			if (processor is not null && filterState is not null
				&& processor.TryPreparePendingFirs(filterState))
				prepared++;
		}
		foreach (SourceVoice? source in synthState.Sources)
			if (source?.Invocation.Sound is FmSynthSound nested)
				prepared += nested.PreparePendingFilterControls(context,
					source.Invocation.State);
		foreach (MultiVoice[] voices in synthState.Multis)
			foreach (MultiVoice voice in voices)
				prepared += voice.Sound.PreparePendingFilterControls(
					synthState.MonoContext ?? context, voice.Invocation.State);
		return prepared;
	}

    /// <summary>Control snapshots are published on the PCM owner at a
    /// quantum boundary. Heavy FIR design runs only on the independent
    /// FirPreparationScheduler worker.</summary>
    public void AdoptPreparedFirControls(SoundState state)
    {
        FmSynthState synthState = ValidateState(state);
		if (synthState.MultiExpansion?.TakeReady() is { } expansion)
			synthState.PreparedExpansion = expansion;
        for (int i = 0; i < _nodes.Length; i++)
            if (_nodes[i].Filter is { } processor
                && synthState.Filters[i] is { } filterState)
                processor.AdoptPreparedFirs(filterState);
        foreach (SourceVoice? source in synthState.Sources)
            if (source?.Invocation.Sound is IFirPreparationSound nested)
                nested.AdoptPreparedFirControls(source.Invocation.State);
        foreach (MultiVoice[] voices in synthState.Multis)
            foreach (MultiVoice voice in voices)
                voice.Sound.AdoptPreparedFirControls(voice.Invocation.State);
    }

    public void QueuePendingFirControls(SoundState state,
        FirPreparationScheduler scheduler)
    {
        FmSynthState synthState = ValidateState(state);
		synthState.MultiExpansion?.Queue(scheduler);
        for (int i = 0; i < _nodes.Length; i++)
            if (_nodes[i].Filter is { } processor
                && synthState.Filters[i] is { } filterState)
                processor.QueuePendingFirs(filterState, scheduler);
        foreach (SourceVoice? source in synthState.Sources)
            if (source?.Invocation.Sound is IFirPreparationSound nested)
                nested.QueuePendingFirControls(source.Invocation.State, scheduler);
        foreach (MultiVoice[] voices in synthState.Multis)
            foreach (MultiVoice voice in voices)
                voice.Sound.QueuePendingFirControls(voice.Invocation.State, scheduler);
    }


	public long? GetEndFrameExclusive(
		RenderContext context, SoundState state)
	{
		ArgumentNullException.ThrowIfNull(context);
		FmSynthState synthState = ValidateState(state);
		if (_hasSources)
			PrepareChildRender(context, synthState);
		long? baseEnd = GetBaseEndFrameExclusive(
			context.Configuration.SampleRate, synthState);
		return AddAuthoredTailFrames(baseEnd, context.Configuration.SampleRate);
	}

	/// <summary>Source excitation stops at the graph's known finite base
	/// end; Filter histories are allowed only their authored maximum tail
	/// budget. Both the session and direct ISound.Render calls enforce the
	/// same exclusive last frame.</summary>
	private long? AddAuthoredTailFrames(long? baseEnd, int sampleRate)
	{
		if (!baseEnd.HasValue)
			return null;
		double frames = Math.Ceiling(_maximumAudioPathTailSeconds * sampleRate);
		if (frames >= long.MaxValue || baseEnd.Value > long.MaxValue - frames)
			return long.MaxValue;
		return baseEnd.Value + (long)frames;
	}


	/// <summary>
	/// Only graph paths carrying audio into the output can leave audible
	/// Filter residue. Oscillator multiplier/Filter parameter inputs are
	/// evaluated while the source is active but cannot independently keep
	/// producing sound after the normal oscillator excitation is stopped.
	/// </summary>
	private double GetMaximumAudioPathTailSeconds()
	{
		// User-authored TailSeconds is a graph-wide maximum allowance,
		// deliberately NOT the sum of serial Filter tails. This preserves
		// the explicit FM Filter budget independently of physical DSP tails.
		double tail = 0;
		HashSet<int> visited = [];
		Visit(_outputIndex);
		return tail;

		void Visit(int index)
		{
			if (!visited.Add(index))
				return;
			CompiledNode compiled = _nodes[index];
			switch (compiled.Definition)
			{
				case FmFilterNode filter:
					if (filter.AudioInputNodeId.HasValue)
					{
						if (filter.Configuration is not null)
							tail = Math.Max(tail, filter.TailSeconds);
						Visit(compiled.InputIndices[0]);
					}
					break;
				case FmOperatorNode:
					foreach (int input in compiled.InputIndices)
						Visit(input);
					break;
				case FmMultiNode:
					// Subgraph's internal filters already own their tails.
					break;
			}
		}
	}

	/// <summary>Indefinite Off leaves an unlooped source running. Once
	/// the note has a sustain-release lifecycle, all ordinary excitation
	/// is bounded by the latest active release, not a one-frame cut.</summary>
	private long? GetIndependentAudioReleaseEnd(
		int nodeIndex, long offFrame, int sampleRate, FmSynthState state)
	{
		long? end = state.HasReleasingSubtree
			? state.ReleasingSubtreeEnd
			: state.NoteOffDisposition == NoteOffDisposition.Indefinite
				? null : checked(offFrame + 1);
		if (state.HasReleasingSubtree && !end.HasValue)
			return null; // An active release is not bounded yet.
		if (state.InheritedAudioReleaseEnd is long inherited)
			end = end.HasValue ? Math.Max(end.Value, inherited) : inherited;
		return end;
	}

	/// <summary>
	/// Natural FM note boundary, before any manually authored Filter tail.
	/// During extra tail frames every ordinary source stops excitation while
	/// each Filter stage keeps processing zero-input frames.
	/// </summary>
	private long? GetBaseEndFrameExclusive(
		int sampleRate, FmSynthState state)
	{
		TimeSpan? noteOffTime = state.NoteOffTime;
		if (noteOffTime.HasValue)
		{
			long releaseFrame = FrameTime.Ceiling(noteOffTime.Value, sampleRate);
			state.HasReleasingSubtree = HasSustainRelease(state);
			state.ReleasingSubtreeEnd = state.HasReleasingSubtree
				? GetReleasingSubtreeEnd(state, releaseFrame, sampleRate) : null;
		}
		else
		{
			state.HasReleasingSubtree = false;
			state.ReleasingSubtreeEnd = null;
		}
		// A single unmodulated output oscillator has no Source EOF,
		// downstream Filter, Operator or release Envelope to traverse.
		// Preserve the same one-frame release/inherited-release rule and
		// scratch gate used by the generic graph calculation.
		if (_singleOutputOscillator is not null && noteOffTime.HasValue)
		{
			long offFrame = FrameTime.Ceiling(noteOffTime.Value, sampleRate);
			long? end = GetIndependentAudioReleaseEnd(0,
				offFrame, sampleRate, state);
			state.SilentFrom[0] = end;
			return end;
		}
		if (!noteOffTime.HasValue && !_hasSources)
			return null;
		long? released = noteOffTime.HasValue
			? FrameTime.Ceiling(noteOffTime.Value, sampleRate) : null;
		long noteOffFrame = released ?? 0;
		// Nested streaming Synths may be queried once per PCM frame by a
		// parent's source lifecycle. Reuse per-invocation scratch, never
		// allocate a fresh graph array for each end check.
		long?[] silentFrom = state.SilentFrom;
		Array.Clear(silentFrom);
		for (int index = 0; index < _nodes.Length; index++)
		{
			CompiledNode node = _nodes[index];
			if (node.Definition is FmEnvelopeNode)
			{
				// Finishing a release leaves its authored last value in
				// force; only a final zero proves this branch silent.
				silentFrom[index] = node.Envelope is null ? 0L
					: released.HasValue
						? !state.HasReleasingSubtree
							&& state.NoteOffDisposition == NoteOffDisposition.Definite
							? checked(noteOffFrame + 1)
							: node.Envelope.IsZeroAfterCompletion
							? node.Envelope.GetEndActiveFrameExclusiveAfterNoteOff(
								noteOffFrame, sampleRate)
							: GetIndependentAudioReleaseEnd(index,
								noteOffFrame, sampleRate, state)
						: node.Envelope.IsZeroAfterCompletion
							? node.Envelope.GetEndActiveFrameExclusiveWithoutNoteOff(
								sampleRate) : null;
			}
			else
			{
				silentFrom[index] = node.Definition switch
				{
					FmFilterNode filter => filter.AudioInputNodeId.HasValue
						? silentFrom[node.InputIndices[0]] : 0L,
					FmOperatorNode op => GetOperatorSilenceEnd(
						op.Operation, node.InputIndices, silentFrom),
					FmSoundSourceNode => GetChildSourceSilenceEnd(state, index),
					FmMultiNode => GetMultiSilenceEnd(state, index),
					FmConstantNode or FmOscillatorNode or FmNoiseNode => released.HasValue
						? GetIndependentAudioReleaseEnd(index,
							noteOffFrame, sampleRate, state) : null,
					_ => null,
				};
			}
		}
		long? outputEnd = silentFrom[_outputIndex];
		if (released.HasValue && state.HasReleasingSubtree
			&& state.ReleasingSubtreeEnd is long releaseEnd)
		{
			// A natural, earlier source EOF still wins; a source
			// ignoring Indefinite Off cannot prolong the parent forever.
			return outputEnd.HasValue
				? Math.Min(outputEnd.Value, releaseEnd) : releaseEnd;
		}
		return outputEnd;
	}

	private static long? GetMultiSilenceEnd(FmSynthState state, int index)
	{
		long max = 0;
		foreach (MultiVoice voice in state.Multis[index])
		{
			// A replacement that has already reached EOF may still carry
			// audible outgoing Sample frames until the fade boundary. At
			// that boundary, or when BOTH children are proved silent
			// earlier, retire the old state while retaining its bounded
			// contribution to the final reported note end.
			if (voice.OnsetFadeFrom is { } retiring
				&& (state.NextFrame >= voice.OnsetFadeEndFrame
					|| (voice.SilenceEnd is long finishedIncomingEnd
						&& retiring.SilenceEnd is long finishedOutgoingEnd
						&& state.NextFrame >= Math.Max(
							finishedIncomingEnd, finishedOutgoingEnd))))
				voice.CompleteOnsetFade();

			if (voice.CanAffectOutput(state.NextFrame))
			{
				if (voice.SilenceEnd is not long incomingEnd)
					return null;
				max = Math.Max(max, incomingEnd);
			}
			if (voice.OnsetFadeFrom is { } outgoing
				&& outgoing.CanAffectOutput(state.NextFrame))
			{
				// The outgoing voice's known EOF may be later than the
				// crossfade, or not yet discoverable at all. Neither
				// extends its audible contribution beyond fadeEnd.
				long boundedEnd = outgoing.SilenceEnd is long childEnd
					? Math.Min(childEnd, voice.OnsetFadeEndFrame)
					: voice.OnsetFadeEndFrame;
				max = Math.Max(max, boundedEnd);
			}
		}
		return max;
	}

	private static long? GetChildSourceSilenceEnd(FmSynthState state, int index)
	{
		SourceVoice? voice = state.Sources[index];
		if (voice is null)
			return 0;
		if (voice.EndFrame is not long end)
			return voice.PostGainSilenceEnd;
		long tail = voice.Filters?.PreparedTailFrames ?? 0;
		long withTail = end > long.MaxValue - tail ? long.MaxValue : end + tail;
		return voice.PostGainSilenceEnd is long postGain
			? Math.Min(withTail, postGain) : withTail;
	}

	private static long? GetOperatorSilenceEnd(
		FmOperatorKind operation,
		IReadOnlyList<int> inputs,
		IReadOnlyList<long?> silentFrom)
	{
		// Called for each operator on every Source-bearing PCM frame.
		// Do not foreach the IReadOnlyList interface: its enumerator
		// boxes/allocates and scales with active Multi constituents.
		if (operation == FmOperatorKind.Multiply)
		{
			// Any factor becoming zero silences the product.
			long? earliest = null;
			for (int i = 0; i < inputs.Count; i++)
				if (silentFrom[inputs[i]] is long end)
					earliest = earliest.HasValue
						? Math.Min(earliest.Value, end) : end;
			return earliest;
		}

		// For addition, minimum and maximum, all inputs must be
		// provably zero before the combination is guaranteed zero.
		long latest = 0;
		for (int i = 0; i < inputs.Count; i++)
		{
			if (silentFrom[inputs[i]] is not long end)
				return null;
			latest = Math.Max(latest, end);
		}
		return latest;
	}

	public void Render(
		RenderContext context,
		SoundState state,
		long startFrame,
		int frameCount,
		Span<float> destination)
	{
		ArgumentNullException.ThrowIfNull(context);
		FmSynthState synthState =
			ValidateState(state);
		if (startFrame < 0)
			throw new ArgumentOutOfRangeException(nameof(startFrame));
		if (frameCount < 0)
			throw new ArgumentOutOfRangeException(nameof(frameCount));

		int outputChannelCount =
			context.Configuration.OutputChannelCount;
		int requiredSamples =
			checked(
				frameCount
					* outputChannelCount);
		if (destination.Length != requiredSamples)
		{
			throw new ArgumentException(
				"Destination length must exactly match frame count and output channel count.",
				nameof(destination));
		}
		if (frameCount == 0)
			return;

		int sampleRate =
			context.Configuration.SampleRate;
		if (synthState.SampleRate != sampleRate)
			synthState.Reset(sampleRate);
		PrepareChildRender(context, synthState);

		EnsureFrame(
			context,
			synthState,
			startFrame);

		// Ordinary mono/stereo/surround renders should not allocate a new
		// gain array for each PCM block. Extremely wide custom layouts use
		// a bounded stack path plus a heap fallback instead of unbounded
		// stack growth.
		Span<double> gains = outputChannelCount <= 16
			? stackalloc double[outputChannelCount]
			: new double[outputChannelCount];
		for (int outputChannel = 0;
			outputChannel < outputChannelCount;
			outputChannel++)
		{
			gains[outputChannel] =
				Spatializer.CalculateGain(
					synthState.Position,
					context.Configuration.OutputChannels[
						outputChannel]);
		}

		long? noteOffFrame =
			synthState.NoteOffTime.HasValue
				? FrameTime.Ceiling(
					synthState.NoteOffTime.Value,
					sampleRate)
				: null;
		long? baseEnd = GetBaseEndFrameExclusive(
			sampleRate, synthState);
		long? finalEnd = AddAuthoredTailFrames(baseEnd, sampleRate);

		for (int frame = 0; frame < frameCount; frame++)
		{
			long relativeFrame =
				checked(startFrame + frame);
			// A direct ISound.Render caller may request frames beyond the
			// advertised lifetime, just as the Pattern transport can. Keep
			// these frames exactly silent even when a recursive IIR would
			// otherwise retain a tiny residual after its explicit budget.
			double value = finalEnd.HasValue
				&& relativeFrame >= finalEnd.Value
				? 0.0
				: EvaluateFrame(context, synthState,
					relativeFrame, noteOffFrame, baseEnd);

			int destinationBase =
				frame * outputChannelCount;
			for (int outputChannel = 0;
				outputChannel < outputChannelCount;
				outputChannel++)
			{
				destination[
					destinationBase
						+ outputChannel] +=
					(float)(
						value
							* gains[outputChannel]);
			}

			synthState.NextFrame =
				checked(relativeFrame + 1);
			if (synthState.ChildEndChanged)
			{
				// Sample EOF can become known only after its integrated
				// pitch trajectory reaches the boundary. Recompute the
				// containing graph's cutoff within this block, so an
				// oscillator branch and serial Filter tails do not run
				// indefinitely after an in-block natural end.
				synthState.ChildEndChanged = false;
				baseEnd = GetBaseEndFrameExclusive(sampleRate, synthState);
				finalEnd = AddAuthoredTailFrames(baseEnd, sampleRate);
			}
		}
	}


	/// <summary>
	/// Multi constituents are known FmSynthSound invocations on an isolated
	/// mono child bus. Render one frame without rebuilding a gain array or
	/// repeating the generic multi-channel/block dispatch for every child.
	/// The same per-frame lifecycle, seeking and natural-end bookkeeping as
	/// Render must apply: this is not a stateless oscillator shortcut.
	/// </summary>
	private float RenderMultiChildFrame(
		RenderContext context, SoundState state, long frame)
	{
		FmSynthState child = ValidateState(state);
		int sampleRate = context.Configuration.SampleRate;
		if (child.SampleRate != sampleRate)
			child.Reset(sampleRate);
		// Single-oscillator children cannot contain Source preparations,
		// panning, Filter state or a nested Multi context. Preserve the
		// frozen preparation sample-rate invariant without invoking the
		// generic Source traversal on every sample.
		bool singleOscillator = _singleOutputOscillator is not null;
		if (!singleOscillator)
			PrepareChildRender(context, child);
		else if (_preparedChildFilterFactory is { } prepared
			&& prepared.SampleRate != sampleRate)
			throw new InvalidOperationException(
				"Frozen source voices cannot change sample rate after preparation.");
		// Sequential child frames are the common path. EnsureFrame is
		// necessary only after a first randomized-phase offset, a gap or
		// an explicit replay, and it may itself render missing history.
		if (child.NextFrame != frame)
			EnsureFrame(context, child, frame);

		long? noteOffFrame = !singleOscillator && child.NoteOffTime.HasValue
			? FrameTime.Ceiling(child.NoteOffTime.Value, sampleRate) : null;
		// Without Note Off, the single-oscillator graph has no Source
		// or nested child that could discover a finite natural end.
		// GetBaseEndFrameExclusive would immediately return null without
		// modifying the audio-silence scratch; avoid doing that per child,
		// per sample. Released children retain the inherited release gate.
		long? baseEnd = singleOscillator && !child.HasNoteOffTime
			? null : GetBaseEndFrameExclusive(sampleRate, child);
		// The single-oscillator graph has no authored Filter tail. The
		// per-frame evaluator does not use noteOffFrame in this shape.
		long? finalEnd = singleOscillator
			? baseEnd : AddAuthoredTailFrames(baseEnd, sampleRate);
		double value = finalEnd.HasValue && frame >= finalEnd.Value
			? 0.0
			: EvaluateFrame(context, child, frame, noteOffFrame, baseEnd);

		child.NextFrame = checked(frame + 1);
		if (child.ChildEndChanged)
		{
			child.ChildEndChanged = false;
			// Keep the same in-block natural-end reconciliation as Render.
			// This updates SilentFrom after a newly discovered Sample EOF.
			GetBaseEndFrameExclusive(sampleRate, child);
		}
		// No positional gain may be silently dropped. The Multi mono bus
		// has zero positional importance, so the spatializer's neutral
		// fast path returns unity for finite positions, while preserving
		// its existing non-finite diagnostic behavior.
		float amplitude = 0;
		amplitude += (float)(value * Spatializer.CalculateGain(
			child.Position, context.Configuration.OutputChannels[0]));
		return amplitude;
	}

	private void EnsureFrame(
		RenderContext context,
		FmSynthState state,
		long startFrame)
	{
		if (state.NextFrame > startFrame)
		{
			if (_hasSources)
				throw new InvalidOperationException(
					"Backward Synth source playback requires PrepareReplay at a producer/control boundary.");
			state.Reset(context.Configuration.SampleRate);
		}

		long? noteOffFrame =
			state.NoteOffTime.HasValue
				? FrameTime.Ceiling(
					state.NoteOffTime.Value,
					context.Configuration.SampleRate)
				: null;
		long? baseEnd = GetBaseEndFrameExclusive(
			context.Configuration.SampleRate, state);
		long? finalEnd = AddAuthoredTailFrames(
			baseEnd, context.Configuration.SampleRate);

		while (state.NextFrame < startFrame)
		{
			if (!finalEnd.HasValue || state.NextFrame < finalEnd.Value)
				EvaluateFrame(context, state, state.NextFrame,
					noteOffFrame, baseEnd);
			state.NextFrame++;
			if (state.ChildEndChanged)
			{
				state.ChildEndChanged = false;
				baseEnd = GetBaseEndFrameExclusive(
					context.Configuration.SampleRate, state);
				finalEnd = AddAuthoredTailFrames(
					baseEnd, context.Configuration.SampleRate);
			}
		}
	}

	private double EvaluateFrame(
		RenderContext context,
		FmSynthState state,
		long frame,
		long? noteOffFrame,
		long? baseEnd)
	{
		// The same authored pitch segment is shared by all Multi children
		// at this outer frame. Reuse the fetched value when retuning each
		// constituent, retaining the original multiplication order.
		double trajectoryMultiplier = state.PitchTrajectory.GetMultiplier(frame);
		double pitchMultiplier =
			state.PitchMultiplier
				* state.PlaybackSpeedMultiplier
				* trajectoryMultiplier;
		if (!double.IsFinite(pitchMultiplier)
			|| !(pitchMultiplier > 0.0))
		{
			throw new InvalidOperationException(
				"FM synth pitch multiplier became non-positive or non-finite.");
		}

		bool draining = baseEnd.HasValue && frame >= baseEnd.Value;
		if (_singleOutputOscillator is { } outputOscillator)
		{
			// The single oscillator has no FM ports, downstream Operator,
			// Filter or other graph state to dispatch. Preserve the exact
			// original oscillator evaluator, finite checks, phase timing and
			// both raw/audible value slots for live voice adoption.
			FmOscillatorNode oscillator =
				(FmOscillatorNode)outputOscillator.Definition;
			double value = draining ? 0.0 : EvaluateOscillator(
				oscillator, outputOscillator, state, pitchMultiplier,
				context.Configuration.SampleRate);
			if (!double.IsFinite(value))
				throw new InvalidOperationException(
					$"FM node {oscillator.Id} produced a non-finite value.");
			state.Values[0] = value;
			// A mono oscillator can smoothly adopt a new output shape
			// without advancing a second phase or allocating in PCM. The
			// first revised frame exactly repeats the last audible value;
			// frame +64 is fully the revised waveform. Repeated edits during
			// an active fade use the last blended value as their new origin.
			if (state.StatelessOutputFadeFrom is double outgoing)
			{
				long delta = frame - state.StatelessOutputFadeStartFrame;
				if (delta >= LiveTransitionFrames(context.Configuration.SampleRate))
					state.ClearStatelessOutputFade();
				else if (delta >= 0)
				{
					double weight = MultiTransitionWeight(
						state.StatelessOutputFadeStartFrame, frame,
						context.Configuration.SampleRate);
					value = outgoing * (1.0 - weight) + value * weight;
				}
			}
			state.AudioValues[0] = state.SilentFrom[0] is long silent
				&& frame >= silent ? 0.0 : value;
			return state.AudioValues[0];
		}
		for (int index = 0;
			index < _nodes.Length;
			index++)
		{
			CompiledNode compiled =
				_nodes[index];
			double value = draining && compiled.Definition is
				(FmConstantNode or FmEnvelopeNode or FmOscillatorNode or FmNoiseNode)
				? 0.0 : compiled.Definition switch
				{
					FmConstantNode constant =>
						constant.Value,

					FmNoiseNode noise =>
						EvaluateNoise(noise, state.NoiseSeed, frame),

					FmEnvelopeNode =>
						EvaluateEnvelope(
							compiled.Envelope,
							frame,
							context.Configuration.SampleRate,
							noteOffFrame),

					FmFilterNode =>
						ProcessAudioFilter(compiled, state, index,
							context.Configuration.SampleRate, draining),

					FmOperatorNode operation =>
						EvaluateOperator(
							operation.Operation,
							compiled.InputIndices,
							state.Values),

					FmSoundSourceNode sourceNode =>
						EvaluateSource(sourceNode, compiled, state, index,
							frame, context.Configuration.SampleRate, noteOffFrame),

					FmMultiNode multi => EvaluateMulti(multi, state, index,
						frame, context, trajectoryMultiplier),

					FmOscillatorNode oscillator =>
						EvaluateOscillator(
							oscillator,
							compiled,
							state,
							pitchMultiplier,
							context.Configuration.SampleRate),

					_ =>
						throw new NotSupportedException(
							$"FM node type {compiled.Definition.GetType().FullName} is not supported."),
				};

			if (!double.IsFinite(value))
			{
				throw new InvalidOperationException(
					$"FM node {compiled.Definition.Id} produced a non-finite value.");
			}

			state.Values[index] = value;
			// Keep raw graph values for pitch/parameter controls, but gate
			// the *audible* contribution independently. A single oscillator
			// may legitimately drive both a Sample pitch port and an Add.
			state.AudioValues[index] = compiled.Definition switch
			{
				FmConstantNode or FmOscillatorNode or FmNoiseNode or FmEnvelopeNode
					when state.SilentFrom[index] is long silent
						&& frame >= silent => 0.0,
				FmOperatorNode op => EvaluateOperator(op.Operation,
					compiled.InputIndices, state.AudioValues),
				_ => value,
			};
		}

		// The generic path retains raw oscillator/Operator values in
		// Values for pitch controls. Stateless output smoothing applies once,
		// after the final stateless Operator has produced audible output.
		// The final AudioValues entry is blended too, so a rapid second
		// edit correctly captures the previous *actual* audible output.
		if (state.HasAdditiveOutputTransition)
			state.AudioValues[_outputIndex] = state.ApplyAdditiveOutputTransition(
				frame, state.AudioValues[state.AdditiveSignalIndex]);
		else if (state.StatelessOutputFadeFrom is double outgoingGraphOutput)
		{
			long delta = frame - state.StatelessOutputFadeStartFrame;
			if (delta >= LiveTransitionFrames(context.Configuration.SampleRate))
				state.ClearStatelessOutputFade();
			else if (delta >= 0)
			{
				double weight = MultiTransitionWeight(
					state.StatelessOutputFadeStartFrame, frame,
					context.Configuration.SampleRate);
				state.AudioValues[_outputIndex] = outgoingGraphOutput * (1.0 - weight)
					+ state.AudioValues[_outputIndex] * weight;
			}
		}
		return state.AudioValues[_outputIndex];
	}

	private static ulong MultiHash(ulong word)
	{
		unchecked
		{
			word += 0x9E3779B97F4A7C15UL;
			word = (word ^ (word >> 30)) * 0xBF58476D1CE4E5B9UL;
			word = (word ^ (word >> 27)) * 0x94D049BB133111EBUL;
			return word ^ (word >> 31);
		}
	}

	/// <summary>Pure, allocation-free onset calculation shared by the
	/// ordinary note path and future off-worker replacement preparation.
	/// Keep the exact existing hash, cycle arithmetic, rounding and fade
	/// semantics so changing a Multi's initial phase policy cannot alter
	/// unrelated notes or seeded replay.</summary>
	private int CalculateMultiSkip(FmMultiNode multi, ulong noiseSeed,
		int index, int voiceIndex, double pitch, double speed, int sampleRate)
	{
		double frequency = FmMultiNode.NeutralFrequencyHz * pitch * speed;
		// A sole unmodulated oscillator has a knowable cycle;
		// other graphs inherit the requested note's nominal pitch.
		if (multi.CycleLength is null && _nodes[index].MultiInput is { } sub
			&& sub._nodes[sub._outputIndex].Definition is FmOscillatorNode
				{ MultiplierNodeId: null } oscillator)
			frequency = oscillator.FrequencyHz
				* (oscillator.Constant ? 1.0 : pitch * speed);
		double length = multi.CycleLength is double overrideLength
			? overrideLength * sampleRate / FmMultiNode.ReferenceSampleRate
				* FmMultiNode.NeutralFrequencyHz / frequency
			: sampleRate / frequency;
		if (!double.IsFinite(length) || length > 1000000.0)
			throw new InvalidOperationException(
				"Multi cycle length exceeds the one-million-frame safety limit.");
		int cycle = Math.Max(1, (int)Math.Round(length));
		return multi.RandomizePhase
			? (int)(MultiHash(noiseSeed
				^ ((ulong)(uint)multi.Id << 32)
				^ (ulong)(uint)(voiceIndex + 1)) % (ulong)cycle)
			: 0;
	}

	private void InitializeMultiOnset(FmMultiNode multi, ulong noiseSeed,
		int index, MultiVoice voice, double pitch, double speed, int sampleRate)
	{
		voice.SkipFrames = CalculateMultiSkip(
			multi, noiseSeed, index, voice.Index, pitch, speed, sampleRate);
		voice.FadeFrames = (voice.SkipFrames + 1) / 2;
		voice.Initialized = true;
	}

	private double EvaluateMulti(FmMultiNode multi, FmSynthState state,
		int index, long frame, RenderContext context,
		double pitchTrajectoryMultiplier)
	{
		MultiVoice[] voices = state.Multis[index];
		if (voices.Length == 0)
			return 0.0;
		int sampleRate = context.Configuration.SampleRate;
		long? externalEnd = null;
		if (state.NoteOffTime is TimeSpan releasedTime)
		{
			long releaseFrame = FrameTime.Ceiling(releasedTime, sampleRate);
			externalEnd = GetIndependentAudioReleaseEnd(index,
				releaseFrame, sampleRate, state) ?? long.MaxValue;
		}
		double sum = 0.0;
		foreach (MultiVoice voice in voices)
		{
			if (!voice.HasStarted)
				continue; // dormant growth reserve needs no DSP work
			double incoming = RenderMultiConstituent(multi, state, index,
				frame, sampleRate, externalEnd, pitchTrajectoryMultiplier, voice);
			if (voice.OnsetFadeFrom is { } outgoing)
			{
				double weight = MultiTransitionWeight(
					voice.OnsetFadeStartFrame, frame, sampleRate);
				if (weight >= 1.0)
				{
					voice.CompleteOnsetFade();
					state.ChildEndChanged = true;
					sum += incoming;
				}
				else
				{
					double previous = RenderMultiConstituent(
						multi, state, index, frame, sampleRate, externalEnd,
						pitchTrajectoryMultiplier, outgoing);
					sum += weight <= 0.0
						? previous
						: previous + (incoming - previous) * weight;
					// If Note Off makes BOTH child histories permanently
					// silent before the 64-frame fade ends, promptly release
					// the outgoing state instead of retaining a dead tail.
					if (voice.SilenceEnd is long incomingEnd
						&& outgoing.SilenceEnd is long outgoingEnd
						&& frame >= incomingEnd && frame >= outgoingEnd)
					{
						voice.CompleteOnsetFade();
						state.ChildEndChanged = true;
					}
				}
			}
			else
				sum += incoming;
		}
		return sum;
	}

	/// <summary>One constituent's original audio and release bookkeeping,
	/// shared by normal Multi rendering and the two bounded, preallocated
	/// children of a live-onset crossfade. No invocation is built or
	/// replayed while rendering an audio quantum.</summary>
	private double RenderMultiConstituent(
		FmMultiNode multi, FmSynthState state, int index, long frame,
		int sampleRate, long? externalEnd, double pitchTrajectoryMultiplier,
		MultiVoice voice)
	{
		SoundState child = voice.Invocation.State;
		voice.UseSampleRate(sampleRate);
		long localFrame = frame - voice.StartFrame;
		// Glide the ratio forward without resetting oscillator phase or
		// re-scaling already-integrated Source playback positions.
		child.PitchMultiplier = state.PitchMultiplier * voice.RatioAt(frame)
			* pitchTrajectoryMultiplier;
		child.PlaybackSpeedMultiplier = state.PlaybackSpeedMultiplier;
		if (!voice.Initialized)
			InitializeMultiOnset(multi, state.NoiseSeed, index, voice,
				child.PitchMultiplier, child.PlaybackSpeedMultiplier, sampleRate);
		long displaced = checked(localFrame + voice.SkipFrames);
		child.NoteOffDisposition = state.NoteOffDisposition == NoteOffDisposition.Indefinite
			|| HasSustainRelease(state)
			? NoteOffDisposition.Indefinite : NoteOffDisposition.Definite;
		child.NoteOffTime = state.NoteOffTime is TimeSpan off
			? off - TimeSpan.FromSeconds((double)voice.StartFrame / sampleRate)
				+ TimeSpan.FromSeconds((double)voice.SkipFrames / sampleRate)
			: null;
		if (child is FmSynthState inner)
			inner.InheritedAudioReleaseEnd = externalEnd is long releaseEnd
				? releaseEnd >= long.MaxValue - voice.SkipFrames
					? long.MaxValue
					: Math.Max(0, releaseEnd - voice.StartFrame
						+ voice.SkipFrames)
				: null;
		double amplitude = voice.Sound.RenderMultiChildFrame(
			state.MonoContext!, child, displaced);
		if (voice.FadeFrames > 0 && localFrame < voice.FadeFrames)
		{
			double t = (double)localFrame / voice.FadeFrames;
			amplitude *= 0.5 - 0.5 * Math.Cos(Math.PI * t);
		}
		// A held one-node oscillator has no natural EOF. Avoid querying its
		// whole lifetime every child/sample. Released voices still use the
		// complete inherited-release calculation; Source/Filter/nested graphs
		// retain their existing paths.
		long? end = voice.Sound._singleOutputOscillator is not null
			&& child.NoteOffTime is null ? null
			: voice.Sound.GetEndFrameExclusive(
				state.MonoContext!, child);
		if (end.HasValue)
		{
			long audibleEnd = Math.Max(0L,
				end.Value - voice.SkipFrames + voice.StartFrame);
			if (voice.SilenceEnd != audibleEnd)
			{
				voice.SilenceEnd = audibleEnd;
				state.ChildEndChanged = true;
			}
		}
		return amplitude * voice.MixAt(frame);
	}

	private static double ProcessAudioFilter(CompiledNode node,
		FmSynthState state, int index, int sampleRate, bool draining)
	{
		FmFilterNode filter = (FmFilterNode)node.Definition;
		FmFilterNodeProcessor.State filterState = state.Filters[index]
			??= node.Filter!.CreateState(sampleRate);
		if (!filter.AudioInputNodeId.HasValue)
			return node.Filter!.Process(filterState, state.Values, draining);
		int input = node.InputIndices[0];
		double raw = state.Values[input];
		state.Values[input] = state.AudioValues[input];
		try
		{
			return node.Filter!.Process(filterState, state.Values, draining);
		}
		finally
		{
			state.Values[input] = raw;
		}
	}

	/// <summary>One isolated mono child frame with per-voice DSP state.
	/// The mono output speaker has zero positional importance, so it never
	/// applies the parent's spatialization. SampleSound stereo source channels
	/// are averaged rather than summed; other ISound implementations already
	/// own their own scalar mix semantics.</summary>
	private static double EvaluateSource(FmSoundSourceNode node,
		CompiledNode compiled, FmSynthState state, int index,
		long frame, int sampleRate, long? noteOffFrame)
	{
		SourceVoice? voice = state.Sources[index];
		if (voice is null)
			return 0.0;

		// The child is selected once per voice. Its relative pitch curve is
		// then integrated by SampleSound (or evaluated by nested Synths).
		// Mutating SoundState.PitchMultiplier instead would retune every
		// sample against the same source position, breaking continuous FM.
		double modulation = compiled.InputIndices.Length == 0 ? 1.0
			: state.Values[compiled.InputIndices[0]];
		double multiplier = node.ExponentialMultiplier
			? Math.Pow(2.0, modulation / 12.0) : modulation;
		if (!double.IsFinite(multiplier))
			throw new InvalidOperationException(
				$"Synth source {node.Id} produced non-finite modulation.");
		// Keep the child's initial SampleSound/PitchTrajectory scale fixed:
		// changing it would retroactively multiply *all* previously
		// integrated sample positions. Instead retune only the current and
		// subsequent frames of the forward-integrated pitch curve.
		double outer = node.Constant ? 1.0
			: state.PitchMultiplier * state.PlaybackSpeedMultiplier
				* state.PitchTrajectory.GetMultiplier(frame);
		outer *= (node.FrequencyHz / voice.OriginalFrequencyHz)
			/ voice.OriginalOuterFactor;
		long envelopeFrame = voice.EnvelopeFrameOffset == 0
			? frame : checked(frame + voice.EnvelopeFrameOffset);
		// The enclosing Multi displaces Note Off by its local skip.
		// Shift that event into the same original Instrument envelope
		// clock as the preserved activeFrame before evaluating release.
		long? envelopeNoteOffFrame = noteOffFrame.HasValue
			? checked(noteOffFrame.Value + voice.EnvelopeFrameOffset)
			: null;
		long? released = envelopeNoteOffFrame.HasValue
			&& envelopeFrame >= envelopeNoteOffFrame.Value
			? envelopeNoteOffFrame : null;
		// Pitch envelopes are in octaves, as in PlaybackVoice.
		if (voice.Invocation.Configuration.Envelopes.Pitch is { } pitchEnvelope)
			multiplier *= Math.Pow(2.0,
				pitchEnvelope.GetValue(frame, sampleRate, released));
		double rate = outer * multiplier;
		if (!double.IsFinite(rate))
			throw new InvalidOperationException(
				$"Synth source {node.Id} produced non-finite playback rate.");
		// A non-positive linear input is a silent, stationary source,
		// not reverse playback. The smallest positive rate avoids exposing
		// an invalid SoundState while keeping source position effectively
		// frozen. The graph emits zero until the input becomes positive.
		bool muted = rate <= 0.0;
		voice.Pitch.SetFrame(frame, muted ? 1e-12 : Math.Max(rate, 1e-12));
		Span<float> mono = stackalloc float[1];
		if (!voice.EndFrame.HasValue || frame < voice.EndFrame.Value)
		{
			if (voice.Invocation.Configuration.Envelopes.Panning is { } panEnvelope)
			{
				// A graph node is scalar: spatialize only on this isolated
				// stereo bus, then fold it back to mono before graph mixing.
				double pan = Math.Clamp(panEnvelope.GetValue(
					frame, sampleRate, released), -1.0, 1.0);
				voice.Invocation.State.Position = new Vector3((float)pan, 0, 0);
				Span<float> stereo = stackalloc float[2];
				voice.Sound.Render(state.StereoChildContext!,
					voice.Invocation.State, frame, 1, stereo);
				// A native stereo Sample has spatially separated source
				// channels; a mono source uses equal-power speaker gain.
				double divisor = voice.Sound is SampleSound pannedSample
					&& pannedSample.SourceChannelCount == 2 ? 2.0 : Math.Sqrt(2);
				mono[0] = (float)((stereo[0] + stereo[1]) / divisor);
			}
			else
				voice.Sound.Render(state.MonoContext!,
					voice.Invocation.State, frame, 1, mono);
			// Streaming sources discover their finite boundary only while
			// rendering. A nested Synth does not necessarily publish a
			// NaturalEndFrameExclusive in SoundState, so explicitly query
			// its post-render end rather than waiting for another PCM block.
			long? observed = voice.Invocation.State.NaturalEndFrameExclusive;
			if (voice.Sound is IStreamingFiniteSound streaming)
			{
				long? nestedEnd = streaming.GetEndFrameExclusive(
					state.MonoContext!, voice.Invocation.State);
				if (nestedEnd.HasValue)
					observed = observed.HasValue
						? Math.Min(observed.Value, nestedEnd.Value) : nestedEnd;
			}
			if (observed is long natural)
			{
				long? previous = voice.EndFrame;
				voice.EndFrame = previous.HasValue
					? Math.Min(previous.Value, natural) : natural;
				if (voice.EndFrame != previous)
					state.ChildEndChanged = true;
			}
		}
		if (voice.EndFrame.HasValue && frame >= voice.EndFrame.Value
			&& voice.Filters is not null && !voice.InputFinished)
		{
			voice.Filters.FinishInput();
			voice.InputFinished = true;
		}
		if (voice.Invocation.Configuration.Envelopes.Panning is null
			&& voice.Sound is SampleSound sample)
			mono[0] /= sample.SourceChannelCount;
		if (muted)
			mono[0] = 0;
		if (voice.Filters is { } filters)
		{
			if (voice.InputFinished)
				filters.RenderTail(mono);
			else
				filters.ProcessFrame(mono);
		}
		if (voice.Invocation.Configuration.Envelopes.Filter is { } filterEnvelope)
		{
			// The legacy IT Filter Envelope is an absolute normalized
			// cutoff, independent of the serial tone-Filter layers.
			double cutoff = Math.Clamp(filterEnvelope.GetValue(
				frame, sampleRate, released), 0.0, 1.0);
			voice.FilterEnvelopeState!.SetParameters(
				new ResonantFilterParameters(cutoff, 0));
			voice.FilterEnvelopeState.ProcessFrame(mono);
		}
		if (voice.ActiveVolumeEnvelope is not null)
			mono[0] *= (float)voice.EvaluateVolume(
				frame, envelopeFrame, sampleRate, released);
		return mono[0];
	}

	private void PrepareChildRender(
		RenderContext context, FmSynthState state)
	{
		if (_preparedChildFilterFactory is { } prepared
			&& prepared.SampleRate != context.Configuration.SampleRate)
			throw new InvalidOperationException(
				"Frozen source voices cannot change sample rate after preparation.");
		if (state.MonoContext is null)
		{
			state.MonoContext = new RenderContext(new RenderConfiguration(
				context.Configuration.SampleRate, [
				new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0)]));
			if (state.Sources.Any(source =>
				source?.Invocation.Configuration.Envelopes.Panning is not null))
				state.StereoChildContext = new RenderContext(
					RenderConfiguration.Stereo(context.Configuration.SampleRate));
			for (int i = 0; i < state.Sources.Length; i++)
			{
				SourceVoice? voice = state.Sources[i];
				if (voice?.Invocation.ToneFilters is { } filters
					&& voice.Filters is null)
				{
					if (_preparedChildFilterFactory is not null)
						throw new InvalidOperationException(
							"Frozen tone Filter plans must exist before PCM rendering.");
					// Legacy direct ISound users that did not supply a producer
					// preparation context retain their existing lazy behavior.
					voice.Filters = new StreamingFilterChain(filters,
						context.Configuration.SampleRate, 1);
				}
				if (voice?.Invocation.Configuration.Envelopes.Filter is not null
					&& voice.FilterEnvelopeState is null)
				{
					if (_preparedChildFilterFactory is not null)
						throw new InvalidOperationException(
							"Frozen Filter envelope state must exist before PCM rendering.");
					voice.FilterEnvelopeState = new ResonantFilterState(1,
						context.Configuration.SampleRate);
				}
			}
		}
		bool forwardIndefinite = state.NoteOffDisposition ==
			NoteOffDisposition.Indefinite || HasSustainRelease(state);
		foreach (SourceVoice? voice in state.Sources)
		{
			if (voice is null)
				continue;
			voice.Invocation.State.NoteOffDisposition = forwardIndefinite
				? NoteOffDisposition.Indefinite : NoteOffDisposition.Definite;
			if (voice.Invocation.State.NoteOffTime != state.NoteOffTime)
				voice.Invocation.State.NoteOffTime = state.NoteOffTime;
			long? childEnd = voice.Sound.GetEndFrameExclusive(
				state.MonoContext, voice.Invocation.State);
			if (voice.Invocation.State.NaturalEndFrameExclusive is long natural)
				childEnd = childEnd.HasValue
					? Math.Min(childEnd.Value, natural) : natural;
			// Every active local envelope, not only Volume, contributes to
			// the parent graph's collective release lifetime. The parent
			// caps this source after those releases, rather than faking a
			// child Synth release or cutting its Sample at Note Off.
			// The source's Volume gain is after all of its tone Filters.
			// Once that curve is *finished* and permanently zero, the
			// source is provably silent even if Pitch/Filter envelopes are
			// still releasing. A nonzero terminal gain must not cut it.
			voice.PostGainSilenceEnd = null;
			if (state.NoteOffTime.HasValue
				&& voice.ActiveVolumeEnvelope?.IsZeroAfterCompletion == true)
			{
				long off = checked(FrameTime.Ceiling(state.NoteOffTime.Value,
					context.Configuration.SampleRate) + voice.EnvelopeFrameOffset);
				long? finished = voice.ActiveVolumeEnvelope
					.GetEndActiveFrameExclusiveAfterNoteOff(off,
						context.Configuration.SampleRate);
				if (finished.HasValue)
				{
					long silence = checked(finished.Value - voice.EnvelopeFrameOffset);
					if (voice.GetOutgoingVolumeReleaseEnd(off,
						context.Configuration.SampleRate) is long outgoingEnd)
						silence = Math.Max(silence, outgoingEnd);
					voice.PostGainSilenceEnd = silence;
					childEnd = childEnd.HasValue
						? Math.Min(childEnd.Value, silence) : silence;
				}
			}
			// Once the zero-input tail has started, never reopen it merely
			// because a child cannot project an updated pitch trajectory.
			if (!voice.InputFinished)
				voice.EndFrame = childEnd;
		}
	}

	private static double EvaluateEnvelope(
		IEnvelopeCurve? curve,
		long frame,
		int sampleRate,
		long? noteOffFrame)
	{
		if (curve is null)
			return 0.0;

		long? effectiveNoteOff =
			noteOffFrame.HasValue
				&& frame >= noteOffFrame.Value
					? noteOffFrame
					: null;

		return curve.GetValue(
			frame,
			sampleRate,
			effectiveNoteOff);
	}

	private static double EvaluateOperator(
		FmOperatorKind operation,
		IReadOnlyList<int> inputs,
		IReadOnlyList<double> values)
	{
		double result =
			values[inputs[0]];

		switch (operation)
		{
			case FmOperatorKind.Add:
				for (int i = 1; i < inputs.Count; i++)
					result += values[inputs[i]];
				break;

			case FmOperatorKind.Multiply:
				for (int i = 1; i < inputs.Count; i++)
					result *= values[inputs[i]];
				break;

			case FmOperatorKind.Minimum:
				for (int i = 1; i < inputs.Count; i++)
					result = Math.Min(result, values[inputs[i]]);
				break;

			case FmOperatorKind.Maximum:
				for (int i = 1; i < inputs.Count; i++)
					result = Math.Max(result, values[inputs[i]]);
				break;

			default:
				throw new ArgumentOutOfRangeException(
					nameof(operation));
		}

		return result;
	}

	/// <summary>Counter-based SplitMix64 whitening. One full-band random
	/// sample per native output frame. No evolving RNG state is necessary:
	/// seed, node and frame identify an exact sample after arbitrary seeks.</summary>
	private static double EvaluateNoise(FmNoiseNode node, ulong voiceSeed, long frame)
	{
		unchecked
		{
			ulong word = voiceSeed
				^ ((ulong)(uint)node.Id * 0xD6E8FEB86659FD93UL)
				^ ((ulong)frame * 0x9E3779B97F4A7C15UL)
				^ 0x4E4F495345484552UL;
			word ^= word >> 30;
			word *= 0xBF58476D1CE4E5B9UL;
			word ^= word >> 27;
			word *= 0x94D049BB133111EBUL;
			word ^= word >> 31;
			return (word >> 11) * (2.0 / (1UL << 53)) - 1.0;
		}
	}

	private static double EvaluateOscillator(
		FmOscillatorNode oscillator,
		CompiledNode compiled,
		FmSynthState state,
		double pitchMultiplier,
		int sampleRate)
	{
		double phase =
			state.Phases[
				compiled.OscillatorIndex];
		double normalized = FmOscillatorWaveformEvaluator.EvaluateNormalized(
			oscillator.Waveform, phase);

		// k == 0 takes this direct branch: no table or exponential work.
		if (compiled.Curvature is { } curvature)
			normalized = curvature.Transform(normalized);

		double midpoint =
			(oscillator.Minimum
				+ oscillator.Maximum)
				/ 2.0;
		double halfRange =
			(oscillator.Maximum
				- oscillator.Minimum)
				/ 2.0;
		double output =
			midpoint
				+ (normalized * halfRange);

		double nodeMultiplier = 1.0;
		if (compiled.InputIndices.Length != 0)
		{
			double input =
				state.Values[
					compiled.InputIndices[0]];
			nodeMultiplier =
				oscillator.ExponentialMultiplier
					? Math.Pow(
						2.0,
						input / 12.0)
					: input;
		}

		double frequency =
			oscillator.FrequencyHz
				* (oscillator.Constant ? 1.0 : pitchMultiplier)
				* nodeMultiplier;
		if (!double.IsFinite(frequency))
		{
			throw new InvalidOperationException(
				$"FM oscillator {oscillator.Id} produced a non-finite frequency.");
		}

		phase +=
			frequency / sampleRate;
		phase -= Math.Floor(phase);
		state.Phases[
			compiled.OscillatorIndex] = phase;

		return output;
	}

	private static FmSynthState ValidateState(
		SoundState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		if (state is not FmSynthState synthState)
		{
			throw new ArgumentException(
				$"State must be created by {nameof(FmSynthSound)}.",
				nameof(state));
		}

		return synthState;
	}
}

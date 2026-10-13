using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Playback;
using Heresy.Render.Samples;
using Heresy.Render.Envelopes;
using Heresy.Render.Filters;
using Heresy.Render.Sounds;
using Heresy.Render.Spatial;
using Heresy.Render.Timing;

namespace Heresy.Render.FmSynthesis;

public sealed class FmSynthSound : ISound, IFirPreparationSound
{
	private sealed class SourceVoice
	{
			public SourceVoice(SoundInvocation invocation, double originalOuterFactor)
		{
			Invocation = invocation;
			OriginalOuterFactor = originalOuterFactor;
			Pitch = new FmSourcePitchCurve();
			invocation.State.PitchTrajectory.SetCurve(0, Pitch);
		}
		public SoundInvocation Invocation { get; }
		public double OriginalOuterFactor { get; }
		public FmSourcePitchCurve Pitch { get; }
		public StreamingFilterChain? Filters { get; set; }
		public ResonantFilterState? FilterEnvelopeState { get; set; }
		public bool InputFinished { get; set; }
		public long? EndFrame { get; set; }
	}

	private sealed class FmSynthState : SoundState
	{
		public FmSynthState(
			int nodeCount,
			int oscillatorCount,
			SourceVoice?[] sources)
		{
			Values = new double[nodeCount];
			Phases = new double[oscillatorCount];
			Filters = new FmFilterNodeProcessor.State?[nodeCount];
			Sources = sources;
		}

		public double[] Values { get; }
		public double[] Phases { get; }
		public FmFilterNodeProcessor.State?[] Filters { get; }
		public SourceVoice?[] Sources { get; }
		public RenderContext? MonoContext { get; set; }
		public RenderContext? StereoChildContext { get; set; }
		public long NextFrame { get; set; }
		public int SampleRate { get; set; }

		public void Reset(int sampleRate)
		{
			if (Sources.Any(source => source is not null) && SampleRate != 0)
				throw new NotSupportedException(
					"Replaying child-source Synth state requires the subsequent seek milestone.");
			Array.Clear(Values);
			Array.Clear(Phases);
			Array.Clear(Filters); // never reuse histories after rewind/rate change
			NextFrame = 0;
			SampleRate = sampleRate;
		}
	}

	private sealed record CompiledNode(
		FmSynthNode Definition,
		int[] InputIndices,
		int OscillatorIndex,
		IEnvelopeCurve? Envelope,
		FmFilterNodeProcessor? Filter,
		ISound? Source);

	private readonly CompiledNode[] _nodes;
	private readonly int _outputIndex;
	private readonly int _oscillatorCount;
	private readonly NewNotePolicy _newNotePolicy;
	private readonly bool _hasSources;
	private const double C4FrequencyHz = 440.0 * 0.59460355750136053336; // 2^(-9/12)

	public FmSynthSound(
		FmSynthGraph graph,
		IEnvelopeCurveResolver envelopeResolver,
		NewNotePolicy? newNotePolicy = null,
		ISoundResolver? soundResolver = null)
	{
		ArgumentNullException.ThrowIfNull(graph);
		ArgumentNullException.ThrowIfNull(envelopeResolver);

		_newNotePolicy =
			newNotePolicy
				?? NewNotePolicy.Cut;

		Dictionary<int, FmSynthNode> byId =
			graph.Nodes.ToDictionary(
				node => node.Id);
		List<FmSynthNode> ordered = [];
		HashSet<int> visited = [];

		Visit(graph.OutputNodeId);

		_hasSources = ordered.Any(node => node is FmSoundSourceNode);
		if (_hasSources && soundResolver is null)
			throw new NotSupportedException(
				"ISound-fed Synth nodes require the prepared child-invocation resolver.");
		// Input modulation is evaluated in graph order and drives the
		// invocation-local child pitch trajectory once per rendered frame.

		Dictionary<int, int> indexById = [];
		for (int index = 0; index < ordered.Count; index++)
			indexById.Add(ordered[index].Id, index);

		List<CompiledNode> compiled = [];
		int oscillatorIndex = 0;
		foreach (FmSynthNode node in ordered)
		{
			int[] inputs =
				node.InputNodeIds
					.Select(id => indexById[id])
					.ToArray();

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

			ISound? sourceSound = null;
			if (node is FmSoundSourceNode sourceNode
				&& !sourceNode.SourceId.IsNone)
			{
				// Resolving is done on the frozen-song preparation thread,
				// never in CreateInvocation or EvaluateFrame.
				soundResolver!.TryResolve(sourceNode.SourceId,
					mixdown: false, out sourceSound);
			}
			compiled.Add(
				new CompiledNode(
					node,
					inputs,
					phaseIndex,
					curve,
					node is FmFilterNode filter
						? new FmFilterNodeProcessor(filter, indexById) : null,
					sourceSound));
		}

		_nodes = [.. compiled];
		_outputIndex = indexById[graph.OutputNodeId];
		_oscillatorCount = oscillatorIndex;

		void Visit(int id)
		{
			if (!visited.Add(id))
				return;

			FmSynthNode node = byId[id];
			foreach (int inputId in node.InputNodeIds)
				Visit(inputId);
			ordered.Add(node);
		}
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

	private FmSynthState CreateBoundState(
		double pitchMultiplier, double playbackSpeedMultiplier)
	{
		if (!(pitchMultiplier > 0.0) || !double.IsFinite(pitchMultiplier))
			throw new ArgumentOutOfRangeException(nameof(pitchMultiplier));
		if (!(playbackSpeedMultiplier > 0.0) || !double.IsFinite(playbackSpeedMultiplier))
			throw new ArgumentOutOfRangeException(nameof(playbackSpeedMultiplier));
		SourceVoice?[] sources = new SourceVoice?[_nodes.Length];
		for (int i = 0; i < _nodes.Length; i++)
		{
			if (_nodes[i].Definition is not FmSoundSourceNode node
				|| _nodes[i].Source is not ISound sound)
				continue;
			double childPitch = (node.FrequencyHz / C4FrequencyHz)
				* (node.Constant ? 1.0 : pitchMultiplier);
			double childSpeed = node.Constant ? 1.0 : playbackSpeedMultiplier;
			SoundInvocation? invocation = sound.CreateInvocation(
				childPitch, childSpeed);
			if (invocation is not null)
				sources[i] = new SourceVoice(invocation,
					node.Constant ? 1.0 : pitchMultiplier * playbackSpeedMultiplier);
		}
		FmSynthState state = new(_nodes.Length, _oscillatorCount, sources)
		{
			PitchMultiplier = pitchMultiplier,
			PlaybackSpeedMultiplier = playbackSpeedMultiplier,
		};
		return state;
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
		return prepared;
	}

    /// <summary>Control snapshots are published on the PCM owner at a
    /// quantum boundary. Heavy FIR design runs only on the independent
    /// FirPreparationScheduler worker.</summary>
    public void AdoptPreparedFirControls(SoundState state)
    {
        FmSynthState synthState = ValidateState(state);
        for (int i = 0; i < _nodes.Length; i++)
            if (_nodes[i].Filter is { } processor
                && synthState.Filters[i] is { } filterState)
                processor.AdoptPreparedFirs(filterState);
        foreach (SourceVoice? source in synthState.Sources)
            if (source?.Invocation.Sound is IFirPreparationSound nested)
                nested.AdoptPreparedFirControls(source.Invocation.State);
    }

    public void QueuePendingFirControls(SoundState state,
        FirPreparationScheduler scheduler)
    {
        FmSynthState synthState = ValidateState(state);
        for (int i = 0; i < _nodes.Length; i++)
            if (_nodes[i].Filter is { } processor
                && synthState.Filters[i] is { } filterState)
                processor.QueuePendingFirs(filterState, scheduler);
        foreach (SourceVoice? source in synthState.Sources)
            if (source?.Invocation.Sound is IFirPreparationSound nested)
                nested.QueuePendingFirControls(source.Invocation.State, scheduler);
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
		if (baseEnd is null)
			return null;
		double tailSeconds = GetMaximumAudioPathTailSeconds();
		double frames = Math.Ceiling(tailSeconds
			* context.Configuration.SampleRate);
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
			}
		}
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
		if (!noteOffTime.HasValue && !_hasSources)
			return null;
		long? released = noteOffTime.HasValue
			? FrameTime.Ceiling(noteOffTime.Value, sampleRate) : null;
		long noteOffFrame = released ?? 0;
		long?[] silentFrom = new long?[_nodes.Length];
		long finiteReleaseEnd = checked(noteOffFrame + 1);
		bool hasIndefiniteEnvelope = false;
		for (int index = 0; index < _nodes.Length; index++)
		{
			CompiledNode node = _nodes[index];
			if (node.Definition is FmEnvelopeNode
				&& node.Envelope is not null)
			{
				long? end =
					node.Envelope.GetEndActiveFrameExclusiveAfterNoteOff(
						noteOffFrame, sampleRate);
				silentFrom[index] = end;
				if (end is long finite)
					finiteReleaseEnd = Math.Max(finiteReleaseEnd, finite);
				else
					hasIndefiniteEnvelope = true;
			}
			else
			{
				silentFrom[index] = node.Definition switch
				{
					FmEnvelopeNode => 0L,
					FmFilterNode filter => filter.AudioInputNodeId.HasValue
						? silentFrom[node.InputIndices[0]] : 0L,
					FmOperatorNode op => GetOperatorSilenceEnd(
						op.Operation, node.InputIndices, silentFrom),
					FmSoundSourceNode => GetChildSourceSilenceEnd(state, index),
					FmConstantNode or FmOscillatorNode => released.HasValue
						? finiteReleaseEnd : null,
					_ => null,
				};
			}
		}
		if (silentFrom[_outputIndex] is long outputSilence)
			return outputSilence;
		// An indefinite source cannot be truncated merely because the
		// outer note was released. A multiplier or envelope on the actual
		// audible path must prove that the output has become silent.
		if (_hasSources)
			return null;
		return hasIndefiniteEnvelope ? null : finiteReleaseEnd;
	}

	private static long? GetChildSourceSilenceEnd(FmSynthState state, int index)
	{
		SourceVoice? voice = state.Sources[index];
		if (voice is null)
			return 0;
		if (voice.EndFrame is not long end)
			return null;
		long tail = voice.Filters?.PreparedTailFrames ?? 0;
		return end > long.MaxValue - tail ? long.MaxValue : end + tail;
	}

	private static long? GetOperatorSilenceEnd(
		FmOperatorKind operation,
		IReadOnlyList<int> inputs,
		IReadOnlyList<long?> silentFrom)
	{
		if (operation == FmOperatorKind.Multiply)
		{
			// Any factor becoming zero silences the product.
			long? earliest = null;
			foreach (int input in inputs)
				if (silentFrom[input] is long end)
					earliest = earliest.HasValue
						? Math.Min(earliest.Value, end) : end;
			return earliest;
		}

		// For addition, minimum and maximum, all inputs must be
		// provably zero before the combination is guaranteed zero.
		long latest = 0;
		foreach (int input in inputs)
		{
			if (silentFrom[input] is not long end)
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

		for (int frame = 0; frame < frameCount; frame++)
		{
			long relativeFrame =
				checked(startFrame + frame);
			double value =
				EvaluateFrame(
					context,
					synthState,
					relativeFrame,
					noteOffFrame, baseEnd);

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
		}
	}

	private void EnsureFrame(
		RenderContext context,
		FmSynthState state,
		long startFrame)
	{
		if (state.NextFrame > startFrame)
			state.Reset(context.Configuration.SampleRate);

		long? noteOffFrame =
			state.NoteOffTime.HasValue
				? FrameTime.Ceiling(
					state.NoteOffTime.Value,
					context.Configuration.SampleRate)
				: null;
		long? baseEnd = GetBaseEndFrameExclusive(
			context.Configuration.SampleRate, state);

		while (state.NextFrame < startFrame)
		{
			EvaluateFrame(
				context,
				state,
				state.NextFrame,
				noteOffFrame, baseEnd);
			state.NextFrame++;
		}
	}

	private double EvaluateFrame(
		RenderContext context,
		FmSynthState state,
		long frame,
		long? noteOffFrame,
		long? baseEnd)
	{
		double pitchMultiplier =
			state.PitchMultiplier
				* state.PlaybackSpeedMultiplier
				* state.PitchTrajectory.GetMultiplier(frame);
		if (!double.IsFinite(pitchMultiplier)
			|| !(pitchMultiplier > 0.0))
		{
			throw new InvalidOperationException(
				"FM synth pitch multiplier became non-positive or non-finite.");
		}

		bool draining = baseEnd.HasValue && frame >= baseEnd.Value;
		for (int index = 0;
			index < _nodes.Length;
			index++)
		{
			CompiledNode compiled =
				_nodes[index];
			double value = draining && compiled.Definition is
				(FmConstantNode or FmEnvelopeNode or FmOscillatorNode)
				? 0.0 : compiled.Definition switch
				{
					FmConstantNode constant =>
						constant.Value,

					FmEnvelopeNode =>
						EvaluateEnvelope(
							compiled.Envelope,
							frame,
							context.Configuration.SampleRate,
							noteOffFrame),

					FmFilterNode =>
						compiled.Filter!.Process(
							state.Filters[index] ??= compiled.Filter.CreateState(
								context.Configuration.SampleRate),
							state.Values, draining),

					FmOperatorNode operation =>
						EvaluateOperator(
							operation.Operation,
							compiled.InputIndices,
							state.Values),

					FmSoundSourceNode sourceNode =>
						EvaluateSource(sourceNode, compiled, state, index,
							frame, context.Configuration.SampleRate, noteOffFrame),

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
		}

		return state.Values[_outputIndex];
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
		double outer = node.Constant ? 1.0
			: state.PitchMultiplier * state.PlaybackSpeedMultiplier
				* state.PitchTrajectory.GetMultiplier(frame)
				/ voice.OriginalOuterFactor;
		long? released = noteOffFrame.HasValue && frame >= noteOffFrame
			? noteOffFrame : null;
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
				voice.Invocation.Sound.Render(state.StereoChildContext!,
					voice.Invocation.State, frame, 1, stereo);
				// A native stereo Sample has spatially separated source
				// channels; a mono source uses equal-power speaker gain.
				double divisor = voice.Invocation.Sound is SampleSound pannedSample
					&& pannedSample.SourceChannelCount == 2 ? 2.0 : Math.Sqrt(2);
				mono[0] = (float)((stereo[0] + stereo[1]) / divisor);
			}
			else
				voice.Invocation.Sound.Render(state.MonoContext!,
					voice.Invocation.State, frame, 1, mono);
			// SampleSound discovers a natural end by observing the actual
			// integrated source position. Modulated sample trajectories are
			// not efficiently projectable, so retain that first observed
			// boundary instead of replacing it with an indefinite guess.
			if (voice.Invocation.State.NaturalEndFrameExclusive is long natural)
				voice.EndFrame = voice.EndFrame.HasValue
					? Math.Min(voice.EndFrame.Value, natural) : natural;
		}
		if (voice.EndFrame.HasValue && frame >= voice.EndFrame.Value
			&& voice.Filters is not null && !voice.InputFinished)
		{
			voice.Filters.FinishInput();
			voice.InputFinished = true;
		}
		if (voice.Invocation.Configuration.Envelopes.Panning is null
			&& voice.Invocation.Sound is SampleSound sample)
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
		if (voice.Invocation.Configuration.Envelopes.Volume is { } envelope)
			mono[0] *= (float)envelope.GetValue(frame, sampleRate, released);
		return mono[0];
	}

	private static void PrepareChildRender(
		RenderContext context, FmSynthState state)
	{
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
				if (voice?.Invocation.ToneFilters is { } filters)
					voice.Filters = new StreamingFilterChain(filters,
						context.Configuration.SampleRate, 1);
				if (voice?.Invocation.Configuration.Envelopes.Filter is not null)
					voice.FilterEnvelopeState = new ResonantFilterState(1,
						context.Configuration.SampleRate);
			}
		}
		foreach (SourceVoice? voice in state.Sources)
		{
			if (voice is null)
				continue;
			if (voice.Invocation.State.NoteOffTime != state.NoteOffTime)
				voice.Invocation.State.NoteOffTime = state.NoteOffTime;
			long? childEnd = voice.Invocation.Sound.GetEndFrameExclusive(
				state.MonoContext, voice.Invocation.State);
			if (voice.Invocation.State.NaturalEndFrameExclusive is long natural)
				childEnd = childEnd.HasValue
					? Math.Min(childEnd.Value, natural) : natural;
			// Instrument-selected samples deliberately ignore Note Off until
			// their Volume envelope has released. The envelope can itself
			// prove a finite end, even for a looping/indefinite child.
			if (state.NoteOffTime.HasValue
				&& voice.Invocation.Configuration.Envelopes.Volume is { } volume)
			{
				long release = FrameTime.Ceiling(state.NoteOffTime.Value,
					context.Configuration.SampleRate);
				long? volumeEnd = volume.GetEndActiveFrameExclusiveAfterNoteOff(
					release, context.Configuration.SampleRate);
				if (volumeEnd.HasValue)
					childEnd = childEnd.HasValue
						? Math.Min(childEnd.Value, volumeEnd.Value)
						: volumeEnd;
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
		double normalized =
			oscillator.Waveform switch
			{
				FmOscillatorWaveform.Sine =>
					Math.Sin(
						2.0
							* Math.PI
							* phase),
				FmOscillatorWaveform.Triangle =>
					1.0
						- (4.0
							* Math.Abs(
								phase - 0.5)),
				FmOscillatorWaveform.Sawtooth =>
					(2.0 * phase) - 1.0,
				FmOscillatorWaveform.Square =>
					phase < 0.5
						? 1.0
						: -1.0,
				_ =>
					throw new ArgumentOutOfRangeException(
						nameof(oscillator.Waveform)),
			};

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

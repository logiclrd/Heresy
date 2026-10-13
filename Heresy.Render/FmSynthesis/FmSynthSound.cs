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
    }

    public void QueuePendingFirControls(SoundState state,
        FirPreparationScheduler scheduler)
    {
        FmSynthState synthState = ValidateState(state);
        for (int i = 0; i < _nodes.Length; i++)
            if (_nodes[i].Filter is { } processor
                && synthState.Filters[i] is { } filterState)
                processor.QueuePendingFirs(filterState, scheduler);
    }

	public long? GetEndFrameExclusive(
		RenderContext context, SoundState state)
	{
		ArgumentNullException.ThrowIfNull(context);
		FmSynthState synthState = ValidateState(state);
		long? baseEnd = GetBaseEndFrameExclusive(
			context.Configuration.SampleRate, synthState.NoteOffTime);
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
		int sampleRate, TimeSpan? noteOffTime)
	{
		if (_hasSources)
			return null; // child releases/finite ends are integrated next milestone
		if (!noteOffTime.HasValue)
			return null;
		long noteOffFrame = FrameTime.Ceiling(noteOffTime.Value, sampleRate);
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
					_ => null,
				};
			}
		}
		if (silentFrom[_outputIndex] is long outputSilence)
			return outputSilence;
		return hasIndefiniteEnvelope ? null : finiteReleaseEnd;
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
			sampleRate, synthState.NoteOffTime);

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
			context.Configuration.SampleRate, state.NoteOffTime);

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
			voice.Invocation.Sound.Render(state.MonoContext!,
				voice.Invocation.State, frame, 1, mono);
		else if (voice.Filters is not null && !voice.InputFinished)
		{
			voice.Filters.FinishInput();
			voice.InputFinished = true;
		}
		if (voice.Invocation.Sound is SampleSound sample)
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
		if (voice.Invocation.Configuration.Envelopes.Volume is { } envelope)
		{
			long? released = noteOffFrame.HasValue && frame >= noteOffFrame
				? noteOffFrame : null;
			mono[0] *= (float)envelope.GetValue(frame, sampleRate, released);
		}
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
			for (int i = 0; i < state.Sources.Length; i++)
			{
				SourceVoice? voice = state.Sources[i];
				if (voice?.Invocation.ToneFilters is { } filters)
					voice.Filters = new StreamingFilterChain(filters,
						context.Configuration.SampleRate, 1);
			}
		}
		foreach (SourceVoice? voice in state.Sources)
		{
			if (voice is null)
				continue;
			voice.Invocation.State.NoteOffTime = state.NoteOffTime;
			voice.EndFrame = voice.Invocation.Sound.GetEndFrameExclusive(
				state.MonoContext, voice.Invocation.State);
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

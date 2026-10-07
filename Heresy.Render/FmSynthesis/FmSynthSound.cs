using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.FmSynthesis;
using Heresy.Render.Envelopes;
using Heresy.Render.Sounds;
using Heresy.Render.Spatial;
using Heresy.Render.Timing;

namespace Heresy.Render.FmSynthesis;

public sealed class FmSynthSound : ISound
{
	private sealed class FmSynthState : SoundState
	{
		public FmSynthState(
			int nodeCount,
			int oscillatorCount)
		{
			Values = new double[nodeCount];
			Phases = new double[oscillatorCount];
		}

		public double[] Values { get; }
		public double[] Phases { get; }
		public long NextFrame { get; set; }
		public int SampleRate { get; set; }

		public void Reset(int sampleRate)
		{
			Array.Clear(Values);
			Array.Clear(Phases);
			NextFrame = 0;
			SampleRate = sampleRate;
		}
	}

	private sealed record CompiledNode(
		FmSynthNode Definition,
		int[] InputIndices,
		int OscillatorIndex,
		IEnvelopeCurve? Envelope);

	private readonly CompiledNode[] _nodes;
	private readonly int _outputIndex;
	private readonly int _oscillatorCount;
	private readonly NewNotePolicy _newNotePolicy;

	public FmSynthSound(
		FmSynthGraph graph,
		IEnvelopeCurveResolver envelopeResolver,
		NewNotePolicy? newNotePolicy = null)
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

			compiled.Add(
				new CompiledNode(
					node,
					inputs,
					phaseIndex,
					curve));
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

	public SoundState CreateState()
		=> new FmSynthState(
			_nodes.Length,
			_oscillatorCount);

	public long? GetEndFrameExclusive(
		RenderContext context,
		SoundState state)
	{
		_ = context
			?? throw new ArgumentNullException(nameof(context));
		ValidateState(state);
		return null;
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

		EnsureFrame(
			context,
			synthState,
			startFrame);

		double[] gains =
			new double[outputChannelCount];
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

		for (int frame = 0; frame < frameCount; frame++)
		{
			long relativeFrame =
				checked(startFrame + frame);
			double value =
				EvaluateFrame(
					context,
					synthState,
					relativeFrame,
					noteOffFrame);

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

		while (state.NextFrame < startFrame)
		{
			EvaluateFrame(
				context,
				state,
				state.NextFrame,
				noteOffFrame);
			state.NextFrame++;
		}
	}

	private double EvaluateFrame(
		RenderContext context,
		FmSynthState state,
		long frame,
		long? noteOffFrame)
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

		for (int index = 0;
			index < _nodes.Length;
			index++)
		{
			CompiledNode compiled =
				_nodes[index];
			double value =
				compiled.Definition switch
				{
					FmConstantNode constant =>
						constant.Value,

					FmEnvelopeNode =>
						EvaluateEnvelope(
							compiled.Envelope,
							frame,
							context.Configuration.SampleRate,
							noteOffFrame),

					FmOperatorNode operation =>
						EvaluateOperator(
							operation.Operation,
							compiled.InputIndices,
							state.Values),

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
				* pitchMultiplier
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

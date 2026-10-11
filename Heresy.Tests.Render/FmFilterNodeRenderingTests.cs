using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmFilterNodeRenderingTests
{
	private const int Rate = 1000;

	[Test]
	public void FeedforwardCombProcessesRealAudioAndDrainsOnlyPastNaturalEnd()
	{
		FmSynthSound sound = Sound(new FmSynthGraph([
			new FmConstantNode(1, 1),
			new FmFilterNode(2, 1, Comb(0.001, 1, 0.5), tailSeconds: 0.002),
		], 2));
		SoundState state = sound.CreateState();
		state.NoteOffTime = TimeSpan.FromMilliseconds(2);
		Assert.That(sound.GetEndFrameExclusive(Context(), state),
			Is.EqualTo(5), "natural end is frame 3, plus 2 authored tail frames");

		float[] pcm = new float[6];
		sound.Render(Context(), state, 0, 6, pcm);
		Assert.That(pcm[0], Is.EqualTo(1));
		Assert.That(pcm[1], Is.EqualTo(1.5f));
		Assert.That(pcm[2], Is.EqualTo(1.5f));
		Assert.That(pcm[3], Is.EqualTo(0.5f),
			"after the natural end the input is zero, not another constant 1");
		Assert.That(pcm[4], Is.Zero);
		Assert.That(pcm[5], Is.Zero);
	}

	[Test]
	public void AudioRateParameterPortSetsAbsoluteCutoffNotRelativeMultiplier()
	{
		FilterInstance low = OnePole(100);
		FmSynthGraph modulated = new([
			new FmConstantNode(1, 1),
			new FmConstantNode(2, 40),
			new FmFilterNode(3, 1, low, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
			]),
		], 3);
		FmSynthSound actual = Sound(modulated);
		FmSynthSound expected = Sound(new FmSynthGraph([
			new FmConstantNode(1, 1),
			new FmFilterNode(3, 1, OnePole(40)),
		], 3));
		float[] a = new float[80];
		float[] b = new float[80];
		actual.Render(Context(), actual.CreateState(), 0, a.Length, a);
		expected.Render(Context(), expected.CreateState(), 0, b.Length, b);
		// The One-Pole factory applies a compatible cutoff instantly;
		// other families may smooth coefficient transitions over 32 frames.
		Assert.That(a, Is.EqualTo(b),
			"modulation must supply the authored 40 Hz, not multiply 100 by 40");

		FmSynthSound invalid = Sound(new FmSynthGraph([
			new FmConstantNode(1, 1),
			new FmConstantNode(2, -300),
			new FmFilterNode(3, 1, low, [
				new FmFilterParameterConnection(1, FilterKind.OnePole,
					0, "CutoffHz", 2),
			]),
		], 3));
		FmSynthSound baseline = Sound(new FmSynthGraph([
			new FmConstantNode(1, 1),
			new FmFilterNode(3, 1, low),
		], 3));
		float[] rejected = new float[60];
		float[] reference = new float[60];
		invalid.Render(Context(), invalid.CreateState(), 0, 60, rejected);
		baseline.Render(Context(), baseline.CreateState(), 0, 60, reference);
		Assert.That(rejected, Is.EqualTo(reference),
			"invalid modulation must preserve the previous valid configuration");
	}

	[Test]
	public void StereoNoteStatesChunksAndBackwardsSeekKeepIndependentHistory()
	{
		FmSynthSound sound = Sound(new FmSynthGraph([
			new FmOscillatorNode(1, FmOscillatorWaveform.Square,
				frequencyHz: 50, constant: true),
			new FmFilterNode(2, 1, Comb(0.005, -0.8, 1)),
		], 2));
		float[] reference = new float[250];
		sound.Render(Context(), sound.CreateState(), 0, 250, reference);

		SoundState splitState = sound.CreateState();
		float[] split = new float[250];
		sound.Render(Context(), splitState, 0, 33, split.AsSpan(0, 33));
		sound.Render(Context(), splitState, 33, 217, split.AsSpan(33));
		Assert.That(split, Is.EqualTo(reference));

		float[] seek = new float[90];
		sound.Render(Context(), splitState, 90, 90, seek);
		Assert.That(seek, Is.EqualTo(reference[90..180]));
		float[] fresh = new float[250];
		sound.Render(Context(), sound.CreateState(), 0, 250, fresh);
		Assert.That(fresh, Is.EqualTo(reference));
	}

	[Test]
	public void SerialFiltersTakeMaximumAuthoredTailNotSumAndDryPathGates()
	{
		FmSynthSound sound = Sound(new FmSynthGraph([
			new FmConstantNode(1, 1),
			new FmFilterNode(2, 1, Comb(0.001, 1, 1), tailSeconds: 0.002),
			new FmFilterNode(3, 2, Comb(0.001, 1, 1), tailSeconds: 0.005),
			new FmOperatorNode(4, FmOperatorKind.Add, [1, 3]),
		], 4));
		SoundState state = sound.CreateState();
		state.NoteOffTime = TimeSpan.Zero;
		Assert.That(sound.GetEndFrameExclusive(Context(), state),
			Is.EqualTo(6), "base frame 1 + max(2,5) frames, not 1+2+5");
		float[] pcm = new float[8];
		sound.Render(Context(), state, 0, 8, pcm);
		Assert.That(pcm[0], Is.EqualTo(2), "dry branch plus filter path");
		Assert.That(pcm[1], Is.EqualTo(2),
			"both stages drain while the direct dry branch becomes silent");
		Assert.That(pcm[2], Is.EqualTo(1));
		Assert.That(pcm[3], Is.Zero);
	}

	[Test]
	public void GatedAndFrequencyOnlyEnvelopeEndsReceiveManualTail()
	{
		ObjectId envelopeId = (ObjectId)71U;
		IEnvelopeCurve curve = Envelope(envelopeId);
		var resolver = new Resolver((envelopeId, curve));
		FmSynthSound gated = new(new FmSynthGraph([
			new FmEnvelopeNode(1, envelopeId),
			new FmFilterNode(2, 1, OnePole(100), tailSeconds: 0.004),
		], 2), resolver);
		FmSynthSound bare = new(new FmSynthGraph([
			new FmEnvelopeNode(1, envelopeId),
		], 1), resolver);
		SoundState original = bare.CreateState();
		SoundState held = gated.CreateState();
		original.NoteOffTime = held.NoteOffTime =
			TimeSpan.FromMilliseconds(4);
		long natural = bare.GetEndFrameExclusive(Context(), original)!.Value;
		Assert.That(gated.GetEndFrameExclusive(Context(), held),
			Is.EqualTo(natural + 4));

		FmSynthSound frequency = new(new FmSynthGraph([
			new FmEnvelopeNode(1, envelopeId),
			new FmOscillatorNode(2, FmOscillatorWaveform.Square, 30,
				multiplierNodeId: 1, constant: true),
			new FmFilterNode(3, 2, OnePole(100), tailSeconds: 0.003),
		], 3), resolver);
		FmSynthSound unfiltered = new(new FmSynthGraph([
			new FmEnvelopeNode(1, envelopeId),
			new FmOscillatorNode(2, FmOscillatorWaveform.Square, 30,
				multiplierNodeId: 1, constant: true),
		], 2), resolver);
		SoundState withFilter = frequency.CreateState();
		SoundState without = unfiltered.CreateState();
		withFilter.NoteOffTime = without.NoteOffTime =
			TimeSpan.FromMilliseconds(4);
		Assert.That(frequency.GetEndFrameExclusive(Context(), withFilter),
			Is.EqualTo(unfiltered.GetEndFrameExclusive(Context(), without) + 3));
	}

	[Test]
	public void IndefiniteEnvelopeIsNotMadeFiniteByFilterTail()
	{
		ObjectId id = (ObjectId)91U;
		FmSynthSound sound = new(new FmSynthGraph([
			new FmEnvelopeNode(1, id),
			new FmFilterNode(2, 1, OnePole(100), tailSeconds: 0.2),
		], 2), new Resolver((id, new IndefiniteEnvelope())));
		SoundState state = sound.CreateState();
		state.NoteOffTime = TimeSpan.FromMilliseconds(5);
		Assert.That(sound.GetEndFrameExclusive(Context(), state), Is.Null);
	}

	[Test]
	public void UnassignedAndDisconnectedNodesHaveDeterministicSilenceOrBypass()
	{
		FmSynthSound bypass = Sound(new FmSynthGraph([
			new FmConstantNode(1, 0.625),
			new FmFilterNode(2, 1),
		], 2));
		float[] direct = new float[8];
		bypass.Render(Context(), bypass.CreateState(), 0, 8, direct);
		Assert.That(direct, Is.All.EqualTo(0.625f));

		FmSynthSound silent = Sound(new FmSynthGraph([
			new FmConstantNode(1, 2),
			new FmFilterNode(2, configuration: Comb(0.003, 1, 1)),
		], 2));
		float[] disconnected = new float[8];
		silent.Render(Context(), silent.CreateState(), 0, 8, disconnected);
		Assert.That(disconnected, Is.All.Zero);
	}

	private static FilterInstance Comb(double delay, double gain, double mix)
	{
		FilterDefinition definition = new((ObjectId)50U, "Comb", [
			new FilterStageDefinition(1, "tap", FilterKind.CombFeedforward),
		]);
		FilterInstance use = new(definition);
		use.SetStageParameters(1, [
			new("DelaySeconds", FilterParameterValue.FromNumber(delay)),
			new("Feedforward", FilterParameterValue.FromNumber(gain)),
			new("Mix", FilterParameterValue.FromNumber(mix)),
		]);
		return use;
	}

	private static FilterInstance OnePole(double cutoff)
	{
		FilterDefinition definition = new((ObjectId)51U, "OnePole");
		FilterInstance use = new(definition);
		use.SetStageParameter(1, "CutoffHz",
			FilterParameterValue.FromNumber(cutoff));
		return use;
	}

	private static FmSynthSound Sound(FmSynthGraph graph)
		=> new(graph, new Resolver());

	private static RenderContext Context()
		=> new(new RenderConfiguration(Rate, [
			new OutputChannelConfiguration(Vector3.Zero, positionalImportance: 0),
		]));

	private static IEnvelopeCurve Envelope(ObjectId id)
	{
		NodeEnvelopeDefinition envelope = new(id, "gated");
		envelope.ReplaceCurve([
			new(TimeSpan.Zero, 1.0),
			new(TimeSpan.FromMilliseconds(2), 1.0),
			new(TimeSpan.FromMilliseconds(8), 0.0),
		], 1, 1, EnvelopeSustainLoopMode.Forward);
		return new NodeEnvelopeCurve(envelope);
	}

	private sealed class IndefiniteEnvelope : IEnvelopeCurve
	{
		public double GetValue(long activeFrame, int sampleRate,
			long? noteOffActiveFrame) => 1;
	}

	private sealed class Resolver(
		params (ObjectId Id, IEnvelopeCurve Curve)[] curves) : IEnvelopeCurveResolver
	{
		public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
		{
			curve = curves.FirstOrDefault(pair => pair.Id == id).Curve;
			return curve is not null;
		}
	}
}

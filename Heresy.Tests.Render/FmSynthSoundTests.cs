using System;
using System.Collections.Generic;
using System.Numerics;

using AwesomeAssertions;

using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Sounds;
using Heresy.Render.Timing;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthSoundTests
{
	[Test]
	public void ConstantOutputRendersAsMonoSourceThroughSpatializer()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmConstantNode(1, 0.5),
					],
					outputNodeId: 1));
		RenderContext context = MonoContext(sampleRate: 4);
		float[] output = new float[3];

		sound.Render(
			context,
			sound.CreateState(),
			startFrame: 0,
			frameCount: 3,
			output);

		output.Should().Equal(0.5f, 0.5f, 0.5f);
	}

	[Test]
	public void SineOscillatorAdvancesAtConfiguredFrequency()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmOscillatorNode(
							1,
							FmOscillatorWaveform.Sine,
							frequencyHz: 1.0),
					],
					outputNodeId: 1));
		RenderContext context = MonoContext(sampleRate: 4);
		float[] output = new float[4];

		sound.Render(
			context,
			sound.CreateState(),
			0,
			4,
			output);

		output[0].Should().BeApproximately(0.0f, 1e-6f);
		output[1].Should().BeApproximately(1.0f, 1e-6f);
		output[2].Should().BeApproximately(0.0f, 1e-6f);
		output[3].Should().BeApproximately(-1.0f, 1e-6f);
	}

	[Test]
	public void NotePitchAndPlaybackSpeedComposeWithOscillatorFrequency()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmOscillatorNode(
							1,
							FmOscillatorWaveform.Sine,
							frequencyHz: 1.0),
					],
					outputNodeId: 1));
		SoundState state = sound.CreateState();
		state.PitchMultiplier = 2.0;
		state.PlaybackSpeedMultiplier = 0.5;
		RenderContext context = MonoContext(sampleRate: 4);
		float[] output = new float[4];

		sound.Render(context, state, 0, 4, output);

		output[1].Should().BeApproximately(1.0f, 1e-6f);
		output[3].Should().BeApproximately(-1.0f, 1e-6f);
	}

	[Test]
	public void LinearMultiplierInputScalesOscillatorFrequency()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmConstantNode(1, 2.0),
						new FmOscillatorNode(
							2,
							FmOscillatorWaveform.Sine,
							frequencyHz: 1.0,
							multiplierNodeId: 1),
					],
					outputNodeId: 2));
		float[] output = new float[4];

		sound.Render(
			MonoContext(4),
			sound.CreateState(),
			0,
			4,
			output);

		output.Should().OnlyContain(
			value => Math.Abs(value) < 1e-6f);
	}

	[Test]
	public void ExponentialMultiplierTreatsInputAsSemitoneShift()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmConstantNode(1, 12.0),
						new FmOscillatorNode(
							2,
							FmOscillatorWaveform.Sine,
							frequencyHz: 1.0,
							multiplierNodeId: 1,
							exponentialMultiplier: true),
					],
					outputNodeId: 2));
		float[] output = new float[4];

		sound.Render(
			MonoContext(4),
			sound.CreateState(),
			0,
			4,
			output);

		output.Should().OnlyContain(
			value => Math.Abs(value) < 1e-6f);
	}

	[Test]
	public void OscillatorMapsWaveformIntoConfiguredOutputRange()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmOscillatorNode(
							1,
							FmOscillatorWaveform.Sine,
							frequencyHz: 1.0,
							minimum: 2.0,
							maximum: 4.0),
					],
					outputNodeId: 1));
		float[] output = new float[2];

		sound.Render(
			MonoContext(4),
			sound.CreateState(),
			0,
			2,
			output);

		output[0].Should().BeApproximately(3.0f, 1e-6f);
		output[1].Should().BeApproximately(4.0f, 1e-6f);
	}

	[TestCase(FmOperatorKind.Add, 5.0)]
	[TestCase(FmOperatorKind.Multiply, 6.0)]
	[TestCase(FmOperatorKind.Minimum, 2.0)]
	[TestCase(FmOperatorKind.Maximum, 3.0)]
	public void OperatorsCombineTheirInputs(
		FmOperatorKind operation,
		double expected)
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmConstantNode(1, 2.0),
						new FmConstantNode(2, 3.0),
						new FmOperatorNode(
							3,
							operation,
							[1, 2]),
					],
					outputNodeId: 3));
		float[] output = new float[1];

		sound.Render(
			MonoContext(1),
			sound.CreateState(),
			0,
			1,
			output);

		output[0].Should()
			.BeApproximately((float)expected, 1e-6f);
	}

	[Test]
	public void EnvelopeNodeUsesResolvedCurveAndReceivesNoteOff()
	{
		ObjectId envelopeId = (ObjectId)42U;
		FmSynthSound sound =
			new(
				new FmSynthGraph(
					[
						new FmEnvelopeNode(
							1,
							envelopeId),
					],
					outputNodeId: 1),
				new TestEnvelopeResolver(
					(envelopeId, new NoteOffProbeCurve())));
		SoundState state = sound.CreateState();
		state.NoteOffTime =
			FrameTime.FrameStartTime(
				frame: 2,
				sampleRate: 4);
		float[] output = new float[4];

		sound.Render(
			MonoContext(4),
			state,
			0,
			4,
			output);

		output.Should().Equal(
			0.0f,
			1.0f,
			12.0f,
			13.0f);
	}

	[Test]
	public void MissingEnvelopeReferenceEvaluatesToZero()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmEnvelopeNode(
							1,
							(ObjectId)99U),
					],
					outputNodeId: 1));
		float[] output = new float[1];

		sound.Render(
			MonoContext(1),
			sound.CreateState(),
			0,
			1,
			output);

		output[0].Should().Be(0.0f);
	}

	[Test]
	public void UnassignedEnvelopeInputRendersZeroWithoutResolverDependency()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmEnvelopeNode(
							1,
							ObjectId.None),
					],
					outputNodeId: 1));
		float[] output = [1, 1, 1, 1];
		sound.Render(
			MonoContext(4),
			sound.CreateState(),
			0,
			4,
			output);
		output.Should().OnlyContain(value => value == 0.0f);
	}

	[Test]
	public void SplitRenderingMatchesSinglePass()
	{
		FmSynthGraph graph =
			new(
				[
					new FmConstantNode(1, 0.5),
					new FmOscillatorNode(
						2,
						FmOscillatorWaveform.Sine,
						frequencyHz: 137.0,
						multiplierNodeId: 1),
				],
				outputNodeId: 2);
		FmSynthSound sound = Sound(graph);
		RenderContext context = MonoContext(48000);

		float[] single = new float[32];
		sound.Render(
			context,
			sound.CreateState(),
			0,
			32,
			single);

		SoundState splitState = sound.CreateState();
		float[] split = new float[32];
		sound.Render(
			context,
			splitState,
			0,
			11,
			split.AsSpan(0, 11));
		sound.Render(
			context,
			splitState,
			11,
			21,
			split.AsSpan(11, 21));

		split.Should().Equal(single);
	}

	[Test]
	public void NonContiguousRenderReplaysPhaseDeterministically()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmOscillatorNode(
							1,
							FmOscillatorWaveform.Sine,
							frequencyHz: 137.0),
					],
					outputNodeId: 1));
		RenderContext context = MonoContext(48000);

		SoundState sequentialState = sound.CreateState();
		float[] sequential = new float[20];
		sound.Render(
			context,
			sequentialState,
			0,
			20,
			sequential);

		SoundState seekState = sound.CreateState();
		float[] seek = new float[5];
		sound.Render(
			context,
			seekState,
			15,
			5,
			seek);

		seek.Should().Equal(sequential[15..20]);
	}

	[Test]
	public void StereoOutputUsesOrdinarySourceSpatialization()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmConstantNode(1, 1.0),
					],
					outputNodeId: 1));
		SoundState state = sound.CreateState();
		state.Position =
			new Vector3(
				-1.0f,
				0.0f,
				0.0f);
		float[] output = new float[2];

		sound.Render(
			new RenderContext(
				RenderConfiguration.Stereo(1)),
			state,
			0,
			1,
			output);

		output[0].Should().BeApproximately(1.0f, 1e-6f);
		output[1].Should().BeApproximately(0.25f, 1e-6f);
	}

	[Test]
	public void SynthHasNoDeterministicFiniteEnd()
	{
		FmSynthSound sound =
			Sound(
				new FmSynthGraph(
					[
						new FmConstantNode(1, 0.0),
					],
					outputNodeId: 1));

		sound.GetEndFrameExclusive(
				MonoContext(1),
				sound.CreateState())
			.Should().BeNull();
	}

	private static FmSynthSound Sound(
		FmSynthGraph graph)
		=> new(
			graph,
			new TestEnvelopeResolver());

	private static RenderContext MonoContext(
		int sampleRate)
		=> new(
			new RenderConfiguration(
				sampleRate,
				[
					new OutputChannelConfiguration(
						Vector3.Zero,
						positionalImportance: 0.0),
				]));

	private sealed class NoteOffProbeCurve : IEnvelopeCurve
	{
		public double GetValue(
			long activeFrame,
			int sampleRate,
			long? noteOffActiveFrame)
			=> activeFrame
				+ (noteOffActiveFrame.HasValue
					? 10.0
					: 0.0);
	}

	private sealed class TestEnvelopeResolver
		: IEnvelopeCurveResolver
	{
		private readonly Dictionary<ObjectId, IEnvelopeCurve> _curves = [];

		public TestEnvelopeResolver(
			params (ObjectId Id, IEnvelopeCurve Curve)[] curves)
		{
			foreach ((ObjectId id, IEnvelopeCurve curve) in curves)
				_curves.Add(id, curve);
		}

		public bool TryResolve(
			ObjectId envelopeId,
			out IEnvelopeCurve? curve)
			=> _curves.TryGetValue(
				envelopeId,
				out curve);
	}
}

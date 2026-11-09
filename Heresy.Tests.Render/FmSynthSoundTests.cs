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
	public void ConstantFrequencyIgnoresNotePitchAndPlaybackSpeed()
	{
		FmSynthSound constant = Sound(new FmSynthGraph(
		[
			new FmOscillatorNode(1, FmOscillatorWaveform.Sine,
				frequencyHz: 1.0, constant: true),
		], outputNodeId: 1));
		RenderContext context = MonoContext(16);
		float[] original = new float[8];
		float[] changedPitch = new float[8];
		SoundState higher = constant.CreateState();
		higher.PitchMultiplier = 2.0;
		higher.PlaybackSpeedMultiplier = 1.5;

		constant.Render(context, constant.CreateState(), 0, 8, original);
		constant.Render(context, higher, 0, 8, changedPitch);
		changedPitch.Should().Equal(original,
			"a constant-rate LFO must keep its period at every note pitch");

		FmSynthSound relative = Sound(new FmSynthGraph(
		[
			new FmOscillatorNode(1, FmOscillatorWaveform.Sine,
				frequencyHz: 1.0),
		], outputNodeId: 1));
		SoundState pitched = relative.CreateState();
		pitched.PitchMultiplier = 2.0;
		float[] relativeOutput = new float[4];
		relative.Render(context, pitched, 0, 4, relativeOutput);
		relativeOutput[1].Should().BeApproximately(
			(float)Math.Sin(Math.PI / 4.0), 1e-6f);
		Math.Abs(relativeOutput[1] - original[1]).Should()
			.BeGreaterThan(0.1f);
	}

	[Test]
	public void SingleOscillatorMatchesGenericGraphAcrossLiveEditsPitchAndRelease()
	{
		const int sampleRate = 48000;
		RenderContext context = MonoContext(sampleRate);
		foreach (FmOscillatorWaveform waveform in Enum.GetValues<FmOscillatorWaveform>())
		{
			foreach (bool constant in new[] { false, true })
			{
				FmSynthGraph Graph(FmOscillatorWaveform kind, double frequency,
					double curvature, bool generic)
				{
					FmOscillatorNode oscillator = new(1, kind, frequency,
						minimum: -0.6, maximum: 0.8, curvature: curvature,
						constant: constant);
					return generic
						? new FmSynthGraph([
							oscillator, new FmConstantNode(2, 1.0),
							new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2])
						], outputNodeId: 3)
						: new FmSynthGraph([oscillator], outputNodeId: 1);
				}
				FmSynthSound direct = Sound(Graph(waveform, 631, 2.5, false));
				FmSynthSound generic = Sound(Graph(waveform, 631, 2.5, true));
				SoundState directState = direct.CreateState();
				SoundState genericState = generic.CreateState();
				foreach (SoundState state in new[] { directState, genericState })
				{
					state.PitchMultiplier = 1.375;
					state.PlaybackSpeedMultiplier = 0.75;
					state.PitchTrajectory.SetMultiplier(95, 1.15);
				}
				float[] directPcm = new float[350];
				float[] genericPcm = new float[350];
				direct.Render(context, directState, 0, 119,
					directPcm.AsSpan(0, 119));
				generic.Render(context, genericState, 0, 119,
					genericPcm.AsSpan(0, 119));

				// Change all authored oscillator parameters that are legal
				// non-structural edits while retaining the original phase.
				FmOscillatorWaveform editedWaveform =
					waveform == FmOscillatorWaveform.Sine
						? FmOscillatorWaveform.Sawtooth
						: FmOscillatorWaveform.Sine;
				FmSynthSound directRevision = Sound(Graph(
					editedWaveform, 417, -1.5, false));
				FmSynthSound genericRevision = Sound(Graph(
					editedWaveform, 417, -1.5, true));
				direct.TryPrepareRunningState(directRevision, directState)
					.Should().BeTrue();
				generic.TryPrepareRunningState(genericRevision, genericState)
					.Should().BeTrue();
				direct.CommitPreparedRunningState(directRevision, directState);
				generic.CommitPreparedRunningState(genericRevision, genericState);
				directState.NoteOffTime =
					TimeSpan.FromSeconds(205.0 / sampleRate);
				genericState.NoteOffTime = directState.NoteOffTime;
				directRevision.Render(context, directState, 119, 231,
					directPcm.AsSpan(119));
				genericRevision.Render(context, genericState, 119, 231,
					genericPcm.AsSpan(119));

				directPcm.Should().Equal(genericPcm,
					"the optimized single oscillator must preserve generic graph " +
					"waveform math, pitch timeline, live revision phase and release");
				directPcm[^1].Should().Be(0,
					"Note Off still ends the standalone oscillator");
			}
		}
	}

	[Test]
	public void ConstantOscillatorStillRespondsToConnectedLinearAndSemitoneMultipliers()
	{
		foreach (bool exponential in new[] { false, true })
		{
			FmSynthSound sound = Sound(new FmSynthGraph(
			[
				new FmConstantNode(1, exponential ? 12.0 : 2.0),
				new FmOscillatorNode(2, FmOscillatorWaveform.Sine,
					frequencyHz: 1.0, multiplierNodeId: 1,
					exponentialMultiplier: exponential, constant: true),
			], outputNodeId: 2));
			SoundState state = sound.CreateState();
			state.PitchMultiplier = 3.0;
			float[] output = new float[3];
			sound.Render(MonoContext(16), state, 0, 3, output);
			output[2].Should().BeApproximately(1.0f, 1e-6f,
				"explicit FM multiplication still doubles a fixed-rate oscillator");
		}
	}

	[Test]
	public void ConstantAndRelativeOscillatorsHaveIndependentPitchRulesInOneGraph()
	{
		FmSynthSound sound = Sound(new FmSynthGraph(
		[
			new FmOscillatorNode(1, FmOscillatorWaveform.Sine,
				frequencyHz: 1.0, constant: true),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine,
				frequencyHz: 1.0),
			new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
		], outputNodeId: 3));
		RenderContext context = MonoContext(16);
		SoundState higher = sound.CreateState();
		higher.PitchMultiplier = 2.0;
		float[] normal = new float[3];
		float[] transposed = new float[3];
		sound.Render(context, sound.CreateState(), 0, 3, normal);
		sound.Render(context, higher, 0, 3, transposed);
		double constantPart = Math.Sin(Math.PI / 8.0);
		normal[1].Should().BeApproximately(
			(float)(2.0 * constantPart), 1e-6f);
		transposed[1].Should().BeApproximately(
			(float)(constantPart + Math.Sin(Math.PI / 4.0)), 1e-6f);
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


	[TestCase(FmOscillatorWaveform.Sine)]
	[TestCase(FmOscillatorWaveform.Triangle)]
	[TestCase(FmOscillatorWaveform.Sawtooth)]
	[TestCase(FmOscillatorWaveform.Square)]
	public void ExplicitZeroCurvatureIsBitIdenticalToPreviousOscillator(
		FmOscillatorWaveform waveform)
	{
		FmSynthSound original = Sound(new FmSynthGraph(
		[
			new FmOscillatorNode(1, waveform, frequencyHz: 3.0),
		], outputNodeId: 1));
		FmSynthSound zero = Sound(new FmSynthGraph(
		[
			new FmOscillatorNode(1, waveform, frequencyHz: 3.0,
				curvature: 0.0),
		], outputNodeId: 1));
		RenderContext context = MonoContext(32);
		float[] expected = new float[64];
		float[] actual = new float[64];
		original.Render(context, original.CreateState(), 0, 64, expected);
		zero.Render(context, zero.CreateState(), 0, 64, actual);
		actual.Should().Equal(expected);
	}

	[TestCase(FmOscillatorWaveform.Sine, 2.0)]
	[TestCase(FmOscillatorWaveform.Sine, -2.0)]
	[TestCase(FmOscillatorWaveform.Triangle, 2.0)]
	[TestCase(FmOscillatorWaveform.Triangle, -2.0)]
	[TestCase(FmOscillatorWaveform.Sawtooth, 2.0)]
	[TestCase(FmOscillatorWaveform.Sawtooth, -2.0)]
	[TestCase(FmOscillatorWaveform.Square, 2.0)]
	[TestCase(FmOscillatorWaveform.Square, -2.0)]
	[TestCase(FmOscillatorWaveform.Sawtooth, 32.0)]
	[TestCase(FmOscillatorWaveform.Sawtooth, -32.0)]
	public void CurveIsAppliedToNormalizedWaveformBeforeVminVmax(
		FmOscillatorWaveform waveform, double curvature)
	{
		FmSynthSound unshaped = Sound(new FmSynthGraph(
		[
			new FmOscillatorNode(1, waveform, frequencyHz: 1.0),
		], outputNodeId: 1));
		FmSynthSound shaped = Sound(new FmSynthGraph(
		[
			new FmOscillatorNode(1, waveform, frequencyHz: 1.0,
				minimum: 2.0, maximum: 4.0, curvature: curvature),
		], outputNodeId: 1));

		RenderContext context = MonoContext(32);
		float[] original = new float[32];
		float[] output = new float[32];
		unshaped.Render(context, unshaped.CreateState(), 0, 32, original);
		shaped.Render(context, shaped.CreateState(), 0, 32, output);

		for (int i = 0; i < output.Length; i++)
		{
			double expectedCurve = 2.0 *
				(Math.Exp(curvature * (original[i] + 1.0)) - 1.0)
				/ (Math.Exp(2.0 * curvature) - 1.0) - 1.0;
			double expected = 3.0 + expectedCurve;
			output[i].Should().BeApproximately((float)expected, 0.0002f,
				$"waveform {waveform} k={curvature} frame={i}");
			output[i].Should().BeInRange(2.0f, 4.0f);
		}
	}

	[Test]
	public void CurvedOutputIsDeterministicAcrossRenderSplitsAndPhaseSeek()
	{
		FmSynthSound sound = Sound(new FmSynthGraph(
		[
			new FmOscillatorNode(1, FmOscillatorWaveform.Sawtooth,
				frequencyHz: 137.0, curvature: 2.0),
			new FmOscillatorNode(2, FmOscillatorWaveform.Sine,
				frequencyHz: 219.0, curvature: -3.0),
			new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
		], outputNodeId: 3));
		RenderContext context = MonoContext(48000);
		float[] full = new float[96];
		sound.Render(context, sound.CreateState(), 0, 96, full);

		SoundState splitState = sound.CreateState();
		float[] split = new float[96];
		sound.Render(context, splitState, 0, 17, split.AsSpan(0, 17));
		sound.Render(context, splitState, 17, 79, split.AsSpan(17, 79));
		split.Should().Equal(full);

		float[] seek = new float[23];
		sound.Render(context, sound.CreateState(), 73, 23, seek);
		seek.Should().Equal(full[73..96]);
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
	public void UnassignedEnvelopeAddsNoSignalToEmptyOrExistingMix()
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
		float[] emptyOutput = new float[4];
		sound.Render(
			MonoContext(4),
			sound.CreateState(),
			0,
			4,
			emptyOutput);
		emptyOutput.Should().OnlyContain(value => value == 0.0f);

		// Sound.Render adds into its caller's PCM mix; silence must not
		// erase an existing voice or spuriously contribute to the bus.
		float[] existingMix = [1, 1, 1, 1];
		sound.Render(
			MonoContext(4),
			sound.CreateState(),
			0,
			4,
			existingMix);
		existingMix.Should().OnlyContain(value => value == 1.0f);
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

	[Test]
	public void ReachableSoundSourceNodeIsRejectedAtPreparationNotDuringPcm()
	{
		FmSynthGraph graph = new([
			new FmSoundSourceNode(1, (ObjectId)9U, 440.0),
		], 1);
		Assert.That(() => Sound(graph),
			Throws.TypeOf<NotSupportedException>().With.Message
				.Contains("prepared child-invocation"));
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

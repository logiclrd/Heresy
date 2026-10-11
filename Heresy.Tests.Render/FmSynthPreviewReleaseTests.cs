using System;
using System.Collections.Generic;
using System.Numerics;

using AwesomeAssertions;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.Core.Sequencing;
using Heresy.Render.Configuration;
using Heresy.Render.Envelopes;
using Heresy.Render.FmSynthesis;
using Heresy.Render.Playback;
using Heresy.Render.Realtime;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class FmSynthPreviewReleaseTests
{
	private static readonly ObjectId SourceId = (ObjectId)11U;
	private static readonly ObjectId EnvelopeId = (ObjectId)12U;

	[Test]
	public void LiveFmEnvelopeNoteOffContinuesItsReleaseRatherThanCutting()
	{
		PlaybackSession session = Session(GraphWithEnvelopeOutput());
		PlaybackSessionAudioSource source = new(session);
		uint id = 0x464D0000; // FM Test virtual voice.
		source.EnqueueLiveEvent(ChannelTarget.Virtual(id),
			[new StartNoteCommand(SourceId)]);
		float[] held = new float[3];
		source.Render(held.Length, held);
		held.Should().OnlyContain(value => Math.Abs(value - 0.8f) < 1e-5f);

		source.EnqueueLiveEvent(ChannelTarget.Virtual(id),
			[new NoteOffCommand()]);
		float[] release = new float[5];
		source.Render(release.Length, release);

		release[0].Should().BeApproximately(0.8f, 1e-5f);
		release[1].Should().BeApproximately(0.8f * 2f / 3f, 1e-5f);
		release[2].Should().BeApproximately(0.8f / 3f, 1e-5f);
		release[3].Should().BeApproximately(0f, 1e-5f);
		release[4].Should().Be(0f);
		session.IsQuiescent.Should().BeFalse("live input remains open");
	}

	[Test]
	public void LiveFmOscillatorMultipliedByEnvelopeKeepsAudibleReleaseTail()
	{
		PlaybackSession session = Session(new FmSynthGraph(
		[
			new FmEnvelopeNode(1, EnvelopeId),
			new FmOscillatorNode(2, FmOscillatorWaveform.Square, 1),
			new FmOperatorNode(3, FmOperatorKind.Multiply, [1, 2]),
		], outputNodeId: 3));
		PlaybackSessionAudioSource source = new(session);
		source.EnqueueLiveEvent(ChannelTarget.Virtual(55),
			[new StartNoteCommand(SourceId)]);
		float[] before = new float[3];
		source.Render(3, before);

		source.EnqueueLiveEvent(ChannelTarget.Virtual(55),
			[new NoteOffCommand()]);
		float[] release = new float[5];
		source.Render(5, release);

		release[0].Should().BeApproximately(0.8f, 1e-5f);
		release[1].Should().BeApproximately(0.8f * 2f / 3f, 1e-5f);
		release[2].Should().BeApproximately(-0.8f / 3f, 1e-5f);
		release[3].Should().BeApproximately(0f, 1e-5f);
		release[4].Should().Be(0f);
	}

	[Test]
	public void AllAdditiveBranchesMustEndBeforeFmSourceCanRetire()
	{
		IEnvelopeCurve longer = EnvelopeCurve(finalTimeMilliseconds: 600);
		IEnvelopeCurve shorter = EnvelopeCurve(finalTimeMilliseconds: 400);
		FmSynthSound sound = new(
			new FmSynthGraph(
			[
				new FmEnvelopeNode(1, EnvelopeId),
				new FmEnvelopeNode(2, (ObjectId)13U),
				new FmOperatorNode(3, FmOperatorKind.Add, [1, 2]),
			], outputNodeId: 3),
			new Resolver((EnvelopeId, shorter), ((ObjectId)13U, longer)));
		SoundState state = sound.CreateState();
		state.NoteOffTime = TimeSpan.FromMilliseconds(300);
		sound.GetEndFrameExclusive(Context(), state).Should().Be(9,
			"both summands must reach silence (5 frames after release plus final sample)");
	}

	[Test]
	public void FrequencyOnlyEnvelopeKeepsTheOscillatorAliveThroughItsTail()
	{
		FmSynthSound fm = new(
			new FmSynthGraph(
			[
				new FmEnvelopeNode(1, EnvelopeId),
				new FmOscillatorNode(2, FmOscillatorWaveform.Square, 1,
					multiplierNodeId: 1),
			], outputNodeId: 2),
			new Resolver((EnvelopeId, EnvelopeCurve(400))));
		SoundState state = fm.CreateState();
		state.NoteOffTime = TimeSpan.FromMilliseconds(300);
		fm.GetEndFrameExclusive(Context(), state).Should().Be(7,
			"the oscillator does not become silent when its pitch "
			+ "envelope ends, but the note is retired after the release");
	}

	[Test]
	public void LiveFrequencyOnlyFmPreviewKeepsThePitchSlideAfterKeyUp()
	{
		PlaybackSession session = Session(new FmSynthGraph(
		[
			new FmEnvelopeNode(1, EnvelopeId),
			new FmOscillatorNode(2, FmOscillatorWaveform.Square, 1,
				multiplierNodeId: 1),
		], outputNodeId: 2));
		PlaybackSessionAudioSource source = new(session);
		source.EnqueueLiveEvent(ChannelTarget.Virtual(55),
			[new StartNoteCommand(SourceId)]);
		float[] before = new float[3];
		source.Render(before.Length, before);
		before.Should().OnlyContain(value => Math.Abs(value) > 0.5f);

		source.EnqueueLiveEvent(ChannelTarget.Virtual(55),
			[new NoteOffCommand()]);
		float[] released = new float[8];
		source.Render(released.Length, released);
		released[..4].Should().OnlyContain(value => Math.Abs(value) > 0.5f,
			"the pitch modulation may taper to zero but the tone must "
			+ "continue until its release phase finishes");
		released[4..].Should().OnlyContain(value => Math.Abs(value) < 1e-6f);
	}

	[Test]
	public void FmWithoutAnyEnvelopeEndsJustAfterNoteOff()
	{
		FmSynthSound sound = new(
			new FmSynthGraph(
			[new FmOscillatorNode(1, FmOscillatorWaveform.Square, 1)],
			outputNodeId: 1),
			new Resolver());
		SoundState state = sound.CreateState();
		sound.GetEndFrameExclusive(Context(), state).Should().BeNull();
		state.NoteOffTime = TimeSpan.FromMilliseconds(300);
		sound.GetEndFrameExclusive(Context(), state).Should().Be(4,
			"an ungated FM oscillator has no post-Note-Off release tail");
	}

	[Test]
	public void BareIndefiniteFmVoiceStillCutsOnLiveNoteOff()
	{
		PlaybackSession session = Session(new FmSynthGraph(
			[new FmConstantNode(1, 1.0)], outputNodeId: 1));
		PlaybackSessionAudioSource source = new(session);
		source.EnqueueLiveEvent(ChannelTarget.Virtual(55),
			[new StartNoteCommand(SourceId)]);
		float[] before = new float[1];
		source.Render(1, before);
		source.EnqueueLiveEvent(ChannelTarget.Virtual(55),
			[new NoteOffCommand()]);
		float[] release = new float[3];
		source.Render(3, release);
		before[0].Should().Be(1.0f);
		release[0].Should().Be(1.0f); // anti-click residue
		release[1].Should().Be(0.0f);
		release[2].Should().Be(0.0f);
	}

	private static FmSynthGraph GraphWithEnvelopeOutput()
		=> new([new FmEnvelopeNode(1, EnvelopeId)], outputNodeId: 1);

	private static IEnvelopeCurve EnvelopeCurve(int finalTimeMilliseconds)
	{
		NodeEnvelopeDefinition definition = new(EnvelopeId, "FM volume");
		definition.ReplaceCurve(
		[
			new(TimeSpan.Zero, 0.8),
			new(TimeSpan.FromMilliseconds(100), 0.8),
			new(TimeSpan.FromMilliseconds(finalTimeMilliseconds), 0.0),
		], 1, 1, EnvelopeSustainLoopMode.Forward);
		return new NodeEnvelopeCurve(definition);
	}

	private static PlaybackSession Session(FmSynthGraph graph)
		=> new(Context(), new NoteScheduleBuilder().Freeze(),
			new SoundResolver(SourceId,
				new FmSynthSound(graph,
					new Resolver((EnvelopeId, EnvelopeCurve(400))))));

	private static RenderContext Context()
		=> new(new RenderConfiguration(10,
			[new OutputChannelConfiguration(Vector3.Zero,
				positionalImportance: 0)]));

	private sealed class Resolver(params (ObjectId Id, IEnvelopeCurve Curve)[] curves)
		: IEnvelopeCurveResolver
	{
		public bool TryResolve(ObjectId id, out IEnvelopeCurve? curve)
		{
			foreach ((ObjectId key, IEnvelopeCurve value) in curves)
				if (key == id)
				{
					curve = value;
					return true;
				}
			curve = null;
			return false;
		}
	}

	private sealed class SoundResolver(ObjectId id, ISound bound) : ISoundResolver
	{
		public bool TryResolve(ObjectId requestedId, bool mixdown,
			out ISound? sound)
		{
			sound = !mixdown && requestedId == id ? bound : null;
			return sound is not null;
		}
	}
}

using System;
using System.Numerics;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class SampleSoundTests
{
	[Test]
	public void MonoSampleAtMatchingRateRendersUnchangedWithNonPositionalOutput()
	{
		SampleSound sound = CreateSound(
			new float[] { 0.0f, 0.25f, -0.5f, 2.0f },
			sampleRate: 4);
		RenderContext context = MonoContext(sampleRate: 4);
		SoundState state = sound.CreateState();
		float[] output = new float[4];

		sound.Render(context, state, 0, 4, output);

		Assert.That(output, Is.EqualTo(new float[] { 0.0f, 0.25f, -0.5f, 2.0f }));
	}

	[Test]
	public void LinearInterpolationIsUsedDuringResampling()
	{
		SampleSound sound = CreateSound(
			new float[] { 0.0f, 1.0f, 0.0f },
			sampleRate: 4);
		RenderContext context = MonoContext(sampleRate: 8);
		SoundState state = sound.CreateState();
		float[] output = new float[4];

		sound.Render(context, state, 0, 4, output);

		Assert.That(output, Is.EqualTo(new float[] { 0.0f, 0.5f, 1.0f, 0.5f }));
	}

	[Test]
	public void SplitRenderMatchesSingleRender()
	{
		SampleSound sound = CreateSound(
			new float[] { 0.0f, 1.0f, 0.25f, -0.75f, 0.5f },
			sampleRate: 5);
		RenderContext context = MonoContext(sampleRate: 8);
		SoundState state = sound.CreateState();

		float[] single = new float[8];
		sound.Render(context, state, 0, 8, single);

		float[] split = new float[8];
		sound.Render(context, state, 0, 3, split.AsSpan(0, 3));
		sound.Render(context, state, 3, 5, split.AsSpan(3, 5));

		Assert.That(split, Is.EqualTo(single));
	}

	[Test]
	public void RendererDoesNotClampFloatOutput()
	{
		SampleSound sound = CreateSound(
			new float[] { 1.5f },
			sampleRate: 1);
		RenderContext context = MonoContext(sampleRate: 1);
		float[] output = new float[1];

		sound.Render(context, sound.CreateState(), 0, 1, output);

		Assert.That(output[0], Is.EqualTo(1.5f));
	}

	[Test]
	public void ForwardLoopWrapsWithoutLeavingLoopRegion()
	{
		SampleSound sound = CreateSound(
			new float[] { 0.0f, 1.0f, 2.0f, 3.0f },
			sampleRate: 4,
			loop: new SampleLoop(SampleLoopMode.Forward, 1, 4));
		RenderContext context = MonoContext(sampleRate: 4);
		float[] output = new float[8];

		sound.Render(context, sound.CreateState(), 0, 8, output);

		Assert.That(
			output,
			Is.EqualTo(new float[] { 0.0f, 1.0f, 2.0f, 3.0f, 1.0f, 2.0f, 3.0f, 1.0f }));
	}

	[Test]
	public void PingPongLoopDoesNotDuplicateEndpoints()
	{
		SampleSound sound = CreateSound(
			new float[] { 0.0f, 1.0f, 2.0f, 3.0f },
			sampleRate: 4,
			loop: new SampleLoop(SampleLoopMode.PingPong, 1, 4));
		RenderContext context = MonoContext(sampleRate: 4);
		float[] output = new float[8];

		sound.Render(context, sound.CreateState(), 0, 8, output);

		Assert.That(
			output,
			Is.EqualTo(new float[] { 0.0f, 1.0f, 2.0f, 3.0f, 2.0f, 1.0f, 2.0f, 3.0f }));
	}

	[Test]
	public void StereoSourceChannelsUseLocalPositionsForCrosstalk()
	{
		SampleDefinition definition = Definition();
		MemorySampleData data = new(
			sampleRate: 1,
			channelCount: 2,
			interleavedSamples: new float[] { 1.0f, 0.0f });
		SampleSound sound = new(definition, data);
		RenderContext context = new(RenderConfiguration.Stereo(sampleRate: 1));
		float[] output = new float[2];

		sound.Render(context, sound.CreateState(), 0, 1, output);

		Assert.That(output[0], Is.EqualTo(1.0f).Within(1e-6f));
		Assert.That(output[1], Is.EqualTo(0.25f).Within(1e-6f));
	}

	[Test]
	public void InvocationPositionTranslatesSampleLocalPositions()
	{
		SampleSound sound = CreateSound(
			new float[] { 1.0f },
			sampleRate: 1);
		RenderContext context = new(RenderConfiguration.Stereo(sampleRate: 1));
		SoundState state = sound.CreateState();
		state.Position = new Vector3(-1.0f, 0.0f, 0.0f);
		float[] output = new float[2];

		sound.Render(context, state, 0, 1, output);

		Assert.That(output[0], Is.EqualTo(1.0f).Within(1e-6f));
		Assert.That(output[1], Is.EqualTo(0.25f).Within(1e-6f));
	}

	[Test]
	public void FiniteSampleReportsExclusiveOutputEndFrame()
	{
		SampleSound sound = CreateSound(
			new float[] { 0.0f, 1.0f, 2.0f, 3.0f },
			sampleRate: 4);
		RenderContext context = MonoContext(sampleRate: 8);
		SoundState state = sound.CreateState();

		Assert.That(sound.GetEndFrameExclusive(context, state), Is.EqualTo(8));

		state.PitchMultiplier = 2.0;
		Assert.That(sound.GetEndFrameExclusive(context, state), Is.EqualTo(4));
	}

	[Test]
	public void LoopingSampleHasNoFiniteEnd()
	{
		SampleSound sound = CreateSound(
			new float[] { 0.0f, 1.0f },
			sampleRate: 2,
			loop: new SampleLoop(SampleLoopMode.Forward, 0, 2));

		Assert.That(
			sound.GetEndFrameExclusive(MonoContext(2), sound.CreateState()),
			Is.Null);
	}

	private static SampleSound CreateSound(
		float[] monoSamples,
		int sampleRate,
		SampleLoop? loop = null)
	{
		SampleDefinition definition = Definition();
		if (loop is not null)
			definition.Loop = loop;

		return new SampleSound(
			definition,
			new MemorySampleData(sampleRate, 1, monoSamples));
	}

	private static SampleDefinition Definition()
		=> new(
			(ObjectId)1U,
			"Sample",
			new ExternalAssetReference("sample.raw"));

	private static RenderContext MonoContext(int sampleRate)
		=> new(
			new RenderConfiguration(
				sampleRate,
				new[]
				{
					new OutputChannelConfiguration(
						Vector3.Zero,
						positionalImportance: 0.0),
				}));
}

using System;

using Heresy.Core.Assets;
using Heresy.Core.Objects;
using Heresy.Core.Samples;
using Heresy.Render.Configuration;
using Heresy.Render.Samples;
using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class SamplePitchTrajectoryTests
{
	[Test]
	public void SampleUsesIntegratedPitchTrajectoryForSourcePosition()
	{
		SampleDefinition definition = new(
			(ObjectId)1U,
			"Ramp",
			new ExternalAssetReference("ramp.raw"));
		SampleSound sound = new(
			definition,
			new MemorySampleData(
				8,
				1,
				new float[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
		RenderContext context = new(
			new RenderConfiguration(
				8,
				new[]
				{
					new OutputChannelConfiguration(
						System.Numerics.Vector3.Zero,
						positionalImportance: 0.0),
				}));
		SoundState state = sound.CreateState();
		state.PitchTrajectory.SetMultiplier(0, 2.0);
		float[] output = new float[4];

		sound.Render(context, state, 0, 4, output);

		Assert.That(output, Is.EqualTo(new float[] { 0, 2, 4, 6 }));
		Assert.That(sound.GetEndFrameExclusive(context, state), Is.EqualTo(4));
	}
}

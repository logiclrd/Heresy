using System;
using System.Numerics;

using Heresy.Render.Configuration;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class RenderConfigurationTests
{
	[Test]
	public void FilteredOutputRequiresPositiveFiniteCutoff()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new OutputChannelConfiguration(
				Vector3.Zero,
				filterType: OutputFilterType.LowPass));

		Assert.DoesNotThrow(
			() => new OutputChannelConfiguration(
				Vector3.Zero,
				filterType: OutputFilterType.HighPass,
				cutoffHz: 120.0));
	}

	[Test]
	public void RenderConfigurationRequiresAtLeastOneOutputChannel()
	{
		Assert.Throws<ArgumentException>(
			() => new RenderConfiguration(
				48000,
				Array.Empty<OutputChannelConfiguration>()));
	}
}

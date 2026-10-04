using Heresy.Render.Filters;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class ResonantFilterStateTests
{
	[Test]
	public void ImpulseFollowsTwoPoleRecurrence()
	{
		ResonantFilterParameters parameters =
			new(0.5, 0.25);
		ResonantFilterState state =
			new(1, 48000, parameters);
		ResonantFilterCoefficients c = state.Coefficients;

		float[] frame0 = { 1.0f };
		float[] frame1 = { 0.0f };
		float[] frame2 = { 0.0f };

		state.ProcessFrame(frame0);
		state.ProcessFrame(frame1);
		state.ProcessFrame(frame2);

		double y0 = c.A;
		double y1 = c.B * y0;
		double y2 = c.B * y1 + c.C * y0;

		Assert.That(frame0[0], Is.EqualTo((float)y0).Within(1e-7f));
		Assert.That(frame1[0], Is.EqualTo((float)y1).Within(1e-7f));
		Assert.That(frame2[0], Is.EqualTo((float)y2).Within(1e-7f));
	}

	[Test]
	public void OutputChannelsHaveIndependentHistory()
	{
		ResonantFilterState state =
			new(2, 48000, new ResonantFilterParameters(0.5, 0.25));
		ResonantFilterCoefficients c = state.Coefficients;

		float[] first = { 1.0f, 0.0f };
		float[] second = { 0.0f, 1.0f };

		state.ProcessFrame(first);
		state.ProcessFrame(second);

		Assert.That(second[0], Is.EqualTo((float)(c.B * c.A)).Within(1e-7f));
		Assert.That(second[1], Is.EqualTo((float)c.A).Within(1e-7f));
	}

	[Test]
	public void ParameterChangesPreserveHistory()
	{
		ResonantFilterState state =
			new(1, 48000, new ResonantFilterParameters(0.5, 0.25));

		float[] impulse = { 1.0f };
		state.ProcessFrame(impulse);

		state.SetParameters(ResonantFilterParameters.Disabled);

		float[] bypass = { 0.75f };
		state.ProcessFrame(bypass);
		Assert.That(bypass[0], Is.EqualTo(0.75f));

		state.SetParameters(new ResonantFilterParameters(0.5, 0.25));
		float[] resumed = { 0.0f };
		state.ProcessFrame(resumed);

		Assert.That(resumed[0], Is.Not.EqualTo(0.0f));
	}
}

using System;

using Heresy.Render.Sounds;

using NUnit.Framework;

namespace Heresy.Tests.Render;

[TestFixture]
public sealed class NewNotePolicyTests
{
	[Test]
	public void FadeRequiresPositiveDuration()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => NewNotePolicy.Fade(TimeSpan.Zero));

		Assert.Throws<ArgumentOutOfRangeException>(
			() => NewNotePolicy.Fade(TimeSpan.FromTicks(-1)));
	}

	[Test]
	public void NonFadeActionRejectsFadeDuration()
	{
		Assert.Throws<ArgumentException>(
			() => new NewNotePolicy(
				NewNoteAction.Continue,
				TimeSpan.FromMilliseconds(100)));
	}

	[Test]
	public void FadeStoresDuration()
	{
		NewNotePolicy policy = NewNotePolicy.Fade(
			TimeSpan.FromMilliseconds(200));

		Assert.That(policy.Action, Is.EqualTo(NewNoteAction.Fade));
		Assert.That(
			policy.FadeDuration,
			Is.EqualTo(TimeSpan.FromMilliseconds(200)));
	}
}

using System;

using Heresy.UserInterface.Startup;

using NUnit.Framework;

namespace Heresy.Tests.UserInterface;

[TestFixture]
public sealed class StartupSplashDismissalTests
{
	[Test]
	public void SplashTimeoutIsFourSecondsFromWindowOpening()
		=> Assert.That(StartupSplashDismissal.Timeout,
			Is.EqualTo(TimeSpan.FromSeconds(4)));

	[TestCase(StartupSplashDismissalReason.KeyPress)]
	[TestCase(StartupSplashDismissalReason.PointerClick)]
	[TestCase(StartupSplashDismissalReason.Timeout)]
	[TestCase(StartupSplashDismissalReason.OwnerClosed)]
	public void EachTriggerClosesOnceAndDisarmsTheTimeout(
		StartupSplashDismissalReason reason)
	{
		int stopped = 0;
		int closed = 0;
		StartupSplashDismissal gate = new(
			() => stopped++,
			() => closed++);
		gate.Dismiss(reason);
		gate.Dismiss(StartupSplashDismissalReason.Timeout);
		gate.Dismiss(StartupSplashDismissalReason.OwnerClosed);
		gate.NotifyWindowClosed();
		Assert.Multiple(() =>
		{
			Assert.That(gate.IsDismissed, Is.True);
			Assert.That(gate.Reason, Is.EqualTo(reason));
			Assert.That(stopped, Is.EqualTo(1));
			Assert.That(closed, Is.EqualTo(1));
		});
	}

	[Test]
	public void ExternalSplashCloseStopsTimeoutButDoesNotRequestAnotherClose()
	{
		int stopped = 0;
		int closed = 0;
		StartupSplashDismissal gate = new(
			() => stopped++,
			() => closed++);
		gate.NotifyWindowClosed();
		gate.Dismiss(StartupSplashDismissalReason.Timeout);
		Assert.Multiple(() =>
		{
			Assert.That(gate.IsDismissed, Is.True);
			Assert.That(gate.Reason,
				Is.EqualTo(StartupSplashDismissalReason.ExternalClose));
			Assert.That(stopped, Is.EqualTo(1));
			Assert.That(closed, Is.Zero);
		});
	}

	[Test]
	public void SynchronousCloseEventCannotReenterTheDismissalCallback()
	{
		int stopped = 0;
		int closed = 0;
		StartupSplashDismissal? gate = null;
		gate = new StartupSplashDismissal(
			() => stopped++,
			() =>
			{
				closed++;
				gate!.NotifyWindowClosed();
				gate.Dismiss(StartupSplashDismissalReason.KeyPress);
			});
		gate.Dismiss(StartupSplashDismissalReason.PointerClick);
		Assert.Multiple(() =>
		{
			Assert.That(closed, Is.EqualTo(1));
			Assert.That(stopped, Is.EqualTo(1));
			Assert.That(gate.Reason,
				Is.EqualTo(StartupSplashDismissalReason.PointerClick));
		});
	}
}

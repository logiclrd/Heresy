using System;

using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class DeferredTempoEventQueueTests
{
	[Test]
	public void FutureEventsAreOrderedByTimeThenStableInsertionOrder()
	{
		DeferredTempoEventQueue queue = new();
		queue.Schedule(TimeSpan.FromMilliseconds(200), 200);
		queue.Schedule(TimeSpan.FromMilliseconds(100), 150);
		queue.Schedule(TimeSpan.FromMilliseconds(200), 250);

		Assert.That(queue.Peek()!.Value.At,
			Is.EqualTo(TimeSpan.FromMilliseconds(100)));
		Assert.That(queue.Pop().Tempo, Is.EqualTo(150));
		Assert.That(queue.Pop().Tempo, Is.EqualTo(200));
		Assert.That(queue.Pop().Tempo, Is.EqualTo(250));
		Assert.That(queue.HasPending, Is.False);
		Assert.That(queue.Peek(), Is.Null);
	}

	[Test]
	public void InvalidTempoAndNegativeTimeDoNotEnterTheMailbox()
	{
		DeferredTempoEventQueue queue = new();
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			queue.Schedule(TimeSpan.FromTicks(-1), 125));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			queue.Schedule(TimeSpan.Zero, double.NaN));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			queue.Schedule(TimeSpan.Zero, double.PositiveInfinity));
		Assert.Throws<InvalidOperationException>(() => queue.Pop());
		Assert.That(queue.HasPending, Is.False);
	}
}

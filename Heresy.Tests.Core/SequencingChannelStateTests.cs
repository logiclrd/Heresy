using System;
using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SequencingChannelStateTests
{
	[Test]
	public void ScopeManagerAllocatesMonotonicallyAndNeverResurrectsRetiredMemory()
	{
		ScopedSequencingChannelMemory manager = new();
		SequencingChannelStateMap root = manager[0];
		Assert.That(manager[0], Is.SameAs(root));
		Assert.That(manager.ActiveScopeCount, Is.Zero);

		long first = manager.AllocateScope();
		long second = manager.AllocateScope();
		Assert.That(first, Is.GreaterThan(0));
		Assert.That(second, Is.GreaterThan(first));
		Assert.That(manager.ActiveScopeCount, Is.EqualTo(2));
		Assert.That(manager.MaterializedScopeCount, Is.Zero,
			"Allocating an invocation without accessing its channels is free of channel maps.");

		manager[first].GetPhysical(0).CurrentSourceId = (Heresy.Core.Objects.ObjectId)42U;
		Assert.That(manager.MaterializedScopeCount, Is.EqualTo(1));
		Assert.That(manager[second].GetPhysical(0).CurrentSourceId,
			Is.EqualTo(Heresy.Core.Objects.ObjectId.None));
		Assert.That(manager[0], Is.SameAs(root));

		Assert.That(manager.ForgetScope(first), Is.True);
		Assert.That(manager.ForgetScope(first), Is.False);
		Assert.That(manager.MaterializedScopeCount, Is.EqualTo(1));
		Assert.Throws<InvalidOperationException>(() => _ = manager[first],
			"Retired scope IDs must never silently recreate a channel map.");
		long third = manager.AllocateScope();
		Assert.That(third, Is.GreaterThan(second));
		Assert.That(manager[0], Is.SameAs(root));
	}

	[Test]
	public void NestedFlattenedContextsShareManagerButNotLocalMemory()
	{
		SequencingContext root = new();
		SequencingContext child = root.FlattenedChild(physicalChannelOffset: 2);
		SequencingContext grandchild = child.FlattenedChild(physicalChannelOffset: 3);
		Assert.That(grandchild.ScopedMemory, Is.SameAs(root.ScopedMemory));
		Assert.That(child.ScopedMemory, Is.SameAs(root.ScopedMemory));
		Assert.That(child.ScopeId, Is.GreaterThan(0));
		Assert.That(grandchild.ScopeId, Is.GreaterThan(child.ScopeId));
		Assert.That(root.ScopeId, Is.Zero);
		Assert.That(child.PhysicalPlaybackOwner, Is.EqualTo(child.ScopeId));
		Assert.That(grandchild.PhysicalPlaybackOwner, Is.EqualTo(grandchild.ScopeId));
		Assert.That(grandchild.MapPhysicalChannel(0), Is.EqualTo(5));
		child.GetPhysicalChannelState(0).ResolveEffectParameter(
			EffectMemorySlot.Retrigger, 0x56);
		Assert.That(grandchild.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Retrigger, 0), Is.Zero);
		Assert.That(root.GetPhysicalChannelState(2)
			.ResolveEffectParameter(EffectMemorySlot.Retrigger, 0), Is.Zero);
		Assert.That(root.ScopedMemory.ActiveScopeCount, Is.EqualTo(2));
	}

	[Test]
	public void ZeroParameterWithoutMemoryResolvesToZero()
	{
		SequencingChannelState state = new();

		byte parameter = state.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00);

		Assert.That(parameter, Is.EqualTo(0x00));
		Assert.That(state.TryGetEffectParameter(EffectMemorySlot.Vibrato, out _), Is.False);
	}

	[Test]
	public void NonzeroParameterIsRememberedAndZeroRecallsIt()
	{
		SequencingChannelState state = new();

		Assert.That(
			state.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x53),
			Is.EqualTo(0x53));
		Assert.That(
			state.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00),
			Is.EqualTo(0x53));
	}

	[Test]
	public void NibbleMemoryPreservesZeroSpeedOrDepthIndependently()
	{
		SequencingChannelState state = new();

		Assert.That(
			state.ResolveEffectParameterNibbles(EffectMemorySlot.Vibrato, 0x53),
			Is.EqualTo(0x53));
		Assert.That(
			state.ResolveEffectParameterNibbles(EffectMemorySlot.Vibrato, 0x70),
			Is.EqualTo(0x73));
		Assert.That(
			state.ResolveEffectParameterNibbles(EffectMemorySlot.Vibrato, 0x04),
			Is.EqualTo(0x74));
		Assert.That(
			state.ResolveEffectParameterNibbles(EffectMemorySlot.Vibrato, 0x00),
			Is.EqualTo(0x74));
	}

	[Test]
	public void FlattenedChildKeepsIndependentEffectMemoryDespiteMatchingPhysicalHost()
	{
		SequencingContext parent = new(physicalChannelBase: 3);
		SequencingContext child = parent.FlattenedChild(physicalChannelOffset: 4);

		// Parent local channel 4 and child local channel 0 both host on
		// physical channel 7, but have separate logical memory namespaces.
		parent.GetPhysicalChannelState(4)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x53);

		byte recalledByChild = child.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00);

		child.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x27);

		byte recalledByParent = parent.GetPhysicalChannelState(4)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00);

		Assert.That(recalledByChild, Is.Zero);
		Assert.That(recalledByParent, Is.EqualTo(0x53));
		Assert.That(child.ChannelStates, Is.Not.SameAs(parent.ChannelStates));
	}

	[Test]
	public void FlattenedChildDoesNotShareMemoryWithDifferentMappedChannel()
	{
		SequencingContext parent = new(physicalChannelBase: 3);
		SequencingContext child = parent.FlattenedChild(physicalChannelOffset: 4);

		parent.GetPhysicalChannelState(3)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x53);

		byte childParameter = child.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00);

		Assert.That(childParameter, Is.EqualTo(0x00));
	}

	[Test]
	public void MixdownChildHasIndependentEffectMemory()
	{
		SequencingContext parent = new();
		parent.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x53);

		SequencingContext mixdown = parent.MixdownChild();

		byte initialChildParameter = mixdown.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00);

		mixdown.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x27);

		byte parentParameter = parent.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00);

		Assert.That(initialChildParameter, Is.EqualTo(0x00));
		Assert.That(parentParameter, Is.EqualTo(0x53));
		Assert.That(mixdown.ChannelStates, Is.Not.SameAs(parent.ChannelStates));
	}
}

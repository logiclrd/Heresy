using Heresy.Core.Sequencing;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SequencingChannelStateTests
{
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
	public void FlattenedChildSharesEffectMemoryOnMappedParentChannel()
	{
		SequencingContext parent = new(physicalChannelBase: 3);
		SequencingContext child = parent.FlattenedChild(physicalChannelOffset: 4);

		// Parent local channel 4 and child local channel 0 are both mapped
		// physical channel 7.
		parent.GetPhysicalChannelState(4)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x53);

		byte recalledByChild = child.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00);

		child.GetPhysicalChannelState(0)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x27);

		byte recalledByParent = parent.GetPhysicalChannelState(4)
			.ResolveEffectParameter(EffectMemorySlot.Vibrato, 0x00);

		Assert.That(recalledByChild, Is.EqualTo(0x53));
		Assert.That(recalledByParent, Is.EqualTo(0x27));
		Assert.That(child.ChannelStates, Is.SameAs(parent.ChannelStates));
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

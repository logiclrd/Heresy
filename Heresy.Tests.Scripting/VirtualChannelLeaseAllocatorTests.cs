using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Timing;

using Heresy.Core.Sequencing;
using Heresy.Scripting.Runtime;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class VirtualChannelLeaseAllocatorTests
{
    [Test]
    public void ReleaseReusesSlotWithoutReusingIdentityOrRevokingReplacement()
    {
        VirtualChannelLeaseAllocator allocator = new();
        VirtualChannelIdentity first = allocator.Acquire();
        Assert.That(allocator.Release(first), Is.True);
        VirtualChannelIdentity second = allocator.Acquire();
        Assert.Multiple(() =>
        {
            Assert.That(second.Slot, Is.EqualTo(first.Slot));
            Assert.That(second.Generation, Is.GreaterThan(first.Generation));
            Assert.That(allocator.Release(first), Is.False);
            Assert.That(allocator.Owns(first), Is.False);
            Assert.That(allocator.Owns(second), Is.True);
            Assert.That(allocator.Release(VirtualChannelIdentity.Legacy(second.Slot)), Is.False);
            Assert.That(allocator.ActiveCount, Is.EqualTo(1));
        });
        Assert.That(allocator.Release(second), Is.True);
        Assert.That(allocator.Release(second), Is.False);
    }

    [Test]
    public void RepeatedHighRateAllocationStaysBoundedByPeakOwnership()
    {
        const int peak = 8;
        VirtualChannelLeaseAllocator allocator = new();
        VirtualChannelIdentity[] leases = new VirtualChannelIdentity[peak];
        ulong prior = 0;
        for (int cycle = 0; cycle < 125000; cycle++)
        {
            for (int i = 0; i < peak; i++)
            {
                leases[i] = allocator.Acquire();
                if (leases[i].Generation <= prior)
                    Assert.Fail("Generation repeated or moved backwards.");
                prior = leases[i].Generation;
            }
            for (int i = peak - 1; i >= 0; i--)
                if (!allocator.Release(leases[i]))
                    Assert.Fail("Live lease was not released.");
        }
        Assert.Multiple(() =>
        {
            Assert.That(prior, Is.EqualTo(1000000UL));
            Assert.That(allocator.ActiveCount, Is.Zero);
            Assert.That(allocator.ReusableCount, Is.EqualTo(peak));
            Assert.That(allocator.SlotCount, Is.EqualTo((ulong)peak));
        });
    }

    [Test]
    public void RelaxedReplayReleasesOnlyAfterLastSortedReference()
    {
        var a = new VirtualChannelIdentity(0, 1);
        var b = new VirtualChannelIdentity(0, 2);
        var old = ChannelTarget.Virtual(a);
        var replacement = ChannelTarget.Virtual(b);
        NoteEvent At(double row, ChannelTarget target) =>
            new(new MusicalTime(System.TimeSpan.Zero, row), target, [new NoteCutCommand()]);
        NoteEvent[] sorted = [
            At(0, replacement), At(1, old),
            At(2, ChannelTarget.Virtual(9)), At(32, old),
        ];
        var steps = RelaxedVirtualChannelReplay.Enumerate(sorted).ToArray();
        Assert.That(steps, Is.EqualTo(new RawPatternStep[] {
            new RawPatternStep.Emit(sorted[0]),
            new RawPatternStep.ReleaseVirtualChannel(b),
            new RawPatternStep.Emit(sorted[1]),
            new RawPatternStep.Emit(sorted[2]),
            new RawPatternStep.Emit(sorted[3]),
            new RawPatternStep.ReleaseVirtualChannel(a),
        }));
    }

    [Test]
    public void StreamingRuntimeQueuesOnlySuccessfulLeaseReleases()
    {
        var program = new LeaseProbe(new IncrementalPatternEventReceiver());
        var first = program.AcquireVirtualChannelLease();
        Assert.That(program.ReleaseVirtualChannelLease(first), Is.True);
        var second = program.AcquireVirtualChannelLease();
        Assert.That(program.ReleaseVirtualChannelLease(first), Is.False);
        Assert.That(program.ReleaseVirtualChannelLease(second), Is.True);
        Assert.That(program.TryTakeVirtualChannelRelease(out var released), Is.True);
        Assert.That(released, Is.EqualTo(first));
        Assert.That(program.TryTakeVirtualChannelRelease(out released), Is.True);
        Assert.That(released, Is.EqualTo(second));
        Assert.That(program.TryTakeVirtualChannelRelease(out _), Is.False);
    }

    private sealed class LeaseProbe(INoteReceiver output)
        : PatternScriptProgram(new SequencingContext(), output, 1, 1)
    {
        protected override void ExecuteScript() { }
    }

    [Test]
    public void DistinctInvocationsUseSeparateAllocatorsAndDoNotShareState()
    {
        VirtualChannelLeaseAllocator first = new(), second = new();
        VirtualChannelIdentity a = first.Acquire(), b = second.Acquire();
        Assert.That(a, Is.EqualTo(b), "The external invocation owner qualifies the identity.");
        Assert.That(first.Release(a), Is.True);
        Assert.That(second.Owns(b), Is.True);
        Assert.That(second.ActiveCount, Is.EqualTo(1));
    }
}

using System;

using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Runtime;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptObjectIdOverloadTests
{
    [TestCase(-1, uint.MaxValue)]
    [TestCase(-3, 4294967293U)]
    [TestCase(int.MinValue, 2147483648U)]
    [TestCase(17, 17U)]
    [TestCase(int.MaxValue, 2147483647U)]
    public void PatternSignedLookupAgreesWithUnsignedIdentity(int value, uint expected)
    {
        PatternLookupProbe probe = new();
        Assert.That(probe.Signed(value), Is.EqualTo(probe.Unsigned(expected)));
        Assert.That(probe.Signed(value), Is.EqualTo((ObjectId)expected));
    }

    [TestCase(-1, uint.MaxValue)]
    [TestCase(-5, 4294967291U)]
    [TestCase(int.MinValue, 2147483648U)]
    [TestCase(42, 42U)]
    public void SequenceSignedLookupAgreesWithUnsignedIdentity(int value, uint expected)
    {
        SequenceLookupProbe probe = new();
        Assert.That(probe.Signed(value), Is.EqualTo(probe.Unsigned(expected)));
        Assert.That(probe.Signed(value), Is.EqualTo((ObjectId)expected));
    }

    [Test]
    public void ZeroStillUsesEstablishedOutOfRangeValidation()
    {
        PatternLookupProbe pattern = new();
        SequenceLookupProbe sequence = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => pattern.Signed(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pattern.Unsigned(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.Signed(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.Unsigned(0));
    }

    private sealed class PatternLookupProbe : PatternScriptProgram
    {
        public PatternLookupProbe()
            : base(new SequencingContext(), new NoteScheduleBuilder(), 1, 1)
        {
        }

        public ObjectId Signed(int id) => _O(id);
        public ObjectId Unsigned(uint id) => _O(id);
        protected override void ExecuteScript() { }
    }

    private sealed class SequenceLookupProbe : SequenceScriptProgram
    {
        public SequenceLookupProbe() : base(new SequencingContext()) { }

        public ObjectId Signed(int id) => _O(id);
        public ObjectId Unsigned(uint id) => _O(id);

        protected override SequenceEntry? ExecuteEntry(
            int absoluteIndex, int sequenceIndex, int previousSequenceIndex)
            => null;
    }
}

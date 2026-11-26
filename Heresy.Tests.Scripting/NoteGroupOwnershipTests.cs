using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;
using Heresy.Scripting.Runtime;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class NoteGroupOwnershipTests
{
    private static ScriptPatternDefinition Pattern(string source) =>
        new((ObjectId)1U, "Group ownership") { RowCount = 32, ChannelCount = 1, Source = source };

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void OwningGroupTransfersLeasesAndFansOutEveryControlWithOriginalSpans(bool relaxed, bool traced)
    {
        string source = """
            using (var chord = NewNoteGroup())
            {
                var first = NoteVirtual(0, _O(17));
                chord += first;
                chord += NoteVirtual(0, _O(18), Semitone(4));
                chord += NoteVirtual(0, _O(19), Semitone(7));
                first.Dispose();
                first.Cut(1);
                chord.Volume(
                    4, 0.25);
                chord.SourceFrame(5, 256);
                chord.Displacement(6, NoteDisplacementAction.Continue);
                chord.Past(7, TrackerPastNoteAction.Cut);
                chord.NoteFilter(8, _O(20));
                chord.ChannelFilter(9, _O(21));
                chord.Off(10);
                chord.Cut(11);
            }
            using var next = NoteVirtual(12, _O(17));
            next.Off(13);
            """;
        if (relaxed) source = "#order relaxed\n" + source;
        var pattern = Pattern(source);
        var eager = ScriptCompiler.CompilePattern(pattern);
        var incremental = ScriptCompiler.CompileIncrementalPattern(pattern, traced);
        Assert.That(eager.Success, Is.True, string.Join("; ", eager.Diagnostics.Select(d => d.Message)));
        Assert.That(incremental.Success, Is.True, string.Join("; ", incremental.Diagnostics.Select(d => d.Message)));
        Assert.That(incremental.Diagnostics.Any(d => d.Code == "HRS2101"), Is.False);
        NoteScheduleBuilder builder = new();
        eager.Program!.GenerateRawNotes(new SequencingContext(), builder, out _);
        var expected = builder.Freeze().ToArray();
        SequencingContext context = new();
        var steps = incremental.Program!.EnumerateRawSteps(context).ToArray();
        var actual = steps.OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        Assert.That(actual.Length, Is.EqualTo(29));
        var identities = actual.Take(3).Select(e => e.Target).ToArray();
        Assert.That(identities.Distinct().Count(), Is.EqualTo(3));
        if (traced)
        {
            string[] starts = ["NoteVirtual(0, _O(17))", "NoteVirtual(0, _O(18), Semitone(4))",
                "NoteVirtual(0, _O(19), Semitone(7))"];
            for (int i = 0; i < starts.Length; i++)
                Assert.That(actual[i].EditorCallsite, Is.EqualTo(new SequenceCallsite(
                    source.IndexOf(starts[i], StringComparison.Ordinal), starts[i].Length)));
        }
        string[] spans = [
            "chord.Volume(\n        4, 0.25)", "chord.SourceFrame(5, 256)",
            "chord.Displacement(6, NoteDisplacementAction.Continue)",
            "chord.Past(7, TrackerPastNoteAction.Cut)", "chord.NoteFilter(8, _O(20))",
            "chord.ChannelFilter(9, _O(21))", "chord.Off(10)", "chord.Cut(11)",
        ];
        for (int i = 0; i < spans.Length; i++)
        {
            NoteEvent[] groupEvents = actual.Skip(3 + i * 3).Take(3).ToArray();
            Assert.That(groupEvents.Select(e => e.Target), Is.EqualTo(identities));
            Assert.That(groupEvents.Select(e => e.Offset.RowOffset), Is.All.EqualTo(4 + i));
            foreach (var note in groupEvents)
                if (traced)
                    Assert.That(note.EditorCallsite, Is.EqualTo(new SequenceCallsite(
                        source.IndexOf(spans[i], StringComparison.Ordinal), spans[i].Length)));
                else
                    Assert.That(note.EditorCallsite, Is.Null);
        }
        Assert.Multiple(() =>
        {
            Assert.That(actual.Select(e => e.Target), Is.EqualTo(expected.Select(e => e.Target)));
            Assert.That(actual.Select(e => e.Commands.Single() is StartNoteCommand start
                ? start with { EditorOriginCallsite = null } : e.Commands.Single()),
                Is.EqualTo(expected.Select(e => e.Commands.Single())));
            Assert.That(identities.Any(t => t.VirtualIdentity.Slot == actual[27].Target.VirtualIdentity.Slot), Is.True);
            Assert.That(identities.Contains(actual[27].Target), Is.False);
            Assert.That(context.Diagnostics.VirtualOwnershipWarnings, Is.EqualTo(1));
            Assert.That(steps.OfType<RawPatternStep.ReleaseVirtualChannel>().Count(), Is.EqualTo(4));
        });
    }

    [Test]
    public void SuspendedFanoutStopsBeforeFollowingAuthoredStatementAndDisposesGroup()
    {
        var pattern = Pattern("""
            using var chord = NewNoteGroup();
            chord += NoteVirtual(0, _O(17));
            chord += NoteVirtual(0, _O(18));
            chord += NoteVirtual(0, _O(19));
            chord.Volume(4, 0.5);
            Note(8, 0, _O(20));
            """);
        var result = ScriptCompiler.CompileIncrementalPattern(pattern);
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        SequencingContext context = new();
        using (var iterator = result.Program!.EnumerateRawSteps(context).GetEnumerator())
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.That(iterator.MoveNext(), Is.True);
                Assert.That(iterator.Current, Is.TypeOf<RawPatternStep.Emit>());
            }
            Assert.That(((RawPatternStep.Emit)iterator.Current).Note.Offset.RowOffset, Is.EqualTo(4));
            // Dispose while the other two member events are not yet emitted.
        }
        Assert.That(context.Diagnostics.VirtualOwnershipWarnings, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedTransfersAndDisposedAliasesDoNotRevokeGroupOwnership(bool relaxed)
    {
        string source = """
            using var a = NewNoteGroup();
            using var b = NewNoteGroup();
            using var note = NoteVirtual(0, _O(17));
            var alias = note;
            a += note;
            b += alias;
            a += alias;
            alias.Off(1);
            alias.Dispose();
            b.Dispose();
            using var stillOwned = NoteVirtual(2, _O(18));
            b += stillOwned;
            stillOwned.Volume(3, 0.5);
            a.Cut(4);
            a.Dispose();
            a.Off(5);
            """;
        if (relaxed) source = "#order relaxed\n" + source;
        var result = ScriptCompiler.CompileIncrementalPattern(Pattern(source));
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        SequencingContext context = new();
        var actual = result.Program!.EnumerateRawSteps(context).OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        Assert.That(actual.Select(e => e.Offset.RowOffset), Is.EqualTo(new double[] { 0, 2, 3, 4 }));
        Assert.That(actual[0].Target, Is.EqualTo(actual[3].Target));
        Assert.That(actual[1].Target, Is.EqualTo(actual[2].Target));
        Assert.That(actual[0].Target, Is.Not.EqualTo(actual[1].Target));
        Assert.That(context.Diagnostics.VirtualOwnershipWarnings, Is.EqualTo(5));
    }

    [Test]
    public void CrossInvocationTransferIsRejectedWithoutLosingEitherLease()
    {
        SequencingContext firstContext = new(), secondContext = new();
        var first = new Probe(firstContext);
        var second = new Probe(secondContext);
        var destination = first.CreateGroup();
        using var destinationScope = destination;
        using var own = first.CreateNote();
        using var foreign = second.CreateNote();
        destination += foreign;
        destination += own;
        destination.Off(1);
        foreign.Off(1);
        Assert.That(firstContext.Diagnostics.VirtualOwnershipWarnings, Is.EqualTo(1));
        Assert.That(secondContext.Diagnostics.VirtualOwnershipWarnings, Is.Zero);
        Assert.That(first.Output.Freeze().Count(), Is.EqualTo(2));
        Assert.That(second.Output.Freeze().Count(), Is.EqualTo(2));
    }

    private sealed class Probe : PatternScriptProgram
    {
        public NoteScheduleBuilder Output { get; }
        public Probe(SequencingContext context) : this(context, new NoteScheduleBuilder()) { }
        private Probe(SequencingContext context, NoteScheduleBuilder output)
            : base(context, output, 16, 1) => Output = output;
        protected override void ExecuteScript() { }
        public NoteGroup CreateGroup() => NewNoteGroup();
        public VirtualNote CreateNote() => NoteVirtual(0, (ObjectId)17U);
    }

    [TestCase("using var chord = NewNoteGroup(); chord += NoteVirtual(0, _O(17)); chord.Off(4);")]
    [TestCase("var chord = NewNoteGroup(); chord += NoteVirtual(0, _O(17)); chord.Dispose();")]
    [TestCase("using (var chord = NewNoteGroup()) { var note = NoteVirtual(0, _O(17)); chord += note; }")]
    public void DirectGroupOwnershipTerminationDoesNotWarn(string source)
    {
        var result = ScriptCompiler.CompileIncrementalPattern(Pattern(source));
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.That(result.Diagnostics.Any(d => d.Code == "HRS2101"), Is.False);
    }

    [Test]
    public void AbandonedGroupOwnershipWarnsAndCleansUp()
    {
        var result = ScriptCompiler.CompileIncrementalPattern(Pattern(
            "var chord = NewNoteGroup(); chord += NoteVirtual(0, _O(17));"));
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.That(result.Diagnostics.Any(d => d.Code == "HRS2101"), Is.True);
        var context = new SequencingContext();
        Assert.That(result.Program!.EnumerateRawSteps(context).OfType<RawPatternStep.Emit>().Count(), Is.EqualTo(1));
        Assert.That(context.Diagnostics.VirtualOwnershipWarnings, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UsingScopeLoweringPreservesNestedScopesCollisionAvoidanceAndOriginalF6Spans(bool relaxed)
    {
        // Deliberately occupy the compiler's initial candidate prefix and
        // put several calls on the same CRLF line as a lowered declaration.
        string source = """
            int __heresyUsingScope0 = 0;
            using var outer = NewNoteGroup(); outer += NoteVirtual(0, _O(17));
            using (var inner = NewNoteGroup())
            {
                inner += NoteVirtual(1, _O(18));
                inner.Cut(2);
            }
            using (var single = NewNoteGroup()) single += NoteVirtual(3, _O(19));
            outer.Volume(4, 0.5 + __heresyUsingScope0);
            outer.Off(5);
            """.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (relaxed) source = "#order relaxed\r\n" + source;

        var pattern = Pattern(source);
        var analysis = ScriptCompiler.AnalyzePatternSource(source);
        Assert.That(analysis.Any(d => d.Severity == ScriptDiagnosticSeverity.Error || d.Code == "HRS2101"),
            Is.False, string.Join("; ", analysis.Select(d => d.Message)));
        var eager = ScriptCompiler.CompilePattern(pattern);
        var incremental = ScriptCompiler.CompileIncrementalPattern(pattern, instrumentActionCallsites: true);
        Assert.That(eager.Success, Is.True, string.Join("; ", eager.Diagnostics.Select(d => d.Message)));
        Assert.That(incremental.Success, Is.True, string.Join("; ", incremental.Diagnostics.Select(d => d.Message)));
        var context = new SequencingContext();
        var events = incremental.Program!.EnumerateRawSteps(context)
            .OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        string[] spans = [
            "NoteVirtual(0, _O(17))", "NoteVirtual(1, _O(18))", "inner.Cut(2)",
            "NoteVirtual(3, _O(19))", "outer.Volume(4, 0.5 + __heresyUsingScope0)", "outer.Off(5)",
        ];
        Assert.That(events.Length, Is.EqualTo(spans.Length));
        for (int i = 0; i < spans.Length; i++)
            Assert.That(events[i].EditorCallsite, Is.EqualTo(new SequenceCallsite(
                source.IndexOf(spans[i], StringComparison.Ordinal), spans[i].Length)));
        Assert.That(events[3].Target.VirtualIdentity.Slot, Is.EqualTo(events[1].Target.VirtualIdentity.Slot));
        Assert.That(events[3].Target, Is.Not.EqualTo(events[1].Target));
        Assert.That(events[4].Target, Is.EqualTo(events[0].Target));
        Assert.That(context.Diagnostics.VirtualOwnershipWarnings, Is.Zero);
    }

    [TestCase("using var chord = NewNoteGroup(); chord += NoteVirtual(0, _O(17)); chord = NewNoteGroup();")]
    [TestCase("using (var chord = NewNoteGroup()) { chord += NoteVirtual(0, _O(17)); chord = NewNoteGroup(); }")]
    [TestCase("using var chord = NewNoteGroup(); chord += NoteVirtual(0, _O(17)); System.Threading.Interlocked.Exchange(ref chord, NewNoteGroup());")]
    public void UsingGroupLoweringDoesNotPermitOtherWritesToReadonlyLocal(string source)
    {
        var diagnostics = ScriptCompiler.AnalyzePatternSource(source);
        Assert.That(diagnostics.Any(d => d.Severity == ScriptDiagnosticSeverity.Error
            && d.Code is "CS1656" or "CS1657"), Is.True,
            string.Join("; ", diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.That(ScriptCompiler.CompilePattern(Pattern(source)).Success, Is.False);
        Assert.That(ScriptCompiler.CompileIncrementalPattern(Pattern(source)).Success, Is.False);
    }

    [Test]
    public void LoweredScopeDoesNotLeakAfterUsingBlock()
    {
        const string source = """
            using (var chord = NewNoteGroup()) { chord += NoteVirtual(0, _O(17)); }
            chord.Off(4);
            """;
        Assert.That(ScriptCompiler.AnalyzePatternSource(source).Any(d =>
            d.Severity == ScriptDiagnosticSeverity.Error && d.Code == "CS0103"), Is.True);
    }

    [TestCase("using var chord = NewNoteGroup(); chord.Note(0, _O(17));")]
    [TestCase("using var chord = NewNoteGroup(); chord.Volume(default(SequenceCallsite), 0, .5);")]
    [TestCase("var chord = new NoteGroup(null);")]
    [TestCase("using var chord = NewNoteGroup(); var note = NoteVirtual(0, _O(17)); var bad = chord + note;")]
    public void UnsupportedGroupMembersAreNotAuthorCallable(string source)
    {
        Assert.That(ScriptCompiler.AnalyzePatternSource(source).Any(d => d.Severity == ScriptDiagnosticSeverity.Error), Is.True);
    }
}

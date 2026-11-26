using System;
using System.Linq;

using Heresy.Core.Diagnostics;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class VirtualNoteOwnershipTests
{
    private static ScriptPatternDefinition Pattern(string source) =>
        new((ObjectId)1U, "Virtual ownership") { RowCount = 16, ChannelCount = 1, Source = source };

    [TestCase(false)]
    [TestCase(true)]
    public void HandleUsesSameIdentityUntilDisposeThenReusesSlotWithNewGeneration(bool relaxed)
    {
        string source = """
            using (var a = NoteVirtual(0, _O(17)))
            {
                a.Volume(1, 0.5);
                a.Note(2, _O(18));
                a.SourceFrame(3, 256);
                a.Displacement(4, NoteDisplacementAction.Continue);
                a.Past(5, TrackerPastNoteAction.Cut);
                a.NoteFilter(6, _O(19));
                a.ChannelFilter(7, _O(20));
                a.Off(8);
            }
            using var b = NoteVirtual(9, _O(17));
            b.Cut(10);
            """;
        if (relaxed) source = "#order relaxed\n" + source;
        var pattern = Pattern(source);
        var eager = ScriptCompiler.CompilePattern(pattern);
        var streamed = ScriptCompiler.CompileIncrementalPattern(pattern);
        Assert.That(eager.Success, Is.True, string.Join("; ", eager.Diagnostics.Select(d => d.Message)));
        Assert.That(streamed.Success, Is.True, string.Join("; ", streamed.Diagnostics.Select(d => d.Message)));
        var context = new SequencingContext();
        NoteScheduleBuilder output = new();
        eager.Program!.GenerateRawNotes(new(), output, out _);
        var expected = output.Freeze().ToArray();
        var steps = streamed.Program!.EnumerateRawSteps(context).ToArray();
        var events = steps.OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        Assert.That(events.Length, Is.EqualTo(11));
        var first = events[0].Target.VirtualIdentity;
        var second = events[9].Target.VirtualIdentity;
        Assert.Multiple(() =>
        {
            Assert.That(first.Generation, Is.Not.Zero);
            Assert.That(events.Take(9).Select(e => e.Target.VirtualIdentity), Is.All.EqualTo(first));
            Assert.That(second.Slot, Is.EqualTo(first.Slot));
            Assert.That(second.Generation, Is.GreaterThan(first.Generation));
            Assert.That(events[10].Target.VirtualIdentity, Is.EqualTo(second));
            Assert.That(events.Select(e => e.Target), Is.EqualTo(expected.Select(e => e.Target)));
            Assert.That(events.Select(e => e.Commands.Single()), Is.EqualTo(expected.Select(e => e.Commands.Single())));
            Assert.That(steps.OfType<RawPatternStep.ReleaseVirtualChannel>().Select(s => s.Identity),
                Is.EqualTo(new[] { first, second }));
            Assert.That(context.Diagnostics.VirtualOwnershipWarnings, Is.Zero);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DisposedAliasCannotEmitOrRevokeReusedSlot(bool relaxed)
    {
        string source = """
            var first = NoteVirtual(0, _O(17));
            var alias = first;
            first.Dispose();
            using var second = NoteVirtual(1, _O(18));
            alias.Cut(2);
            alias.Dispose();
            second.Volume(3, 0.25);
            """;
        if (relaxed) source = "#order relaxed\n" + source;
        var result = ScriptCompiler.CompileIncrementalPattern(Pattern(source));
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        SequencingContext context = new();
        var events = result.Program!.EnumerateRawSteps(context).OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(events.Select(e => e.Offset.RowOffset), Is.EqualTo(new double[] { 0, 1, 3 }));
            Assert.That(events[0].Target.VirtualIdentity.Slot, Is.EqualTo(events[1].Target.VirtualIdentity.Slot));
            Assert.That(events[0].Target, Is.Not.EqualTo(events[1].Target));
            Assert.That(events[2].Target, Is.EqualTo(events[1].Target));
            Assert.That(context.Diagnostics.VirtualOwnershipWarnings, Is.EqualTo(1));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InitializerAndMethodHaveFullAuthoredF6Spans(bool relaxed)
    {
        string source = """
            using var note = NoteVirtual(0, _O(17));
            note.Note(
                2, _O(18), pitchMultiplier: 2);
            note.Off(3);
            """;
        if (relaxed) source = "#order relaxed\n" + source;
        var result = ScriptCompiler.CompileIncrementalPattern(Pattern(source), true);
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var events = result.Program!.EnumerateRawSteps(new()).OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        var spans = events.Select(e => e.EditorCallsite is SequenceCallsite site
            ? source.Substring(site.Start, site.Length) : "").ToArray();
        Assert.That(spans, Is.EqualTo(new[] {
            "NoteVirtual(0, _O(17))",
            "note.Note(\n    2, _O(18), pitchMultiplier: 2)",
            "note.Off(3)",
        }));
        Assert.That(((StartNoteCommand)events[1].Commands.Single()).EditorOriginCallsite,
            Is.EqualTo(events[1].EditorCallsite));
    }

    [Test]
    public void SuspendedUsingDisposesWithoutLeakWarningWhereBareHandleDoesNot()
    {
        foreach (bool owned in new[] { false, true })
        {
            string source = owned
                ? "using (var note = NoteVirtual(0, _O(17))) { note.Off(4); }"
                : "var note = NoteVirtual(0, _O(17)); note.Off(4);";
            var result = ScriptCompiler.CompileIncrementalPattern(Pattern(source));
            Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            var context = new SequencingContext();
            using (var iterator = result.Program!.EnumerateRawSteps(context).GetEnumerator())
            {
                Assert.That(iterator.MoveNext(), Is.True);
                Assert.That(iterator.Current, Is.TypeOf<RawPatternStep.Emit>());
            }
            Assert.That(context.Diagnostics.VirtualOwnershipWarnings, Is.EqualTo(owned ? 0 : 1));
        }
    }

    [Test]
    public void OwnershipAnalysisWarnsOnDiscardAndUnprovenLocalButNotUsing()
    {
        foreach (string source in new[] {
            "NoteVirtual(0, _O(17));",
            "var note = NoteVirtual(0, _O(17)); note.Off(4);",
        })
            Assert.That(ScriptCompiler.AnalyzePatternSource(source).Any(d =>
                d.Code == "HRS2101" && d.Severity == ScriptDiagnosticSeverity.Warning), Is.True);
        foreach (string source in new[] {
            "using var note = NoteVirtual(0, _O(17)); note.Off(4);",
            "using (var note = NoteVirtual(0, _O(17))) { note.Off(4); }",
            "var note = NoteVirtual(0, _O(17)); note.Off(4); note.Dispose();",
        })
            Assert.That(ScriptCompiler.AnalyzePatternSource(source).Any(d => d.Code == "HRS2101"), Is.False);
    }

    [TestCase("NoteVirtual(0, 7U, _O(17));")]
    [TestCase("OffVirtual(0, 7U);")]
    [TestCase("CutVirtual(0, 7U);")]
    [TestCase("VolumeVirtual(0, 7U, 0.5);")]
    [TestCase("SourceFrameVirtual(0, 7U, 256);")]
    [TestCase("DisplacementVirtual(0, 7U, NoteDisplacementAction.Continue);")]
    [TestCase("PastVirtual(0, 7U, TrackerPastNoteAction.Cut);")]
    [TestCase("NoteFilterVirtual(0, 7U, _O(19));")]
    [TestCase("ChannelFilterVirtual(0, 7U, _O(19));")]
    public void RetiredNumericScriptApiIsRejectedInAllModes(string source)
    {
        foreach (string authored in new[] { source, "#order relaxed\n" + source })
        {
            Assert.That(ScriptCompiler.AnalyzePatternSource(authored).Any(d =>
                d.Severity == ScriptDiagnosticSeverity.Error), Is.True);
            Assert.That(ScriptCompiler.CompilePattern(Pattern(authored)).Success, Is.False);
            Assert.That(ScriptCompiler.CompileIncrementalPattern(Pattern(authored)).Success, Is.False);
            Assert.That(ScriptCompiler.CompileIncrementalPattern(Pattern(authored), true).Success, Is.False);
        }
    }

    [TestCase("using var note = NoteVirtual(0, _O(17)); note.Off(default(Heresy.Core.Sequences.SequenceCallsite), 2);")]
    [TestCase("var note = new VirtualNote(null, default);")]
    [TestCase("HasPendingEvent();")]
    [TestCase("AcquireVirtualChannelLease();")]
    public void TrustedHostMembersCannotBeAuthored(string source)
    {
        Assert.That(ScriptCompiler.AnalyzePatternSource(source).Any(d => d.Severity == ScriptDiagnosticSeverity.Error), Is.True);
    }
}

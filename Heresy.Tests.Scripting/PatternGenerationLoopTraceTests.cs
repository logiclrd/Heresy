using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class PatternGenerationLoopTraceTests
{
    private static PatternGenerationTraceResult Capture(string source, int limit = 100_000)
    {
        var pattern = new ScriptPatternDefinition((ObjectId)1U, "Loop trace")
        { Source = source, RowCount = 16, ChannelCount = 2 };
        var compiled = ScriptCompiler.CompilePatternGenerationTrace(pattern);
        Assert.That(compiled.Success, Is.True, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        return compiled.Program!.Capture(maximumRecords: limit);
    }

    private static void AssertBalanced(PatternGenerationTraceResult trace)
    {
        Stack<PatternGenerationTraceMarker> scopes = new();
        var timeline = trace.Markers.Select(m => (m.TraceOrdinal, Marker: (PatternGenerationTraceMarker?)m, Event: (PatternGenerationTraceEvent?)null))
            .Concat(trace.Events.Select(e => (e.TraceOrdinal, Marker: (PatternGenerationTraceMarker?)null, Event: (PatternGenerationTraceEvent?)e)))
            .OrderBy(item => item.TraceOrdinal).ToArray();
        Assert.That(timeline.Select(i => i.TraceOrdinal), Is.EqualTo(Enumerable.Range(0, timeline.Length).Select(i => (long)i)));
        foreach (var entry in timeline)
        {
            long? active = scopes.TryPeek(out var current) ? current.ScopeId : null;
            if (entry.Event is { } note)
                Assert.That(note.EnclosingScopeId, Is.EqualTo(active));
            else if (entry.Marker is { } marker)
            {
                if (marker.Kind is PatternGenerationTraceMarkerKind.LoopEnter or PatternGenerationTraceMarkerKind.IterationBegin)
                {
                    Assert.That(marker.ScopeId, Is.EqualTo(marker.TraceOrdinal));
                    Assert.That(marker.ParentScopeId, Is.EqualTo(active));
                    scopes.Push(marker);
                }
                else
                {
                    var opening = scopes.Pop();
                    Assert.That(marker.ScopeId, Is.EqualTo(opening.ScopeId));
                    Assert.That(marker.ParentScopeId, Is.EqualTo(opening.ParentScopeId));
                    Assert.That(marker.SourceSpan, Is.EqualTo(opening.SourceSpan));
                    Assert.That(marker.IterationIndex, Is.EqualTo(opening.IterationIndex));
                    Assert.That(marker.Kind, Is.EqualTo(opening.Kind == PatternGenerationTraceMarkerKind.LoopEnter
                        ? PatternGenerationTraceMarkerKind.LoopExit : PatternGenerationTraceMarkerKind.IterationEnd));
                }
            }
        }
        Assert.That(scopes, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedLoopsPreserveSourceSpansAndExecutionIdentity(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\r\n" : "") + """
            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    if (j == 1) continue;
                    Note(i * 3 + j, 0, _O(17));
                }
            }
            Off(8, 0);
            """;
        var trace = Capture(source);
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        AssertBalanced(trace);
        Assert.That(trace.Events.Select(e => e.Note.Offset.RowOffset), Is.EqualTo(new double[] { 0, 2, 3, 5, 8 }));
        var loops = trace.Markers.Where(m => m.Kind == PatternGenerationTraceMarkerKind.LoopEnter).ToArray();
        Assert.That(loops.Length, Is.EqualTo(3));
        Assert.That(loops[1].SourceSpan, Is.EqualTo(loops[2].SourceSpan));
        Assert.That(loops[1].ScopeId, Is.Not.EqualTo(loops[2].ScopeId));
        Assert.That(loops[1].ParentScopeId, Is.Not.EqualTo(loops[2].ParentScopeId));
        Assert.That(source.Substring(loops[0].SourceSpan.Start, loops[0].SourceSpan.Length),
            Does.StartWith("for (int i = 0;").And.EndWith("}"));
        Assert.That(source.Substring(loops[1].SourceSpan.Start, loops[1].SourceSpan.Length),
            Does.StartWith("for (int j = 0;").And.EndWith("}"));
        Assert.That(trace.Markers.Where(m => m.Kind == PatternGenerationTraceMarkerKind.IterationBegin
            && m.ParentScopeId == loops[1].ScopeId).Select(m => m.IterationIndex),
            Is.EqualTo(new int?[] { 0, 1, 2 }));
        Assert.That(trace.Events.Last().EnclosingScopeId, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ZeroIterationAndForEachWhileDoBreakAndReturnUnwind(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\n" : "") + """
            int count = 0;
            while (count > 0) { Note(0, 0, _O(17)); }
            foreach (int n in new List<int> { 0, 1, 2 })
            {
                Note(n, 0, _O(17));
                if (n == 1) break;
            }
            do
            {
                Off(4, 0);
                count++;
            } while (count < 1);
            for (int i = 0; i < 2; i++)
            {
                Cut(6, 0);
                return;
            }
            """;
        // The existing strict compiler generates a C# iterator and does
        // not lower authored return statements to yield break. Exercise
        // early return in relaxed mode; retain existing strict language rules.
        if (!relaxed) source = source.Replace("return;", "break;", StringComparison.Ordinal);
        var trace = Capture(source);
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        AssertBalanced(trace);
        Assert.That(trace.Events.Select(e => e.Note.Offset.RowOffset), Is.EqualTo(new double[] { 0, 1, 4, 6 }));
        var enters = trace.Markers.Where(m => m.Kind == PatternGenerationTraceMarkerKind.LoopEnter).ToArray();
        Assert.That(enters.Length, Is.EqualTo(4));
        Assert.That(enters.Select(loop => trace.Markers.Count(m =>
            m.Kind == PatternGenerationTraceMarkerKind.IterationBegin && m.ParentScopeId == loop.ScopeId)),
            Is.EqualTo(new[] { 0, 2, 1, 1 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailureAndRecordLimitRetainBalancedPrefix(bool relaxed)
    {
        string header = relaxed ? "#order relaxed\n" : "";
        var failed = Capture(header + "for (int i = 0; i < 3; i++) { Note(i, 0, _O(17)); Off(17, 0); }");
        Assert.That(failed.Status, Is.EqualTo(PatternGenerationTraceStatus.Failed));
        Assert.That(failed.Events.Count, Is.EqualTo(1));
        AssertBalanced(failed);
        var limited = Capture(header + "for (int i = 0; i < 3; i++) Note(i, 0, _O(17));", 6);
        Assert.That(limited.Status, Is.EqualTo(PatternGenerationTraceStatus.Truncated));
        Assert.That(limited.Events.Count, Is.EqualTo(1));
        Assert.That(limited.Markers.Count + limited.Events.Count + limited.Invocations.Count, Is.EqualTo(6));
        AssertBalanced(limited);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroupFanoutDrainIsNotAnAuthoredLoop(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\n" : "") + """
            using var group = NewNoteGroup();
            group += NoteVirtual(0, _O(17));
            group += NoteVirtual(0, _O(18));
            for (int i = 1; i < 3; i++)
                group.Volume(i, 0.5);
            """;
        var trace = Capture(source);
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        AssertBalanced(trace);
        Assert.That(trace.Events.Count, Is.EqualTo(6));
        Assert.That(trace.Markers.Count(m => m.Kind == PatternGenerationTraceMarkerKind.LoopEnter), Is.EqualTo(1));
        Assert.That(trace.Events[2].EnclosingScopeId, Is.EqualTo(trace.Events[3].EnclosingScopeId));
        Assert.That(trace.Events[4].EnclosingScopeId, Is.EqualTo(trace.Events[5].EnclosingScopeId));
        Assert.That(trace.Events[2].EnclosingScopeId, Is.Not.EqualTo(trace.Events[4].EnclosingScopeId));
    }

    [Test]
    public void RelaxedCallbackLoopHasOriginalSpanAndBalancedIdentity()
    {
        const string source = """
            #order relaxed
            Action<int> emit = n =>
            {
                for (int i = 0; i < 2; i++)
                    Note(n + i, 0, _O(17));
            };
            emit(0);
            emit(4);
            """;
        var trace = Capture(source);
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        AssertBalanced(trace);
        var loops = trace.Markers.Where(m => m.Kind == PatternGenerationTraceMarkerKind.LoopEnter).ToArray();
        Assert.That(loops.Length, Is.EqualTo(2));
        Assert.That(loops[0].SourceSpan, Is.EqualTo(loops[1].SourceSpan));
        Assert.That(loops[0].ScopeId, Is.Not.EqualTo(loops[1].ScopeId));
        Assert.That(source.Substring(loops[0].SourceSpan.Start, loops[0].SourceSpan.Length),
            Does.StartWith("for (int i = 0;"));
    }

    [Test]
    public void DiagnosticScopeUnwindsAfterCancellationWithoutMaskingIt()
    {
        SequencingCancellation cancellation = new();
        PatternGenerationTraceBudget budget = new(cancellation, 4, TimeSpan.FromSeconds(10));
        Assert.Throws<OperationCanceledException>(() =>
        {
            using var loop = budget.EnterLoop(new SequenceCallsite(0, 3));
            using var iteration = budget.EnterIteration();
            cancellation.RequestStop();
            budget.Checkpoint();
        });
        Assert.That(budget.Markers.Select(m => m.Kind), Is.EqualTo(new[] {
            PatternGenerationTraceMarkerKind.LoopEnter, PatternGenerationTraceMarkerKind.IterationBegin,
            PatternGenerationTraceMarkerKind.IterationEnd, PatternGenerationTraceMarkerKind.LoopExit }));
        Assert.That(budget.CurrentScopeId, Is.Null);
    }

    [TestCase("__HeresyEnterTraceIteration();")]
    [TestCase("using var scope = __HeresyEnterTraceLoop(default);")]
    public void AuthoredSourceCannotInvokeDiagnosticScopes(string source)
    {
        var pattern = new ScriptPatternDefinition((ObjectId)1U, "Invalid") { Source = source };
        Assert.That(ScriptCompiler.CompilePatternGenerationTrace(pattern).Success, Is.False);
    }
}

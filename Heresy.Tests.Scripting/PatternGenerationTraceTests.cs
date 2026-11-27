using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;
using Heresy.Scripting.Runtime;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class PatternGenerationTraceTests
{
    private static ScriptPatternDefinition Pattern(string source)
        => new((ObjectId)1U, "Diagnostic") { Source = source, RowCount = 16, ChannelCount = 2 };

    private static CompiledPatternGenerationTrace Compile(string source)
    {
        var result = ScriptCompiler.CompilePatternGenerationTrace(Pattern(source));
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.Program!;
    }

    private static NoteEvent[] Playback(string source, bool f6 = false)
    {
        var result = ScriptCompiler.CompileIncrementalPattern(Pattern(source), f6);
        Assert.That(result.Success, Is.True, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
    }

    private static NoteCommand MusicalCommand(NoteEvent e)
        => e.Commands.Single() is StartNoteCommand start
            ? start with { EditorOriginCallsite = null } : e.Commands.Single();

    private static void AssertMusicalParity(NoteEvent[] expected, NoteEvent[] actual)
    {
        Assert.That(actual.Select(e => e.Offset), Is.EqualTo(expected.Select(e => e.Offset)));
        Assert.That(actual.Select(e => e.Target), Is.EqualTo(expected.Select(e => e.Target)));
        Assert.That(actual.Select(MusicalCommand), Is.EqualTo(expected.Select(MusicalCommand)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RetainsRawOrderAndMatchesPlaybackDisposition(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\r\n" : "") +
            "Cut(4, 0);\r\nOff(1, 1, timeOffsetSeconds: 0.25);\r\nNote(4, 0, _O(17), 1 + Random());";
        var trace = Compile(source).Capture();
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        Assert.That(trace.Source, Is.EqualTo(source));
        Assert.That(trace.Relaxed, Is.EqualTo(relaxed));
        Assert.That(trace.Events.Select(e => e.Note.Offset.RowOffset), Is.EqualTo(new double[] { 4, 1, 4 }));
        Assert.That(trace.Events.Select(e => e.OccurrenceId), Is.EqualTo(new long[] { 0, 1, 2 }));
        Assert.That(trace.Events.Select(e => e.DroppedInStrictMode), Is.EqualTo(new[] { false, !relaxed, false }));
        var notes = trace.Events.Where(e => !e.DroppedInStrictMode).Select(e => e.Note).ToArray();
        if (relaxed) notes = notes.OrderBy(e => e.Offset.RowOffset).ThenBy(e => e.EmissionOrder).ToArray();
        AssertMusicalParity(Playback(source), notes);
        var f6 = Playback(source, true);
        Assert.That(notes.Select(e => e.EditorCallsite), Is.EqualTo(f6.Select(e => e.EditorCallsite)));
        Assert.That(trace.Events.Select(e => source.Substring(e.Note.EditorCallsite!.Value.Start,
            e.Note.EditorCallsite.Value.Length)), Is.EqualTo(new[] {
                "Cut(4, 0)", "Off(1, 1, timeOffsetSeconds: 0.25)", "Note(4, 0, _O(17), 1 + Random())" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailureRetainsPrefixAndFactoryCanRunAgain(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\n" : "") +
            "Note(0, 0, _O(17), 1 + Random()); Off(17, 0);";
        var compiled = Compile(source);
        var first = compiled.Capture();
        var second = compiled.Capture();
        Assert.That(first.Status, Is.EqualTo(PatternGenerationTraceStatus.Failed));
        Assert.That(first.Detail, Does.Contain("ArgumentOutOfRangeException"));
        Assert.That(first.Events.Count, Is.EqualTo(1));
        Assert.That(second.Status, Is.EqualTo(first.Status));
        AssertMusicalParity(first.Events.Select(e => e.Note).ToArray(), second.Events.Select(e => e.Note).ToArray());
        if (relaxed)
        {
            var playback = ScriptCompiler.CompileIncrementalPattern(Pattern(source));
            using var iterator = playback.Program!.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
            Assert.Throws<ArgumentOutOfRangeException>(() => iterator.MoveNext());
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RecordLimitRetainsExactlyThePrefixAndExactFitCompletes(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\n" : "") +
            string.Concat(Enumerable.Range(0, 8).Select(i => $"Note({i}, 0, _O(17));"));
        var program = Compile(source);
        var limited = program.Capture(maximumRecords: 6);
        Assert.That(limited.Status, Is.EqualTo(PatternGenerationTraceStatus.Truncated));
        Assert.That(limited.Events.Select(e => e.OccurrenceId), Is.EqualTo(new long[] { 0, 1, 2 }));
        Assert.That(program.Capture(maximumRecords: 16).Status, Is.EqualTo(PatternGenerationTraceStatus.Completed));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreCanceledCaptureDoesNotExecute(bool relaxed)
    {
        var program = Compile((relaxed ? "#order relaxed\n" : "") + "Note(0, 0, _O(17));");
        SequencingCancellation cancellation = new();
        cancellation.RequestStop();
        var trace = program.Capture(cancellation);
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Canceled));
        Assert.That(trace.Events, Is.Empty);
        Assert.That(program.Capture().Events.Count, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CpuOnlyLoopHasDiagnosticStoppingBoundary(bool relaxed)
    {
        var program = Compile((relaxed ? "#order relaxed\n" : "") + "Note(0, 0, _O(17)); while (true) { }");
        var trace = program.Capture(maximumDuration: TimeSpan.FromMilliseconds(100));
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Truncated));
        // On a heavily loaded runner a time limit may expire before first emission.
        Assert.That(trace.Events.Count, Is.LessThanOrEqualTo(1));
    }

    [Test]
    public void RunningStrictLoopObservesCancellation()
    {
        var program = Compile("while (true) { }");
        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var trace = program.Capture(new SequencingCancellation(cancellation.Token));
        // The structural record cap can win the race against the timer.
        Assert.That(trace.Status, Is.AnyOf(PatternGenerationTraceStatus.Canceled,
            PatternGenerationTraceStatus.Truncated));
        if (trace.Status == PatternGenerationTraceStatus.Truncated)
            Assert.That(trace.Detail, Does.Contain("record limit"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroupFanoutUsesRealOwnershipAndStableTargets(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\n" : "") + """
            using var group = NewNoteGroup();
            group += NoteVirtual(0, _O(17));
            group += NoteVirtual(0, _O(18));
            group.Off(4);
            group.Cut(8);
            """;
        var compiled = Compile(source);
        var trace = compiled.Capture();
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        Assert.That(trace.Events.Count, Is.EqualTo(6));
        var events = trace.Events.Select(e => e.Note).ToArray();
        AssertMusicalParity(Playback(source), events);
        Assert.That(events.Skip(2).Take(2).Select(e => e.Target), Is.EqualTo(events.Take(2).Select(e => e.Target)));
        Assert.That(events.Skip(2).Take(2).Select(e => e.EditorCallsite), Is.All.EqualTo(events[2].EditorCallsite));
        // One group creation (invocation + group), two starts (invocation + event + transfer),
        // then one Off invocation and its first member event.
        var prefix = compiled.Capture(maximumRecords: 10);
        Assert.That(prefix.Status, Is.EqualTo(PatternGenerationTraceStatus.Truncated));
        AssertMusicalParity(events.Take(3).ToArray(), prefix.Events.Select(e => e.Note).ToArray());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RepeatedGroupCallsAndRecycledVirtualSlotsHaveDistinctProvenance(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\n" : "") + """
            for (int i = 0; i < 2; i++)
            {
                using var group = NewNoteGroup();
                var first = NoteVirtual(i * 6, _O(17));
                group += first;
                group += first;
                group += NoteVirtual(i * 6, _O(18));
                for (int j = 1; j < 3; j++)
                    group.Off(i * 6 + j);
            }
            """;
        var trace = Compile(source).Capture();
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        Assert.That(trace.Groups.Count, Is.EqualTo(2));
        Assert.That(trace.Groups.Select(g => g.CreationSpan), Is.All.EqualTo(trace.Groups[0].CreationSpan));
        Assert.That(trace.Groups.Select(g => g.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.That(trace.Transfers.Select(t => t.Succeeded), Is.EqualTo(new[] { true, false, true, true, false, true }));
        Assert.That(trace.Events.Count, Is.EqualTo(12));
        var starts = trace.Events.Where(e => e.Note.Commands.Single() is StartNoteCommand).ToArray();
        Assert.That(starts.Select(e => e.Note.Target.VirtualIdentity).Distinct().Count(), Is.EqualTo(4));
        Assert.That(starts.Select(e => e.Note.Target.VirtualIdentity.Slot).Distinct().Count(), Is.EqualTo(2));
        var offs = trace.Events.Where(e => e.Note.Commands.Single() is NoteOffCommand).ToArray();
        Assert.That(offs.GroupBy(e => e.InvocationId).Select(g => g.Count()), Is.EqualTo(new[] { 2, 2, 2, 2 }));
        Assert.That(offs.Select(e => e.GroupId).Distinct().Count(), Is.EqualTo(2));
        foreach (var operation in offs.GroupBy(e => e.InvocationId))
        {
            var invocation = trace.Invocations.Single(i => i.Id == operation.Key);
            Assert.That(invocation.Method, Is.EqualTo("Off"));
            Assert.That(invocation.GroupId, Is.Not.Null);
            Assert.That(source.Substring(invocation.SourceSpan.Start, invocation.SourceSpan.Length),
                Is.EqualTo("group.Off(i * 6 + j)"));
            Assert.That(operation.Select(e => e.GroupId), Is.All.EqualTo(invocation.GroupId));
            Assert.That(operation.Select(e => e.Note.Target.VirtualIdentity),
                Is.EqualTo(trace.Transfers.Where(t => t.Succeeded && t.GroupId == invocation.GroupId).Select(t => t.Channel!.Value)));
        }
        AssertMusicalParity(Playback(source), trace.Events.Select(e => e.Note).ToArray());
    }

    [Test]
    public void RelaxedBoundDelegateKeepsGroupIdentityAndRepeatedInvocationIdentity()
    {
        const string source = """
            #order relaxed
            using var group = NewNoteGroup();
            group += NoteVirtual(0, _O(17));
            group += NoteVirtual(0, _O(18));
            Action<double, double> off = group.Off;
            Action<double, double> equal = group.Off;
            if (off == equal)
            {
                off(2, 0);
                off(4, 0);
            }
            """;
        var trace = Compile(source).Capture();
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        var offs = trace.Events.Skip(2).ToArray();
        Assert.That(offs.Length, Is.EqualTo(4));
        Assert.That(offs.Select(e => e.GroupId), Is.All.EqualTo(trace.Groups.Single().Id));
        Assert.That(offs.GroupBy(e => e.InvocationId).Select(g => g.Count()), Is.EqualTo(new[] { 2, 2 }));
        AssertMusicalParity(Playback(source), trace.Events.Select(e => e.Note).ToArray());
    }

    [Test]
    public void RelaxedCallbackEmitterRetainsParentInvocationAndExactEmitterSpan()
    {
        const string source = """
            #order relaxed
            Action<int> emit = n => Note(n, 0, _O(17));
            emit(0);
            emit(1);
            """;
        var trace = Compile(source).Capture();
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        Assert.That(trace.Events.Count, Is.EqualTo(2));
        var calls = trace.Events.Select(e => trace.Invocations.Single(i => i.Id == e.InvocationId)).ToArray();
        Assert.That(calls.Select(c => c.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.That(calls.Select(c => c.ParentInvocationId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(calls.Select(c => source.Substring(c.SourceSpan.Start, c.SourceSpan.Length)),
            Is.All.EqualTo("Note(n, 0, _O(17))"));
        Assert.That(calls.Select(c => c.ParentInvocationId), Is.All.Not.Null);
    }

    [Test]
    public void RelaxedCallbacksKeepF6SourceProvenanceAndEvaluation()
    {
        const string source = """
            #order relaxed
            Action<int> emit = n => Note(n, 0, _O(17));
            for (int i = 3; i >= 0; i--) emit(i);
            """;
        var trace = Compile(source).Capture();
        Assert.That(trace.Status, Is.EqualTo(PatternGenerationTraceStatus.Completed), trace.Detail);
        Assert.That(trace.Events.Select(e => e.Note.Offset.RowOffset), Is.EqualTo(new double[] { 3, 2, 1, 0 }));
        var sorted = trace.Events.OrderBy(e => e.Note.Offset.RowOffset).Select(e => e.Note).ToArray();
        AssertMusicalParity(Playback(source), sorted);
        Assert.That(sorted.Select(e => e.EditorCallsite), Is.EqualTo(Playback(source, true).Select(e => e.EditorCallsite)));
    }

    [TestCase("__HeresyDiagnosticCheckpoint();")]
    [TestCase("base.__HeresyDiagnosticCheckpoint();")]
    [TestCase("#order relaxed\nSystem.IO.File.Delete(\"x\");")]
    [TestCase("#order relaxed\nvar x = typeof(Heresy.Scripting.Compilation.PatternGenerationTraceBudget);")]
    public void DiagnosticCompilationDoesNotAuthorizeNewAuthoredCapabilities(string source)
    {
        Assert.That(ScriptCompiler.CompilePatternGenerationTrace(Pattern(source)).Success, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GeneratedProductionAndF6TypesDoNotReferenceDiagnosticRuntime(bool relaxed)
    {
        string source = (relaxed ? "#order relaxed\n" : "") +
            "for (int i = 0; i < 2; i++) Note(i, 0, _O(17));";
        var normal = ScriptCompiler.CompileIncrementalPattern(Pattern(source));
        var f6 = ScriptCompiler.CompileIncrementalPattern(Pattern(source), true);
        Assert.That(normal.Success && f6.Success, Is.True);
        Type ordinaryType = GeneratedType(normal.Program!);
        Type f6Type = GeneratedType(f6.Program!);
        Type diagnosticType = GeneratedType(Compile(source));
        Assert.That(ordinaryType.BaseType, Is.EqualTo(typeof(PatternScriptProgram)));
        Assert.That(f6Type.BaseType, Is.EqualTo(typeof(PatternScriptProgram)));
        Assert.That(diagnosticType.BaseType, Is.EqualTo(typeof(DiagnosticPatternScriptProgram)));
        Assert.That(DiagnosticCalls(ordinaryType), Is.Empty);
        Assert.That(DiagnosticCalls(f6Type), Is.Empty);
        Assert.That(DiagnosticCalls(diagnosticType), Is.SupersetOf(new[] {
            "__HeresyDiagnosticCheckpoint", "__HeresyEnterTraceLoop", "__HeresyEnterTraceIteration" }));
    }

    private static Type GeneratedType(object factory)
        => (Type)factory.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(f => f.FieldType == typeof(Type)).GetValue(factory)!;

    // Decode method tokens only at actual IL instruction boundaries, including
    // compiler-generated iterator MoveNext and lambda methods.
    private static System.Collections.Generic.HashSet<string> DiagnosticCalls(Type type)
    {
        System.Collections.Generic.HashSet<string> calls = [];
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
            .GroupBy(op => op.Value).ToDictionary(g => g.Key, g => g.First());
        foreach (var generated in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
        foreach (var method in generated.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            for (int i = 0; i < il.Length;)
            {
                short key = il[i++];
                if (key == 0xfe) key = unchecked((short)(0xfe00 | il[i++]));
                var op = opcodes[key];
                if (op.OperandType == OperandType.InlineMethod)
                {
                    var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i));
                    if (called?.DeclaringType == typeof(DiagnosticPatternScriptProgram))
                        calls.Add(called.Name);
                }
                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4,
                };
            }
        }
        return calls;
    }
}

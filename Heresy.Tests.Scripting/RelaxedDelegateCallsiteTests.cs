using System;
using System.Linq;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Compilation;
using Heresy.Scripting.Runtime;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class RelaxedDelegateCallsiteTests
{
    private static NoteEvent[] Render(string source, bool f6)
    {
        var pattern = new ScriptPatternDefinition((ObjectId)1U, "Delegate provenance")
        { Source = source, RowCount = 16, ChannelCount = 1 };
        var compiled = ScriptCompiler.CompileIncrementalPattern(pattern, instrumentActionCallsites: f6);
        Assert.That(compiled.Success, Is.True, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        return compiled.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
    }

    private static void AssertTrace(string source, string[] expectedSpans)
    {
        var ordinary = Render(source, false);
        var traced = Render(source, true);
        Assert.Multiple(() =>
        {
            Assert.That(ordinary.All(n => n.EditorCallsite is null), Is.True);
            Assert.That(traced.Select(n => n.Offset), Is.EqualTo(ordinary.Select(n => n.Offset)));
            Assert.That(traced.Select(n => n.Target), Is.EqualTo(ordinary.Select(n => n.Target)));
            Assert.That(traced.Select(n => n.Commands.Single().GetType()),
                Is.EqualTo(ordinary.Select(n => n.Commands.Single().GetType())));
            Assert.That(traced.Select(n => source.Substring(n.EditorCallsite!.Value.Start,
                n.EditorCallsite.Value.Length)), Is.EqualTo(expectedSpans));
        });
        foreach (var note in traced)
            if (note.Commands.Single() is StartNoteCommand start)
                Assert.That(start.EditorOriginCallsite, Is.EqualTo(note.EditorCallsite));
    }

    [Test]
    public void DirectAndFrameworkInvokedCallbacksUseDifferentSpans()
    {
        const string source = """
            #order relaxed
            Action<int> foo = n => Note(n, 0, _O(17));
            for (int i = 0; i < 3; i++)
                foo(i);
            new List<int> { 3, 4 }.ForEach(foo);
            foo.Invoke(5);
            Note(6, 0, _O(17));
            """;
        AssertTrace(source, new[] { "foo(i)", "foo(i)", "foo(i)",
            "Note(n, 0, _O(17))", "Note(n, 0, _O(17))", "foo.Invoke(5)", "Note(6, 0, _O(17))" });
    }

    [Test]
    public void NestedCallsRestoreOuterSpanAndEvaluateReceiverAndArgumentsFirst()
    {
        const string source = """
            #order relaxed
            Action<int> inner = n => Note(n, 0, _O(17));
            Action<int> outer = n =>
            {
                Note(n + 1, 0, _O(17));
                inner(n + 2);
                Note(n + 3, 0, _O(17));
            };
            Func<int, int> arg = n => { Note(n, 0, _O(17)); return n; };
            Func<Action<int>> pick = () => { Note(0, 0, _O(17)); return outer; };
            pick()(arg(1));
            Note(5, 0, _O(17));
            """;
        AssertTrace(source, new[] { "pick()", "arg(1)", "pick()(arg(1))",
            "inner(n + 2)", "pick()(arg(1))", "Note(5, 0, _O(17))" });
    }

    [Test]
    public void MethodGroupCallsPreserveEqualityAndExplicitInvokeAndNamedArguments()
    {
        const string source = """
            #order relaxed
            Action<double, double> tempo = Tempo;
            Action<double, double> equal = Tempo;
            if (tempo == equal)
                tempo(1, 125);
            Action<double, int> speed = Speed;
            speed.Invoke(arg2: 3, arg1: 0);
            Action<double, int, double> cut = Cut;
            cut(2, 0, 0);
            """;
        AssertTrace(source, new[] { "speed.Invoke(arg2: 3, arg1: 0)", "tempo(1, 125)", "cut(2, 0, 0)" });
    }

    [Test]
    public void ValueReturningDelegateShortCircuitAndOrdinaryMemberNamedInvokeAreNotDisturbed()
    {
        const string source = """
            #order relaxed
            Func<bool> skip = () => { Note(0, 0, _O(17)); return true; };
            bool unused = false && skip();
            Func<int, int> twice = n => { Note(n, 0, _O(17)); return n * 2; };
            var pair = (Invoke: twice, Other: 0);
            int row = pair.Invoke(2);
            Note(row, 0, _O(17));
            """;
        AssertTrace(source, new[] { "pair.Invoke(2)", "Note(row, 0, _O(17))" });
    }

    [TestCase("__HeresyPushDelegateCallsite(default(SequenceCallsite));")]
    [TestCase("__HeresyRestoreDelegateCallsite(null);")]
    [TestCase("Action action = () => {}; __HeresyWrapDelegate(action, a => a);")]
    public void GeneratedOnlyDelegateTraceHelpersCannotBeAuthored(string code)
    {
        var pattern = new ScriptPatternDefinition((ObjectId)1U, "Forbidden trace")
        { Source = "#order relaxed\n" + code, RowCount = 4, ChannelCount = 1 };
        Assert.That(ScriptCompiler.CompilePattern(pattern).Success, Is.False);
        Assert.That(ScriptCompiler.CompileIncrementalPattern(pattern).Success, Is.False);
        Assert.That(ScriptCompiler.CompileIncrementalPattern(pattern, true).Success, Is.False);
    }

    [Test]
    public void TraceScopeRestoresAfterExceptionAndDoesNotLeakIntoSubsequentEmissions()
    {
        NoteScheduleBuilder output = new();
        var program = new ScopeProbe(output);
        program.Run();
        var events = output.Freeze().ToArray();
        Assert.That(events.Select(n => n.EditorCallsite), Is.EqualTo(new SequenceCallsite?[]
        {
            new(100, 5), new(200, 5), new(100, 5), new(300, 5), null,
        }));
    }

    private sealed class ScopeProbe(NoteScheduleBuilder output)
        : PatternScriptProgram(new SequencingContext(), output, 8, 1)
    {
        protected override void ExecuteScript() { }

        public void Run()
        {
            var outer = __HeresyPushDelegateCallsite(new SequenceCallsite(100, 5));
            try
            {
                Note(new SequenceCallsite(1, 1), 0, 0, (ObjectId)17U);
                var inner = __HeresyPushDelegateCallsite(new SequenceCallsite(200, 5));
                try
                {
                    Note(1, 0, (ObjectId)17U);
                    throw new InvalidOperationException();
                }
                catch (InvalidOperationException) { }
                finally { __HeresyRestoreDelegateCallsite(inner); }
                Cut(new SequenceCallsite(2, 1), 2, 0);
            }
            finally { __HeresyRestoreDelegateCallsite(outer); }
            Cut(new SequenceCallsite(300, 5), 3, 0);
            Cut(4, 0);
        }
    }
}

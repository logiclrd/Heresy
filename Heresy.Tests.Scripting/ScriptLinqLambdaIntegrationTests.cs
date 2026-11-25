using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Core.Sequencing;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptLinqLambdaIntegrationTests
{
    [Test]
    public void WhereSelectOrderByAndCapturesProduceIdenticalEagerStreamedAndF6Rows()
    {
        ScriptPatternDefinition pattern = new((ObjectId)1U, "LINQ notes")
        {
            RowCount = 16,
            ChannelCount = 2,
            Source = """
                int displacement = 2;
                var rows = new List<int> { 1, 2, 3, 4, 5, 6, 7 };
                var filtered = rows
                    .Where(n => (n % 2) == 1)
                    .Select(n => n + displacement)
                    .OrderBy(n => n)
                    .ToArray();
                foreach (int row in filtered)
                    Note(row, 0, _O(17));
                """,
        };

        ScriptCompiler.AnalyzePatternSource(pattern.Source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);

        var eager = ScriptCompiler.CompilePattern(pattern);
        var streamed = ScriptCompiler.CompileIncrementalPattern(pattern);
        var f6 = ScriptCompiler.CompileIncrementalPattern(
            pattern, instrumentActionCallsites: true);
        eager.Success.Should().BeTrue();
        streamed.Success.Should().BeTrue();
        f6.Success.Should().BeTrue();

        NoteScheduleBuilder collector = new();
        eager.Program!.GenerateRawNotes(
            new SequencingContext(), collector, out _);
        collector.Freeze().Select(n => n.Offset.RowOffset)
            .Should().Equal(3, 5, 7, 9);
        streamed.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Row)
            .Should().Equal(3, 5, 7, 9);
        f6.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Row)
            .Should().Equal(3, 5, 7, 9);
    }

    [Test]
    public void LambdaBlockLoopsUseSynchronousCheckpointsInsideStreamedPattern()
    {
        ScriptPatternDefinition pattern = new((ObjectId)1U, "LINQ lambda loops")
        {
            RowCount = 12,
            ChannelCount = 2,
            Source = """
                var rows = Enumerable.Range(1, 4)
                    .Where(n =>
                    {
                        int sum = 0;
                        for (int k = 0; k < n; k++)
                            sum += k;
                        return sum % 2 == 0;
                    })
                    .Select(n => n + 1)
                    .ToList();
                foreach (int row in rows)
                    Note(row, 0, _O(17));
                """,
        };
        var eager = ScriptCompiler.CompilePattern(pattern);
        var streamed = ScriptCompiler.CompileIncrementalPattern(pattern);
        var f6 = ScriptCompiler.CompileIncrementalPattern(
            pattern, instrumentActionCallsites: true);

        eager.Success.Should().BeTrue();
        streamed.Success.Should().BeTrue();
        f6.Success.Should().BeTrue();
        streamed.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Row)
            .Should().Equal(2, 5);
        f6.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Row)
            .Should().Equal(2, 5);
    }

    [Test]
    public void GroupByToLookupAndAggregateRemainApprovedLinqToObjectsOperations()
    {
        const string source = """
            var grouped = Enumerable.Range(1, 8)
                .GroupBy(n => n % 3)
                .OrderBy(group => group.Key)
                .ToList();
            var lookup = Enumerable.Range(1, 8).ToLookup(n => n % 3);
            int total = lookup[1].Aggregate(0, (sum, n) => sum + n);
            foreach (var group in grouped)
                foreach (int n in group.OrderBy(n => n))
                    if (n < total)
                        Note(n, 0, _O(17));
            """;
        ScriptCompiler.AnalyzePatternSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        ScriptPatternDefinition pattern = new((ObjectId)1U, "LINQ grouping")
        {
            RowCount = 12,
            ChannelCount = 1,
            Source = source,
        };
        ScriptCompiler.CompilePattern(pattern).Success.Should().BeTrue();
        ScriptCompiler.CompileIncrementalPattern(pattern).Success.Should().BeTrue();
        ScriptCompiler.CompileIncrementalPattern(
            pattern, instrumentActionCallsites: true).Success.Should().BeTrue();
    }

    [Test]
    public void DelegatesCannotRevealMethodInfoViaProtectedOrBlockedReflectionApi()
    {
        const string protectedAccess = """
            var function = new System.Func<int, int>(n => n + 1);
            var info = function.GetMethodImpl();
            """;
        ScriptCompiler.AnalyzePatternSource(protectedAccess)
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);

        const string blockedProperty = """
            var function = new System.Func<int, int>(n => n + 1);
            var info = function.Method;
            """;
        ScriptCompiler.AnalyzePatternSource(blockedProperty)
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [Test]
    public void SequenceLinqLambdasCompileInNormalAndF6Modes()
    {
        ScriptSequenceDefinition sequence = new((ObjectId)2U, "LINQ order")
        {
            Source = """
                var order = Enumerable.Range(0, 3)
                    .Where(index => index != 1)
                    .Select(index => index + 17)
                    .ToArray();
                if (sequenceIndex >= order.Length)
                    return default;
                return Play(_O(order[sequenceIndex]));
                """,
        };
        ScriptCompiler.AnalyzeSequenceSource(sequence.Source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        ScriptCompiler.CompileIncrementalSequence(sequence).Success.Should().BeTrue();
        ScriptCompiler.CompileIncrementalSequence(
            sequence, instrumentPlayCallsites: true).Success.Should().BeTrue();
    }

    [TestCase("var query = Enumerable.Range(1, 3).Where(n => System.IO.File.Exists(\"secret\"));")]
    [TestCase("var query = Enumerable.Range(1, 3).Select(n => System.IO.Path.GetTempFileName());")]
    [TestCase("var query = Enumerable.Range(1, 3).Where(n => string.Intern(n.ToString()) != null);")]
    [TestCase("var query = Enumerable.Range(1, 3).Where(n => System.Environment.ExitCode > 0);")]
    [TestCase("var query = Enumerable.Range(1, 3).Select(n => typeof(int));")]
    [TestCase("var query = Enumerable.Range(1, 3).Where(n => { Note(n, 0, _O(17)); return true; });")]
    [TestCase("var query = System.Linq.ParallelEnumerable.AsParallel(Enumerable.Range(1, 3));")]
    [TestCase("var query = System.Linq.Queryable.AsQueryable(Enumerable.Range(1, 3));")]
    [TestCase("System.Linq.Expressions.Expression<System.Func<int, bool>> predicate = n => n > 2;")]
    public void LambdaBodiesCannotEscapeRestrictedApiOrSongContext(string source)
    {
        ScriptCompiler.AnalyzePatternSource(source)
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        ScriptCompiler.AnalyzeSequenceSource(source)
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [Test]
    public void RelaxedCallbacksEmitThroughExpressionBodiesBlocksAndDeferredLinq()
    {
        const string source = """
            #order relaxed
            int channel = 1;
            Action<int> emit = n => Note(n, channel, _O(17));
            emit(6);
            new List<int> { 4, 1, 3 }.ForEach(emit);
            var ignored = Enumerable.Range(0, 3).Select(n =>
            {
                Cut(n, 0);
                return n;
            });
            var deferred = Enumerable.Range(0, 3).Select(n =>
            {
                for (int j = 0; j < 1; j++)
                    Off(5 - n, 0);
                return n;
            });
            foreach (int n in deferred)
                if (n == 0)
                    Note(0, 0, _O(17));
            new List<int> { 2, 1 }.ForEach(n => Cut(n, 1));
            """;
        var pattern = new ScriptPatternDefinition((ObjectId)1U, "Relaxed callbacks")
        { Source = source, RowCount = 8, ChannelCount = 2 };
        var eager = ScriptCompiler.CompilePattern(pattern);
        var ordinary = ScriptCompiler.CompileIncrementalPattern(pattern);
        var traced = ScriptCompiler.CompileIncrementalPattern(pattern, instrumentActionCallsites: true);
        Assert.Multiple(() =>
        {
            Assert.That(eager.Success, Is.True, string.Join("; ", eager.Diagnostics.Select(d => d.Message)));
            Assert.That(ordinary.Success, Is.True, string.Join("; ", ordinary.Diagnostics.Select(d => d.Message)));
            Assert.That(traced.Success, Is.True, string.Join("; ", traced.Diagnostics.Select(d => d.Message)));
        });

        NoteScheduleBuilder receiver = new();
        eager.Program!.GenerateRawNotes(new SequencingContext(), receiver, out _);
        var expected = receiver.Freeze().ToArray();
        var actual = ordinary.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        var annotated = traced.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(expected.Select(n => n.Offset.RowOffset),
                Is.EqualTo(new[] { 0d, 1d, 1d, 2d, 3d, 3d, 4d, 4d, 5d, 6d }));
            Assert.That(actual.Select(n => n.Offset), Is.EqualTo(expected.Select(n => n.Offset)));
            Assert.That(actual.Select(n => n.Target), Is.EqualTo(expected.Select(n => n.Target)));
            Assert.That(actual.Select(n => n.Commands.Single()), Is.EqualTo(expected.Select(n => n.Commands.Single())));
            Assert.That(annotated.Select(n => n.Offset), Is.EqualTo(expected.Select(n => n.Offset)));
            Assert.That(annotated.Select(n => n.Target), Is.EqualTo(expected.Select(n => n.Target)));
            Assert.That(annotated.Select(n => n.Commands.Single().GetType()),
                Is.EqualTo(expected.Select(n => n.Commands.Single().GetType())));
            Assert.That(actual.All(n => n.EditorCallsite is null), Is.True);
            // Framework-owned invocation of an expression lambda highlights
            // its direct emitter. Direct delegate-call overrides are separate.
            Assert.That(annotated.Where(n => n.Offset.RowOffset == 1
                    && n.Commands.Single() is StartNoteCommand)
                .Select(n => source.Substring(n.EditorCallsite!.Value.Start, n.EditorCallsite.Value.Length)),
                Is.EqualTo(new[] { "Note(n, channel, _O(17))" }));
            Assert.That(annotated.Where(n => n.Offset.RowOffset == 2)
                .Select(n => source.Substring(n.EditorCallsite!.Value.Start, n.EditorCallsite.Value.Length)),
                Is.EqualTo(new[] { "Cut(n, 1)" }));
            Assert.That(annotated.Where(n => n.Commands.Single() is NoteOffCommand)
                .Select(n => source.Substring(n.EditorCallsite!.Value.Start, n.EditorCallsite.Value.Length)),
                Is.All.EqualTo("Off(5 - n, 0)"));
        });
    }

    [Test]
    public void RelaxedForClauseCanEmitWithoutAStandaloneStatement()
    {
        const string source = """
            #order relaxed
            for (int row = 3; row >= 0; Note(row, 0, _O(17)), row--) { }
            """;
        var pattern = new ScriptPatternDefinition((ObjectId)1U, "For clause")
        { Source = source, RowCount = 4, ChannelCount = 1 };
        var normal = ScriptCompiler.CompileIncrementalPattern(pattern);
        var f6 = ScriptCompiler.CompileIncrementalPattern(pattern, instrumentActionCallsites: true);
        Assert.That(normal.Success && f6.Success, Is.True,
            string.Join("; ", normal.Diagnostics.Concat(f6.Diagnostics).Select(d => d.Message)));
        var expected = normal.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        var traced = f6.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(e => e.Note).ToArray();
        Assert.That(expected.Select(n => n.Offset.RowOffset), Is.EqualTo(new[] { 0d, 1d, 2d, 3d }));
        Assert.That(traced.Select(n => n.Offset), Is.EqualTo(expected.Select(n => n.Offset)));
        Assert.That(traced.Select(n => source.Substring(n.EditorCallsite!.Value.Start,
            n.EditorCallsite.Value.Length)), Is.All.EqualTo("Note(row, 0, _O(17))"));
        pattern.Source = source.Replace("#order relaxed", "#order strict", StringComparison.Ordinal);
        Assert.That(ScriptCompiler.CompileIncrementalPattern(pattern).Success, Is.False);
    }

    [Test]
    public void RelaxedCallbackExceptionDiscardsPreviouslyCollectedEvents()
    {
        var pattern = new ScriptPatternDefinition((ObjectId)1U, "Callback failure")
        {
            RowCount = 4, ChannelCount = 1,
            Source = """
                #order relaxed
                new List<int> { 0, 3, 5 }.ForEach(n => Note(n, 0, _O(17)));
                """,
        };
        var eager = ScriptCompiler.CompilePattern(pattern);
        var streamed = ScriptCompiler.CompileIncrementalPattern(pattern);
        var traced = ScriptCompiler.CompileIncrementalPattern(pattern, instrumentActionCallsites: true);
        Assert.That(eager.Success && streamed.Success && traced.Success, Is.True);
        NoteScheduleBuilder output = new();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            eager.Program!.GenerateRawNotes(new SequencingContext(), output, out _));
        Assert.That(output.Count, Is.Zero);
        foreach (var compiled in new[] { streamed, traced })
        {
            using var iterator = compiled.Program!.EnumerateRawSteps(new SequencingContext()).GetEnumerator();
            Assert.Throws<ArgumentOutOfRangeException>(() => iterator.MoveNext());
        }
    }

    [Test]
    public void LambdaBodyDiagnosticsProjectToAuthoredSource()
    {
        const string source = """
            var values = Enumerable.Range(1, 8)
                .Where(n => System.IO.Path.GetTempFileName().Length > n);
            """;
        var diagnostics = ScriptCompiler.AnalyzePatternSource(source);
        var blocked = diagnostics.First(d => d.Code == "HRS2001"
            && d.Message.Contains("GetTempFileName"));
        blocked.Span.Start.Should().BeGreaterThan(0);
        blocked.Span.Start.Should().BeLessThan(source.Length);
    }

    [Test]
    public void ApprovedMethodGroupPredicateWorksInPatternAndSequence()
    {
        const string source = """
            var words = new List<string> { "one", "" };
            Func<string, bool> isEmpty = string.IsNullOrEmpty;
            var empty = words.Where(isEmpty).ToList();
            """;
        ScriptCompiler.AnalyzePatternSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        ScriptCompiler.AnalyzeSequenceSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [Test]
    public void InstrumentAndEnvelopeScriptsDoNotAcquireLinqOrLambdaSupport()
    {
        ScriptCompiler.AnalyzeInstrumentSource(
            "new List<int> { 1, 2 }.Where(n => n > 1).Count()")
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        ScriptCompiler.AnalyzeEnvelopeSource("Enumerable.Range(1, 5).Sum()")
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }
}

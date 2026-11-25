using System;
using System.Collections.Generic;
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
public sealed class ScriptManagedDataIntegrationTests
{
    [Test]
    public void PatternCollectionsAndForeachProduceSameEagerAndStreamedRows()
    {
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Managed notes")
        {
            RowCount = 8,
            ChannelCount = 2,
            Source = """
                var rows = new System.Collections.Generic.List<int> { 0, 2, 4 };
                var byName = new System.Collections.Generic.Dictionary<string, int>();
                byName["count"] = rows.Count;
                foreach (int row in rows)
                {
                    if (byName["count"] > 0)
                        Note(row, 0, _O(17));
                }
                """,
        };

        ScriptCompiler.AnalyzePatternSource(pattern.Source)
            .Should().NotContain(d =>
                d.Severity == ScriptDiagnosticSeverity.Error);

        var eager = ScriptCompiler.CompilePattern(pattern);
        var streamed = ScriptCompiler.CompileIncrementalPattern(pattern);
        var traced = ScriptCompiler.CompileIncrementalPattern(
            pattern, instrumentActionCallsites: true);
        eager.Success.Should().BeTrue();
        streamed.Success.Should().BeTrue();
        traced.Success.Should().BeTrue();

        NoteScheduleBuilder collector = new();
        eager.Program!.GenerateRawNotes(
            new SequencingContext(), collector, out _);
        collector.Freeze().Select(n => n.Offset.RowOffset)
            .Should().Equal(0, 2, 4);

        streamed.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Row)
            .Should().Equal(0, 2, 4);
        traced.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Row)
            .Should().Equal(0, 2, 4);
    }

    [Test]
    public void SequenceCollectionsCompileWithAndWithoutF6CallsiteInstrumentation()
    {
        ScriptSequenceDefinition sequence = new((ObjectId)2U, "Managed sequence")
        {
            Source = """
                var order = new System.Collections.Generic.List<int> { 17, 19 };
                var labels = new System.Collections.Generic.Dictionary<int, string>();
                labels[0] = "first";
                if (sequenceIndex >= order.Count)
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

    [Test]
    public void ApprovedStringConstructionArraysAndSynchronousUsingCompile()
    {
        const string source = """
            string label = "Hi";
            string greeting = $"{label}, song";
            int[] rows = new int[] { 0, 2 };
            using (var reader = new System.IO.StringReader(greeting))
            {
                label = reader.ReadToEnd();
            }
            Note(rows[0], 0, _O(17));
            """;

        ScriptCompiler.AnalyzePatternSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [TestCase("System.IO.File.Delete(\"user-song.hm\");")]
    [TestCase("System.IO.Path.GetTempFileName();")]
    [TestCase("var intern = string.Intern(\"oops\");")]
    [TestCase("System.Environment.Exit(1);")]
    [TestCase("var list = new System.Collections.Generic.List<System.Type>();")]
    [TestCase("var type = typeof(string);")]
    [TestCase("var culture = new System.Globalization.CultureInfo(\"fr-CA\");")]
    [TestCase("System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator = \"!!\";")]
    [TestCase("var source = new System.Threading.CancellationTokenSource(); source.CancelAfter(20);")]
    [TestCase("var reader = new System.IO.StreamReader(\"song.hm\");")]
    [TestCase("var obj = new Heresy.Core.Sequencing.SequencingContext();")]
    public void ApprovedManagedSyntaxDoesNotOpenForbiddenCapabilities(string source)
    {
        ScriptCompiler.AnalyzePatternSource(source)
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        ScriptCompiler.AnalyzeSequenceSource(source)
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [Test]
    public void ApprovedFrameworkMethodGroupsAreAvailable()
    {
        const string source = """
            System.Func<string, bool> isEmpty = string.IsNullOrEmpty;
            bool result = isEmpty("");
            """;
        ScriptCompiler.AnalyzePatternSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        ScriptCompiler.AnalyzeSequenceSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [Test]
    public void NonPatternContextsRetainAllExistingRestrictions()
    {
        ScriptCompiler.AnalyzeInstrumentSource(
                "new System.Collections.Generic.List<string>()")
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        ScriptCompiler.AnalyzeEnvelopeSource("\"text\"")
            .Should().Contain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }
}

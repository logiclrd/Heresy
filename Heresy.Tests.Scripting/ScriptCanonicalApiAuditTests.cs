using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Scripting.Analysis;
using Heresy.Scripting.Compilation;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptCanonicalApiAuditTests
{
    private static ScriptAnalysisDiagnostic[] Audit(string source,
        ScriptWrapperKind kind = ScriptWrapperKind.Pattern)
        => ScriptCompiler.ValidateCanonicalAuthoredOperations(source, kind)
            .Where(d => d.Severity == ScriptDiagnosticSeverity.Error)
            .ToArray();

    [Test]
    public void ApprovedManagedCollectionsAreValidUnderCanonicalPolicy()
    {
        const string source = """
            var melody = new System.Collections.Generic.List<string>
            {
                "C4", "E4"
            };
            melody.Add("G4");
            var frequencies = new System.Collections.Generic.Dictionary<string, int>();
            foreach (string note in melody)
                frequencies[note] = note.Length;
            var text = $"{melody[0]} = {frequencies.Count}";
            var interned = string.IsInterned(text);
            int[] notes = new int[3];
            """;

        Audit(source).Should().BeEmpty(
            "ordinary managed allocation is approved by the owner policy");
        ScriptCompiler.AnalyzePatternSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error,
                "approved managed-data syntax is now enabled for Patterns");
    }

    [Test]
    public void CanonicalSequenceAllowsCollectionsInProduction()
    {
        const string source = """
            var indices = new System.Collections.Generic.List<int> { 17 };
            return Play(_O(indices[0]));
            """;

        Audit(source, ScriptWrapperKind.Sequence).Should().BeEmpty();
        ScriptCompiler.AnalyzeSequenceSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [Test]
    public void OrdinaryHeresyPatternAndSequenceHelpersArePreserved()
    {
        Audit("Note(0, 0, _O(17)); Cut(2, 0);").Should().BeEmpty();
        Audit("return Play(_O(17));", ScriptWrapperKind.Sequence)
            .Should().BeEmpty();
        Audit("double n = Semitone(12); Note(0, 0, _O(17), n);")
            .Should().BeEmpty();
    }

    [TestCase("System.IO.Path.GetTempFileName();", "GetTempFileName")]
    [TestCase("string.Intern(\"new interned text\");", "Intern")]
    [TestCase("new System.Globalization.CultureInfo(\"en-US\");",
        "CultureInfo")]
    [TestCase("System.Environment.Exit(1);", "Environment")]
    [TestCase("System.IO.File.Delete(\"song.hm\");", "System.IO.File")]
    [TestCase("System.TimeZoneInfo.ClearCachedData();", "ClearCachedData")]
    [TestCase("System.Threading.CancellationTokenSource cts = new(); cts.CancelAfter(100);",
        "CancelAfter")]
    [TestCase("System.Globalization.CultureInfo.CurrentUICulture.TextInfo.ListSeparator = \"!!\";",
        "ListSeparator")]
    [TestCase("System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.SetAllDateTimePatterns(new[] { \"yyyy\" }, 'd');",
        "SetAllDateTimePatterns")]
    [TestCase("var values = new System.Collections.Generic.List<System.Type>();",
        "System.Type")]
    [TestCase("var type = typeof(string);", "Runtime type")]
    public void CanonicalApiRejectsForbiddenOperationsBeforeTrustedRewrites(
        string source, string expected)
    {
        Audit(source).Should().Contain(d =>
            d.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Test]
    public void IndirectMethodGroupCannotCaptureForbiddenRuntimeEntryPoint()
    {
        const string source = """
            System.Func<string> later = System.IO.Path.GetTempFileName;
            """;

        Audit(source).Should().Contain(d =>
            d.Message.Contains("GetTempFileName"),
            "the selected method is blocked before it becomes a delegate");
    }

    [Test]
    public void BlockedApiDiagnosticsMapBackToOriginalSource()
    {
        const string source = """
            int first = 1;
            System.IO.Path.GetTempFileName();
            """;

        var diagnostic = Audit(source).First(d =>
            d.Message.Contains("GetTempFileName"));
        diagnostic.Span.Start.Should().Be(
            source.IndexOf("System.IO.Path.GetTempFileName()",
                StringComparison.Ordinal));
        diagnostic.Span.Length.Should().BeGreaterThan(0);
        diagnostic.Span.Start.Should().BeLessThan(source.Length);
    }

    [Test]
    public void CanonicalAuditIsNotConfusedByGeneratedSequenceFallthrough()
    {
        const string source = """
            if (sequenceIndex > 0) return default;
            return Play(_O(17));
            """;

        Audit(source, ScriptWrapperKind.Sequence).Should().BeEmpty();
    }

    [Test]
    public void CanonicalCodeCannotEscapeItsGeneratedMethod()
    {
        const string source = """
            }
            public void Escaped() { System.IO.File.Delete("song.hm"); }
            protected override void ExecuteScript() {
            """;

        Audit(source).Should().Contain(d =>
            d.Message.Contains("wrapper", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ProductionInstrumentAndEnvelopeRestrictionsRemainUnchanged()
    {
        var diagnostics = ScriptCompiler.AnalyzeInstrumentSource(
            "new System.Collections.Generic.List<string>()");
        diagnostics.Should().Contain(d =>
            d.Severity == ScriptDiagnosticSeverity.Error);
        var envelope = ScriptCompiler.AnalyzeEnvelopeSource(
            "new System.Collections.Generic.List<string>()");
        envelope.Should().Contain(d =>
            d.Severity == ScriptDiagnosticSeverity.Error);
    }

    [Test]
    public void EagerIncrementalAndF6PatternPathsRemainInAgreement()
    {
        ScriptPatternDefinition definition = new((ObjectId)1U, "Canonical")
        {
            RowCount = 8,
            ChannelCount = 2,
            Source = "Note(0, 0, _O(17)); Cut(4, 0);",
        };

        ScriptCompiler.CompilePattern(definition).Success.Should().BeTrue();
        ScriptCompiler.CompileIncrementalPattern(definition).Success.Should().BeTrue();
        ScriptCompiler.CompileIncrementalPattern(
            definition, instrumentActionCallsites: true).Success.Should().BeTrue();
    }
}

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
public sealed class ScriptMethodGroupEmissionTests
{
    private static ScriptAnalysisDiagnostic[] Errors(string source, bool sequence = false)
        => (sequence
            ? ScriptCompiler.AnalyzeSequenceSource(source)
            : ScriptCompiler.AnalyzePatternSource(source))
            .Where(d => d.Severity == ScriptDiagnosticSeverity.Error)
            .ToArray();

    [Test]
    public void ApprovedFrameworkMethodGroupsEmitIdenticalEagerStreamedAndF6Notes()
    {
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Method group LINQ")
        {
            RowCount = 8,
            ChannelCount = 1,
            Source = """
                var words = new List<string> { "", "one", "", "two" };
                Func<string, bool> isEmpty = string.IsNullOrEmpty;
                int row = 0;
                foreach (string item in words.Where(isEmpty))
                {
                    Note(row, 0, _O(17));
                    row += 2;
                }
                """,
        };

        Errors(pattern.Source).Should().BeEmpty();
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
            .Should().Equal(0, 2);
        streamed.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(x => x.Row)
            .Should().Equal(0, 2);
        f6.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(x => x.Row)
            .Should().Equal(0, 2);
    }

    [Test]
    public void ApprovedNonEmittingPatternHelpersCanBeMethodGroupTargetsOrLambdaCalls()
    {
        const string source = """
            Func<int, Heresy.Core.Objects.ObjectId> resolve = _O;
            Func<double> random = Random;
            Func<double, double> semitone = Semitone;
            Func<int, Heresy.Core.Objects.ObjectId> fromLambda = id => _O(id);
            Action checkpoint = () => Checkpoint();
            Note(0, 0, resolve(17), semitone(0));
            """;
        Errors(source).Should().BeEmpty();

        ScriptPatternDefinition pattern = new((ObjectId)1U, "Non-emitting helpers")
        {
            RowCount = 4,
            ChannelCount = 1,
            Source = source,
        };
        ScriptCompiler.CompileIncrementalPattern(pattern).Success.Should().BeTrue();
        ScriptCompiler.CompileIncrementalPattern(
            pattern, instrumentActionCallsites: true).Success.Should().BeTrue();
    }

    [Test]
    public void SequenceCanUseApprovedMethodGroupTargets()
    {
        const string source = """
            Func<int, Heresy.Core.Objects.ObjectId> resolve = _O;
            Func<string, bool> predicate = string.IsNullOrEmpty;
            return predicate("") ? Play(resolve(17)) : default;
            """;
        Errors(source, sequence: true).Should().BeEmpty();
        ScriptSequenceDefinition sequence = new((ObjectId)1U, "Sequence method group")
        {
            Source = source,
        };
        ScriptCompiler.CompileIncrementalSequence(sequence).Success.Should().BeTrue();
        ScriptCompiler.CompileIncrementalSequence(
            sequence, instrumentPlayCallsites: true).Success.Should().BeTrue();
    }

    [TestCase("Action<double, double> emit = Tempo;")]
    [TestCase("Action<double, int> emit = Speed;")]
    [TestCase("Action<double, int, double> emit = Off;")]
    [TestCase("Action<double, int, double> emit = Cut;")]
    public void StrictPatternRejectsMethodGroupsForEventGenerators(string source)
    {
        Errors(source).Should().Contain(d =>
            d.Message.Contains("Pattern event generator method groups",
                StringComparison.Ordinal));
    }

    [TestCase("Action emit = () => Tempo(0, 6);")]
    [TestCase("Action emit = () => Note(0, 0, _O(17));")]
    [TestCase("var rows = Enumerable.Range(0, 2).Select(n => { Speed(n, 6); return n; });")]
    public void StrictPatternRejectsEventGenerationInsideLambdas(string source)
    {
        Errors(source).Should().Contain(d =>
            d.Message.Contains("cannot run inside lambdas", StringComparison.Ordinal));
    }

    [Test]
    public void StrictPatternStillAllowsDirectEventGeneratorStatements()
    {
        const string source = """
            Tempo(0, 6);
            Note(1, 0, _O(17));
            Off(2, 0);
            """;
        Errors(source).Should().BeEmpty();
    }

    [Test]
    public void MethodGroupsStillEnforceBlockedMemberPolicy()
    {
        const string source = """
            Func<string> create = System.IO.Path.GetTempFileName;
            """;
        Errors(source).Should().Contain(d =>
            d.Message.Contains("GetTempFileName", StringComparison.Ordinal));
        Errors(source, sequence: true).Should().Contain(d =>
            d.Message.Contains("GetTempFileName", StringComparison.Ordinal));
    }

    [Test]
    public void RelaxedDirectiveRecognizedButEmitterMethodGroupsStillRestricted()
    {
        const string source = """
            #order relaxed
            Action<double, double> emit = Tempo;
            """;
        Errors(source).Should().Contain(d =>
            d.Message.Contains("Pattern event generator method groups", StringComparison.Ordinal),
            "callback emission is not implemented in this intermediate slice");
    }
}

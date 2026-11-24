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
public sealed class ScriptStrictRewriterAuditTests
{
    [Test]
    public void LocalDelegateNamedNoteDoesNotCreateFakeEventOrF6Callsite()
    {
        const string source = """
            {
                Action<double, int, Heresy.Core.Objects.ObjectId> Note =
                    (row, channel, sourceId) => { };
                Note(0, 0, _O(17));
            }
            Note(2, 0, _O(17));
            """;
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Shadowed Note")
        {
            Source = source, RowCount = 8, ChannelCount = 1,
        };
        ScriptCompiler.AnalyzePatternSource(source)
            .Should().NotContain(d => d.Severity == ScriptDiagnosticSeverity.Error);
        var eager = ScriptCompiler.CompilePattern(pattern);
        var streamed = ScriptCompiler.CompileIncrementalPattern(pattern);
        var traced = ScriptCompiler.CompileIncrementalPattern(
            pattern, instrumentActionCallsites: true);
        eager.Success.Should().BeTrue();
        streamed.Success.Should().BeTrue();
        traced.Success.Should().BeTrue();

        NoteScheduleBuilder collector = new();
        eager.Program!.GenerateRawNotes(new SequencingContext(), collector, out _);
        collector.Freeze().Select(n => n.Offset.RowOffset).Should().Equal(2);
        var ordinary = streamed.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Note).ToArray();
        var highlighted = traced.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Note).ToArray();
        ordinary.Should().HaveCount(1);
        highlighted.Should().HaveCount(1);
        ordinary[0].EditorCallsite.Should().BeNull();

        int actual = source.LastIndexOf("Note(2,", StringComparison.Ordinal);
        highlighted[0].EditorCallsite.Should().Be(
            new SequenceCallsite(actual, "Note(2, 0, _O(17))".Length));
        highlighted[0].Offset.Should().Be(ordinary[0].Offset);
    }

    [Test]
    public void NestedNonEmittingCallNamedTempoIsNotMistakenForEventGenerator()
    {
        const string source = """
            Func<int, int> Tempo = value => value + 1;
            int next = Tempo(1);
            Note(next, 0, _O(17));
            """;
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Shadowed Tempo")
        {
            Source = source, RowCount = 8, ChannelCount = 1,
        };
        var streamed = ScriptCompiler.CompileIncrementalPattern(pattern);
        var traced = ScriptCompiler.CompileIncrementalPattern(
            pattern, instrumentActionCallsites: true);
        streamed.Success.Should().BeTrue();
        traced.Success.Should().BeTrue();

        var normal = streamed.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Note).ToArray();
        var highlighted = traced.Program!.EnumerateRawSteps(new SequencingContext())
            .OfType<RawPatternStep.Emit>().Select(s => s.Note).ToArray();
        normal.Should().HaveCount(1);
        highlighted.Should().HaveCount(1);
        normal[0].Offset.RowOffset.Should().Be(2);
        highlighted[0].EditorCallsite.Should().Be(new SequenceCallsite(
            source.IndexOf("Note(next", StringComparison.Ordinal),
            "Note(next, 0, _O(17))".Length));
    }

    [Test]
    public void ShadowedPlayDelegateKeepsOrdinaryAndF6SequenceSemantics()
    {
        const string source = """
            {
                Func<int, int> Play = value => value + 1;
                int result = Play(3);
                if (result != 4) return default;
            }
            return Play(_O(17));
            """;
        ScriptSequenceDefinition sequence = new((ObjectId)1U, "Shadowed Play")
        {
            Source = source,
        };
        var ordinary = ScriptCompiler.CompileIncrementalSequence(sequence);
        var traced = ScriptCompiler.CompileIncrementalSequence(
            sequence, instrumentPlayCallsites: true);
        ordinary.Success.Should().BeTrue();
        traced.Success.Should().BeTrue();

        SequenceEntry? a = ordinary.Program!
            .Create(new SequencingContext()).GetSequenceEntry(0, 0, -1);
        SequenceEntry? b = traced.Program!
            .Create(new SequencingContext()).GetSequenceEntry(0, 0, -1);
        a?.PatternId.Should().Be((ObjectId)17U);
        b?.PatternId.Should().Be(a?.PatternId);
        a?.EditorCallsite.Should().BeNull();
        int start = source.LastIndexOf("Play(_O(17))", StringComparison.Ordinal);
        b?.EditorCallsite.Should().Be(
            new SequenceCallsite(start, "Play(_O(17))".Length));
    }

    [TestCase("EmitPendingStep();")]
    [TestCase("CpuCheckpoint();")]
    [TestCase("ShouldCooperate();")]
    public void InternalPatternRuntimeMethodsAreNotApprovedScriptHelpers(
        string source)
    {
        ScriptCompiler.AnalyzePatternSource(source)
            .Should().Contain(d =>
                d.Severity == ScriptDiagnosticSeverity.Error
                && d.Message.Contains("Unapproved Heresy host member",
                    StringComparison.Ordinal));
    }

    [Test]
    public void NormalHeresyHelpersRemainAvailableToPatternsAndSequences()
    {
        ScriptCompiler.AnalyzePatternSource("""
            Checkpoint();
            double sample = Random();
            Note(0, 0, _O(17));
            """).Should().NotContain(d =>
                d.Severity == ScriptDiagnosticSeverity.Error);

        ScriptCompiler.AnalyzeSequenceSource("""
            Checkpoint();
            double sample = Random();
            return Play(_O(17));
            """).Should().NotContain(d =>
                d.Severity == ScriptDiagnosticSeverity.Error);
    }
}

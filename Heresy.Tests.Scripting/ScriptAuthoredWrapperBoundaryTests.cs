using System;
using System.Linq;

using AwesomeAssertions;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Sequences;
using Heresy.Scripting.Compilation;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptAuthoredWrapperBoundaryTests
{
    private static SyntaxTree PatternTree(string authored)
        => CSharpSyntaxTree.ParseText($$"""
            public sealed class GeneratedPattern
            {
                public GeneratedPattern() { }
                public void ExecuteScript()
                {
            #line 1 "heresy-script"
            {{authored}}
            #line default
                }
            }
            """);

    private static SyntaxTree SequenceTree(string authored)
        => CSharpSyntaxTree.ParseText($$"""
            public sealed class GeneratedSequence
            {
                public GeneratedSequence() { }
                public void ExecuteEntry()
                {
            #line 1 "heresy-script"
            {{authored}}
            #line default
                }
            }
            """);

    private static SyntaxTree IncrementalPatternTree(string authored)
        => CSharpSyntaxTree.ParseText($$"""
            public sealed class GeneratedPattern
            {
                public GeneratedPattern() { }
                public void ExecuteScript() { }
                public void EnumerateScript()
                {
            #line 1 "heresy-script"
            {{authored}}
            #line default
                    yield break;
                }
            }
            """);

    [Test]
    public void CanonicalPatternSequenceAndIncrementalWrappersAreAccepted()
    {
        ScriptAuthoredWrapperBoundary.Validate(
            PatternTree("int pitch = 2;"),
            ScriptWrapperKind.Pattern).Should().BeEmpty();
        ScriptAuthoredWrapperBoundary.Validate(
            SequenceTree("return;"),
            ScriptWrapperKind.Sequence).Should().BeEmpty();
        ScriptAuthoredWrapperBoundary.Validate(
            IncrementalPatternTree("int pitch = 2;"),
            ScriptWrapperKind.IncrementalPattern).Should().BeEmpty();
    }

    [Test]
    public void ExtraAuthoredMemberIsNotTreatedAsTrustedWrapperCode()
    {
        SyntaxTree tree = PatternTree("""
            }
            public void InjectedMember() { System.IO.File.Delete("song.hm"); }
            public void ExecuteScript() {
            """);
        ScriptAuthoredWrapperBoundary.Validate(tree, ScriptWrapperKind.Pattern)
            .Should().ContainSingle(v =>
                v.Reason.Contains("wrapper's declarations", StringComparison.Ordinal));
    }

    [Test]
    public void SourceCannotDeclareAnotherTopLevelClassByClosingTrustedClass()
    {
        SyntaxTree tree = PatternTree("""
            }
            }
            public class EscapedClass { public static void Run() { } }
            class Bogus {
                public void ExecuteScript() {
            """);
        ScriptAuthoredWrapperBoundary.Validate(tree, ScriptWrapperKind.Pattern)
            .Should().Contain(v =>
                v.Reason.Contains("script class", StringComparison.Ordinal));
    }

    [Test]
    public void AuthoredLineDirectiveCannotReclassifyUntrustedCallsAsGenerated()
    {
        SyntaxTree tree = SequenceTree("""
            #line default
            System.IO.File.Delete("song.hm");
            """);
        ScriptAuthoredWrapperBoundary.Validate(tree, ScriptWrapperKind.Sequence)
            .Should().Contain(v => v.Reason.Contains("markers"));
    }

    [Test]
    public void AuthorCodeMappedIntoWrongMethodIsRejected()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
            class GeneratedPattern
            {
                GeneratedPattern()
                {
            #line 1 "heresy-script"
                    System.IO.File.Delete("song.hm");
            #line default
                }
                void ExecuteScript() { }
            }
            """);
        ScriptAuthoredWrapperBoundary.Validate(tree, ScriptWrapperKind.Pattern)
            .Should().Contain(v => v.Reason.Contains("escaped") &&
                v.Location != null);
    }

    [Test]
    public void TrustedGeneratedCodeIsNotMistakenForAuthoredCode()
    {
        // This check is intentionally *structural*, not an API authorization
        // decision. The trusted wrapper source is owned by the compiler;
        // forbidden calls here must NOT be used to authorize an author call.
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
            class GeneratedPattern
            {
                GeneratedPattern() { System.IO.File.Delete("engine-owned"); }
                void ExecuteScript()
                {
            #line 1 "heresy-script"
                    int pitch = 1;
            #line default
                }
            }
            """);
        ScriptAuthoredWrapperBoundary.Validate(tree, ScriptWrapperKind.Pattern)
            .Should().BeEmpty();
    }

    [Test]
    public void ExistingCompilerPathsStillAcceptOrdinaryPatternAndSequenceScripts()
    {
        ScriptCompiler.AnalyzePatternSource("Note(0, 0, _O(17));")
            .Should().NotContain(d => d.Severity ==
                Heresy.Scripting.Analysis.ScriptDiagnosticSeverity.Error);
        ScriptCompiler.AnalyzeSequenceSource("return Play(_O(17));")
            .Should().NotContain(d => d.Severity ==
                Heresy.Scripting.Analysis.ScriptDiagnosticSeverity.Error);
    }

    [Test]
    public void IncrementalPatternAndF6PathUseTheSameTrustBoundary()
    {
        ScriptPatternDefinition pattern = new((ObjectId)1U, "Boundary")
        {
            RowCount = 4,
            ChannelCount = 2,
            Source = "Note(0, 0, _O(17)); Cut(2, 0);",
        };
        var ordinary = ScriptCompiler.CompileIncrementalPattern(pattern);
        var instrumented = ScriptCompiler.CompileIncrementalPattern(
            pattern, instrumentActionCallsites: true);

        ordinary.Success.Should().BeTrue();
        instrumented.Success.Should().BeTrue();
    }

    [Test]
    public void ProductionCompilerNowAcceptsApprovedManagedCollections()
    {
        ScriptCompiler.AnalyzePatternSource("var xs = new System.Collections.Generic.List<string>();")
            .Should().NotContain(d => d.Severity ==
                Heresy.Scripting.Analysis.ScriptDiagnosticSeverity.Error);
    }
}

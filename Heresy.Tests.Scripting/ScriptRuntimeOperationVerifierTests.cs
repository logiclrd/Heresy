using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AwesomeAssertions;

using Heresy.Scripting.Compilation;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptRuntimeOperationVerifierTests
{
    // These are compiler *references*, not the approved script API. The
    // verifier must reject forbidden symbols despite their being resolvable.
    private static readonly MetadataReference[] References =
    [
        ..new[]
        {
            typeof(object).Assembly,
            typeof(File).Assembly,
            typeof(List<>).Assembly,
        }.Distinct().Select(assembly =>
            MetadataReference.CreateFromFile(assembly.Location)),
    ];

    private static IReadOnlyList<ScriptRuntimeOperationFinding> Check(
        string body, bool requireValidCSharp = true)
    {
        string source = """
            using System;
            using System.IO;
            using System.Collections.Generic;
            using System.Globalization;
            using System.Threading;
            using System.Buffers;
            class Script
            {
                void Run()
                {
            """ + "\n" + body + "\n" + """
                }
            }
            """;

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.Preview));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "HeresyRuntimeOperationPolicyProbe", [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(false));
        if (requireValidCSharp)
        {
            Diagnostic[] errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            errors.Should().BeEmpty("test inputs must bind correctly before their capabilities are audited");
        }

        SemanticModel model = compilation.GetSemanticModel(tree);
        BlockSyntax block = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single().Body!;
        return ScriptRuntimeOperationVerifier.Analyze(model, block);
    }

    [Test]
    public void SafeCollectionAllocationIndexingIterationAndStringsAreAccepted()
    {
        const string source = """
            var melody = new List<string> { "C4", "E4" };
            melody.Add("G4");
            var lookup = new Dictionary<string, int>();
            foreach (string note in melody)
                lookup[note] = note.Length;
            string text = $"{melody[0]}: {lookup.Count}";
            var interned = string.IsInterned(text);
            var random = new Random(123);
            int n = random.Next(100);
            """;
        Check(source).Should().BeEmpty();
    }

    [Test]
    public void ApprovedCancellationStateWithoutCallbackRegistrationIsAccepted()
    {
        const string source = """
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            bool stopped = cts.Token.IsCancellationRequested;
            var timed = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            timed.CancelAsync();
            timed.Dispose();
            """;
        Check(source).Should().BeEmpty();
    }

    [TestCase("Path.GetTempFileName()", "GetTempFileName")]
    [TestCase("string.Intern(\"global\")", "Intern")]
    [TestCase("new CultureInfo(\"en-US\")", "CultureInfo")]
    [TestCase("new StreamReader(\"song.txt\")", "StreamReader")]
    [TestCase("new StreamWriter(\"song.txt\")", "StreamWriter")]
    [TestCase("ArrayPool<int>.Shared", "Shared")]
    [TestCase("new CancellationTokenSource().CancelAfter(5)", "CancelAfter")]
    [TestCase("new CancellationToken().Register(() => {})", "Register")]
    [TestCase("CultureInfo.ClearCachedData()", "ClearCachedData")]
    [TestCase("TimeZoneInfo.ClearCachedData()", "ClearCachedData")]
    [TestCase("new DateTimeFormatInfo().SetAllDateTimePatterns(new[] { \"MM/dd\" }, 'd')", "SetAllDateTimePatterns")]
    [TestCase("Environment.Exit(3)", "Environment")]
    [TestCase("File.Delete(\"song.txt\")", "File")]
    [TestCase("typeof(string)", "Runtime type")]
    public void DirectForbiddenCapabilitiesAreIdentified(string expression,
        string expected)
    {
        var findings = Check(expression + ";");
        findings.Should().Contain(x =>
            x.Reason.Contains(expected, StringComparison.Ordinal),
            "the resolved runtime symbol, not source spelling, must be checked");
    }

    [Test]
    public void MethodGroupsCannotCaptureBlockedMembersForLaterInvocation()
    {
        var findings = Check("""
            Func<string> create = Path.GetTempFileName;
            CancellationToken token = default;
            Func<Action, CancellationTokenRegistration> register = token.Register;
            """);

        findings.Should().Contain(x => x.Reason.Contains("GetTempFileName"));
        findings.Should().Contain(x => x.Reason.Contains("Register"));
    }

    [Test]
    public void CulturePropertyWritesAreBlockedEvenWhenNestedOrCompound()
    {
        var findings = Check("""
            string original = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator = "#";
            CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator += "!";
            """);
        findings.Count(x => x.Reason.Contains("Blocked runtime member")
            && x.Reason.Contains("NumberDecimalSeparator")).Should().Be(2,
                "read is permitted, direct assignment and compound assignment are not");
    }

    [Test]
    public void GlobalizationReadsRemainAllowed()
    {
        Check("""
            string shortDate = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
            var textInfo = CultureInfo.CurrentUICulture.TextInfo;
            string name = textInfo.ToTitleCase("a song");
            """).Should().BeEmpty();
    }

    [Test]
    public void TypeArgumentsAndRuntimeTypeAccessCannotLaunderForbiddenTypes()
    {
        var findings = Check("""
            List<Type> instances = new();
            var type = typeof(string);
            """);

        findings.Should().Contain(x => x.Reason.Contains("System.Type"));
        findings.Should().Contain(x => x.Reason.Contains("Runtime type"));
    }

    [Test]
    public void UserDefinedOperatorCannotBypassBlockedFrameworkMemberInItsBody()
    {
        // No immediate compiler authorization is implied by checking one
        // scope: the eventual compilation gate must audit *all* source bodies.
        const string source = """
            using System;
            using System.IO;
            class Script
            {
                public static int Work() => Path.GetTempFileName().Length;
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "IsolatedScope", [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        SemanticModel model = compilation.GetSemanticModel(tree);
        ExpressionSyntax expression = tree.GetRoot().DescendantNodes()
            .OfType<ArrowExpressionClauseSyntax>().Single().Expression;
        ScriptRuntimeOperationVerifier.Analyze(model, expression)
            .Should().Contain(x => x.Reason.Contains("GetTempFileName"));
    }

    [Test]
    public void InvalidOrUnboundExecutableScopesAreNotSilentlyApproved()
    {
        const string source = "class Script { void Run() { UnknownApi.Run(); } }";
        var findings = Check("UnknownApi.Run();", requireValidCSharp: false);
        findings.Should().NotBeEmpty();

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "NoSilentNull", [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        SemanticModel model = compilation.GetSemanticModel(tree);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        Action checkRoot = () => ScriptRuntimeOperationVerifier.Analyze(model, root);
        checkRoot.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void NoExistingScriptCompilerBehaviorIsChangedByAnUnusedVerifier()
    {
        const string source = "var notes = new List<string>();";
        var restrictions = ScriptCompiler.AnalyzePatternSource(source);
        restrictions.Should().Contain(x => x.Message.Contains("Object creation"));
    }
}

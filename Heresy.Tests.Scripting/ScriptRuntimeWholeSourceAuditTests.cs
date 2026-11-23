using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AwesomeAssertions;

using Heresy.Scripting.Compilation;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptRuntimeWholeSourceAuditTests
{
    private static readonly MetadataReference[] References =
        new[]
        {
            typeof(object).Assembly,
            typeof(File).Assembly,
            typeof(List<>).Assembly,
        }.Distinct().Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();

    private static IReadOnlyList<ScriptRuntimeOperationFinding> Audit(
        string source, bool expectValidCSharp = true,
        OutputKind output = OutputKind.DynamicallyLinkedLibrary)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.Preview));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "HeresyWholeSourceProbe", [tree], References,
            new CSharpCompilationOptions(output, allowUnsafe: false));

        if (expectValidCSharp)
            compilation.GetDiagnostics()
                .Where(x => x.Severity == DiagnosticSeverity.Error)
                .Should().BeEmpty("audit probes must be valid bound C#");

        return ScriptRuntimeOperationVerifier.AnalyzeSource(compilation);
    }

    [Test]
    public void OrdinaryHelpersFieldInitializersAndSynchronousDisposalAreAllowed()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.IO;

            class Script
            {
                private List<string> notes = new() { "C4", "E4" };
                private static string label = "song";
                public string Name { get; } = "safe";
                public string Describe => Name + label;

                public void Run()
                {
                    using var reader = new StringReader("C4 E4");
                    using (var writer = new StringWriter())
                    {
                        writer.Write(reader.ReadToEnd());
                        notes.Add(writer.ToString());
                    }
                    Func<string> choose = () => notes[0];
                    string next = choose();
                    string Local() => next;
                    string result = Local();
                }
            }
            """;

        Audit(source).Should().BeEmpty();
    }

    [TestCase("""
        using System.IO;
        class Script
        {
            static readonly string Stolen = Path.GetTempFileName();
            public void Run() { }
        }
        """, "GetTempFileName")]
    [TestCase("""
        using System.IO;
        class Script
        {
            public string Current { get; } = Path.GetTempFileName();
        }
        """, "GetTempFileName")]
    [TestCase("""
        using System.IO;
        class Script
        {
            public string Current => Path.GetTempFileName();
        }
        """, "GetTempFileName")]
    [TestCase("""
        using System.IO;
        class Script
        {
            public string Current
            {
                get { return Path.GetTempFileName(); }
            }
        }
        """, "GetTempFileName")]
    [TestCase("""
        using System.IO;
        class Script
        {
            public Script(int n) { }
            public Script() : this(Path.GetTempFileName().Length) { }
        }
        """, "GetTempFileName")]
    [TestCase("""
        using System.IO;
        class Script
        {
            public void Run()
            {
                string Local() => Path.GetTempFileName();
            }
        }
        """, "GetTempFileName")]
    [TestCase("""
        using System;
        using System.IO;
        class Script
        {
            public void Run()
            {
                Func<string> callback = () => Path.GetTempFileName();
            }
        }
        """, "GetTempFileName")]
    [TestCase("""
        using System.IO;
        struct Script
        {
            public static Script operator +(Script left, Script right)
            {
                string tmp = Path.GetTempFileName();
                return left;
            }
        }
        """, "GetTempFileName")]
    [TestCase("""
        using System.IO;
        class Script
        {
            public static implicit operator string(Script script)
                => Path.GetTempFileName();
        }
        """, "GetTempFileName")]
    public void FindsHiddenForbiddenWorkInEveryExecutableDeclaration(
        string source, string expected)
    {
        Audit(source).Should().Contain(f => f.Reason.Contains(expected),
            "unreachable, deferred and initializer work is still authored code");
    }

    [Test]
    public void UsingDeclarationInspectsUserDefinedDisposeMethodBody()
    {
        const string source = """
            using System;
            using System.IO;
            class Resource : IDisposable
            {
                public void Dispose() { File.Delete("something"); }
            }
            class Script
            {
                void Run() { using var resource = new Resource(); }
            }
            """;
        Audit(source).Should().Contain(f => f.Reason.Contains("File"));
    }

    [Test]
    public void UsingStatementInspectsUserDefinedPatternDispose()
    {
        const string source = """
            using System.IO;
            ref struct Resource
            {
                public void Dispose() { File.Delete("something"); }
            }
            class Script
            {
                void Run() { using (var resource = new Resource()) { } }
            }
            """;
        Audit(source).Should().Contain(f => f.Reason.Contains("File"));
    }

    [Test]
    public void UsingSourceCannotSmuggleUnapprovedTypeThroughDisposal()
    {
        const string source = """
            using System;
            using System.IO;
            class Script
            {
                void Run()
                {
                    using var reader = new StringReader("safe");
                    using var stream = new MemoryStream();
                }
            }
            """;
        var findings = Audit(source);
        findings.Should().Contain(f => f.Reason.Contains("System.IO.MemoryStream"));
        findings.Should().NotContain(f => f.Reason.Contains("StringReader"));
    }

    [Test]
    public void GlobalizationCompoundWritesAndIncrementAreNotTreatedAsReads()
    {
        const string source = """
            using System.Globalization;
            class Script
            {
                void Run()
                {
                    var info = CultureInfo.CurrentCulture.DateTimeFormat;
                    info.ShortDatePattern += "!";
                }
            }
            """;
        Audit(source).Should().Contain(f =>
            f.Reason.Contains("Blocked runtime member") &&
            f.Reason.Contains("ShortDatePattern"));
    }

    [Test]
    public void DangerousAttributesExternAndFinalizersAreRejected()
    {
        const string source = """
            using System;
            using System.Runtime.InteropServices;
            class Script
            {
                [DllImport("libc")]
                public static extern int getpid();

                ~Script() { }
            }
            """;
        var findings = Audit(source);
        findings.Should().Contain(f => f.Reason.Contains("attributes"));
        findings.Should().Contain(f => f.Reason.Contains("Extern"));
        findings.Should().Contain(f => f.Reason.Contains("finalizers"));
    }

    [Test]
    public void AssemblySpoofingCannotHideForbiddenBaseTypes()
    {
        const string source = """
            using System.IO;
            class Bad : FileStream
            {
                public Bad() : base("secret", FileMode.Open) { }
            }
            """;
        Audit(source).Should().Contain(f =>
            f.Reason.Contains("System.IO.FileStream"));
    }

    [Test]
    public void AsyncDisposalAndUnreviewedRecordSynthesisFailClosed()
    {
        const string source = """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            record Resource(string Name);
            class Script
            {
                async Task Run()
                {
                    await using var stream = new MemoryStream();
                }
            }
            """;
        var findings = Audit(source);
        findings.Should().Contain(f => f.Reason.Contains("Record generated"));
        findings.Should().Contain(f => f.Reason.Contains("Asynchronous disposal"));
    }

    [Test]
    public void CompilerErrorsAndMultipleAuthoredTreesNeverSilentlyPass()
    {
        const string source = "class Script { void Run() { NoSuchCapability(); } }";
        Audit(source, expectValidCSharp: false).Should().Contain(f =>
            f.Reason.Contains("Unresolved C# source") ||
            f.Reason.Contains("Invalid or unresolved"));

        CSharpCompilation twoTrees = CSharpCompilation.Create(
            "TwoSources",
            [CSharpSyntaxTree.ParseText("class Script {}"),
                CSharpSyntaxTree.ParseText("class Other {}")],
            References, new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        Action audit = () => ScriptRuntimeOperationVerifier.AnalyzeSource(twoTrees);
        audit.Should().Throw<InvalidOperationException>();
    }
}

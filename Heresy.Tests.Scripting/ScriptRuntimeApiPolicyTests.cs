using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Heresy.Scripting.Compilation;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using NUnit.Framework;

namespace Heresy.Tests.Scripting;

[TestFixture]
public sealed class ScriptRuntimeApiPolicyTests
{
    private static readonly MetadataReference[] References =
        [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

    private static ScriptRuntimeApiPolicy Policy => ScriptRuntimeApiPolicy.Default;

    private static CSharpCompilation Compile(string source = "")
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        return CSharpCompilation.Create("HeresyUntrustedPolicyProbe",
            [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static INamedTypeSymbol Type(CSharpCompilation compilation, string name)
        => compilation.GetTypeByMetadataName(name)
            ?? throw new AssertionException("Runtime type not resolved: " + name);

    private static ISymbol Member(CSharpCompilation compilation,
        string type, string member)
        => Type(compilation, type).GetMembers(member).First();

    [Test]
    public void EmbeddedFinalOwnerPolicyLoadsWithoutSilentlyIgnoringEntries()
    {
        Policy.EntryCount.Should().BeGreaterThan(150);

        Action unknownSection = () =>
            ScriptRuntimeApiPolicy.Parse("Basic types:\nSystem.Boolean\nUnsupported block:\n");
        Action unknownEntry = () =>
            ScriptRuntimeApiPolicy.Parse("Basic types:\nSome.Unreviewed.Type\n");
        Action unknownBlock = () =>
            ScriptRuntimeApiPolicy.Parse("Selectively block:\nRunAnything\n");

        unknownSection.Should().Throw<FormatException>();
        unknownEntry.Should().Throw<FormatException>();
        unknownBlock.Should().Throw<FormatException>();
    }

    [Test]
    public void ApprovedPrimitiveCollectionsStringsAndTokensResolveThroughCoreLibrary()
    {
        CSharpCompilation c = Compile();
        string[] names =
        [
            "System.Boolean", "System.Int128", "System.String",
            "System.DateOnly", "System.TimeZoneInfo",
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.ObjectModel.Collection`1",
            "System.Text.StringBuilder", "System.IO.StringWriter",
            "System.Threading.CancellationToken",
            "System.Threading.CancellationTokenSource",
        ];
        foreach (string name in names)
            Policy.IsAllowedRuntimeType(Type(c, name), c)
                .Should().BeTrue("the owner approved " + name);

        INamedTypeSymbol dictionary =
            Type(c, "System.Collections.Generic.Dictionary`2").Construct(
                c.GetSpecialType(SpecialType.System_String),
                c.GetSpecialType(SpecialType.System_Int32));
        Policy.IsAllowedRuntimeType(dictionary, c).Should().BeTrue();
    }

    [Test]
    public void CoreLibraryEnumsExceptionsInterfacesAndCalendarSubclassesResolve()
    {
        CSharpCompilation c = Compile();
        Policy.IsAllowedRuntimeType(Type(c, "System.StringComparison"), c)
            .Should().BeTrue("all CoreLib enums are approved");
        Policy.IsAllowedRuntimeType(Type(c, "System.InvalidOperationException"), c)
            .Should().BeTrue("CoreLib Exception subclasses are approved");
        Policy.IsAllowedRuntimeType(Type(c, "System.IServiceProvider"), c)
            .Should().BeTrue("a type may be approved without exposing host instances");
        Policy.IsAllowedRuntimeType(Type(c, "System.Globalization.GregorianCalendar"), c)
            .Should().BeTrue("CoreLib Calendar subclasses are approved");
    }

    [Test]
    public void ArrayElementsAndGenericArgumentsCannotSmuggleDisallowedTypes()
    {
        CSharpCompilation c = Compile();
        INamedTypeSymbol list = Type(c, "System.Collections.Generic.List`1");
        ITypeSymbol allowed = c.GetSpecialType(SpecialType.System_String);
        ITypeSymbol forbidden = Type(c, "System.Type");

        Policy.IsAllowedRuntimeType(list.Construct(allowed), c)
            .Should().BeTrue();
        Policy.IsAllowedRuntimeType(list.Construct(forbidden), c)
            .Should().BeFalse();
        Policy.IsAllowedRuntimeType(c.CreateArrayTypeSymbol(allowed), c)
            .Should().BeTrue();
        Policy.IsAllowedRuntimeType(c.CreateArrayTypeSymbol(forbidden), c)
            .Should().BeFalse();
        Policy.IsAllowedRuntimeType(Type(c, "System.Environment"), c)
            .Should().BeFalse("being in CoreLib alone is insufficient");
    }

    [Test]
    public void MatchingNamespaceAndMetadataNameInUserAssemblyDoNotConferPermission()
    {
        const string source =
            "namespace System.Collections.Generic { public class List<T> {} }";
        CSharpCompilation c = Compile(source);
        SyntaxTree tree = c.SyntaxTrees.Single();
        ClassDeclarationSyntax declaration = tree.GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        INamedTypeSymbol spoof = c.GetSemanticModel(tree)
            .GetDeclaredSymbol(declaration)!;

        spoof.MetadataName.Should().Be("List`1");
        Policy.IsAllowedRuntimeType(spoof, c)
            .Should().BeFalse("only the actual core-library assembly is trusted");
    }

    [Test]
    public void BlockedApiMembersAreDeniedButAdjacentSafeMembersRemainAvailable()
    {
        CSharpCompilation c = Compile();

        string[] blocked =
        [
            "System.Object::GetType",
            "System.Delegate::CreateDelegate",
            "System.Delegate::DynamicInvoke",
            "System.Delegate::Method",
            "System.Delegate::Target",
            "System.Exception::TargetSite",
            "System.String::Intern",
            "System.TimeProvider::CreateTimer",
            "System.TimeZoneInfo::ClearCachedData",
            "System.Buffers.ArrayPool`1::Shared",
            "System.Globalization.CultureInfo::ClearCachedData",
            "System.Globalization.DateTimeFormatInfo::SetAllDateTimePatterns",
            "System.IO.Path::GetTempFileName",
            "System.Text.Encoding::RegisterProvider",
            "System.Threading.CancellationToken::Register",
            "System.Threading.CancellationToken::UnsafeRegister",
            "System.Threading.CancellationTokenSource::CancelAfter",
        ];

        foreach (string spec in blocked)
        {
            string[] parts = spec.Split("::", StringSplitOptions.None);
            ISymbol[] members = Type(c, parts[0]).GetMembers(parts[1]).ToArray();
            members.Should().NotBeEmpty("runtime API still defines " + spec);
            foreach (ISymbol member in members)
                Policy.IsBlockedMember(member, ScriptMemberUse.CallOrMethodGroup, c)
                    .Should().BeTrue(spec + " is forbidden for all overloads");
        }

        Policy.IsBlockedMember(Member(c, "System.String", "IsInterned"),
            ScriptMemberUse.CallOrMethodGroup, c)
            .Should().BeFalse("the owner intentionally permits IsInterned");
        Policy.IsBlockedMember(Member(c, "System.Threading.CancellationTokenSource",
                "CancelAsync"), ScriptMemberUse.CallOrMethodGroup, c)
            .Should().BeFalse("the approved block applies to CancelAfter only");
    }

    [Test]
    public void ReaderWriterPathConstructorsAndCultureMutationAreNarrowlyBlocked()
    {
        CSharpCompilation c = Compile();
        foreach (string name in new[] { "System.IO.StreamReader", "System.IO.StreamWriter" })
        {
            IMethodSymbol[] constructors = Type(c, name).InstanceConstructors.ToArray();
            constructors.Any(ctor => ctor.Parameters.Length > 0
                && ctor.Parameters[0].Type.SpecialType == SpecialType.System_String)
                .Should().BeTrue();
            foreach (IMethodSymbol ctor in constructors)
            {
                bool path = ctor.Parameters.Length > 0
                    && ctor.Parameters[0].Type.SpecialType == SpecialType.System_String;
                Policy.IsBlockedMember(ctor, ScriptMemberUse.Construct, c)
                    .Should().Be(path, "only path-taking constructors are forbidden");
            }
        }

        foreach (IMethodSymbol ctor in Type(c, "System.Globalization.CultureInfo")
            .InstanceConstructors)
            Policy.IsBlockedMember(ctor, ScriptMemberUse.Construct, c)
                .Should().BeTrue("CultureInfo instantiation is forbidden");

        ISymbol culture = Member(c, "System.Globalization.CultureInfo", "CurrentCulture");
        Policy.IsBlockedMember(culture, ScriptMemberUse.Read, c).Should().BeFalse();
        Policy.IsBlockedMember(culture, ScriptMemberUse.Write, c).Should().BeTrue();

        ISymbol format = Member(c, "System.Globalization.DateTimeFormatInfo",
            "ShortDatePattern");
        Policy.IsBlockedMember(format, ScriptMemberUse.Read, c).Should().BeFalse();
        Policy.IsBlockedMember(format, ScriptMemberUse.Write, c).Should().BeTrue();
    }

    [Test]
    public void BlockedMethodGroupResolvesToSameForbiddenRoslynSymbol()
    {
        const string source = """
            using System;
            using System.Threading;
            class Script
            {
                void Test(CancellationToken token)
                {
                    Func<Action, CancellationTokenRegistration> callback = token.Register;
                }
            }
            """;
        CSharpCompilation c = Compile(source);
        SyntaxTree tree = c.SyntaxTrees.Single();
        MemberAccessExpressionSyntax access = tree.GetRoot()
            .DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(node => node.Name.Identifier.ValueText == "Register");
        IMethodSymbol resolved = c.GetSemanticModel(tree)
            .GetSymbolInfo(access).Symbol as IMethodSymbol
            ?? throw new AssertionException("Method-group conversion did not resolve");

        Policy.IsBlockedMember(resolved, ScriptMemberUse.CallOrMethodGroup, c)
            .Should().BeTrue("method groups must receive the same restriction as calls");
    }

    [Test]
    public void UserDefinedFinalizersAreBlockedWithoutDisallowingEveryUserMethod()
    {
        CSharpCompilation c = Compile("class C { ~C() {} void Work() {} }");
        SyntaxTree tree = c.SyntaxTrees.Single();
        SemanticModel model = c.GetSemanticModel(tree);
        DestructorDeclarationSyntax destructor = tree.GetRoot()
            .DescendantNodes().OfType<DestructorDeclarationSyntax>().Single();
        MethodDeclarationSyntax work = tree.GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        Policy.IsBlockedMember(model.GetDeclaredSymbol(destructor)!,
            ScriptMemberUse.CallOrMethodGroup, c).Should().BeTrue();
        Policy.IsBlockedMember(model.GetDeclaredSymbol(work)!,
            ScriptMemberUse.CallOrMethodGroup, c).Should().BeFalse();
    }
}

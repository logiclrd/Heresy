using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Heresy.Scripting.Compilation;

/// <summary>
/// Describes which fixed, trusted wrapper contains user-supplied statements.
/// This is independent of the runtime API allow-list: before validating
/// executable operations we must prove where the authored source can occur.
/// </summary>
internal enum ScriptWrapperKind
{
    None,
    Pattern,
    IncrementalPattern,
    Sequence,
}

internal sealed record ScriptWrapperBoundaryViolation(
    Location? Location, string Reason);

/// <summary>
/// Checks the *structure* of Heresy's generated Pattern/Sequence wrappers.
/// Source text is embedded between the fixed #line markers. An author cannot
/// be permitted to close that method and declare a new member or type.
/// This validator does not authorize runtime operations: checkpoint, yield and
/// F6 metadata rewrites share the mapped region but remain trusted generated
/// code. Full capability checks must use canonical authored source.
/// </summary>
internal static class ScriptAuthoredWrapperBoundary
{
    private const string AuthoredPath = "heresy-script";
    private const string OpenMarker = "#line 1 \"heresy-script\"";
    private const string CloseMarker = "#line default";

    internal static IReadOnlyList<ScriptWrapperBoundaryViolation> Validate(
        SyntaxTree tree, ScriptWrapperKind kind)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (kind == ScriptWrapperKind.None)
            return [];

        List<ScriptWrapperBoundaryViolation> errors = [];
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        if (root.Members.Count != 1
            || root.Members[0] is not ClassDeclarationSyntax generatedClass)
        {
            errors.Add(new(null,
                "User source must remain inside the generated script class."));
            return errors;
        }

        string entryName = kind switch
        {
            ScriptWrapperKind.Pattern => "ExecuteScript",
            ScriptWrapperKind.IncrementalPattern => "EnumerateScript",
            ScriptWrapperKind.Sequence => "ExecuteEntry",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        string[] expectedMethodNames = kind == ScriptWrapperKind.IncrementalPattern
            ? ["ExecuteScript", "EnumerateScript"] : [entryName];

        ConstructorDeclarationSyntax[] constructors = generatedClass.Members
            .OfType<ConstructorDeclarationSyntax>().ToArray();
        MethodDeclarationSyntax[] methods = generatedClass.Members
            .OfType<MethodDeclarationSyntax>().ToArray();

        bool correctShape = constructors.Length == 1
            && constructors[0].Identifier.ValueText
                == generatedClass.Identifier.ValueText
            && generatedClass.Members.Count == 1 + expectedMethodNames.Length
            && methods.Length == expectedMethodNames.Length
            && expectedMethodNames.All(name =>
                methods.Count(m => m.Identifier.ValueText == name) == 1);
        // A hostile or malformed source fragment can declare the same
        // method twice. Never let malformed wrapper shape throw here:
        // report a restricted-script diagnostic instead.
        MethodDeclarationSyntax? entry = methods.FirstOrDefault(
            m => m.Identifier.ValueText == entryName);

        if (!correctShape || entry?.Body is null)
        {
            errors.Add(new(null,
                "User source changed the generated script wrapper's declarations."));
            return errors;
        }

        string[] markers = root.DescendantTrivia(descendIntoTrivia: true)
            .Where(t => t.IsKind(SyntaxKind.LineDirectiveTrivia))
            .Select(t => t.ToString().Trim()).ToArray();

        if (markers.Length != 2 || markers[0] != OpenMarker
            || markers[1] != CloseMarker)
        {
            errors.Add(new(null,
                "The generated script source markers were modified or escaped."));
            return errors;
        }

        // #line is a source-location projection, not an ownership boundary.
        // An author who closes the method can make subsequent tokens appear
        // authored while actually placing them into a class or another type.
        // Check every mapped token's syntax ancestry, not just calls.
        foreach (SyntaxToken token in root.DescendantTokens())
        {
            Location location = token.GetLocation();
            var mapped = location.GetMappedLineSpan();
            if (!mapped.IsValid
                || !string.Equals(mapped.Path, AuthoredPath,
                    StringComparison.Ordinal))
                continue;

            if (token.Parent is null
                || !token.Parent.AncestorsAndSelf()
                    .Any(n => ReferenceEquals(n, entry.Body)))
                errors.Add(new(location,
                    "Author code escaped the generated script method body."));
        }

        return errors;
    }
}

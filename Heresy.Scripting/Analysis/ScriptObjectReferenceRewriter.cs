using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Heresy.Core.Objects;

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Heresy.Scripting.Analysis;

/// <summary>
/// Rewrites only the numeric literal tokens of semantically resolved Heresy
/// _O(id) intrinsics. Roslyn supplies the source spans and symbol identity;
/// textual mentions in comments, strings and shadowed functions remain intact.
/// </summary>
public static class ScriptObjectReferenceRewriter
{
    public static string Rewrite(
        string source, IReadOnlyDictionary<ObjectId, ObjectId> ids)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ids);

        ScriptReferenceAnalysisSnapshot snapshot =
            ScriptReferenceAnalyzer.CreateSnapshot(source);
        if (!snapshot.Analysis.IsReliable)
            throw new InvalidOperationException(
                "Cannot deep-duplicate a script with unreliable object references: "
                + string.Join("; ", snapshot.Analysis.Diagnostics
                    .Where(item => item.Severity == ScriptDiagnosticSeverity.Error)
                    .Select(item => item.Message)));

        InvocationExpressionSyntax[] invocations = snapshot.SyntaxTree.GetRoot()
            .DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        List<TextChange> changes = [];
        foreach (ScriptObjectReference reference in snapshot.Analysis.References)
        {
            InvocationExpressionSyntax invocation = invocations.SingleOrDefault(
                node => node.SpanStart == reference.Span.Start
                    && node.Span.Length == reference.Span.Length)
                ?? throw new InvalidOperationException(
                    "Script reference span no longer identifies an invocation.");
            if (invocation.ArgumentList.Arguments[0].Expression
                is not LiteralExpressionSyntax literal)
                throw new InvalidOperationException(
                    "Script reference no longer has a numeric literal.");

            if (ids.TryGetValue(reference.Id, out ObjectId replacement))
            {
                if (replacement.IsNone)
                    throw new InvalidOperationException(
                        "A duplicated reference cannot target ObjectId.None.");
                changes.Add(new TextChange(literal.Span,
                    replacement.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        // Apply all original-document spans in a single Roslyn SourceText edit.
        // Re-analyze to prove that every intrinsic is still found in order,
        // with exactly the intended target ID. No unrecognized _O call may
        // suddenly become an intrinsic through the rewrite.
        string rewritten = snapshot.Text.WithChanges(changes).ToString();
        ScriptReferenceAnalysis check = ScriptReferenceAnalyzer.Analyze(rewritten);
        ObjectId[] expected = snapshot.Analysis.References
            .Select(reference => ids.TryGetValue(reference.Id,
                out ObjectId replacement) ? replacement : reference.Id)
            .ToArray();
        if (!check.IsReliable
            || !check.References.Select(item => item.Id).SequenceEqual(expected))
            throw new InvalidOperationException(
                "Rewritten script failed semantic reference verification.");
        return rewritten;
    }
}

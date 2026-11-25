using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Heresy.Scripting.Analysis;

public enum ScriptPatternOrder
{
	Strict,
	Relaxed,
}

/// <summary>
/// Lexical result only: recognizing a directive does not authorize relaxed
/// execution. The Pattern compiler must explicitly consume this result.
/// </summary>
public sealed record ScriptPatternOrderAnalysis(
	ScriptPatternOrder Order,
	string CompilerSource,
	IReadOnlyList<ScriptSourceSpan> Directives,
	IReadOnlyList<ScriptAnalysisDiagnostic> Diagnostics);

public static class ScriptPatternOrderDirective
{
	private static readonly CSharpParseOptions ParseOptions =
		new(LanguageVersion.Preview, kind: SourceCodeKind.Script);

	private static readonly Regex Prefix =
		new(@"^#[ \t]*order(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant);

	private static readonly Regex Valid =
		new(@"^#[ \t]*order[ \t]+(strict|relaxed)[ \t]*(?://.*)?$",
			RegexOptions.CultureInvariant);

	public static ScriptPatternOrderAnalysis Analyze(string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		return Analyze(CSharpSyntaxTree.ParseText(source, ParseOptions));
	}

	internal static ScriptPatternOrderAnalysis Analyze(SyntaxTree tree)
	{
		SourceText source = tree.GetText();
		SyntaxNode root = tree.GetRoot();
		// Do not inspect raw lines independently: only genuine Roslyn
		// directive trivia may select a mode, never strings or comments.
		SyntaxToken firstToken = root.DescendantTokens()
			.FirstOrDefault(token => token.Span.Length != 0);
		int firstCode = firstToken.RawKind == 0 ? source.Length : firstToken.SpanStart;
		List<ScriptSourceSpan> directives = [];
		List<ScriptAnalysisDiagnostic> diagnostics = [];
		ScriptPatternOrder order = ScriptPatternOrder.Strict;
		char[]? masked = null;

		foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
		{
			if (trivia.GetStructure() is not BadDirectiveTriviaSyntax directive
				|| !directive.IsActive)
				continue;

			TextLine line = source.Lines.GetLineFromPosition(trivia.SpanStart);
			int start = trivia.SpanStart;
			string text = source.ToString(TextSpan.FromBounds(start, line.End));
			if (!Prefix.IsMatch(text))
				continue;

			ScriptSourceSpan span = new(start, text.Length);
			directives.Add(span);
			Match match = Valid.Match(text);
			string? error = !match.Success
				? "Expected #order strict or #order relaxed."
				: directives.Count != 1
					? "Only one #order directive is permitted."
					: start >= firstCode
						? "#order must precede executable script source."
						: null;
			if (error is not null)
			{
				diagnostics.Add(new ScriptAnalysisDiagnostic("HRS1002",
					ScriptDiagnosticSeverity.Error, error, span));
				continue;
			}

			order = match.Groups[1].Value == "relaxed"
				? ScriptPatternOrder.Relaxed : ScriptPatternOrder.Strict;
			masked ??= source.ToString().ToCharArray();
			Array.Fill(masked, ' ', span.Start, span.Length);
		}

		return new(order, masked is null ? source.ToString() : new string(masked),
			directives.ToArray(), diagnostics.ToArray());
	}
}

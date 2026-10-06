using System;
using System.Collections.Generic;
using System.Linq;

using Heresy.Scripting.Analysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Heresy.UserInterface.Documents;

public enum ScriptSyntaxHighlightKind
{
	Keyword,
	String,
	Number,
	Comment,
	Preprocessor,
	ObjectReference,
}

public sealed record ScriptSyntaxHighlightSpan(
	ScriptSourceSpan Span,
	ScriptSyntaxHighlightKind Kind);

public static class ScriptSyntaxHighlightCatalog
{
	public static IReadOnlyList<ScriptSyntaxHighlightSpan> Create(
		ScriptReferenceAnalysisSnapshot snapshot,
		IReadOnlyList<ScriptReferenceVisualToken> references)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(references);

		SyntaxNode root =
			snapshot.SyntaxTree.GetRoot();
		List<ScriptSyntaxHighlightSpan> spans = [];
		List<ScriptSourceSpan> protectedSpans = [];

		foreach (SyntaxTrivia trivia
			in root.DescendantTrivia(descendIntoTrivia: true))
		{
			ScriptSyntaxHighlightKind? kind =
				ClassifyTrivia(trivia);
			if (kind is null || trivia.Span.Length == 0)
				continue;

			ScriptSourceSpan span =
				new(
					trivia.Span.Start,
					trivia.Span.Length);
			spans.Add(new ScriptSyntaxHighlightSpan(span, kind.Value));
			protectedSpans.Add(span);
		}

		foreach (ScriptReferenceVisualToken reference in references)
		{
			spans.Add(
				new ScriptSyntaxHighlightSpan(
					reference.SourceSpan,
					ScriptSyntaxHighlightKind.ObjectReference));
			protectedSpans.Add(reference.SourceSpan);
		}

		foreach (LiteralExpressionSyntax literal
			in root.DescendantNodes()
				.OfType<LiteralExpressionSyntax>())
		{
			ScriptSyntaxHighlightKind? kind =
				ClassifyLiteral(literal);
			if (kind is null)
				continue;

			AddIfUnprotected(
				spans,
				protectedSpans,
				new ScriptSourceSpan(
					literal.Span.Start,
					literal.Span.Length),
				kind.Value);
		}

		foreach (SyntaxToken token
			in root.DescendantTokens(descendIntoTrivia: true))
		{
			if (token.Span.Length == 0)
				continue;

			ScriptSyntaxHighlightKind? kind =
				ClassifyToken(token);
			if (kind is null)
				continue;

			AddIfUnprotected(
				spans,
				protectedSpans,
				new ScriptSourceSpan(
					token.Span.Start,
					token.Span.Length),
				kind.Value);
		}

		return spans
			.OrderBy(span => span.Span.Start)
			.ThenByDescending(span => span.Span.Length)
			.ToArray();
	}

	private static ScriptSyntaxHighlightKind? ClassifyTrivia(
		SyntaxTrivia trivia)
	{
		if (trivia.IsDirective
			|| trivia.IsKind(SyntaxKind.DisabledTextTrivia))
		{
			return ScriptSyntaxHighlightKind.Preprocessor;
		}

		if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
			|| trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
			|| trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
			|| trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
		{
			return ScriptSyntaxHighlightKind.Comment;
		}

		return null;
	}

	private static ScriptSyntaxHighlightKind? ClassifyLiteral(
		LiteralExpressionSyntax literal)
	{
		if (literal.IsKind(SyntaxKind.NumericLiteralExpression))
			return ScriptSyntaxHighlightKind.Number;

		if (literal.IsKind(SyntaxKind.StringLiteralExpression)
			|| literal.IsKind(SyntaxKind.CharacterLiteralExpression))
		{
			return ScriptSyntaxHighlightKind.String;
		}

		return null;
	}

	private static ScriptSyntaxHighlightKind? ClassifyToken(
		SyntaxToken token)
	{
		SyntaxKind kind = token.Kind();
		SyntaxKind contextualKind = token.ContextualKind();

		if (SyntaxFacts.IsKeywordKind(kind)
			|| (contextualKind != SyntaxKind.IdentifierToken
				&& SyntaxFacts.IsKeywordKind(contextualKind)))
		{
			return ScriptSyntaxHighlightKind.Keyword;
		}

		if (kind == SyntaxKind.InterpolatedStringTextToken)
			return ScriptSyntaxHighlightKind.String;

		return null;
	}

	private static void AddIfUnprotected(
		ICollection<ScriptSyntaxHighlightSpan> spans,
		IReadOnlyList<ScriptSourceSpan> protectedSpans,
		ScriptSourceSpan candidate,
		ScriptSyntaxHighlightKind kind)
	{
		if (candidate.Length == 0)
			return;

		foreach (ScriptSourceSpan protectedSpan in protectedSpans)
		{
			if (Overlaps(candidate, protectedSpan))
				return;
		}

		spans.Add(
			new ScriptSyntaxHighlightSpan(
				candidate,
				kind));
	}

	private static bool Overlaps(
		ScriptSourceSpan left,
		ScriptSourceSpan right)
		=> left.Start < right.End
			&& right.Start < left.End;
}

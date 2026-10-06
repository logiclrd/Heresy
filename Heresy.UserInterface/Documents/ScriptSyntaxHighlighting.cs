using System;
using System.Collections.Generic;

using Heresy.Scripting.Analysis;

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
		return [];
	}
}

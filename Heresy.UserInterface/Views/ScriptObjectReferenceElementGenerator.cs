using System;
using System.Collections.Generic;
using System.Linq;

using AvaloniaEdit.Rendering;

using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Replaces semantic _O(id) expressions with one visual element while leaving
/// the AvaloniaEdit document text untouched.
/// </summary>
internal sealed class ScriptObjectReferenceElementGenerator
	: VisualLineElementGenerator
{
	private ScriptReferenceVisualToken[] _tokens = [];

	public void SetTokens(
		IReadOnlyList<ScriptReferenceVisualToken> tokens)
	{
		ArgumentNullException.ThrowIfNull(tokens);

		_tokens =
			tokens
				.OrderBy(token => token.SourceSpan.Start)
				.ToArray();
	}

	public override int GetFirstInterestedOffset(
		int startOffset)
	{
		int lineEnd =
			CurrentContext.VisualLine
				.LastDocumentLine.EndOffset;

		foreach (ScriptReferenceVisualToken token in _tokens)
		{
			if (token.SourceSpan.Start < startOffset)
				continue;
			if (token.SourceSpan.Start >= lineEnd)
				return -1;

			return token.SourceSpan.Start;
		}

		return -1;
	}

	public override VisualLineElement ConstructElement(
		int offset)
	{
		foreach (ScriptReferenceVisualToken token in _tokens)
		{
			if (token.SourceSpan.Start != offset)
				continue;

			int lineEnd =
				CurrentContext.VisualLine
					.LastDocumentLine.EndOffset;
			if (token.SourceSpan.End > lineEnd)
			{
				throw new InvalidOperationException(
					"Object-reference visual tokens must remain on one document line.");
			}

			return new ScriptObjectReferenceVisualLineElement(
				token);
		}

		throw new InvalidOperationException(
			"No semantic object reference starts at the requested offset.");
	}
}

/// <summary>
/// A single visual column that consumes the full canonical _O(id) source span.
/// Keeping the raw document length here lets AvaloniaEdit's own caret,
/// selection, clipboard, and undo machinery continue to use source offsets.
/// </summary>
internal sealed class ScriptObjectReferenceVisualLineElement
	: FormattedTextElement
{
	public ScriptObjectReferenceVisualLineElement(
		ScriptReferenceVisualToken token)
		: base(
			token?.Text
				?? throw new ArgumentNullException(nameof(token)),
			token.SourceSpan.Length)
	{
		Token = token;
	}

	public ScriptReferenceVisualToken Token { get; }
}

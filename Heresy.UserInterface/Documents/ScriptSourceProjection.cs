using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Heresy.Core.Objects;
using Heresy.Scripting.Analysis;

namespace Heresy.UserInterface.Documents;

public readonly record struct ScriptProjectionSelection(
	int Start,
	int End);

public sealed record ScriptProjectionEdit(
	string Source,
	int SourceCaret);

public sealed record ScriptProjectedReferenceToken(
	ObjectId Id,
	ScriptSourceSpan SourceSpan,
	ScriptSourceSpan DisplaySpan,
	string DisplayName,
	SongObjectKind Kind,
	ScriptObjectReferenceResolution Resolution);

/// <summary>
/// Editable display projection for script source. Object references remain
/// canonical _O(id) in Source while Text replaces semantic references with
/// atomic named tokens.
/// </summary>
public sealed class ScriptSourceProjection
{
	private const char TokenOpen = '⟦';
	private const char TokenClose = '⟧';

	private ScriptSourceProjection(
		string source,
		string text,
		IReadOnlyList<ScriptProjectedReferenceToken> tokens)
	{
		Source = source;
		Text = text;
		Tokens = tokens;
	}

	public string Source { get; }
	public string Text { get; }
	public IReadOnlyList<ScriptProjectedReferenceToken> Tokens { get; }

	public static ScriptSourceProjection Create(
		string source,
		IReadOnlyList<ProjectedScriptObjectReference> references)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(references);

		ProjectedScriptObjectReference[] ordered =
			references
				.OrderBy(reference => reference.Span.Start)
				.ToArray();

		StringBuilder text = new(source.Length);
		List<ScriptProjectedReferenceToken> tokens = [];
		int sourcePosition = 0;

		foreach (ProjectedScriptObjectReference reference in ordered)
		{
			ValidateReferenceSpan(
				source,
				reference,
				sourcePosition);

			text.Append(
				source,
				sourcePosition,
				reference.Span.Start - sourcePosition);

			string displayName =
				SanitizeDisplayName(reference.DisplayName);
			string tokenText =
				$"{TokenOpen}{displayName}{TokenClose}";
			ScriptSourceSpan displaySpan =
				new(
					text.Length,
					tokenText.Length);

			text.Append(tokenText);
			tokens.Add(
				new ScriptProjectedReferenceToken(
					reference.Id,
					reference.Span,
					displaySpan,
					displayName,
					reference.Kind,
					reference.Resolution));

			sourcePosition = reference.Span.End;
		}

		text.Append(
			source,
			sourcePosition,
			source.Length - sourcePosition);

		return new ScriptSourceProjection(
			source,
			text.ToString(),
			tokens);
	}

	public ScriptProjectionSelection NormalizeSelection(
		int start,
		int end)
	{
		int first =
			Math.Clamp(
				Math.Min(start, end),
				0,
				Text.Length);
		int last =
			Math.Clamp(
				Math.Max(start, end),
				first,
				Text.Length);

		if (first == last)
		{
			ScriptProjectedReferenceToken? containing =
				FindTokenContainingInterior(first);
			if (containing is null)
				return new(first, first);

			int distanceFromStart =
				first - containing.DisplaySpan.Start;
			int distanceFromEnd =
				containing.DisplaySpan.End - first;
			int snapped =
				distanceFromStart <= distanceFromEnd
					? containing.DisplaySpan.Start
					: containing.DisplaySpan.End;
			return new(snapped, snapped);
		}

		return ExpandSelectionOverTokens(
			first,
			last);
	}

	public int MoveCaret(
		int displayPosition,
		int direction)
	{
		int position =
			Math.Clamp(
				displayPosition,
				0,
				Text.Length);
		int sign = Math.Sign(direction);
		if (sign == 0)
			return NormalizeSelection(position, position).Start;

		foreach (ScriptProjectedReferenceToken token in Tokens)
		{
			if (sign > 0
				&& position >= token.DisplaySpan.Start
				&& position < token.DisplaySpan.End)
			{
				return token.DisplaySpan.End;
			}

			if (sign < 0
				&& position > token.DisplaySpan.Start
				&& position <= token.DisplaySpan.End)
			{
				return token.DisplaySpan.Start;
			}
		}

		return Math.Clamp(
			position + sign,
			0,
			Text.Length);
	}

	public ScriptProjectionEdit Replace(
		int selectionStart,
		int selectionEnd,
		string replacement)
	{
		ArgumentNullException.ThrowIfNull(replacement);

		ScriptProjectionSelection selection =
			ExpandSelectionForEdit(
				selectionStart,
				selectionEnd);
		int sourceStart =
			SourcePositionFromDisplayBoundary(
				selection.Start);
		int sourceEnd =
			SourcePositionFromDisplayBoundary(
				selection.End);

		return new ScriptProjectionEdit(
			Source[..sourceStart]
				+ replacement
				+ Source[sourceEnd..],
			sourceStart + replacement.Length);
	}

	public ScriptProjectionEdit DeleteBackward(
		int selectionStart,
		int selectionEnd)
	{
		int first =
			Math.Clamp(
				Math.Min(selectionStart, selectionEnd),
				0,
				Text.Length);
		int last =
			Math.Clamp(
				Math.Max(selectionStart, selectionEnd),
				first,
				Text.Length);

		if (first != last)
			return Replace(first, last, string.Empty);

		if (first == 0)
		{
			return new(
				Source,
				0);
		}

		foreach (ScriptProjectedReferenceToken token in Tokens)
		{
			if (first > token.DisplaySpan.Start
				&& first <= token.DisplaySpan.End)
			{
				return Replace(
					token.DisplaySpan.Start,
					token.DisplaySpan.End,
					string.Empty);
			}
		}

		return Replace(
			first - 1,
			first,
			string.Empty);
	}

	public ScriptProjectionEdit DeleteForward(
		int selectionStart,
		int selectionEnd)
	{
		int first =
			Math.Clamp(
				Math.Min(selectionStart, selectionEnd),
				0,
				Text.Length);
		int last =
			Math.Clamp(
				Math.Max(selectionStart, selectionEnd),
				first,
				Text.Length);

		if (first != last)
			return Replace(first, last, string.Empty);

		if (first == Text.Length)
		{
			return new(
				Source,
				Source.Length);
		}

		foreach (ScriptProjectedReferenceToken token in Tokens)
		{
			if (first >= token.DisplaySpan.Start
				&& first < token.DisplaySpan.End)
			{
				return Replace(
					token.DisplaySpan.Start,
					token.DisplaySpan.End,
					string.Empty);
			}
		}

		return Replace(
			first,
			first + 1,
			string.Empty);
	}

	public string GetSourceText(
		int selectionStart,
		int selectionEnd)
	{
		ScriptProjectionSelection selection =
			NormalizeSelection(
				selectionStart,
				selectionEnd);
		if (selection.Start == selection.End)
			return string.Empty;

		int sourceStart =
			SourcePositionFromDisplayBoundary(
				selection.Start);
		int sourceEnd =
			SourcePositionFromDisplayBoundary(
				selection.End);
		return Source[sourceStart..sourceEnd];
	}

	public ScriptProjectionEdit ReconcileTextChange(
		string changedText)
	{
		ArgumentNullException.ThrowIfNull(changedText);

		if (changedText == Text)
		{
			return new(
				Source,
				SourcePositionFromDisplay(Text.Length));
		}

		int prefix = 0;
		int commonLength =
			Math.Min(
				Text.Length,
				changedText.Length);
		while (prefix < commonLength
			&& Text[prefix] == changedText[prefix])
		{
			prefix++;
		}

		int oldEnd = Text.Length;
		int newEnd = changedText.Length;
		while (oldEnd > prefix
			&& newEnd > prefix
			&& Text[oldEnd - 1] == changedText[newEnd - 1])
		{
			oldEnd--;
			newEnd--;
		}

		return Replace(
			prefix,
			oldEnd,
			changedText[prefix..newEnd]);
	}

	public int SourcePositionFromDisplay(int displayPosition)
	{
		ScriptProjectionSelection normalized =
			NormalizeSelection(
				displayPosition,
				displayPosition);
		return SourcePositionFromDisplayBoundary(
			normalized.Start);
	}

	public int DisplayPositionFromSource(int sourcePosition)
	{
		int position =
			Math.Clamp(
				sourcePosition,
				0,
				Source.Length);
		int sourceCursor = 0;
		int displayCursor = 0;

		foreach (ScriptProjectedReferenceToken token in Tokens)
		{
			if (position <= token.SourceSpan.Start)
			{
				return displayCursor
					+ position
					- sourceCursor;
			}

			int ordinaryLength =
				token.SourceSpan.Start - sourceCursor;
			displayCursor += ordinaryLength;

			if (position < token.SourceSpan.End)
			{
				int distanceFromStart =
					position - token.SourceSpan.Start;
				int distanceFromEnd =
					token.SourceSpan.End - position;
				return distanceFromStart <= distanceFromEnd
					? token.DisplaySpan.Start
					: token.DisplaySpan.End;
			}

			displayCursor = token.DisplaySpan.End;
			sourceCursor = token.SourceSpan.End;
		}

		return displayCursor
			+ position
			- sourceCursor;
	}

	private ScriptProjectionSelection ExpandSelectionForEdit(
		int start,
		int end)
	{
		int first =
			Math.Clamp(
				Math.Min(start, end),
				0,
				Text.Length);
		int last =
			Math.Clamp(
				Math.Max(start, end),
				first,
				Text.Length);

		if (first == last)
		{
			ScriptProjectedReferenceToken? containing =
				FindTokenContainingInterior(first);
			if (containing is not null)
			{
				return new(
					containing.DisplaySpan.Start,
					containing.DisplaySpan.End);
			}

			return new(first, last);
		}

		return ExpandSelectionOverTokens(
			first,
			last);
	}

	private ScriptProjectionSelection ExpandSelectionOverTokens(
		int first,
		int last)
	{
		int expandedStart = first;
		int expandedEnd = last;

		foreach (ScriptProjectedReferenceToken token in Tokens)
		{
			if (expandedStart < token.DisplaySpan.End
				&& expandedEnd > token.DisplaySpan.Start)
			{
				expandedStart =
					Math.Min(
						expandedStart,
						token.DisplaySpan.Start);
				expandedEnd =
					Math.Max(
						expandedEnd,
						token.DisplaySpan.End);
			}
		}

		return new(
			expandedStart,
			expandedEnd);
	}

	private ScriptProjectedReferenceToken? FindTokenContainingInterior(
		int displayPosition)
	{
		foreach (ScriptProjectedReferenceToken token in Tokens)
		{
			if (displayPosition > token.DisplaySpan.Start
				&& displayPosition < token.DisplaySpan.End)
			{
				return token;
			}
		}

		return null;
	}

	private int SourcePositionFromDisplayBoundary(
		int displayPosition)
	{
		int position =
			Math.Clamp(
				displayPosition,
				0,
				Text.Length);
		int sourceCursor = 0;
		int displayCursor = 0;

		foreach (ScriptProjectedReferenceToken token in Tokens)
		{
			if (position <= token.DisplaySpan.Start)
			{
				return sourceCursor
					+ position
					- displayCursor;
			}

			int ordinaryLength =
				token.DisplaySpan.Start - displayCursor;
			sourceCursor += ordinaryLength;

			if (position < token.DisplaySpan.End)
			{
				throw new InvalidOperationException(
					"Display positions inside object-reference tokens are not editable boundaries.");
			}

			sourceCursor = token.SourceSpan.End;
			displayCursor = token.DisplaySpan.End;
		}

		return sourceCursor
			+ position
			- displayCursor;
	}

	private static void ValidateReferenceSpan(
		string source,
		ProjectedScriptObjectReference reference,
		int previousEnd)
	{
		if (reference.Span.Start < previousEnd
			|| reference.Span.Start < 0
			|| reference.Span.Length < 0
			|| reference.Span.End > source.Length)
		{
			throw new ArgumentException(
				"Projected object references must have ordered, non-overlapping source spans.",
				nameof(reference));
		}
	}

	private static string SanitizeDisplayName(
		string displayName)
		=> displayName
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Replace('\t', ' ')
			.Replace(TokenOpen, '‹')
			.Replace(TokenClose, '›');
}

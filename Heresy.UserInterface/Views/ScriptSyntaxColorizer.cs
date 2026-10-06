using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Media;

using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Applies Roslyn-derived syntax spans after AvaloniaEdit element generation.
/// Object-reference spans therefore color the generated one-column name element
/// while ordinary source spans color normal VisualLineText elements.
/// </summary>
internal sealed class ScriptSyntaxColorizer
	: DocumentColorizingTransformer
{
	private static readonly IBrush KeywordBrush =
		new SolidColorBrush(Color.FromRgb(0x00, 0x5C, 0xC5));
	private static readonly IBrush StringBrush =
		new SolidColorBrush(Color.FromRgb(0xA3, 0x15, 0x15));
	private static readonly IBrush NumberBrush =
		new SolidColorBrush(Color.FromRgb(0x09, 0x86, 0x58));
	private static readonly IBrush CommentBrush =
		new SolidColorBrush(Color.FromRgb(0x4F, 0x7B, 0x4F));
	private static readonly IBrush PreprocessorBrush =
		new SolidColorBrush(Color.FromRgb(0xAF, 0x00, 0xDB));
	private static readonly IBrush ObjectReferenceBrush =
		new SolidColorBrush(Color.FromRgb(0x26, 0x7F, 0x99));

	private ScriptSyntaxHighlightSpan[] _spans = [];

	public void SetSpans(
		IReadOnlyList<ScriptSyntaxHighlightSpan> spans)
	{
		ArgumentNullException.ThrowIfNull(spans);

		_spans =
			spans
				.OrderBy(span => span.Span.Start)
				.ToArray();
	}

	protected override void ColorizeLine(
		DocumentLine line)
	{
		int lineStart = line.Offset;
		int lineEnd = line.EndOffset;

		foreach (ScriptSyntaxHighlightSpan span in _spans)
		{
			if (span.Span.End <= lineStart)
				continue;
			if (span.Span.Start >= lineEnd)
				break;

			int start =
				Math.Max(
					lineStart,
					span.Span.Start);
			int end =
				Math.Min(
					lineEnd,
					span.Span.End);
			if (start >= end)
				continue;

			IBrush brush =
				GetBrush(span.Kind);
			ChangeLinePart(
				start,
				end,
				element =>
					element.TextRunProperties
						.SetForegroundBrush(brush));
		}
	}

	private static IBrush GetBrush(
		ScriptSyntaxHighlightKind kind)
		=> kind switch
		{
			ScriptSyntaxHighlightKind.Keyword =>
				KeywordBrush,
			ScriptSyntaxHighlightKind.String =>
				StringBrush,
			ScriptSyntaxHighlightKind.Number =>
				NumberBrush,
			ScriptSyntaxHighlightKind.Comment =>
				CommentBrush,
			ScriptSyntaxHighlightKind.Preprocessor =>
				PreprocessorBrush,
			ScriptSyntaxHighlightKind.ObjectReference =>
				ObjectReferenceBrush,
			_ => throw new ArgumentOutOfRangeException(
				nameof(kind),
				kind,
				"Unknown script syntax highlight kind."),
		};
}

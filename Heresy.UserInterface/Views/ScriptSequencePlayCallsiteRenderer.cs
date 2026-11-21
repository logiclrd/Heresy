using System;

using Avalonia;
using Avalonia.Media;

using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

using Heresy.Core.Sequences;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Read-only, editor-F6-only background for the source Play identifier
/// corresponding to the Sequence order currently sounding. Canonical
/// script text, selection, caret and syntax coloring are untouched.
/// </summary>
internal sealed class ScriptSequencePlayCallsiteRenderer : IBackgroundRenderer
{
    private static readonly IBrush ActiveBrush =
        new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x80, 0x00));

    private SequenceCallsite? _span;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpan(SequenceCallsite? span, int documentLength)
    {
        _span = span is { Start: >= 0, Length: > 0 } valid
            && valid.Start <= documentLength
            && valid.Length <= documentLength - valid.Start
            ? valid : null;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_span is not SequenceCallsite span || !textView.VisualLinesValid
            || textView.VisualLines.Count == 0)
            return;

        int start = textView.VisualLines[0].FirstDocumentLine.Offset;
        int end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        if (span.Start + span.Length < start || span.Start > end)
            return;

        TextSegment segment = new()
        {
            StartOffset = span.Start,
            Length = span.Length,
        };
        foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(
            textView, segment))
            drawingContext.DrawRectangle(ActiveBrush, null, rect);
    }
}

using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Media;

using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

using Heresy.Core.Sequences;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Read-only, editor-F6-only source overlay. Sequence playback highlights
/// the active Play invocation; Pattern playback can highlight several
/// scheduled calls in the same musical row. No editor selections or text
/// mutations are used.
/// </summary>
internal sealed class ScriptSequencePlayCallsiteRenderer : IBackgroundRenderer
{
    private static readonly IBrush ActiveBrush =
        new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x80, 0x00));

    // Preserve the single-span view used by the Sequence playback tests.
    private SequenceCallsite? _span;
    private IReadOnlyList<SequenceCallsite> _spans = [];

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpan(SequenceCallsite? span, int documentLength)
        => SetSpans(span is SequenceCallsite valid
            ? [valid] : [], documentLength);

    public void SetSpans(IReadOnlyList<SequenceCallsite>? spans, int documentLength)
    {
        List<SequenceCallsite> accepted = [];
        if (spans is not null)
            foreach (SequenceCallsite span in spans)
                if (span.Start >= 0 && span.Length > 0
                    && span.Start <= documentLength
                    && span.Length <= documentLength - span.Start
                    && !accepted.Contains(span))
                    accepted.Add(span);
        _spans = accepted.ToArray();
        _span = accepted.Count != 0 ? accepted[^1] : null;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_spans.Count == 0 || !textView.VisualLinesValid
            || textView.VisualLines.Count == 0)
            return;

        int start = textView.VisualLines[0].FirstDocumentLine.Offset;
        int end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (SequenceCallsite span in _spans)
        {
            if (span.Start + span.Length < start || span.Start > end)
                continue;

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
}

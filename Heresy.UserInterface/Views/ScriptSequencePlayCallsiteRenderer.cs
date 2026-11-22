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
    private static readonly IBrush SustainedNoteBrush =
        new SolidColorBrush(Color.FromArgb(0x65, 0x15, 0x70, 0xB5));

    // Preserve the single-span view used by the Sequence playback tests.
    private SequenceCallsite? _span;
    private IReadOnlyList<SequenceCallsite> _spans = [];
    private IReadOnlyList<SequenceCallsite> _activeNoteSpans = [];

    public KnownLayer Layer => KnownLayer.Background;

    public void SetSpan(SequenceCallsite? span, int documentLength)
    {
        SetSpans(span is SequenceCallsite valid
            ? [valid] : [], documentLength);
    }

    /// <summary>Returns true only when the displayed green spans change.
    /// The common per-position case reuses the prior immutable array
    /// without list/array allocation or a redundant editor redraw.</summary>
    public bool SetSpans(IReadOnlyList<SequenceCallsite>? spans,
        int documentLength)
    {
        if (MatchesDisplayed(spans, _spans, documentLength))
            return false;
        SequenceCallsite[] accepted = Normalize(spans, documentLength);
        if (MatchesDisplayed(accepted, _spans, documentLength))
            return false;
        _spans = accepted;
        _span = accepted.Length != 0 ? accepted[^1] : null;
        return true;
    }

    /// <summary>Independent blue note-lifetime overlay: a changed blue
    /// set must redraw even if green activations are unchanged.</summary>
    public bool SetActiveNoteSpans(IReadOnlyList<SequenceCallsite>? spans,
        int documentLength)
    {
        if (MatchesDisplayed(spans, _activeNoteSpans, documentLength))
            return false;
        SequenceCallsite[] accepted = Normalize(spans, documentLength);
        if (MatchesDisplayed(accepted, _activeNoteSpans, documentLength))
            return false;
        _activeNoteSpans = accepted;
        return true;
    }

    private static bool MatchesDisplayed(IReadOnlyList<SequenceCallsite>? incoming,
        IReadOnlyList<SequenceCallsite> displayed, int documentLength)
    {
        if (incoming is null || incoming.Count == 0)
            return displayed.Count == 0;
        if (incoming.Count != displayed.Count)
            return false;
        for (int i = 0; i < incoming.Count; i++)
        {
            SequenceCallsite span = incoming[i];
            if (span != displayed[i] || span.Start < 0
                || span.Length <= 0 || span.Start > documentLength
                || span.Length > documentLength - span.Start)
                return false;
        }
        return true;
    }

    private static SequenceCallsite[] Normalize(
        IReadOnlyList<SequenceCallsite>? spans, int documentLength)
    {
        if (spans is null || spans.Count == 0)
            return [];
        List<SequenceCallsite> accepted = [];
        foreach (SequenceCallsite span in spans)
            if (span.Start >= 0 && span.Length > 0
                && span.Start <= documentLength
                && span.Length <= documentLength - span.Start
                && !accepted.Contains(span))
                accepted.Add(span);
        return accepted.ToArray();
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if ((_spans.Count == 0 && _activeNoteSpans.Count == 0)
            || !textView.VisualLinesValid
            || textView.VisualLines.Count == 0)
            return;

        int start = textView.VisualLines[0].FirstDocumentLine.Offset;
        int end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        // Voice lifetime is blue; scheduled activation remains green and
        // is drawn last to take priority when both cover the same source.
        DrawSpans(_activeNoteSpans, SustainedNoteBrush);
        DrawSpans(_spans, ActiveBrush);

        void DrawSpans(IReadOnlyList<SequenceCallsite> spans, IBrush brush)
        {
            foreach (SequenceCallsite span in spans)
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
                    drawingContext.DrawRectangle(brush, null, rect);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

using Heresy.Scripting.Analysis;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Draws compiler diagnostics as wavy underlines using raw document spans.
/// BackgroundGeometryBuilder performs the raw-to-visual mapping, including
/// spans consumed by generated object-reference elements.
/// </summary>
internal sealed class ScriptDiagnosticRenderer
	: IBackgroundRenderer
{
	private const double WaveStep = 2.0;
	private const double WaveAmplitude = 1.0;

	private static readonly IPen ErrorPen =
		new ImmutablePen(
			new ImmutableSolidColorBrush(
				Color.FromRgb(0xD1, 0x34, 0x38)),
			1.25);

	private static readonly IPen WarningPen =
		new ImmutablePen(
			new ImmutableSolidColorBrush(
				Color.FromRgb(0xC1, 0x84, 0x01)),
			1.25);

	private static readonly IPen InfoPen =
		new ImmutablePen(
			new ImmutableSolidColorBrush(
				Color.FromRgb(0x2B, 0x78, 0xC5)),
			1.25);

	private ScriptDiagnosticVisualMarker[] _markers = [];

	public KnownLayer Layer => KnownLayer.Text;

	public void SetMarkers(
		IReadOnlyList<ScriptDiagnosticVisualMarker> markers)
	{
		ArgumentNullException.ThrowIfNull(markers);

		_markers =
			markers
				.OrderBy(marker => marker.Span.Start)
				.ToArray();
	}

	public void Draw(
		TextView textView,
		DrawingContext drawingContext)
	{
		ArgumentNullException.ThrowIfNull(textView);
		ArgumentNullException.ThrowIfNull(drawingContext);

		if (!textView.VisualLinesValid
			|| textView.VisualLines.Count == 0
			|| _markers.Length == 0)
		{
			return;
		}

		int viewStart =
			textView.VisualLines[0]
				.FirstDocumentLine.Offset;
		int viewEnd =
			textView.VisualLines[^1]
				.LastDocumentLine.EndOffset;

		foreach (ScriptDiagnosticVisualMarker marker
			in _markers)
		{
			if (marker.Span.End < viewStart)
				continue;
			if (marker.Span.Start > viewEnd)
				break;

			TextSegment segment =
				new()
				{
					StartOffset = marker.Span.Start,
					Length = marker.Span.Length,
				};

			foreach (Rect rect
				in BackgroundGeometryBuilder.GetRectsForSegment(
					textView,
					segment))
			{
				DrawSquiggle(
					drawingContext,
					rect,
					GetPen(marker.Severity));
			}
		}
	}

	private static void DrawSquiggle(
		DrawingContext drawingContext,
		Rect rect,
		IPen pen)
	{
		double left = rect.Left;
		double right =
			Math.Max(
				rect.Right,
				left + WaveStep * 2.0);
		double baseY =
			Math.Max(
				rect.Top + 1.0,
				rect.Bottom - 1.5);

		double x = left;
		double y = baseY;
		bool up = true;

		while (x < right)
		{
			double nextX =
				Math.Min(
					right,
					x + WaveStep);
			double nextY =
				baseY
					+ (up
						? -WaveAmplitude
						: WaveAmplitude);

			drawingContext.DrawLine(
				pen,
				new Point(x, y),
				new Point(nextX, nextY));

			x = nextX;
			y = nextY;
			up = !up;
		}
	}

	private static IPen GetPen(
		ScriptDiagnosticSeverity severity)
		=> severity switch
		{
			ScriptDiagnosticSeverity.Error =>
				ErrorPen,
			ScriptDiagnosticSeverity.Warning =>
				WarningPen,
			ScriptDiagnosticSeverity.Info =>
				InfoPen,
			_ => throw new ArgumentOutOfRangeException(
				nameof(severity),
				severity,
				"Unknown script diagnostic severity."),
		};
}

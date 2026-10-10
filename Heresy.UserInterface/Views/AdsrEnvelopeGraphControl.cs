using System;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using Heresy.Core.Envelopes;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.EnvelopeEditing;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Reusable, document-backed interactive ADSR graph. Uses the same live
/// envelope object and document revision path as the numeric editor, and
/// can also be hosted by the FM synth envelope inspector.
/// </summary>
public sealed class AdsrEnvelopeGraphControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly AdsrEnvelopeDefinition _envelope;
	private readonly GraphSurface _surface;
	private readonly TextBlock _sustainLabel = new()
	{
		FontSize = 12,
		VerticalAlignment = VerticalAlignment.Center,
	};
	private readonly Canvas _labels = new()
	{
		IsHitTestVisible = false,
	};

	public event EventHandler? EnvelopeChanged;

	public AdsrEnvelopeGraphControl(
		DocumentWorkspace workspace,
		AdsrEnvelopeDefinition envelope)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_envelope = envelope
			?? throw new ArgumentNullException(nameof(envelope));
		if (!workspace.Document.TryGet(envelope.Id, out var live)
			|| !ReferenceEquals(live, envelope))
		{
			throw new InvalidOperationException(
				"The envelope must belong to the current document.");
		}

		_surface = new GraphSurface(
			AdsrGraphValues.FromEnvelope(envelope));
		_surface.PreviewChanged += _ => UpdateSustainMarker();
		_surface.Committed += CommitGraphValues;

		Grid body = new()
		{
			Height = 260,
			MinWidth = 340,
			ColumnSpacing = 8,
		};
		body.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(134)));
		_labels.Children.Add(new TextBlock
		{
			Text = "Note Volume",
			FontWeight = FontWeight.SemiBold,
			FontSize = 12,
		});
		_labels.Children.Add(new TextBlock
		{
			Text = "0",
			FontSize = 12,
			VerticalAlignment = VerticalAlignment.Center,
		});
		_labels.Children.Add(_sustainLabel);
		Grid.SetColumn(_labels, 1);
		body.Children.Add(_surface);
		body.Children.Add(_labels);
		_labels.SizeChanged += (_, _) => UpdateSustainMarker();
		Content = body;
		AttachedToVisualTree += OnAttached;
		DetachedFromVisualTree += OnDetached;
		UpdateSustainMarker();
	}

	/// <summary>Resync after numeric editing or an external document edit.</summary>
	public void RefreshFromEnvelope()
	{
		_surface.SetValues(AdsrGraphValues.FromEnvelope(_envelope));
		UpdateSustainMarker();
	}

	private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
	{
		_workspace.Document.Changed += OnDocumentChanged;
		RefreshFromEnvelope();
	}

	private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
	{
		_workspace.Document.Changed -= OnDocumentChanged;
		_surface.CancelDrag();
	}

	private void OnDocumentChanged(object? sender,
		SongDocumentChangedEventArgs e)
	{
		_ = e;
		if (!_surface.IsDragging)
			RefreshFromEnvelope();
	}

	private void CommitGraphValues(AdsrGraphValues edited)
	{
		try
		{
			EnvelopeDocumentEditor.UpdateAdsrEnvelope(
				_workspace,
				_envelope,
				TimeSpan.FromSeconds(edited.AttackSeconds),
				TimeSpan.FromSeconds(edited.DecaySeconds),
				edited.SustainLevel,
				TimeSpan.FromSeconds(edited.ReleaseSeconds));
			RefreshFromEnvelope();
			EnvelopeChanged?.Invoke(this, EventArgs.Empty);
		}
		catch
		{
			// A failed or stale edit never leaves the graph displaying
			// values that were not accepted by the live document.
			RefreshFromEnvelope();
			throw;
		}
	}

	private void UpdateSustainMarker()
	{
		if (_labels.Children.Count < 3)
			return;
		AdsrGraphLayout graph = _surface.GetLayout();
		AdsrGraphValues values = _surface.Values;
		double actual = values.SustainLevel;
		string suffix = actual is < 0 or > 1
			? " (clipped)"
			: "";
		_sustainLabel.Text =
			"Sustain  " + actual.ToString("G5", CultureInfo.CurrentCulture)
				+ suffix;

		Canvas.SetLeft(_sustainLabel, 0);
		Canvas.SetTop(_sustainLabel, Math.Clamp(
			graph.SustainY - 9,
			0,
			Math.Max(0, _labels.Bounds.Height - 22)));
		Canvas.SetTop(_labels.Children[0], Math.Max(0, graph.Top - 12));
		Canvas.SetTop(_labels.Children[1], Math.Max(0,
			graph.Top + graph.Height - 11));
	}

	private sealed class GraphSurface : Control
	{
		private static readonly IBrush Background =
			new SolidColorBrush(Color.FromRgb(23, 27, 34));
		private static readonly IBrush Outline =
			new SolidColorBrush(Color.FromRgb(110, 120, 140));
		private static readonly IBrush Curve =
			new SolidColorBrush(Color.FromRgb(97, 207, 246));
		private static readonly IBrush Guide =
			new SolidColorBrush(Color.FromArgb(110, 126, 160, 190));
		private static readonly IBrush Handle =
			new SolidColorBrush(Color.FromRgb(246, 206, 108));
		private static readonly IBrush SelectedHandle =
			new SolidColorBrush(Color.FromRgb(255, 255, 255));

		private AdsrGraphValues _values;
		private AdsrGraphValues _initial;
		private AdsrGraphLayout _dragLayout;
		private AdsrGraphHandle _dragHandle;
		private double _dragStartX;

		public GraphSurface(AdsrGraphValues values)
		{
			_values = values;
			MinWidth = 190;
			MinHeight = 175;
			ClipToBounds = true;
		}

		public event Action<AdsrGraphValues>? PreviewChanged;
		public event Action<AdsrGraphValues>? Committed;
		public AdsrGraphValues Values => _values;
		public bool IsDragging => _dragHandle != AdsrGraphHandle.None;

		public AdsrGraphLayout GetLayout()
			=> AdsrGraphLayout.Create(_values, Bounds.Width, Bounds.Height,
				IsDragging ? _dragLayout.PixelsPerSecond : null,
				IsDragging ? _dragLayout.HoldSeconds : null);

		public void SetValues(AdsrGraphValues values)
		{
			if (IsDragging || values == _values)
				return;
			_values = values;
			InvalidateVisual();
			PreviewChanged?.Invoke(values);
		}

		public void CancelDrag()
		{
			if (!IsDragging)
				return;
			_dragHandle = AdsrGraphHandle.None;
			_values = _initial;
			InvalidateVisual();
			PreviewChanged?.Invoke(_values);
		}

		public override void Render(DrawingContext context)
		{
			base.Render(context);
			context.FillRectangle(Background, new Rect(Bounds.Size));
			if (Bounds.Width < 36 || Bounds.Height < 48)
				return;
			AdsrGraphLayout g = GetLayout();
			double floor = g.Top + g.Height;
			context.DrawLine(new Pen(Outline, 1),
				new Point(g.Left, g.Top),
				new Point(g.Left, floor));
			context.DrawLine(new Pen(Outline, 1),
				new Point(g.Left, floor),
				new Point(Bounds.Width - 1, floor));
			foreach (double x in new[] { g.AttackX, g.DecayX,
				g.ReleaseStartX, g.ReleaseEndX })
			{
				context.DrawLine(new Pen(Guide, 1), new Point(x, g.Top),
					new Point(x, floor));
			}

			Pen curvePen = new(Curve, 2.5);
			context.DrawLine(curvePen,
				new Point(g.Left, floor),
				new Point(g.AttackX, g.Top));
			context.DrawLine(curvePen,
				new Point(g.AttackX, g.Top),
				new Point(g.DecayX, g.SustainY));
			context.DrawLine(curvePen,
				new Point(g.DecayX, g.SustainY),
				new Point(g.ReleaseStartX, g.SustainY));
			context.DrawLine(curvePen,
				new Point(g.ReleaseStartX, g.SustainY),
				new Point(g.ReleaseEndX, floor));

			DrawDurationHandle(context, g.AttackX, g.Top + 8,
				AdsrGraphHandle.Attack);
			DrawDurationHandle(context, g.DecayX, g.Top + 23,
				AdsrGraphHandle.Decay);
			DrawDurationHandle(context, g.ReleaseEndX, g.Top + 38,
				AdsrGraphHandle.Release);
			context.FillRectangle(
				_dragHandle == AdsrGraphHandle.Sustain
					? SelectedHandle : Handle,
				new Rect((g.DecayX + g.ReleaseStartX) / 2 - 4,
					g.SustainY - 4, 8, 8));
		}

		private void DrawDurationHandle(DrawingContext context,
			double x, double y, AdsrGraphHandle handle)
		{
			context.FillRectangle(
				_dragHandle == handle ? SelectedHandle : Handle,
				new Rect(x - 5, y - 5, 10, 10));
		}

		protected override void OnPointerPressed(PointerPressedEventArgs e)
		{
			base.OnPointerPressed(e);
			if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
				return;
			Point pointer = e.GetPosition(this);
			AdsrGraphLayout layout = GetLayout();
			AdsrGraphHandle hit = layout.HitHandle(pointer.X, pointer.Y);
			if (hit == AdsrGraphHandle.None)
				return;
			_dragHandle = hit;
			_dragLayout = layout;
			_initial = _values;
			_dragStartX = pointer.X;
			e.Pointer.Capture(this);
			e.Handled = true;
			InvalidateVisual();
		}

		protected override void OnPointerMoved(PointerEventArgs e)
		{
			base.OnPointerMoved(e);
			if (!IsDragging)
				return;
			Point pointer = e.GetPosition(this);
			AdsrGraphValues edited = AdsrGraphValues.MoveHandle(
				_initial, _dragHandle, pointer.X - _dragStartX,
				pointer.Y, _dragLayout);
			if (edited != _values)
			{
				_values = edited;
				InvalidateVisual();
				PreviewChanged?.Invoke(edited);
			}
			e.Handled = true;
		}

		protected override void OnPointerReleased(PointerReleasedEventArgs e)
		{
			base.OnPointerReleased(e);
			if (!IsDragging)
				return;
			AdsrGraphValues completed = _values;
			AdsrGraphValues initial = _initial;
			_dragHandle = AdsrGraphHandle.None;
			e.Pointer.Capture(null);
			e.Handled = true;
			InvalidateVisual();
			if (initial != completed)
				Committed?.Invoke(completed);
		}

		protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
		{
			base.OnPointerCaptureLost(e);
			CancelDrag();
		}
	}
}

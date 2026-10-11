using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.EnvelopeEditing;

namespace Heresy.UserInterface.Views;

/// <summary>Shared envelope polyline, sustain guides and endpoint matching.</summary>
public sealed class NodeEnvelopeGraphControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly NodeEnvelopeDefinition _envelope;
	private readonly NodeEnvelopeGraphSurface _surface;
	private readonly Button _match = new()
	{
		Content = "Match",
		IsVisible = false,
		IsEnabled = false,
		HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
		MinWidth = 75,
	};

	public event EventHandler? EnvelopeChanged;
	public event EventHandler? SelectionChanged;
	public int SelectedNodeIndex => _surface.SelectedNodeIndex;

	public NodeEnvelopeGraphControl(
		DocumentWorkspace workspace, NodeEnvelopeDefinition envelope)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_envelope = envelope
			?? throw new ArgumentNullException(nameof(envelope));
		if (!workspace.Document.TryGet(envelope.Id, out SongObject? stored)
			|| !ReferenceEquals(stored, envelope))
			throw new InvalidOperationException(
				"The envelope is not part of the active document.");

		_surface = new NodeEnvelopeGraphSurface(envelope);
		_surface.SelectionChanged += () =>
		{
			UpdateMatch();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
		};
		_surface.CommitRequested += Commit;
		_match.Click += (_, _) => MatchSelected();
		StackPanel content = new() { Spacing = 5 };
		content.Children.Add(_surface);
		content.Children.Add(_match);
		Content = content;
		UpdateMatch();
		AttachedToVisualTree += OnAttached;
		DetachedFromVisualTree += OnDetached;
	}

	public void RefreshFromEnvelope()
	{
		_surface.RefreshFromEnvelope();
		UpdateMatch();
	}

	private void UpdateMatch()
	{
		int selected = SelectedNodeIndex;
		int start = _envelope.SustainStartIndex;
		int end = _envelope.SustainEndIndex;
		bool visible = _envelope.SustainLoopMode != EnvelopeSustainLoopMode.None
			&& start != end && (selected == start || selected == end);
		_match.IsVisible = visible;
		_match.IsEnabled = visible
			&& _envelope.Nodes[start].Value != _envelope.Nodes[end].Value;
	}

	private void MatchSelected()
	{
		if (!_match.IsEnabled || !_match.IsVisible)
			return;
		int selected = SelectedNodeIndex;
		int opposite = selected == _envelope.SustainStartIndex
			? _envelope.SustainEndIndex : _envelope.SustainStartIndex;
		EnvelopeNode[] nodes = _envelope.Nodes.ToArray();
		nodes[selected] = nodes[selected] with
		{
			Value = nodes[opposite].Value,
		};
		Commit(new NodeEnvelopeGraphEdit(
			nodes, _envelope.SustainStartIndex, _envelope.SustainEndIndex,
			selected));
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

	private void OnDocumentChanged(object? sender, SongDocumentChangedEventArgs e)
	{
		_ = e;
		if (!_surface.IsDragging)
			RefreshFromEnvelope();
	}

	private void Commit(NodeEnvelopeGraphEdit edited)
	{
		try
		{
			EnvelopeDocumentEditor.UpdateNodeEnvelope(
				_workspace, _envelope, edited.Nodes,
				edited.SustainStartIndex, edited.SustainEndIndex,
				_envelope.SustainLoopMode);
			_surface.Select(edited.SelectedNodeIndex);
			RefreshFromEnvelope();
			EnvelopeChanged?.Invoke(this, EventArgs.Empty);
		}
		catch
		{
			RefreshFromEnvelope();
			throw;
		}
	}
}

/// <summary>All editing geometry is delegated to the pure geometry model,
/// keeping drawing and pointer hit/drag rules identical.</summary>
internal sealed class NodeEnvelopeGraphSurface : Control
{
	private static readonly IBrush DarkBackground =
		new SolidColorBrush(Color.FromRgb(23, 27, 34));
	private static readonly IBrush LightBackground =
		new SolidColorBrush(Color.FromRgb(246, 247, 248));
	private static readonly IBrush Axis =
		new SolidColorBrush(Color.FromRgb(110, 120, 140));
	private static readonly IBrush Guide =
		new SolidColorBrush(Color.FromArgb(65, 146, 161, 181));
	private static readonly IBrush Curve =
		new SolidColorBrush(Color.FromRgb(97, 207, 246));
	private static readonly IBrush Dot =
		new SolidColorBrush(Color.FromRgb(242, 223, 159));
	private static readonly IBrush Bracket =
		new SolidColorBrush(Color.FromRgb(255, 255, 255));
	private static readonly IBrush Sustain =
		new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

	private readonly NodeEnvelopeDefinition _envelope;
	private EnvelopeNode[] _previewNodes;
	private int _selected = -1;
	private int _dragNode = -1;
	private int _dragGuide = -1;
	private int _originalSustainStart;
	private int _originalSustainEnd;
	private int _previewSustainStart;
	private int _previewSustainEnd;
	private bool _dragMoved;
	private Point _press;
	private NodeEnvelopeGraphGeometry? _fixedLayout;

	public event Action<NodeEnvelopeGraphEdit>? CommitRequested;
	public event Action? SelectionChanged;
	public int SelectedNodeIndex => _selected;
	public bool IsDragging => _dragNode >= 0 || _dragGuide >= 0;
	public NodeEnvelopeGraphSurface(NodeEnvelopeDefinition envelope)
	{
		_envelope = envelope;
		_previewNodes = envelope.Nodes.ToArray();
		_previewSustainStart = envelope.SustainStartIndex;
		_previewSustainEnd = envelope.SustainEndIndex;
		MinWidth = 260;
		MinHeight = 200;
		Height = 250;
		ClipToBounds = true;
		Focusable = true;
		IsTabStop = true;
	}

	public void RefreshFromEnvelope()
	{
		if (IsDragging)
			return;
		_previewNodes = _envelope.Nodes.ToArray();
		_previewSustainStart = _envelope.SustainStartIndex;
		_previewSustainEnd = _envelope.SustainEndIndex;
		if (_selected >= _previewNodes.Length)
			Select(-1);
		InvalidateVisual();
	}

	public void Select(int nodeIndex)
	{
		if (nodeIndex < -1 || nodeIndex >= _previewNodes.Length)
			nodeIndex = -1;
		if (_selected == nodeIndex)
			return;
		_selected = nodeIndex;
		SelectionChanged?.Invoke();
		InvalidateVisual();
	}

	public void CancelDrag()
	{
		if (!IsDragging)
			return;
		_dragNode = -1;
		_dragGuide = -1;
		_dragMoved = false;
		_fixedLayout = null;
		_previewNodes = _envelope.Nodes.ToArray();
		_previewSustainStart = _envelope.SustainStartIndex;
		_previewSustainEnd = _envelope.SustainEndIndex;
		InvalidateVisual();
	}

	private NodeEnvelopeGraphGeometry? Layout()
	{
		if (Bounds.Width <= 2 * NodeEnvelopeGraphGeometry.Padding
			|| Bounds.Height <= 2 * NodeEnvelopeGraphGeometry.Padding)
			return null;
		return _fixedLayout is not null
			? _fixedLayout.WithNodes(_previewNodes)
			: new NodeEnvelopeGraphGeometry(_previewNodes,
				Bounds.Width, Bounds.Height, _envelope.Vmin, _envelope.Vmax);
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		context.FillRectangle(
			ActualThemeVariant == ThemeVariant.Light
				? LightBackground : DarkBackground,
			new Rect(Bounds.Size));
		NodeEnvelopeGraphGeometry? graph = Layout();
		if (graph is null)
			return;
		context.DrawLine(new Pen(Axis, 1),
			new Point(graph.Left, graph.Top),
			new Point(graph.Left, graph.Bottom));
		context.DrawLine(new Pen(Axis, 1),
			new Point(graph.Left, graph.Bottom),
			new Point(graph.Right, graph.Bottom));

		int start = _previewSustainStart;
		int end = _previewSustainEnd;
		double sx = graph.Point(start).X;
		double ex = graph.Point(end).X;
		if (_envelope.SustainLoopMode != EnvelopeSustainLoopMode.None)
			context.FillRectangle(Sustain,
				new Rect(sx, graph.Top, Math.Max(0, ex - sx),
					graph.Bottom - graph.Top));

		for (int i = 0; i < graph.Count; i++)
		{
			(double x, _) = graph.Point(i);
			context.DrawLine(new Pen(Guide, 1),
				new Point(x, graph.Top),
				new Point(x, graph.Bottom));
			if (_envelope.SustainLoopMode != EnvelopeSustainLoopMode.None
				&& i >= start && i <= end)
				context.DrawLine(new Pen(Sustain, 2),
					new Point(x, graph.Top),
					new Point(x, graph.Bottom));
		}

		for (int i = 0; i + 1 < graph.Count; i++)
		{
			(double x1, double y1) = graph.Point(i);
			(double x2, double y2) = graph.Point(i + 1);
			context.DrawLine(new Pen(Curve, 2),
				new Point(x1, y1), new Point(x2, y2));
			if (_envelope.SustainLoopMode != EnvelopeSustainLoopMode.None
				&& i >= start && i < end)
				context.DrawLine(new Pen(Sustain, 3),
					new Point(x1, y1), new Point(x2, y2));
		}

		for (int i = 0; i < graph.Count; i++)
		{
			(double x, double y) = graph.Point(i);
			context.DrawEllipse(Dot, null, new Point(x, y),
				NodeEnvelopeGraphGeometry.NodeRadius,
				NodeEnvelopeGraphGeometry.NodeRadius);
			if (i == _selected)
				DrawSelection(context, x, y);
		}
	}

	private static void DrawSelection(DrawingContext context, double x, double y)
	{
		// A 6-pixel dot: brackets are 8 pixels tall and have a
		// 2-pixel clear gap to its nearest horizontal edge.
		Pen pen = new(Bracket, 1);
		const double inner = 5;
		const double outer = 7;
		const double halfHeight = NodeEnvelopeGraphGeometry.NodeRadius + 1;
		foreach (double dir in new[] { -1.0, +1.0 })
		{
			double a = x + dir * inner;
			double b = x + dir * outer;
			context.DrawLine(pen, new Point(b, y - halfHeight),
				new Point(b, y + halfHeight));
			context.DrawLine(pen, new Point(a, y - halfHeight),
				new Point(b, y - halfHeight));
			context.DrawLine(pen, new Point(a, y + halfHeight),
				new Point(b, y + halfHeight));
		}
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			return;
		NodeEnvelopeGraphGeometry? graph = Layout();
		if (graph is null)
			return;
		Point pointer = e.GetPosition(this);
		int selected = graph.HitNode(pointer.X, pointer.Y);

		// The dot wins over its vertical guide, including double-click.
		int guide = selected < 0
			? graph.HitGuide(pointer.X, pointer.Y) : -1;
		if (e.ClickCount >= 2)
		{
			if (selected < 0)
				Select(-1);
			if (selected >= 0)
			{
				NodeEnvelopeGraphEdit? edited = NodeEnvelopeGraphGeometry.Delete(
					_previewNodes, selected, _previewSustainStart,
					_previewSustainEnd);
				if (edited is not null)
					CommitRequested?.Invoke(edited);
			}
			else if (guide >= 0)
			{
				// Double-clicking any guide explicitly creates a
				// one-node sustain, unlike a self-drag.
				if (guide != _previewSustainStart
					|| guide != _previewSustainEnd)
					CommitRequested?.Invoke(new NodeEnvelopeGraphEdit(
						_previewNodes.ToArray(), guide, guide, _selected));
			}
			else
			{
				int segment = graph.HitSegment(
					pointer.X, pointer.Y, out EnvelopeNode? point);
				if (segment >= 0 && point is not null)
					CommitRequested?.Invoke(NodeEnvelopeGraphGeometry.Insert(
						_previewNodes, segment, point,
						_previewSustainStart, _previewSustainEnd));
			}
			e.Handled = true;
			return;
		}

		Select(selected);
		Focus();
		if (selected >= 0 || (guide >= 0
			&& (guide == _previewSustainStart
				|| guide == _previewSustainEnd
				|| guide < _previewSustainStart
				|| guide > _previewSustainEnd)))
		{
			_dragNode = selected;
			_dragGuide = selected >= 0 ? -1 : guide;
			_originalSustainStart = _previewSustainStart;
			_originalSustainEnd = _previewSustainEnd;
			_press = pointer;
			_dragMoved = false;
			_fixedLayout = graph;
			e.Pointer.Capture(this);
		}
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (!IsDragging || _fixedLayout is null)
			return;
		Point pointer = e.GetPosition(this);
		if (!_dragMoved && Math.Abs(pointer.X - _press.X) < 3
			&& Math.Abs(pointer.Y - _press.Y) < 3)
			return;
		UpdateDraggedPosition(pointer);
		e.Handled = true;
	}

	private void UpdateDraggedPosition(Point pointer)
	{
		if (!IsDragging || _fixedLayout is null)
			return;
		if (!_dragMoved && Math.Abs(pointer.X - _press.X) < 3
			&& Math.Abs(pointer.Y - _press.Y) < 3)
			return;
		_dragMoved = true;
		if (_dragNode >= 0)
		{
			EnvelopeNode moved = NodeEnvelopeGraphGeometry.Move(
				_previewNodes, _dragNode, _fixedLayout,
				pointer.X, pointer.Y);
			if (moved != _previewNodes[_dragNode])
			{
				_previewNodes[_dragNode] = moved;
				InvalidateVisual();
			}
		}
		else if (_dragGuide >= 0)
		{
			int target = _fixedLayout.ClosestGuide(pointer.X);
			if (target >= 0)
			{
				(int start, int end) =
					_dragGuide == _originalSustainStart
						|| _dragGuide == _originalSustainEnd
					? NodeEnvelopeGraphGeometry.MoveSustainBoundary(
						_originalSustainStart, _originalSustainEnd,
						_dragGuide, target)
					: NodeEnvelopeGraphGeometry.SelectSustainRegion(
						_dragGuide, target,
						_originalSustainStart, _originalSustainEnd);
				if (start != _previewSustainStart
					|| end != _previewSustainEnd)
				{
					_previewSustainStart = start;
					_previewSustainEnd = end;
					InvalidateVisual();
				}
			}
		}
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (!IsDragging)
			return;
		// Render the final pointer position even on platforms which
		// coalesce the last move directly into pointer release.
		UpdateDraggedPosition(e.GetPosition(this));
		int dragged = _dragNode;
		bool changed = _dragMoved
			&& (!_previewNodes.SequenceEqual(_envelope.Nodes)
				|| _previewSustainStart != _envelope.SustainStartIndex
				|| _previewSustainEnd != _envelope.SustainEndIndex);
		NodeEnvelopeGraphEdit result = new(_previewNodes.ToArray(),
			_previewSustainStart, _previewSustainEnd,
			dragged >= 0 ? dragged : _selected);
		_dragNode = -1;
		_dragGuide = -1;
		_dragMoved = false;
		_fixedLayout = null;
		e.Pointer.Capture(null);
		if (changed)
			CommitRequested?.Invoke(result);
		else
			RefreshFromEnvelope();
		e.Handled = true;
	}

	// Return false only for the host-level Ctrl+Tab escape chord.
	private bool HandleGraphKey(Key key, KeyModifiers modifiers)
	{
		if (key == Key.Tab)
		{
			if ((modifiers & KeyModifiers.Control) != 0)
				return false;
			int next = _selected < 0
				? (modifiers & KeyModifiers.Shift) != 0
					? _previewNodes.Length - 1 : 0
				: (_selected + ((modifiers & KeyModifiers.Shift) != 0
					? _previewNodes.Length - 1 : 1)) % _previewNodes.Length;
			Select(next);
			return true;
		}
		if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt
			| KeyModifiers.Meta)) != 0)
			return false;
		int time = key == Key.Left ? -1 : key == Key.Right ? 1 : 0;
		int value = key == Key.Down ? -1 : key == Key.Up ? 1 : 0;
		if (time == 0 && value == 0)
			return false;
		if (_selected < 0)
			Select(0);
		EnvelopeNode updated = NodeEnvelopeGraphGeometry.Nudge(
			_previewNodes, _selected, time, value,
			_envelope.Vmin, _envelope.Vmax);
		if (updated != _previewNodes[_selected])
		{
			EnvelopeNode[] nodes = _previewNodes.ToArray();
			nodes[_selected] = updated;
			CommitRequested?.Invoke(new NodeEnvelopeGraphEdit(
				nodes, _previewSustainStart, _previewSustainEnd, _selected));
		}
		return true;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Tab
			&& (e.KeyModifiers & KeyModifiers.Control) != 0)
		{
			// Ctrl+Tab always leaves the graph; ordinary Tab stays
			// within its sequence of envelope nodes.
			NavigationDirection direction =
				(e.KeyModifiers & KeyModifiers.Shift) != 0
					? NavigationDirection.Previous : NavigationDirection.Next;
			e.Handled = TopLevel.GetTopLevel(this)?.FocusManager
				.TryMoveFocus(direction, default) ?? false;
			return;
		}
		if (HandleGraphKey(e.Key, e.KeyModifiers))
		{
			e.Handled = true;
			return;
		}
		base.OnKeyDown(e);
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		CancelDrag();
	}
}

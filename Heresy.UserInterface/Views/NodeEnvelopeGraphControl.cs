using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.EnvelopeEditing;

namespace Heresy.UserInterface.Views;

/// <summary>Shared node-envelope polyline editor. Sustain *guide* gestures
/// and the Match action are deliberately a separate UI milestone.</summary>
public sealed class NodeEnvelopeGraphControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly NodeEnvelopeDefinition _envelope;
	private readonly NodeEnvelopeGraphSurface _surface;

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
		_surface.SelectionChanged += () => SelectionChanged?.Invoke(this,
			EventArgs.Empty);
		_surface.CommitRequested += Commit;
		Content = _surface;
		AttachedToVisualTree += OnAttached;
		DetachedFromVisualTree += OnDetached;
	}

	public void RefreshFromEnvelope() => _surface.RefreshFromEnvelope();

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
/// keeping rendering, insertion and drag hit rules identical.</summary>
internal sealed class NodeEnvelopeGraphSurface : Control
{
	private static readonly IBrush Background =
		new SolidColorBrush(Color.FromRgb(23, 27, 34));
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
		new SolidColorBrush(Color.FromArgb(0x04, 0x80, 0x80, 0x80));

	private readonly NodeEnvelopeDefinition _envelope;
	private EnvelopeNode[] _previewNodes;
	private int _selected = -1;
	private int _dragNode = -1;
	private bool _dragMoved;
	private Point _press;
	private NodeEnvelopeGraphGeometry? _fixedLayout;

	public event Action<NodeEnvelopeGraphEdit>? CommitRequested;
	public event Action? SelectionChanged;
	public int SelectedNodeIndex => _selected;
	public bool IsDragging => _dragNode >= 0;
	public NodeEnvelopeGraphSurface(NodeEnvelopeDefinition envelope)
	{
		_envelope = envelope;
		_previewNodes = envelope.Nodes.ToArray();
		MinWidth = 260;
		MinHeight = 200;
		Height = 250;
		ClipToBounds = true;
	}

	public void RefreshFromEnvelope()
	{
		if (IsDragging)
			return;
		_previewNodes = _envelope.Nodes.ToArray();
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
		_dragMoved = false;
		_fixedLayout = null;
		_previewNodes = _envelope.Nodes.ToArray();
		InvalidateVisual();
	}

	private NodeEnvelopeGraphGeometry? Layout()
	{
		if (Bounds.Width <= 2 * NodeEnvelopeGraphGeometry.Padding
			|| Bounds.Height <= 2 * NodeEnvelopeGraphGeometry.Padding)
			return null;
		return _fixedLayout ??
			new NodeEnvelopeGraphGeometry(_previewNodes,
				Bounds.Width, Bounds.Height);
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		context.FillRectangle(Background, new Rect(Bounds.Size));
		NodeEnvelopeGraphGeometry? graph = Layout();
		if (graph is null)
			return;
		context.DrawLine(new Pen(Axis, 1),
			new Point(graph.Left, graph.Top),
			new Point(graph.Left, graph.Bottom));
		context.DrawLine(new Pen(Axis, 1),
			new Point(graph.Left, graph.Bottom),
			new Point(graph.Right, graph.Bottom));

		int start = _envelope.SustainStartIndex;
		int end = _envelope.SustainEndIndex;
		double sx = graph.Point(start).X;
		double ex = graph.Point(end).X;
		context.FillRectangle(Sustain,
			new Rect(sx, graph.Top, Math.Max(0, ex - sx),
				graph.Bottom - graph.Top));

		for (int i = 0; i < graph.Count; i++)
		{
			(double x, _) = graph.Point(i);
			context.DrawLine(new Pen(Guide, 1),
				new Point(x, graph.Top),
				new Point(x, graph.Bottom));
			if (i >= start && i <= end)
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
			if (i >= start && i < end)
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

		if (e.ClickCount >= 2)
		{
			if (selected >= 0)
			{
				NodeEnvelopeGraphEdit? edited = NodeEnvelopeGraphGeometry.Delete(
					_previewNodes, selected, _envelope.SustainStartIndex,
					_envelope.SustainEndIndex);
				if (edited is not null)
					CommitRequested?.Invoke(edited);
			}
			else
			{
				int segment = graph.HitSegment(
					pointer.X, pointer.Y, out EnvelopeNode? point);
				if (segment >= 0 && point is not null)
					CommitRequested?.Invoke(NodeEnvelopeGraphGeometry.Insert(
						_previewNodes, segment, point,
						_envelope.SustainStartIndex, _envelope.SustainEndIndex));
			}
			e.Handled = true;
			return;
		}

		Select(selected);
		if (selected >= 0)
		{
			_dragNode = selected;
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
		_dragMoved = true;
		EnvelopeNode moved = NodeEnvelopeGraphGeometry.Move(
			_previewNodes, _dragNode, _fixedLayout,
			pointer.X, pointer.Y);
		if (moved != _previewNodes[_dragNode])
		{
			_previewNodes[_dragNode] = moved;
			InvalidateVisual();
		}
		e.Handled = true;
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (!IsDragging)
			return;
		int dragged = _dragNode;
		bool changed = _dragMoved
			&& !_previewNodes.SequenceEqual(_envelope.Nodes);
		NodeEnvelopeGraphEdit result = new(_previewNodes.ToArray(),
			_envelope.SustainStartIndex, _envelope.SustainEndIndex, dragged);
		_dragNode = -1;
		_dragMoved = false;
		_fixedLayout = null;
		e.Pointer.Capture(null);
		if (changed)
			CommitRequested?.Invoke(result);
		else
			RefreshFromEnvelope();
		e.Handled = true;
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		CancelDrag();
	}
}

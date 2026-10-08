using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.FmSynthesis;
using Heresy.UserInterface.FmEditing;

namespace Heresy.UserInterface.Views;

public sealed class FmSynthGraphCanvas : UserControl
{
	public const double NodeWidth = 170.0;
	public const double NodeHeight = 72.0;

	private readonly FmSynthDefinition _synth;
	private readonly Action<int> _selected;
	private readonly Action<int, double, double> _moved;
	private readonly Action<int, int, int> _connected;
	private readonly Action<
		int, int, int, IReadOnlyList<FmSynthRoutePoint>> _waypointsChanged;
	private readonly Canvas _canvas = new();
	private readonly ConnectionLayer _connectionLayer;
	private readonly ConnectionLayer _portLayer;
	private readonly Dictionary<int, Border> _nodeControls = [];
	private readonly Dictionary<int, FmSynthNodePosition> _positions = [];

	private int? _selectedNodeId;
	private int? _dragNodeId;
	private Point _dragPointerStart;
	private FmSynthNodePosition _dragOriginal;
	private int? _hoverNodeId;
	private FmSynthConnectionPort? _dragPort;
	private Point _dragPortPointer;
	private WaypointDrag? _waypointDrag;

	private sealed class WaypointDrag
	{
		public required FmSynthConnectionCandidate Connection { get; init; }
		public required FmSynthRoutePoint[] Waypoints { get; init; }
		public required int Index { get; init; }
		public required Point PointerStart { get; init; }
		public required FmSynthRoutePoint InitialPoint { get; init; }
		public FmSynthRoutePoint CurrentPoint { get; set; }
		public bool Moved { get; set; }
	}

	public FmSynthGraphCanvas(
		FmSynthDefinition synth,
		Action<int> selected,
		Action<int, double, double> moved,
		Action<int, int, int> connected,
		Action<int, int, int, IReadOnlyList<FmSynthRoutePoint>> waypointsChanged)
	{
		_synth =
			synth
				?? throw new ArgumentNullException(nameof(synth));
		_selected =
			selected
				?? throw new ArgumentNullException(nameof(selected));
		_moved =
			moved
				?? throw new ArgumentNullException(nameof(moved));
		_connected =
			connected
				?? throw new ArgumentNullException(nameof(connected));
		_waypointsChanged =
			waypointsChanged
				?? throw new ArgumentNullException(nameof(waypointsChanged));

		MinWidth = 1200;
		MinHeight = 720;
		ClipToBounds = false;

		_connectionLayer =
			new ConnectionLayer(
				DrawConnections)
			{
				IsHitTestVisible = false,
			};
		_portLayer =
			new ConnectionLayer(
				DrawConnectionPorts)
			{
				IsHitTestVisible = false,
			};
		Grid root =
			new()
			{
				Background =
					new SolidColorBrush(
						Color.FromRgb(
							24,
							24,
							28)),
			};
		root.Children.Add(
			_connectionLayer);
		root.Children.Add(
			_canvas);
		// Handles and the in-progress drag must remain visible above nodes;
		// this overlay never intercepts pointer events from the canvas.
		root.Children.Add(
			_portLayer);
		root.PointerPressed += OnWirePointerPressed;
		root.PointerMoved += OnWirePointerMoved;
		root.PointerReleased += OnWirePointerReleased;
		root.PointerCaptureLost += OnWirePointerCaptureLost;
		Content = root;
		Refresh();
	}

	public void Refresh()
	{
		_hoverNodeId = null;
		_dragPort = null;
		_waypointDrag = null;
		_canvas.Children.Clear();
		_nodeControls.Clear();
		_positions.Clear();

		int index = 0;
		foreach (FmSynthNode node in _synth.Graph.Nodes)
		{
			FmSynthNodePosition position =
				GetPersistedPosition(node.Id)
				?? DefaultPosition(
					node.Id,
					index);
			_positions[node.Id] = position;

			Border control =
				BuildNodeControl(
					node,
					position);
			Canvas.SetLeft(
				control,
				position.X);
			Canvas.SetTop(
				control,
				position.Y);
			_canvas.Children.Add(control);
			_nodeControls.Add(
				node.Id,
				control);
			index++;
		}

		ApplySelectionVisuals();
		_connectionLayer.InvalidateVisual();
		_portLayer.InvalidateVisual();
	}

	public void SetSelectedNode(
		int? nodeId)
	{
		if (_selectedNodeId == nodeId)
			return;

		_selectedNodeId = nodeId;
		ApplySelectionVisuals();
	}

	/// <summary>
	/// Use the identical routed wire geometry for drawing, hit testing, and
	/// waypoint ordering. The drag preview substitutes editor-only hints but
	/// never mutates the document until the pointer is released.
	/// </summary>
	private FmSynthRenderedConnection[] GetRenderedConnections()
	{
		FmSynthNode[] nodes = _synth.Graph.Nodes.ToArray();
		List<FmSynthRenderedConnection> connections = [];
		foreach (FmSynthNode target in nodes)
		{
			if (!_positions.TryGetValue(
				target.Id,
				out FmSynthNodePosition targetPosition))
			{
				continue;
			}
			FmSynthLayoutRect targetRect = ToRect(targetPosition);
			for (int inputIndex = 0;
				inputIndex < target.InputNodeIds.Count;
				inputIndex++)
			{
				int sourceId = target.InputNodeIds[inputIndex];
				if (!_positions.TryGetValue(
					sourceId,
					out FmSynthNodePosition sourcePosition))
				{
					continue;
				}
				FmSynthLayoutRect sourceRect = ToRect(sourcePosition);
				FmSynthLayoutRect[] obstacles = nodes
					.Where(node => node.Id != sourceId
						&& node.Id != target.Id)
					.Select(node => ToRect(_positions[node.Id]))
					.ToArray();
				FmSynthConnectionRoutingHint? hint =
					_synth.ConnectionRoutingHints.FirstOrDefault(candidate =>
						candidate.SourceNodeId == sourceId
						&& candidate.TargetNodeId == target.Id
						&& candidate.TargetInputIndex == inputIndex);
				FmSynthConnectionCandidate connection =
					new(sourceId, target.Id, inputIndex);
				IReadOnlyList<FmSynthRoutePoint> waypoints =
					hint?.RoutePoints ?? Array.Empty<FmSynthRoutePoint>();
				if (_waypointDrag is WaypointDrag drag
					&& drag.Connection == connection
					&& drag.Moved)
				{
					waypoints = FmSynthWaypointGeometry.Replace(
						drag.Waypoints, drag.Index, drag.CurrentPoint);
				}

				FmSynthNode sourceNode =
					nodes.First(node => node.Id == sourceId);
				FmSynthRoutePoint startPort =
					FmSynthConnectionPorts.GetPorts(
						sourceNode, sourceRect)[0].Center;
				FmSynthRoutePoint endPort =
					FmSynthConnectionPorts.GetPorts(
						target, targetRect)[inputIndex + 1].Center;
				FmSynthRoutePoint[] route =
					FmSynthConnectionRouter.Route(
						sourceRect, targetRect, obstacles, waypoints,
						startPort, endPort);
				connections.Add(
					new FmSynthRenderedConnection(
						connection, route, waypoints));
			}
		}
		return [.. connections];
	}

	private void DrawConnections(DrawingContext context)
	{
		Pen connectionPen =
			new(
				new SolidColorBrush(
					Color.FromRgb(125, 175, 235)),
				2.0);
		foreach (FmSynthRenderedConnection connection in GetRenderedConnections())
		{
			IReadOnlyList<FmSynthRoutePoint> route = connection.Route;
			for (int pointIndex = 1;
				pointIndex < route.Count;
				pointIndex++)
			{
				context.DrawLine(
					connectionPen,
					new Point(
						route[pointIndex - 1].X,
						route[pointIndex - 1].Y),
					new Point(
						route[pointIndex].X,
						route[pointIndex].Y));
			}
			DrawArrowhead(context, connectionPen, route);

			// Existing waypoints are always visible so that the user can
			// move or double-click them without an inspector panel.
			foreach (FmSynthRoutePoint waypoint in connection.Waypoints)
			{
				context.DrawEllipse(
					Brushes.Gold,
					new Pen(Brushes.Black, 1.5),
					new Point(waypoint.X, waypoint.Y),
					4.5,
					4.5);
			}
		}
	}

	private double RenderScaling
		=> TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

	private void OnWirePointerPressed(
		object? sender,
		PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			return;

		Point pointer = e.GetPosition(_canvas);
		FmSynthRoutePoint position = new(pointer.X, pointer.Y);
		FmSynthRenderedConnection[] connections = GetRenderedConnections();
		FmSynthWaypointHit? waypoint = FmSynthWaypointGeometry.HitWaypoint(
			connections, position, RenderScaling);
		if (e.ClickCount == 2)
		{
			if (waypoint is FmSynthWaypointHit hit)
			{
				FmSynthRenderedConnection connection =
					connections.First(item => item.Connection == hit.Connection);
				FmSynthRoutePoint[] remaining = FmSynthWaypointGeometry.Remove(
					connection.Waypoints, hit.WaypointIndex);
				_waypointDrag = null;
				_waypointsChanged(
					hit.Connection.SourceNodeId,
					hit.Connection.TargetNodeId,
					hit.Connection.TargetInputIndex,
					remaining);
				e.Handled = true;
			}
			return;
		}

		if (waypoint is FmSynthWaypointHit old)
		{
			FmSynthRenderedConnection wire =
				connections.First(item => item.Connection == old.Connection);
			_waypointDrag = new WaypointDrag
			{
				Connection = old.Connection,
				Waypoints = [.. wire.Waypoints],
				Index = old.WaypointIndex,
				InitialPoint = wire.Waypoints[old.WaypointIndex],
				CurrentPoint = wire.Waypoints[old.WaypointIndex],
				PointerStart = pointer,
			};
		}
		else if (FmSynthWaypointGeometry.HitConnection(
			connections, position, RenderScaling)
			is FmSynthConnectionLineHit line)
		{
			FmSynthRenderedConnection wire =
				connections.First(item => item.Connection == line.Connection);
			FmSynthRoutePoint[] inserted = FmSynthWaypointGeometry.Insert(
				wire.Waypoints, line.InsertIndex, line.NearestPoint);
			_waypointDrag = new WaypointDrag
			{
				Connection = line.Connection,
				Waypoints = inserted,
				Index = line.InsertIndex,
				InitialPoint = line.NearestPoint,
				CurrentPoint = line.NearestPoint,
				PointerStart = pointer,
			};
		}
		else
		{
			return;
		}

		e.Pointer.Capture((IInputElement)sender!);
		e.Handled = true;
	}

	private void OnWirePointerMoved(
		object? sender,
		PointerEventArgs e)
	{
		_ = sender;
		if (_waypointDrag is not WaypointDrag drag)
			return;
		Point pointer = e.GetPosition(_canvas);
		double dx = pointer.X - drag.PointerStart.X;
		double dy = pointer.Y - drag.PointerStart.Y;
		if (!drag.Moved
			&& ((dx * dx + dy * dy) * RenderScaling * RenderScaling) < 9.0)
		{
			return;
		}

		drag.Moved = true;
		drag.CurrentPoint = new FmSynthRoutePoint(
			drag.InitialPoint.X + dx,
			drag.InitialPoint.Y + dy);
		_connectionLayer.InvalidateVisual();
		e.Handled = true;
	}

	private void OnWirePointerReleased(
		object? sender,
		PointerReleasedEventArgs e)
	{
		_ = sender;
		if (_waypointDrag is not WaypointDrag drag)
			return;
		_waypointDrag = null;
		e.Pointer.Capture(null);
		if (drag.Moved && drag.CurrentPoint != drag.InitialPoint)
		{
			FmSynthRoutePoint[] points = FmSynthWaypointGeometry.Replace(
				drag.Waypoints, drag.Index, drag.CurrentPoint);
			_waypointsChanged(
				drag.Connection.SourceNodeId,
				drag.Connection.TargetNodeId,
				drag.Connection.TargetInputIndex,
				points);
		}
		_connectionLayer.InvalidateVisual();
		e.Handled = true;
	}

	private void OnWirePointerCaptureLost(
		object? sender,
		PointerCaptureLostEventArgs e)
	{
		_ = sender;
		_ = e;
		_waypointDrag = null;
		_connectionLayer.InvalidateVisual();
	}

	private void DrawConnectionPorts(
		DrawingContext context)
	{
		if (_dragPort is FmSynthConnectionPort start)
		{
			context.DrawLine(
				new Pen(Brushes.LightSkyBlue, 2.0),
				new Point(start.Center.X, start.Center.Y),
				_dragPortPointer);
		}

		foreach (FmSynthNode node in _synth.Graph.Nodes)
		{
			if (node.Id != _hoverNodeId
				&& node.Id != _dragPort?.NodeId)
			{
				continue;
			}
			if (!_positions.TryGetValue(
				node.Id,
				out FmSynthNodePosition position))
			{
				continue;
			}

			foreach (FmSynthConnectionPort port in
				FmSynthConnectionPorts.GetPorts(node, ToRect(position)))
			{
				bool compatible =
					_dragPort is not FmSynthConnectionPort dragging
						|| FmSynthConnectionPorts.TryResolve(
							dragging, port, out _);
				IBrush fill =
					!compatible
						? Brushes.Gray
						: port.Kind == FmSynthConnectionPortKind.Output
							? Brushes.LightGreen
							: node is FmOperatorNode operation
								&& port.InputIndex == operation.InputNodeIds.Count
								? Brushes.Gold
								: Brushes.LightSkyBlue;
				context.DrawEllipse(
					fill,
					new Pen(Brushes.Black, 1.5),
					new Point(port.Center.X, port.Center.Y),
					5.5,
					5.5);
			}
		}
	}

	private FmSynthConnectionPort? FindPortAt(
		Point pointer)
	{
		foreach (FmSynthNode node in _synth.Graph.Nodes)
		{
			if (_positions.TryGetValue(
				node.Id,
				out FmSynthNodePosition position)
				&& FmSynthConnectionPorts.HitTest(
					FmSynthConnectionPorts.GetPorts(
						node,
						ToRect(position)),
					new FmSynthRoutePoint(pointer.X, pointer.Y))
				is FmSynthConnectionPort port)
			{
				return port;
			}
		}
		return null;
	}

	private int? FindNodeBorderAt(
		Point pointer)
	{
		const double borderHoverWidth = 12.0;
		foreach ((int nodeId, FmSynthNodePosition position) in _positions)
		{
			FmSynthLayoutRect rect = ToRect(position);
			if (pointer.X < rect.Left - borderHoverWidth
				|| pointer.X > rect.Right + borderHoverWidth
				|| pointer.Y < rect.Top - borderHoverWidth
				|| pointer.Y > rect.Bottom + borderHoverWidth)
			{
				continue;
			}
			double edgeDistance =
				Math.Min(
					Math.Min(
						Math.Abs(pointer.X - rect.Left),
						Math.Abs(pointer.X - rect.Right)),
					Math.Min(
						Math.Abs(pointer.Y - rect.Top),
						Math.Abs(pointer.Y - rect.Bottom)));
			if (edgeDistance <= borderHoverWidth)
				return nodeId;
		}
		return null;
	}

	private void UpdateHover(
		Point pointer)
	{
		int? hovered = FindNodeBorderAt(pointer);
		if (_hoverNodeId == hovered)
			return;
		_hoverNodeId = hovered;
		_portLayer.InvalidateVisual();
	}

	private static void DrawArrowhead(
		DrawingContext context,
		Pen pen,
		IReadOnlyList<FmSynthRoutePoint> route)
	{
		if (FmSynthConnectionArrowheadGeometry.FromRoute(route)
			is not FmSynthConnectionArrowhead arrow)
		{
			return;
		}

		Point tip = new(arrow.Tip.X, arrow.Tip.Y);
		context.DrawLine(
			pen,
			tip,
			new Point(
				arrow.FirstWing.X,
				arrow.FirstWing.Y));
		context.DrawLine(
			pen,
			tip,
			new Point(
				arrow.SecondWing.X,
				arrow.SecondWing.Y));
	}

	private Border BuildNodeControl(
		FmSynthNode node,
		FmSynthNodePosition position)
	{
		TextBlock title =
			new()
			{
				Text =
					$"#{node.Id} {NodeTypeName(node)}"
					+ (node.Id == _synth.Graph.OutputNodeId
						? "  [OUTPUT]"
						: string.Empty),
				FontWeight = FontWeight.SemiBold,
				Foreground = Brushes.White,
			};
		TextBlock detail =
			new()
			{
				Text = NodeDetail(node),
				Foreground =
					new SolidColorBrush(
						Color.FromRgb(
							205,
							205,
							215)),
				TextWrapping = TextWrapping.Wrap,
				FontSize = 11,
			};
		StackPanel content =
			new()
			{
				Spacing = 4,
				Margin = new Thickness(8, 6),
			};
		content.Children.Add(title);
		content.Children.Add(detail);

		Border border =
			new()
			{
				Width = NodeWidth,
				Height = NodeHeight,
				CornerRadius = new CornerRadius(5),
				BorderThickness = new Thickness(2),
				Background =
					new SolidColorBrush(
						Color.FromRgb(
							48,
							52,
							62)),
				Child = content,
			};

		border.PointerExited += (_, e) =>
		{
			_ = e;
			if (_dragPort is null && _dragNodeId is null)
			{
				_hoverNodeId = null;
				_portLayer.InvalidateVisual();
			}
		};
		border.PointerPressed += (_, e) =>
			OnNodePointerPressed(
				border,
				node.Id,
				position,
				e);
		border.PointerMoved += (_, e) =>
			OnNodePointerMoved(
				border,
				node.Id,
				e);
		border.PointerReleased += (_, e) =>
			OnNodePointerReleased(
				border,
				node.Id,
				e);
		border.PointerCaptureLost += (_, e) =>
			OnNodePointerCaptureLost(
				border,
				node.Id,
				e);

		return border;
	}

	private void OnNodePointerPressed(
		Border border,
		int nodeId,
		FmSynthNodePosition position,
		PointerPressedEventArgs e)
	{
		PointerPoint point =
			e.GetCurrentPoint(border);
		if (!point.Properties.IsLeftButtonPressed)
			return;

		_selectedNodeId = nodeId;
		ApplySelectionVisuals();
		_selected(nodeId);

		Point pointer = e.GetPosition(_canvas);
		if (FindPortAt(pointer)
			is FmSynthConnectionPort port
			&& port.NodeId == nodeId)
		{
			_dragPort = port;
			_dragPortPointer = pointer;
			_hoverNodeId = nodeId;
			_portLayer.InvalidateVisual();
			e.Pointer.Capture(border);
			e.Handled = true;
			return;
		}

		_dragNodeId = nodeId;
		_dragPointerStart =
			e.GetPosition(_canvas);
		_dragOriginal = position;
		e.Pointer.Capture(border);
		e.Handled = true;
	}

	private void OnNodePointerMoved(
		Border border,
		int nodeId,
		PointerEventArgs e)
	{
		Point current =
			e.GetPosition(_canvas);
		if (_dragPort is not null)
		{
			_dragPortPointer = current;
			UpdateHover(current);
			_portLayer.InvalidateVisual();
			e.Handled = true;
			return;
		}
		if (_dragNodeId != nodeId)
		{
			UpdateHover(current);
			return;
		}
		double x =
			Math.Max(
				0.0,
				_dragOriginal.X
					+ current.X
					- _dragPointerStart.X);
		double y =
			Math.Max(
				0.0,
				_dragOriginal.Y
					+ current.Y
					- _dragPointerStart.Y);
		FmSynthNodePosition preview =
			new(
				nodeId,
				x,
				y);
		_positions[nodeId] = preview;
		Canvas.SetLeft(
			border,
			x);
		Canvas.SetTop(
			border,
			y);
		_connectionLayer.InvalidateVisual();
		_portLayer.InvalidateVisual();
		e.Handled = true;
	}

	private void OnNodePointerReleased(
		Border border,
		int nodeId,
		PointerReleasedEventArgs e)
	{
		if (_dragPort is FmSynthConnectionPort start
			&& start.NodeId == nodeId)
		{
			FmSynthConnectionPort? end = FindPortAt(e.GetPosition(_canvas));
			_dragPort = null;
			_hoverNodeId = null;
			e.Pointer.Capture(null);
			_portLayer.InvalidateVisual();
			if (end is FmSynthConnectionPort target
				&& FmSynthConnectionPorts.TryResolve(
					start, target, out FmSynthConnectionCandidate connection))
			{
				_connected(
					connection.SourceNodeId,
					connection.TargetNodeId,
					connection.TargetInputIndex);
			}
			e.Handled = true;
			return;
		}
		if (_dragNodeId != nodeId)
			return;

		_dragNodeId = null;
		e.Pointer.Capture(null);
		if (_positions.TryGetValue(
			nodeId,
			out FmSynthNodePosition position)
			&& position != _dragOriginal)
		{
			_moved(
				nodeId,
				position.X,
				position.Y);
		}
		e.Handled = true;
	}

	private void OnNodePointerCaptureLost(
		Border border,
		int nodeId,
		PointerCaptureLostEventArgs e)
	{
		_ = e;
		if (_dragPort is FmSynthConnectionPort start
			&& start.NodeId == nodeId)
		{
			_dragPort = null;
			_hoverNodeId = null;
			_portLayer.InvalidateVisual();
			return;
		}
		if (_dragNodeId != nodeId)
			return;

		_dragNodeId = null;
		_positions[nodeId] = _dragOriginal;
		Canvas.SetLeft(
			border,
			_dragOriginal.X);
		Canvas.SetTop(
			border,
			_dragOriginal.Y);
		_connectionLayer.InvalidateVisual();
		_portLayer.InvalidateVisual();
	}

	private void ApplySelectionVisuals()
	{
		foreach ((int id, Border border) in _nodeControls)
		{
			border.BorderBrush =
				id == _selectedNodeId
					? Brushes.White
					: id == _synth.Graph.OutputNodeId
						? new SolidColorBrush(
							Color.FromRgb(
								230,
								190,
								75))
						: new SolidColorBrush(
							Color.FromRgb(
								95,
								100,
								115));
		}
	}

	private FmSynthNodePosition? GetPersistedPosition(
		int nodeId)
	{
		foreach (FmSynthNodePosition position in
			_synth.NodePositions)
		{
			if (position.NodeId == nodeId)
				return position;
		}
		return null;
	}

	private static FmSynthNodePosition DefaultPosition(
		int nodeId,
		int index)
		=> new(
			nodeId,
			80.0 + ((index % 4) * 220.0),
			80.0 + ((index / 4) * 130.0));

	private static FmSynthLayoutRect ToRect(
		FmSynthNodePosition position)
		=> new(
			position.X,
			position.Y,
			NodeWidth,
			NodeHeight);

	private static string NodeTypeName(
		FmSynthNode node)
		=> node switch
		{
			FmConstantNode => "Constant",
			FmOscillatorNode => "Oscillator",
			FmEnvelopeNode => "Envelope",
			FmOperatorNode => "Operator",
			_ => node.GetType().Name,
		};

	private static string NodeDetail(
		FmSynthNode node)
		=> node switch
		{
			FmConstantNode constant =>
				constant.Value.ToString(
					"0.###",
					CultureInfo.InvariantCulture),
			FmOscillatorNode oscillator =>
				$"{oscillator.Waveform}, "
				+ $"{oscillator.FrequencyHz.ToString("0.###", CultureInfo.InvariantCulture)} Hz",
			FmEnvelopeNode envelope =>
				$"Envelope <{envelope.EnvelopeId.Value}>",
			FmOperatorNode op =>
				$"{op.Operation}: "
				+ string.Join(
					", ",
					op.InputNodeIds.Select(
						id => $"#{id}")),
			_ => string.Empty,
		};

	private sealed class ConnectionLayer : Control
	{
		private readonly Action<DrawingContext> _render;

		public ConnectionLayer(
			Action<DrawingContext> render)
		{
			_render =
				render
					?? throw new ArgumentNullException(nameof(render));
		}

		public override void Render(
			DrawingContext context)
		{
			base.Render(context);
			_render(context);
		}
	}
}

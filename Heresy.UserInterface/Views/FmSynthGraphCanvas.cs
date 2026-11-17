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
	private readonly Action<FmSynthConnectionCandidate>? _connectionSelected;
	private readonly Action<int, double, double> _moved;
	private readonly Action<int, int, int> _connected;
	private readonly Action<
		int, int, int, IReadOnlyList<FmSynthRoutePoint>> _waypointsChanged;
	private readonly Canvas _canvas = new();
	private readonly Grid _graphRoot = new();
	private readonly Grid _graphScene = new();
	private ScrollViewer? _scrollViewer;
	private FmSynthGraphBounds _contentBounds;
	private FmSynthGraphScrollRange _scrollRange;
	private FmSynthRoutePoint _camera;
	private bool _hasCamera;
	private bool _projectingScrollbars;
	private bool _rangeLayoutPending;
	private Vector _lastProjectedOffset;
	private double _viewportWidth;
	private double _viewportHeight;
	private bool _isBackgroundPanning;
	private Point _panPointerStart;
	private FmSynthRoutePoint _panCameraStart;
	private readonly ConnectionLayer _connectionLayer;
	private readonly ConnectionLayer _portLayer;
	private readonly Dictionary<int, Border> _nodeControls = [];
	private readonly Dictionary<int, FmSynthNodePosition> _positions = [];

	private int? _selectedNodeId;
	private FmSynthConnectionCandidate? _selectedConnection;
	private int? _dragNodeId;
	private Point _dragPointerStart;
	private FmSynthNodePosition _dragOriginal;
	private int? _hoverNodeId;
	private FmSynthConnectionPort? _dragPort;
	private Point _dragPortPointer;
	private bool _rootPortDrag;
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
		Action<int, int, int, IReadOnlyList<FmSynthRoutePoint>> waypointsChanged,
		Action<FmSynthConnectionCandidate>? connectionSelected = null)
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
		_connectionSelected = connectionSelected;

		// The scroll extent follows the geometry, not a fixed canvas minimum.
		Focusable = true;
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
		_graphRoot.Background =
			new SolidColorBrush(Color.FromRgb(24, 24, 28));
		_graphRoot.ClipToBounds = false;
		_graphScene.ClipToBounds = false;
		_graphScene.Children.Add(_connectionLayer);
		_graphScene.Children.Add(_canvas);
		// The overlay renders dots and previews above all node bodies.
		_graphScene.Children.Add(_portLayer);
		_graphRoot.Children.Add(_graphScene);
		_graphRoot.PointerPressed += OnWirePointerPressed;
		_graphRoot.PointerMoved += OnWirePointerMoved;
		_graphRoot.PointerReleased += OnWirePointerReleased;
		_graphRoot.PointerCaptureLost += OnWirePointerCaptureLost;
		Content = _graphRoot;
		Refresh();
	}

	public void Refresh()
	{
		_hoverNodeId = null;
		_dragPort = null;
		_rootPortDrag = false;
		_waypointDrag = null;
		_isBackgroundPanning = false;
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

		if (_selectedConnection is FmSynthConnectionCandidate selection
			&& !GetRenderedConnections().Any(item => item.Connection == selection))
			_selectedConnection = null;
		ApplySelectionVisuals();
		UpdateContentExtent();
		_connectionLayer.InvalidateVisual();
		_portLayer.InvalidateVisual();
	}

	/// <summary>Attach the existing editor ScrollViewer to track its
	/// available viewport and allow background-drag panning.</summary>
	public void AttachScrollViewer(ScrollViewer viewer)
	{
		ArgumentNullException.ThrowIfNull(viewer);
		_scrollViewer = viewer;
		viewer.SizeChanged += (_, _) => UpdateViewportSize();
		// The ScrollViewer may only clamp its native Offset after the
		// next layout pass. That must never change the actual camera.
		viewer.LayoutUpdated += (_, _) =>
		{
			if (!_rangeLayoutPending)
				return;
			_rangeLayoutPending = false;
			ProjectCameraOntoScrollbars();
		};
		viewer.PropertyChanged += (_, change) =>
		{
			if (change.Property == ScrollViewer.ViewportProperty)
				UpdateViewportSize();
			else if (change.Property == ScrollViewer.OffsetProperty)
				OnScrollOffsetChanged();
		};
		UpdateViewportSize();
	}

	private void UpdateViewportSize()
	{
		if (_scrollViewer is null)
			return;
		Size viewport = _scrollViewer.Viewport;
		if (!double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height))
			return;
		if (Math.Abs(_viewportWidth - viewport.Width) < 0.1
			&& Math.Abs(_viewportHeight - viewport.Height) < 0.1)
			return;
		_viewportWidth = viewport.Width;
		_viewportHeight = viewport.Height;
		UpdateContentExtent();
	}

	private void UpdateContentExtent()
	{
		FmSynthNode[] nodes = _synth.Graph.Nodes.ToArray();
		List<IReadOnlyList<FmSynthRoutePoint>> edges =
			GetRenderedConnections()
				.Select(connection => (IReadOnlyList<FmSynthRoutePoint>)connection.Route)
				.ToList();
		// While a port is being dragged, its wire preview also participates
		// in the extents. The resulting range is still only a scrollbar
		// projection and never pushes the true camera back into range.
		if (_dragPort is FmSynthConnectionPort drag)
			edges.Add([drag.Center,
				new FmSynthRoutePoint(_dragPortPointer.X, _dragPortPointer.Y)]);
		FmSynthGraphBounds bounds = FmSynthGraphExtentGeometry.Calculate(
			nodes.Where(n => _positions.ContainsKey(n.Id))
				.Select(n => ToRect(_positions[n.Id], n)), edges);
		_contentBounds = bounds;
		if (!_hasCamera)
		{
			// Open with the same 20-DIP node/wire margin as before,
			// leaving the new 20% overscan to either side available.
			_camera = new FmSynthRoutePoint(bounds.Left, bounds.Top);
			_hasCamera = true;
		}
		_scrollRange = FmSynthGraphScrollRange.FromBounds(
			bounds, _viewportWidth, _viewportHeight);
		_graphRoot.Width = _scrollRange.Width;
		_graphRoot.Height = _scrollRange.Height;
		_rangeLayoutPending = _scrollViewer is not null;
		ProjectCameraOntoScrollbars();
	}

	/// <summary>
	/// The camera is the unrestricted absolute graph coordinate at the
	/// top-left of the viewport. Only the scrollbar value is clamped.
	/// </summary>
	private void SetCamera(FmSynthRoutePoint camera)
	{
		_camera = camera;
		_hasCamera = true;
		ProjectCameraOntoScrollbars();
	}

	private void ProjectCameraOntoScrollbars()
	{
		FmSynthRoutePoint projection = _scrollRange.Project(_camera);
		_lastProjectedOffset = new Vector(projection.X, projection.Y);
		if (_scrollViewer is not null)
		{
			_projectingScrollbars = true;
			try
			{
				_scrollViewer.Offset = _lastProjectedOffset;
			}
			finally
			{
				_projectingScrollbars = false;
			}
		}
		UpdateSceneTransform();
	}

	private void OnScrollOffsetChanged()
	{
		if (_scrollViewer is null)
			return;
		Vector offset = _scrollViewer.Offset;
		// ScrollViewer can asynchronously reclamp its old offset during
		// layout after the range changed. Such a projection update must
		// not move the camera; ordinary user scrollbar/wheel edits do.
		if (!_projectingScrollbars && !_rangeLayoutPending
			&& (Math.Abs(offset.X - _lastProjectedOffset.X) > 0.01
				|| Math.Abs(offset.Y - _lastProjectedOffset.Y) > 0.01))
		{
			_camera = _scrollRange.CameraAt(
				new FmSynthRoutePoint(offset.X, offset.Y));
			_hasCamera = true;
			_lastProjectedOffset = offset;
		}
		UpdateSceneTransform();
	}

	private void UpdateSceneTransform()
	{
		Vector offset = _scrollViewer?.Offset ?? default;
		// ScrollViewer draws the scene at (transform - offset).
		// Compensate for its bounded offset to display the independent,
		// potentially far-out-of-range camera exactly where requested.
		_graphScene.RenderTransform =
			new TranslateTransform(offset.X - _camera.X,
				offset.Y - _camera.Y);
	}

	private Point PointerInWindow(PointerEventArgs e)
	{
		TopLevel? window = TopLevel.GetTopLevel(this);
		return window is null ? e.GetPosition(this) : e.GetPosition(window);
	}

	public void SetSelectedNode(int? nodeId)
	{
		if (_selectedNodeId == nodeId && _selectedConnection is null)
			return;
		_selectedNodeId = nodeId;
		_selectedConnection = null;
		ApplySelectionVisuals();
		_connectionLayer.InvalidateVisual();
	}

	/// <summary>The node/connection selection space is exclusive.</summary>
	public void SetSelectedConnection(FmSynthConnectionCandidate? connection)
	{
		if (_selectedConnection == connection && _selectedNodeId is null)
			return;
		_selectedConnection = connection;
		_selectedNodeId = null;
		ApplySelectionVisuals();
		_connectionLayer.InvalidateVisual();
	}

	private void SelectConnection(FmSynthConnectionCandidate connection)
	{
		Focus();
		SetSelectedConnection(connection);
		_connectionSelected?.Invoke(connection);
	}

	/// <summary>Suggest a free editor position for a newly inserted node.
	/// This is evaluated BEFORE insertion, against the existing scene.
	/// The 80-DIP search height covers every currently available initial
	/// node shape; after insertion its true height governs reveal/extents.
	/// </summary>
	public FmSynthRoutePoint FindUnobstructedSpawnPosition()
	{
		FmSynthLayoutRect[] bodies = _synth.Graph.Nodes
			.Where(node => _positions.ContainsKey(node.Id))
			.Select(node => ToRect(_positions[node.Id], node))
			.ToArray();
		IReadOnlyList<FmSynthRoutePoint>[] edges = GetRenderedConnections()
			.Select(connection => connection.Route)
			.ToArray();
		return FmSynthGraphNodePlacement.FindUnobstructedPosition(
			bodies, edges, _camera, _viewportWidth, _viewportHeight,
			NodeWidth, FmSynthGraphNodePlacement.InitialNodeHeight);
	}

	/// <summary>Only a newly added node requests a camera adjustment.
	/// Normal content/range updates always preserve the existing camera.
	/// </summary>
	public void RevealNewNode(int nodeId)
	{
		// A new extent can change scrollbar visibility and thus the viewport.
		// Resolve that layout before determining whether the buffer is visible.
		_scrollViewer?.UpdateLayout();
		UpdateViewportSize();
		if (_viewportWidth <= 0 || _viewportHeight <= 0
			|| !_positions.TryGetValue(nodeId, out FmSynthNodePosition position))
			return;
		FmSynthNode? node = _synth.Graph.Nodes
			.FirstOrDefault(candidate => candidate.Id == nodeId);
		if (node is null)
			return;
		FmSynthRoutePoint camera = FmSynthGraphNodePlacement.CameraToReveal(
			_camera, ToRect(position, node), _viewportWidth, _viewportHeight);
		if (camera != _camera)
			SetCamera(camera);
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
			FmSynthLayoutRect targetRect = ToRect(targetPosition, target);
			foreach ((int inputIndex, int sourceId) in
				FmSynthConnectionPorts.GetConnectedInputs(target))
			{
				if (!_positions.TryGetValue(
					sourceId,
					out FmSynthNodePosition sourcePosition))
				{
					continue;
				}
				FmSynthNode sourceNode = nodes.First(node => node.Id == sourceId);
				FmSynthLayoutRect sourceRect = ToRect(sourcePosition, sourceNode);
				FmSynthLayoutRect[] obstacles = nodes
					.Where(node => node.Id != sourceId
						&& node.Id != target.Id)
					.Select(node => ToRect(_positions[node.Id], node))
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
		Pen connectionPen = new(
			new SolidColorBrush(Color.FromRgb(125, 175, 235)),
			FmSynthWaypointGeometry.WireStrokeWidthDips);
		Pen selectedPen = new(
			Brushes.White, FmSynthWaypointGeometry.SelectedWireStrokeWidthDips);
		foreach (FmSynthRenderedConnection connection in GetRenderedConnections())
		{
			Pen pen = _selectedConnection == connection.Connection
				? selectedPen : connectionPen;
			IReadOnlyList<FmSynthRoutePoint> route = connection.Route;
			for (int pointIndex = 1;
				pointIndex < route.Count;
				pointIndex++)
			{
				context.DrawLine(
					pen,
					new Point(
						route[pointIndex - 1].X,
						route[pointIndex - 1].Y),
					new Point(
						route[pointIndex].X,
						route[pointIndex].Y));
			}
			DrawArrowhead(context, pen, route);

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
		// A socket near a node boundary can be hit *outside* the node
		// body. The root owns that gesture; body movement is never
		// initiated here. Repeated port presses work like body port presses.
		if (FindPortAt(pointer) is FmSynthConnectionPort borderPort)
		{
			Focus();
			SetSelectedNode(borderPort.NodeId);
			_selected(borderPort.NodeId);
			_dragPort = borderPort;
			_dragPortPointer = pointer;
			_hoverNodeId = borderPort.NodeId;
			_rootPortDrag = true;
			_portLayer.InvalidateVisual();
			e.Pointer.Capture((IInputElement)sender!);
			e.Handled = true;
			return;
		}

		// Wire coordinates are rendered by _connectionLayer, not _canvas.
		// Resolve pointer positions in the *same* visual that draws the
		// segments; this avoids assuming sibling layer origins remain
		// perfectly identical after scaling, scrolling or layout changes.
		Point wirePointer = e.GetPosition(_connectionLayer);
		FmSynthRoutePoint position = new(wirePointer.X, wirePointer.Y);
		FmSynthRenderedConnection[] connections = GetRenderedConnections();
		FmSynthWaypointHit? waypoint = FmSynthWaypointGeometry.HitWaypoint(
			connections, position, RenderScaling);
		// A double-click deletes a waypoint only when it actually hits
		// one. Repeated empty-background drags may arrive with ClickCount=2;
		// they must remain eligible to pan the unbounded camera.
		if (e.ClickCount == 2 && waypoint is FmSynthWaypointHit hit)
		{
			SelectConnection(hit.Connection);
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
			return;
		}

		if (waypoint is FmSynthWaypointHit old)
		{
			SelectConnection(old.Connection);
			FmSynthRenderedConnection wire =
				connections.First(item => item.Connection == old.Connection);
			_waypointDrag = new WaypointDrag
			{
				Connection = old.Connection,
				Waypoints = [.. wire.Waypoints],
				Index = old.WaypointIndex,
				InitialPoint = wire.Waypoints[old.WaypointIndex],
				CurrentPoint = wire.Waypoints[old.WaypointIndex],
				PointerStart = PointerInWindow(e),
			};
		}
		else if (FmSynthWaypointGeometry.HitConnection(
			connections, position, RenderScaling, _selectedConnection)
			is FmSynthConnectionLineHit line)
		{
			SelectConnection(line.Connection);
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
				PointerStart = PointerInWindow(e),
			};
		}
		else
		{
			// The background pans without limit on either axis, even
			// when neither scrollbar has a movable range.
			_isBackgroundPanning = true;
			_panPointerStart = PointerInWindow(e);
			_panCameraStart = _camera;
			e.Pointer.Capture((IInputElement)sender!);
			e.Handled = true;
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
		if (_isBackgroundPanning)
		{
			Point current = PointerInWindow(e);
			Vector delta = current - _panPointerStart;
			SetCamera(new FmSynthRoutePoint(
				_panCameraStart.X - delta.X,
				_panCameraStart.Y - delta.Y));
			e.Handled = true;
			return;
		}
		if (_rootPortDrag)
		{
			Point current = e.GetPosition(_canvas);
			_dragPortPointer = current;
			UpdateContentExtent();
			UpdateHover(current);
			_portLayer.InvalidateVisual();
			e.Handled = true;
			return;
		}
		if (_waypointDrag is not WaypointDrag drag)
		{
			// Also reveal corner-mounted append ports while hovering just
			// OUTSIDE the node's border (where PointerMoved targets root).
			UpdateHover(e.GetPosition(_canvas));
			return;
		}
		Point pointer = PointerInWindow(e);
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
		UpdateContentExtent();
		_connectionLayer.InvalidateVisual();
		e.Handled = true;
	}

	private void OnWirePointerReleased(
		object? sender,
		PointerReleasedEventArgs e)
	{
		_ = sender;
		if (_isBackgroundPanning)
		{
			_isBackgroundPanning = false;
			e.Pointer.Capture(null);
			e.Handled = true;
			return;
		}
		if (_rootPortDrag)
		{
			_rootPortDrag = false;
			if (_dragPort is FmSynthConnectionPort start)
				FinishPortDrag(start, e);
			else
				e.Pointer.Capture(null);
			return;
		}
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
		// Only wire/waypoint previews can have changed the extent. A plain
		// background pan changed the camera alone: rebuilding its range here
		// would leave a pending-layout guard that can swallow the user's next
		// scrollbar edit before another layout pass.
		bool previewChangedExtent = _waypointDrag is not null || _rootPortDrag;
		_waypointDrag = null;
		_isBackgroundPanning = false;
		if (_rootPortDrag)
		{
			_rootPortDrag = false;
			_dragPort = null;
			_hoverNodeId = null;
			_portLayer.InvalidateVisual();
		}
		if (previewChangedExtent)
			UpdateContentExtent();
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
				FmSynthConnectionPorts.GetPorts(node, ToRect(position, node)))
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

	/// <summary>
	/// Find the closest actual socket in the 8-physical-pixel node-edge
	/// region. Restrict body-originated presses to that body's own sockets:
	/// a close adjacent node must not steal the body drag.
	/// </summary>
	private FmSynthConnectionPort? FindPortAt(
		Point pointer, int? owningNodeId = null)
	{
		FmSynthRoutePoint point = new(pointer.X, pointer.Y);
		double scaling = RenderScaling;
		double radius = Math.Max(FmSynthConnectionPorts.HitRadius,
			FmSynthNodeHandleGeometry.ProximityPixels / scaling);
		double bestSquared = radius * radius;
		FmSynthConnectionPort? best = null;
		foreach (FmSynthNode node in _synth.Graph.Nodes)
		{
			if (owningNodeId.HasValue && node.Id != owningNodeId.Value)
				continue;
			if (!_positions.TryGetValue(
				node.Id, out FmSynthNodePosition position))
				continue;
			FmSynthLayoutRect rect = ToRect(position, node);
			if (!FmSynthNodeHandleGeometry.IsNearBoundary(
				rect, point, scaling))
				continue;
			foreach (FmSynthConnectionPort port in
				FmSynthConnectionPorts.GetPorts(node, rect))
			{
				double dx = point.X - port.Center.X;
				double dy = point.Y - port.Center.Y;
				double squared = dx * dx + dy * dy;
				if (squared > bestSquared || best is not null
					&& squared == bestSquared)
					continue;
				bestSquared = squared;
				best = port;
			}
		}
		return best;
	}

	private int? FindNodeBorderAt(Point pointer)
	{
		FmSynthRoutePoint point = new(pointer.X, pointer.Y);
		double scaling = RenderScaling;
		double nearest = double.PositiveInfinity;
		int? best = null;
		foreach ((int nodeId, FmSynthNodePosition position) in _positions)
		{
			FmSynthNode node = _synth.Graph.Nodes
				.First(item => item.Id == nodeId);
			FmSynthLayoutRect rect = ToRect(position, node);
			if (!FmSynthNodeHandleGeometry.IsNearBoundary(
				rect, point, scaling))
				continue;
			double distance =
				FmSynthNodeHandleGeometry.DistanceToBoundary(rect, point);
			if (distance < nearest
				|| (distance == nearest && nodeId < best))
			{
				nearest = distance;
				best = nodeId;
			}
		}
		return best;
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
		TextBlock title = new()
		{
			Text = GraphNodeTitle(node),
			FontWeight = FontWeight.SemiBold,
			Foreground = Brushes.White,
			TextTrimming = TextTrimming.CharacterEllipsis,
		};
		ToolTip.SetTip(title, title.Text);

		TextBlock detail = new()
		{
			Text = NodeDetail(node),
			Foreground = new SolidColorBrush(Color.FromRgb(205, 205, 215)),
			TextWrapping = TextWrapping.Wrap,
			FontSize = 11,
		};
		StackPanel content = new()
		{
			Spacing = 4,
			// Keep a right-side lane available for the common Out label.
			Margin = new Thickness(8, 6, 40, 6),
		};
		if (node is FmFilterNode or FmOperatorNode
			or FmOscillatorNode or FmSoundSourceNode or FmMultiNode)
		{
			// Graph rows and connection centres use the same DIP constants.
			content.Spacing = 0;
			title.VerticalAlignment = VerticalAlignment.Center;
			Grid header = new()
			{
				Height = FmSynthConnectionPorts.FilterHeaderHeight,
			};
			header.Children.Add(title);
			content.Children.Add(header);

			if (node is FmFilterNode filter)
			{
				foreach (FmFilterInputPort port in filter.GetPorts())
				{
					string label = port.IsAudio ? "Input"
						: $"{filter.Configuration!.Definition.GetStage(port.StageId).Name}."
							+ port.ParameterName;
					content.Children.Add(PortLabelRow(
						label, "FilterInputPortLabelRow", Brushes.LightSkyBlue));
				}
			}
			else if (node is FmOperatorNode operation)
			{
				// The mode gets its own row BEFORE the connected inputs.
				content.Children.Add(PortLabelRow(
					$"Mode: {operation.Operation}",
					"OperatorModeRow", Brushes.LightGray,
					FmSynthConnectionPorts.OperatorModeRowHeight));
				foreach (int sourceId in operation.InputNodeIds)
				{
					FmSynthNode? source = _synth.Graph.Nodes
						.FirstOrDefault(other => other.Id == sourceId);
					string label = source is null
						? $"#{sourceId} (missing)"
						: GraphNodeTitle(source);
					content.Children.Add(PortLabelRow(
						label, "OperatorInputPortLabelRow", Brushes.LightSkyBlue));
				}
			}
			else
			{
				// Preserve the original waveform, sample or voice status
				// as an independent row; never mislabel it as the socket.
				detail.TextWrapping = TextWrapping.NoWrap;
				detail.TextTrimming = TextTrimming.CharacterEllipsis;
				detail.VerticalAlignment = VerticalAlignment.Center;
				ToolTip.SetTip(detail, detail.Text);
				content.Children.Add(new Border
				{
					Name = "SingleInputStatusRow",
					Height = FmSynthConnectionPorts.StatusRowHeight,
					Child = detail,
				});
				content.Children.Add(PortLabelRow(
					node is FmMultiNode ? "In" : "Pitch",
					"SingleInputPortLabelRow", Brushes.LightSkyBlue));
			}
		}
		else
		{
			content.Children.Add(title);
			content.Children.Add(detail);
		}

		// Output is always at BASE node-height midpoint (36 DIP below
		// its top), never the midpoint of a taller Filter/Operator.
		TextBlock outputLabel = new()
		{
			Name = "NodeOutputPortLabel",
			Text = "Out",
			FontSize = 10,
			Foreground = Brushes.LightGreen,
			Height = FmSynthConnectionPorts.FilterHeaderHeight,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0,
				FmSynthConnectionPorts.OutputCenterOffset
					- FmSynthConnectionPorts.FilterBorderThickness
					- FmSynthConnectionPorts.FilterHeaderHeight / 2.0
					+ FmSynthConnectionPorts.OutputLabelTextDrop,
				2, 0),
			IsHitTestVisible = false,
		};
		Grid body = new();
		body.Children.Add(content);
		body.Children.Add(outputLabel);

		Border border = new()
		{
			Width = NodeWidth,
			Height = FmSynthConnectionPorts.GetNodeHeight(node),
			CornerRadius = new CornerRadius(5),
			BorderThickness = new Thickness(
				FmSynthConnectionPorts.FilterBorderThickness),
			Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
			Child = body,
		};

		border.PointerExited += (_, e) =>
		{
			// Keep the handles displayed when moving from inside the
			// body to the adjacent outside 8-pixel zone. The root will
			// continue tracking once the pointer leaves the border.
			if (_dragPort is null && _dragNodeId is null)
				UpdateHover(e.GetPosition(_canvas));
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

	private string GraphNodeTitle(FmSynthNode node)
		=> $"#{node.Id} {NodeTypeName(node)}"
			+ (node.Id == _synth.Graph.OutputNodeId ? "  [OUTPUT]" : "");

	private static Border PortLabelRow(
		string label, string name, IBrush color,
		double height = FmSynthConnectionPorts.FilterInputRowHeight)
	{
		TextBlock text = new()
		{
			Text = label,
			FontSize = 10,
			Foreground = color,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center,
		};
		ToolTip.SetTip(text, label);
		return new Border
		{
			Name = name,
			Height = height,
			Child = text,
		};
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

		// Node selection must leave keyboard focus on the graph so that
		// Delete invokes node removal, not the last inspector text field.
		Focus();
		SetSelectedNode(nodeId);
		_selected(nodeId);

		Point pointer = e.GetPosition(_canvas);
		if (FindPortAt(pointer, nodeId)
			is FmSynthConnectionPort port)
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
		_dragPointerStart = PointerInWindow(e);
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
			UpdateContentExtent();
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
		Point onWindow = PointerInWindow(e);
		// Coordinates are signed: a node may move past either axis origin.
		double x = _dragOriginal.X + onWindow.X - _dragPointerStart.X;
		double y = _dragOriginal.Y + onWindow.Y - _dragPointerStart.Y;
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
		UpdateContentExtent();
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
			FinishPortDrag(start, e);
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

	private void FinishPortDrag(
		FmSynthConnectionPort start, PointerReleasedEventArgs e)
	{
		FmSynthConnectionPort? end = FindPortAt(e.GetPosition(_canvas));
		_dragPort = null;
		_hoverNodeId = null;
		UpdateContentExtent();
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
			UpdateContentExtent();
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
		UpdateContentExtent();
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
		FmSynthNodePosition position, FmSynthNode node)
		=> new(
			position.X,
			position.Y,
			NodeWidth,
			FmSynthConnectionPorts.GetNodeHeight(node));

	private static string NodeTypeName(
		FmSynthNode node)
		=> node switch
		{
			FmConstantNode => "Constant",
			FmNoiseNode => "White Noise",
			FmOscillatorNode => "Oscillator",
			FmSoundSourceNode => "Source",
			FmMultiNode => "Multi",
			FmEnvelopeNode => "Envelope",
			FmFilterNode => "Filter",
			FmOperatorNode => "Operator",
			_ => node.GetType().Name,
		};

	private static string NodeDetail(
		FmSynthNode node)
		=> node switch
		{
			FmNoiseNode => "Native-rate, unfiltered white noise",
			FmConstantNode constant =>
				constant.Value.ToString(
					"0.###",
					CultureInfo.InvariantCulture),
			FmOscillatorNode oscillator =>
				$"{oscillator.Waveform}, "
				+ $"{oscillator.FrequencyHz.ToString("0.###", CultureInfo.InvariantCulture)} Hz",
			FmMultiNode multi => $"{multi.Distribution}, {multi.GetFrequencyRatios().Length} voices",
			FmSoundSourceNode source =>
				$"Source <{source.SourceId.Value}> @ "
				+ $"{source.FrequencyHz.ToString("0.###", CultureInfo.InvariantCulture)} Hz"
				+ (source.Constant ? " (Constant)" : ""),
			FmEnvelopeNode envelope =>
				$"Envelope <{envelope.EnvelopeId.Value}>",
			FmFilterNode filter =>
				filter.Configuration is null ? "Input → bypass (unassigned)"
					: $"Filter <{filter.Configuration.DefinitionId.Value}>, "
						+ $"{filter.GetPorts().Count - 1} modulation ports",
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

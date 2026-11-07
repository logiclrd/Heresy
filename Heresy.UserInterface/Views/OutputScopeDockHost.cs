using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using Heresy.Render.Realtime;

namespace Heresy.UserInterface.Views;

public enum OutputScopeDockEdge
{
	Left,
	Top,
	Right,
	Bottom,
}

/// <summary>
/// Four-edge dock around the existing main-window content. Its header can
/// be dragged to any edge; the adjacent GridSplitter resizes the pane.
/// </summary>
public sealed class OutputScopeDockHost : Grid
{
	private readonly Control _document;
	private readonly OutputScopePane _pane;
	private bool _scopeVisible;
	private bool _dragging;
	private Point _dragStart;
	private double _horizontalThickness = 200.0;
	private double _verticalThickness = 320.0;

	public OutputScopeDockHost(
		Control document, AudioOutputWaveformCapture capture)
	{
		_document = document ?? throw new ArgumentNullException(nameof(document));
		_pane = new OutputScopePane(capture);
		_pane.CloseRequested += (_, _) => SetScopeVisible(false);
		_pane.DragHandle.PointerPressed += (_, e) =>
		{
			if (!e.GetCurrentPoint(_pane.DragHandle)
				.Properties.IsLeftButtonPressed)
				return;
			_dragging = true;
			_dragStart = e.GetPosition(this);
			e.Pointer.Capture(_pane.DragHandle);
			e.Handled = true;
		};
		_pane.DragHandle.PointerReleased += (_, e) =>
		{
			if (!_dragging)
				return;
			_dragging = false;
			Point drop = e.GetPosition(this);
			e.Pointer.Capture(null);
			if (Math.Abs(drop.X - _dragStart.X)
				+ Math.Abs(drop.Y - _dragStart.Y) > 8.0)
				DockTo(NearestEdge(drop, Bounds.Size));
			e.Handled = true;
		};
		_pane.DragHandle.PointerCaptureLost += (_, _) => _dragging = false;
		RebuildLayout();
	}

	public bool ScopeVisible => _scopeVisible;
	public OutputScopeDockEdge Edge { get; private set; } =
		OutputScopeDockEdge.Bottom;
	public OutputScopePane ScopePane => _pane;
	public event EventHandler? ScopeVisibilityChanged;

	public void SetScopeVisible(bool visible)
	{
		if (_scopeVisible == visible)
			return;
		SaveThickness();
		_scopeVisible = visible;
		RebuildLayout();
		_pane.SetActive(visible);
		ScopeVisibilityChanged?.Invoke(this, EventArgs.Empty);
	}

	public void DockTo(OutputScopeDockEdge edge)
	{
		if (!Enum.IsDefined(edge))
			throw new ArgumentOutOfRangeException(nameof(edge));
		if (Edge == edge)
			return;
		SaveThickness();
		Edge = edge;
		RebuildLayout();
	}

	public void Shutdown() => _pane.Shutdown();

	public static OutputScopeDockEdge NearestEdge(Point position, Size size)
	{
		// Choose the closest of all four edges, even when the released
		// pointer is inside the central document region.
		double best = Math.Abs(position.X);
		OutputScopeDockEdge result = OutputScopeDockEdge.Left;
		void Check(double distance, OutputScopeDockEdge edge)
		{
			if (distance >= best)
				return;
			best = distance;
			result = edge;
		}
		Check(Math.Abs(position.Y), OutputScopeDockEdge.Top);
		Check(Math.Abs(size.Width - position.X), OutputScopeDockEdge.Right);
		Check(Math.Abs(size.Height - position.Y), OutputScopeDockEdge.Bottom);
		return result;
	}

	private void SaveThickness()
	{
		if (!_scopeVisible)
			return;
		bool horizontal = Edge is OutputScopeDockEdge.Top
			or OutputScopeDockEdge.Bottom;
		int track = Edge is OutputScopeDockEdge.Top
			or OutputScopeDockEdge.Left ? 0 : 2;
		if (horizontal && RowDefinitions.Count == 3)
		{
			double height = RowDefinitions[track].Height.Value;
			if (height >= 90.0)
				_horizontalThickness = height;
		}
		else if (!horizontal && ColumnDefinitions.Count == 3)
		{
			double width = ColumnDefinitions[track].Width.Value;
			if (width >= 150.0)
				_verticalThickness = width;
		}
	}

	private void RebuildLayout()
	{
		Children.Clear();
		RowDefinitions.Clear();
		ColumnDefinitions.Clear();
		if (!_scopeVisible)
		{
			Children.Add(_document);
			return;
		}

		bool horizontal = Edge is OutputScopeDockEdge.Top
			or OutputScopeDockEdge.Bottom;
		bool first = Edge is OutputScopeDockEdge.Top
			or OutputScopeDockEdge.Left;
		int scopeIndex = first ? 0 : 2;
		int documentIndex = first ? 2 : 0;
		const double splitterThickness = 6.0;
		GridSplitter splitter = new()
		{
			Background = Brushes.Gray,
			ResizeDirection = horizontal
				? GridResizeDirection.Rows
				: GridResizeDirection.Columns,
		};
		if (horizontal)
		{
			RowDefinitions.Add(first
				? new RowDefinition(new GridLength(_horizontalThickness))
				{ MinHeight = 90.0 }
				: new RowDefinition(new GridLength(1, GridUnitType.Star)));
			RowDefinitions.Add(
				new RowDefinition(new GridLength(splitterThickness)));
			RowDefinitions.Add(first
				? new RowDefinition(new GridLength(1, GridUnitType.Star))
				: new RowDefinition(new GridLength(_horizontalThickness))
				{ MinHeight = 90.0 });
			Grid.SetRow(_pane, scopeIndex);
			Grid.SetRow(_document, documentIndex);
			Grid.SetRow(splitter, 1);
		}
		else
		{
			ColumnDefinitions.Add(first
				? new ColumnDefinition(new GridLength(_verticalThickness))
				{ MinWidth = 150.0 }
				: new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
			ColumnDefinitions.Add(
				new ColumnDefinition(new GridLength(splitterThickness)));
			ColumnDefinitions.Add(first
				? new ColumnDefinition(new GridLength(1, GridUnitType.Star))
				: new ColumnDefinition(new GridLength(_verticalThickness))
				{ MinWidth = 150.0 });
			Grid.SetColumn(_pane, scopeIndex);
			Grid.SetColumn(_document, documentIndex);
			Grid.SetColumn(splitter, 1);
		}
		Children.Add(_document);
		Children.Add(_pane);
		Children.Add(splitter);
	}
}

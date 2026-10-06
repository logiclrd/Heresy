using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.ViewModels;

namespace Heresy.UserInterface.Views;

public sealed class PatternEffectStripControl : UserControl
{
	private const double TabWidth = 52;
	private const double RevealWidth = 5;
	private const double EdgeWidth = 16;
	private const double ScrollStep = 12;

	private static readonly Color[] Palette =
	[
		Color.FromRgb(73, 91, 171),
		Color.FromRgb(0, 121, 107),
		Color.FromRgb(123, 31, 162),
		Color.FromRgb(216, 67, 21),
		Color.FromRgb(57, 73, 171),
		Color.FromRgb(93, 64, 55),
		Color.FromRgb(69, 90, 100),
		Color.FromRgb(85, 139, 47),
		Color.FromRgb(158, 157, 36),
		Color.FromRgb(0, 105, 92),
		Color.FromRgb(94, 53, 177),
		Color.FromRgb(198, 40, 40),
	];

	private readonly Canvas _canvas = new();
	private readonly double _viewportWidth;
	private readonly double _collapsedWidth;
	private readonly double _rowHeight;
	private readonly Action _requestExpansion;
	private readonly Action<int, ExpandedEffectField> _requestSelection;
	private readonly Action<int> _requestEditNative;
	private readonly Action<int, bool> _requestInsertNative;
	private readonly Action _requestInsertNativeEmpty;
	private readonly Action _requestCopyStack;
	private readonly Action _requestPasteStack;
	private readonly Action<int> _requestDelete;
	private readonly Action<int, bool> _requestInsert;
	private readonly Action<int, int> _requestReorder;
	private readonly DispatcherTimer _scrollTimer;

	private IReadOnlyList<PatternEffectViewModel> _effects =
		Array.Empty<PatternEffectViewModel>();
	private bool _expanded;
	private bool _keyboardActive;
	private PatternCellField _collapsedField;
	private int _selectedEffectIndex = -1;
	private ExpandedEffectField _selectedExpandedField;
	private double _scrollOffset;
	private int _scrollDirection;
	private int _dragSourceIndex = -1;

	public PatternEffectStripControl(
		double viewportWidth,
		double collapsedWidth,
		double rowHeight,
		Action requestExpansion,
		Action<int, ExpandedEffectField> requestSelection,
		Action<int> requestEditNative,
		Action<int, bool> requestInsertNative,
		Action requestInsertNativeEmpty,
		Action requestCopyStack,
		Action requestPasteStack,
		Action<int> requestDelete,
		Action<int, bool> requestInsert,
		Action<int, int> requestReorder)
	{
		if (!(viewportWidth > 0))
			throw new ArgumentOutOfRangeException(nameof(viewportWidth));
		if (!(collapsedWidth > 0) || collapsedWidth > viewportWidth)
			throw new ArgumentOutOfRangeException(nameof(collapsedWidth));
		if (!(rowHeight > 0))
			throw new ArgumentOutOfRangeException(nameof(rowHeight));

		_viewportWidth = viewportWidth;
		_collapsedWidth = collapsedWidth;
		_rowHeight = rowHeight;
		_requestExpansion =
			requestExpansion ?? throw new ArgumentNullException(nameof(requestExpansion));
		_requestSelection =
			requestSelection ?? throw new ArgumentNullException(nameof(requestSelection));
		_requestEditNative =
			requestEditNative ?? throw new ArgumentNullException(nameof(requestEditNative));
		_requestInsertNative =
			requestInsertNative ?? throw new ArgumentNullException(nameof(requestInsertNative));
		_requestInsertNativeEmpty =
			requestInsertNativeEmpty ?? throw new ArgumentNullException(nameof(requestInsertNativeEmpty));
		_requestCopyStack =
			requestCopyStack ?? throw new ArgumentNullException(nameof(requestCopyStack));
		_requestPasteStack =
			requestPasteStack ?? throw new ArgumentNullException(nameof(requestPasteStack));
		_requestDelete =
			requestDelete ?? throw new ArgumentNullException(nameof(requestDelete));
		_requestInsert =
			requestInsert ?? throw new ArgumentNullException(nameof(requestInsert));
		_requestReorder =
			requestReorder ?? throw new ArgumentNullException(nameof(requestReorder));

		Width = collapsedWidth;
		Height = rowHeight;
		HorizontalAlignment = HorizontalAlignment.Right;
		Background = Brushes.Transparent;
		VerticalAlignment = VerticalAlignment.Center;
		ClipToBounds = true;
		Content = _canvas;

		_scrollTimer =
			new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(80),
			};
		_scrollTimer.Tick += (_, _) =>
		{
			if (_scrollDirection != 0)
				ScrollBy(_scrollDirection * ScrollStep);
		};
	}

	public bool IsExpanded => _expanded;

	public void SetEffects(IReadOnlyList<PatternEffectViewModel> effects)
	{
		_effects =
			effects ?? throw new ArgumentNullException(nameof(effects));
		ContextMenu =
			_effects.Count == 0
				? BuildEmptyContextMenu()
				: null;
		_scrollOffset = ClampScroll(_scrollOffset);
		Rebuild();
	}

	public void SetVisualState(
		bool expanded,
		bool keyboardActive,
		PatternCellField collapsedField,
		int selectedEffectIndex,
		ExpandedEffectField selectedExpandedField)
	{
		if (_expanded && !expanded)
		{
			_scrollOffset = 0;
			StopScrolling();
			_dragSourceIndex = -1;
		}

		_expanded = expanded;
		Width = _expanded ? _viewportWidth : _collapsedWidth;
		_keyboardActive = keyboardActive;
		_collapsedField = collapsedField;
		_selectedEffectIndex = selectedEffectIndex;
		_selectedExpandedField = selectedExpandedField;
		_scrollOffset = ClampScroll(_scrollOffset);
		Rebuild();
	}

	public void ScrollBy(double delta)
	{
		if (!_expanded)
			return;

		double next = ClampScroll(_scrollOffset + delta);
		if (Math.Abs(next - _scrollOffset) < 0.01)
			return;

		_scrollOffset = next;
		Rebuild();
	}

	private void Rebuild()
	{
		_canvas.Children.Clear();

		if (_effects.Count == 0)
		{
			if (_keyboardActive
				&& _collapsedField is
					PatternCellField.EffectCommand
					or PatternCellField.EffectParameter)
			{
				AddEmptyCursorPlaceholder();
			}
			return;
		}

		double layoutWidth =
			_expanded ? _viewportWidth : _collapsedWidth;
		EffectStripLayoutItem[] layout =
			_expanded
				? EffectStripLayout.Expanded(
					_effects.Count,
					layoutWidth,
					TabWidth,
					EdgeWidth,
					_scrollOffset)
				: EffectStripLayout.Compact(
					_effects.Count,
					layoutWidth,
					TabWidth,
					RevealWidth);

		foreach (EffectStripLayoutItem item in layout)
		{
			Control tab = BuildTab(item.Index, item.Width);
			Canvas.SetLeft(tab, item.X);
			Canvas.SetTop(tab, 1);
			_canvas.Children.Add(tab);
		}

		if (_expanded
			&& EffectStripLayout.GetMaximumExpandedScroll(
				_effects.Count,
				_viewportWidth,
				TabWidth,
				EdgeWidth) > 0)
		{
			AddScrollEdge(isLeft: true);
			AddScrollEdge(isLeft: false);
		}
	}

	private Control BuildTab(int index, double width)
	{
		PatternEffectViewModel effect = _effects[index];
		Border outer =
			new()
			{
				Width = width,
				Height = Math.Max(1, _rowHeight - 2),
				CornerRadius = new CornerRadius(3),
				Background = BrushFor(effect.ColorKey),
				BorderThickness = new Thickness(
					IsWholeTabSelected(index, effect) ? 2 : 1),
				BorderBrush =
					IsWholeTabSelected(index, effect)
						? Brushes.White
						: new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
				ClipToBounds = true,
			};

		outer.Child =
			effect.IsTrackerStyle
				? BuildTrackerContent(index, effect, width)
				: BuildNativeContent(effect);
		outer.ContextMenu = BuildContextMenu(index, effect);

		outer.PointerEntered += (_, _) => _requestExpansion();
		outer.PointerPressed += (_, e) =>
		{
			PointerPointProperties properties =
				e.GetCurrentPoint(outer).Properties;
			if (properties.IsRightButtonPressed)
				return;

			if (!effect.IsTrackerStyle
				&& properties.IsLeftButtonPressed
				&& e.ClickCount >= 2)
			{
				_requestSelection(index, ExpandedEffectField.Native);
				_requestEditNative(index);
				e.Handled = true;
				return;
			}

			if (!_expanded)
			{
				ExpandedEffectField collapsedField;
				if (!effect.IsTrackerStyle)
				{
					collapsedField = ExpandedEffectField.Native;
				}
				else
				{
					Point point = e.GetPosition(outer);
					collapsedField =
						point.X < Math.Min(20, width * 0.4)
							? ExpandedEffectField.Command
							: ExpandedEffectField.Parameter;
				}

				_requestSelection(index, collapsedField);
				e.Handled = true;
				return;
			}

			ExpandedEffectField field;
			if (!effect.IsTrackerStyle)
			{
				field = ExpandedEffectField.Native;
			}
			else
			{
				Point point = e.GetPosition(outer);
				field = point.X < Math.Min(20, width * 0.4)
					? ExpandedEffectField.Command
					: ExpandedEffectField.Parameter;
			}

			_requestSelection(index, field);

			if (properties.IsLeftButtonPressed)
			{
				_dragSourceIndex = index;
				e.Pointer.Capture(outer);
			}
			e.Handled = true;
		};

		outer.PointerReleased += (_, e) =>
		{
			if (!_expanded || _dragSourceIndex < 0)
				return;

			int source = _dragSourceIndex;
			_dragSourceIndex = -1;
			Point point = e.GetPosition(_canvas);
			int target = GetDragTargetIndex(point.X);
			e.Pointer.Capture(null);

			if (target != source)
				_requestReorder(source, target);
			e.Handled = true;
		};

		ToolTip.SetTip(outer, effect.CompactText);
		return outer;
	}

	private ContextMenu BuildContextMenu(
		int index,
		PatternEffectViewModel effect)
	{
		MenuItem insertTrackerBefore =
			new() { Header = "Insert Tracker Slot Before" };
		insertTrackerBefore.Click += (_, _) =>
			_requestInsert(index, false);

		MenuItem insertTrackerAfter =
			new() { Header = "Insert Tracker Slot After" };
		insertTrackerAfter.Click += (_, _) =>
			_requestInsert(index, true);

		MenuItem insertNativeBefore =
			new() { Header = "Insert Native Effect Before..." };
		insertNativeBefore.Click += (_, _) =>
			_requestInsertNative(index, false);

		MenuItem insertNativeAfter =
			new() { Header = "Insert Native Effect After..." };
		insertNativeAfter.Click += (_, _) =>
			_requestInsertNative(index, true);

		MenuItem copyStack = new() { Header = "Copy Effect Stack" };
		copyStack.Click += (_, _) => _requestCopyStack();

		MenuItem pasteStack = new() { Header = "Paste Effect Stack" };
		pasteStack.Click += (_, _) => _requestPasteStack();

		MenuItem delete = new() { Header = "Delete" };
		delete.Click += (_, _) => _requestDelete(index);

		List<object> items = [];
		if (!effect.IsTrackerStyle)
		{
			MenuItem edit =
				new()
				{
					Header = "Edit Parameters...",
				};
			edit.Click += (_, _) =>
			{
				_requestSelection(index, ExpandedEffectField.Native);
				_requestEditNative(index);
			};
			items.Add(edit);
			items.Add(new Separator());
		}
		items.Add(copyStack);
		items.Add(pasteStack);
		items.Add(new Separator());
		items.Add(insertTrackerBefore);
		items.Add(insertTrackerAfter);
		items.Add(insertNativeBefore);
		items.Add(insertNativeAfter);
		items.Add(new Separator());
		items.Add(delete);

		return new ContextMenu
		{
			ItemsSource = items,
		};
	}

	private ContextMenu BuildEmptyContextMenu()
	{
		MenuItem pasteStack =
			new()
			{
				Header = "Paste Effect Stack",
			};
		pasteStack.Click += (_, _) =>
			_requestPasteStack();

		MenuItem insertNative =
			new()
			{
				Header = "Insert Native Effect...",
			};
		insertNative.Click += (_, _) =>
			_requestInsertNativeEmpty();

		return new ContextMenu
		{
			ItemsSource = new object[]
			{
				pasteStack,
				new Separator(),
				insertNative,
			},
		};
	}

	private int GetDragTargetIndex(double canvasX)
	{
		if (_effects.Count == 0)
			return 0;

		double maxScroll =
			EffectStripLayout.GetMaximumExpandedScroll(
				_effects.Count,
				_viewportWidth,
				TabWidth,
				EdgeWidth);
		double edge = maxScroll > 0
			? Math.Min(EdgeWidth, _viewportWidth / 2)
			: 0;
		double contentWidth = _effects.Count * TabWidth;
		double firstX = maxScroll > 0
			? edge
			: Math.Max(0, _viewportWidth - contentWidth);
		double contentX =
			canvasX + _scrollOffset - firstX;
		int index =
			(int)Math.Floor(
				(contentX + (TabWidth / 2))
					/ TabWidth);
		return Math.Clamp(index, 0, _effects.Count - 1);
	}

	private Control BuildTrackerContent(
		int index,
		PatternEffectViewModel effect,
		double width)
	{
		Grid grid = new();
		double commandWidth = Math.Min(20, width * 0.4);
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(commandWidth)));
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

		Border command =
			BuildTextSegment(
				effect.Command?.ToString() ?? string.Empty,
				IsTrackerSegmentSelected(
					index,
					ExpandedEffectField.Command));
		Border parameter =
			BuildTextSegment(
				effect.Parameter?.ToString("X2") ?? string.Empty,
				IsTrackerSegmentSelected(
					index,
					ExpandedEffectField.Parameter));

		Grid.SetColumn(command, 0);
		Grid.SetColumn(parameter, 1);
		grid.Children.Add(command);
		grid.Children.Add(parameter);
		return grid;
	}

	private Control BuildNativeContent(PatternEffectViewModel effect)
		=> new TextBlock
		{
			Text = effect.CompactText,
			Foreground = Brushes.White,
			FontSize = 11,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(3, 0),
		};

	private Border BuildTextSegment(string text, bool selected)
		=> new()
		{
			BorderThickness = selected
				? new Thickness(2)
				: new Thickness(0),
			BorderBrush = Brushes.White,
			Child = new TextBlock
			{
				Text = text,
				Foreground = Brushes.White,
				FontSize = 11,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
			},
		};

	private void AddEmptyCursorPlaceholder()
	{
		double width = _expanded ? _viewportWidth : _collapsedWidth;
		double x = Math.Max(0, width - TabWidth);
		Border placeholder =
			new()
			{
				Width = Math.Min(TabWidth, width),
				Height = Math.Max(1, _rowHeight - 2),
				BorderBrush = Brushes.White,
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(3),
				Opacity = 0.65,
			};

		Grid grid = new();
		grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(20)));
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		Border command =
			new()
			{
				BorderBrush = Brushes.White,
				BorderThickness =
					_collapsedField == PatternCellField.EffectCommand
						? new Thickness(2)
						: new Thickness(0),
			};
		Border parameter =
			new()
			{
				BorderBrush = Brushes.White,
				BorderThickness =
					_collapsedField == PatternCellField.EffectParameter
						? new Thickness(2)
						: new Thickness(0),
			};
		Grid.SetColumn(parameter, 1);
		grid.Children.Add(command);
		grid.Children.Add(parameter);
		placeholder.Child = grid;

		Canvas.SetLeft(placeholder, x);
		Canvas.SetTop(placeholder, 1);
		_canvas.Children.Add(placeholder);
	}

	private bool IsWholeTabSelected(
		int index,
		PatternEffectViewModel effect)
	{
		if (!_keyboardActive)
			return false;

		if (_expanded)
		{
			return index == _selectedEffectIndex
				&& _selectedExpandedField == ExpandedEffectField.Native;
		}

		if (_effects.Count > 1)
			return _collapsedField is
				PatternCellField.EffectCommand
				or PatternCellField.EffectParameter;

		return !effect.IsTrackerStyle
			&& _collapsedField is
				PatternCellField.EffectCommand
					or PatternCellField.EffectParameter;
	}

	private bool IsTrackerSegmentSelected(
		int index,
		ExpandedEffectField field)
	{
		if (!_keyboardActive)
			return false;

		if (_expanded)
		{
			return index == _selectedEffectIndex
				&& _selectedExpandedField == field;
		}

		if (_effects.Count != 1)
			return false;

		return (_collapsedField == PatternCellField.EffectCommand
				&& field == ExpandedEffectField.Command)
			|| (_collapsedField == PatternCellField.EffectParameter
				&& field == ExpandedEffectField.Parameter);
	}

	private void AddScrollEdge(bool isLeft)
	{
		int direction = isLeft ? -1 : 1;
		Border edge =
			new()
			{
				Width = EdgeWidth,
				Height = _rowHeight,
				Background =
					new SolidColorBrush(Color.FromArgb(210, 35, 35, 35)),
				Child = new TextBlock
				{
					Text = isLeft ? "‹" : "›",
					Foreground = Brushes.White,
					FontWeight = FontWeight.Bold,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center,
				},
			};

		edge.PointerEntered += (_, _) =>
		{
			_scrollDirection = direction;
			ScrollBy(direction * ScrollStep);
			_scrollTimer.Start();
		};
		edge.PointerExited += (_, _) => StopScrolling();
		edge.PointerPressed += (_, e) =>
		{
			ScrollBy(direction * ScrollStep);
			e.Handled = true;
		};

		Canvas.SetLeft(
			edge,
			isLeft ? 0 : Math.Max(0, _viewportWidth - EdgeWidth));
		Canvas.SetTop(edge, 0);
		_canvas.Children.Add(edge);
	}

	private void StopScrolling()
	{
		_scrollDirection = 0;
		_scrollTimer.Stop();
	}

	private double ClampScroll(double value)
	{
		double max =
			EffectStripLayout.GetMaximumExpandedScroll(
				_effects.Count,
				_viewportWidth,
				TabWidth,
				EdgeWidth);
		return Math.Clamp(value, 0, max);
	}

	private static IBrush BrushFor(int key)
		=> new SolidColorBrush(
			Palette[Math.Abs(key % Palette.Length)]);
}

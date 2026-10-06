using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.ViewModels;

namespace Heresy.UserInterface.Views;

public sealed class PatternEditorControl : UserControl
{
	private const double CellWidth = 190;
	private const double RowHeaderWidth = 54;
	private const double RowHeight = 28;
	private const double VolumeWidth = 34;
	private const double EffectWidth = 54;

	private readonly Window _owner;
	private readonly DocumentWorkspace _workspace;
	private readonly DataPatternDefinition _pattern;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly string _backLabel;
	private readonly TextBox _rowCount;
	private readonly TextBox _channelCount;
	private readonly TextBox _minorHighlight;
	private readonly TextBox _majorHighlight;
	private readonly ComboBox _noteSource;
	private readonly ComboBox _noteOctave;
	private readonly PatternNoteInputState _noteInputState;
	private readonly ScrollViewer _scroll;
	private readonly TextBlock _message;
	private readonly Dictionary<(int Row, int Channel), Border> _cellBorders = [];
	private readonly Dictionary<(int Row, int Channel), Border> _noteFields = [];
	private readonly Dictionary<(int Row, int Channel), TextBlock> _noteTexts = [];
	private readonly Dictionary<(int Row, int Channel), Border> _volumeFields = [];
	private readonly Dictionary<(int Row, int Channel), TextBlock> _volumeTexts = [];
	private readonly Dictionary<(int Row, int Channel), PatternEffectStripControl> _effectStrips = [];
	private readonly PatternVolumeInputState _volumeInput = new();

	private PatternEffectCursor _cursor =
		new(0, 0, PatternCellField.Note);
	private Grid? _patternGrid;
	private (int Row, int Channel)? _expandedCell;

	public PatternEditorControl(
		Window owner,
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		Action close,
		Action<string> changed,
		string backLabel = "← Document")
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		_pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
		_close = close ?? throw new ArgumentNullException(nameof(close));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		_backLabel = backLabel ?? throw new ArgumentNullException(nameof(backLabel));

		_rowCount = NumberBox(pattern.RowCount);
		_channelCount = NumberBox(pattern.ChannelCount);
		_minorHighlight = NumberBox(pattern.MinorHighlightRows);
		_majorHighlight = NumberBox(pattern.MajorHighlightRows);

		PatternSourceOption[] noteSources =
			PatternSourceCatalog.GetSources(workspace.Document);
		PatternSourceOption? defaultSource = null;
		foreach (PatternSourceOption source in noteSources)
		{
			if (source.Id != pattern.Id)
			{
				defaultSource = source;
				break;
			}
		}

		_noteInputState =
			new(
				defaultSource?.Id ?? Heresy.Core.Objects.ObjectId.None,
				baseOctave: 4);
		_noteSource =
			new ComboBox
			{
				ItemsSource = noteSources,
				SelectedItem = defaultSource,
				Width = 180,
			};
		_noteSource.SelectionChanged += (_, _) =>
		{
			_noteInputState.SourceId =
				_noteSource.SelectedItem is PatternSourceOption option
					? option.Id
					: Heresy.Core.Objects.ObjectId.None;
			FocusCursorCell();
		};

		_noteOctave =
			new ComboBox
			{
				ItemsSource = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 },
				SelectedItem = 4,
				Width = 58,
			};
		_noteOctave.SelectionChanged += (_, _) =>
		{
			if (_noteOctave.SelectedItem is int octave)
				_noteInputState.BaseOctave = octave;
			FocusCursorCell();
		};

		_scroll =
			new ScrollViewer
			{
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			};
		_message =
			new TextBlock
			{
				Text =
					"Arrow keys move the tracker cursor. Type notes directly in the note field; Enter opens detailed note editing or expands a stacked effect strip.",
				TextWrapping = TextWrapping.Wrap,
			};

		Content = BuildContent();
		RefreshGrid();
	}

	private Control BuildContent()
	{
		Button back = new() { Content = _backLabel, MinWidth = 100 };
		back.Click += (_, _) => _close();

		TextBlock title =
			new()
			{
				Text = _pattern.Name,
				FontSize = 20,
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			};

		Button applyLayout = new() { Content = "Apply layout" };
		applyLayout.Click += async (_, _) => await ApplyLayoutAsync();

		StackPanel layout =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
				VerticalAlignment = VerticalAlignment.Center,
			};
		layout.Children.Add(new TextBlock
		{
			Text = "Source",
			VerticalAlignment = VerticalAlignment.Center,
		});
		layout.Children.Add(_noteSource);
		layout.Children.Add(new TextBlock
		{
			Text = "Octave",
			VerticalAlignment = VerticalAlignment.Center,
		});
		layout.Children.Add(_noteOctave);
		layout.Children.Add(new TextBlock
		{
			Text = "Rows",
			VerticalAlignment = VerticalAlignment.Center,
		});
		layout.Children.Add(_rowCount);
		layout.Children.Add(new TextBlock
		{
			Text = "Channels",
			VerticalAlignment = VerticalAlignment.Center,
		});
		layout.Children.Add(_channelCount);
		layout.Children.Add(new TextBlock
		{
			Text = "Minor",
			VerticalAlignment = VerticalAlignment.Center,
		});
		layout.Children.Add(_minorHighlight);
		layout.Children.Add(new TextBlock
		{
			Text = "Major",
			VerticalAlignment = VerticalAlignment.Center,
		});
		layout.Children.Add(_majorHighlight);
		layout.Children.Add(applyLayout);

		DockPanel header =
			new()
			{
				Margin = new Thickness(10, 8),
			};
		DockPanel.SetDock(back, Dock.Left);
		DockPanel.SetDock(layout, Dock.Right);
		header.Children.Add(back);
		header.Children.Add(layout);
		header.Children.Add(title);

		DockPanel root = new();
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);

		Border messageBorder =
			new()
			{
				Padding = new Thickness(10, 3),
				Child = _message,
			};
		DockPanel.SetDock(messageBorder, Dock.Bottom);
		root.Children.Add(messageBorder);
		root.Children.Add(_scroll);

		root.PointerMoved += OnRootPointerMoved;
		root.AddHandler(
			InputElement.PointerPressedEvent,
			OnRootPointerPressed,
			RoutingStrategies.Tunnel,
			handledEventsToo: true);
		return root;
	}

	private async Task ApplyLayoutAsync()
	{
		try
		{
			int rows = ParseInt(_rowCount, "Rows");
			int channels = ParseInt(_channelCount, "Channels");
			int minor = ParseInt(_minorHighlight, "Minor highlight");
			int major = ParseInt(_majorHighlight, "Major highlight");

			if (PatternDocumentEditor.WouldDiscardCells(
				_pattern,
				rows,
				channels))
			{
				ConfirmDialog confirm =
					new(
						"Shrink pattern",
						"Shrinking this pattern will discard one or more populated cells outside the new dimensions. Continue?",
						"Shrink");
				if (!await confirm.ShowDialog<bool>(_owner))
					return;
			}

			PatternDocumentEditor.UpdateLayout(
				_workspace,
				_pattern,
				rows,
				channels,
				minor,
				major);
			_cursor.Clamp(rows, channels);
			RefreshGrid();
			_changed("Pattern layout updated");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void RefreshGrid()
	{
		CollapseVisualEffects(collapseCursor: true);
		_cellBorders.Clear();
		_noteFields.Clear();
		_noteTexts.Clear();
		_volumeFields.Clear();
		_volumeTexts.Clear();
		_effectStrips.Clear();

		Grid grid = new();
		_patternGrid = grid;
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(RowHeaderWidth)));
		for (int channel = 0; channel < _pattern.ChannelCount; channel++)
			grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(CellWidth)));

		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		AddText(grid, "Row", 0, 0, FontWeight.SemiBold);
		for (int channel = 0; channel < _pattern.ChannelCount; channel++)
		{
			AddText(
				grid,
				$"Channel {channel + 1}",
				0,
				channel + 1,
				FontWeight.SemiBold);
		}

		for (int row = 0; row < _pattern.RowCount; row++)
		{
			int gridRow = row + 1;
			grid.RowDefinitions.Add(
				new RowDefinition(new GridLength(RowHeight)));

			FontWeight rowWeight = GetRowWeight(row);
			AddText(
				grid,
				row.ToString("X2", CultureInfo.InvariantCulture),
				gridRow,
				0,
				rowWeight);

			for (int channel = 0; channel < _pattern.ChannelCount; channel++)
			{
				Border cell =
					BuildCell(
						row,
						channel,
						rowWeight);
				Grid.SetRow(cell, gridRow);
				Grid.SetColumn(cell, channel + 1);
				grid.Children.Add(cell);
			}
		}

		_scroll.Content = grid;
		RefreshCursorVisuals();
		FocusCursorCell();
	}

	private Border BuildCell(
		int row,
		int channel,
		FontWeight rowWeight)
	{
		PatternCellViewModel view =
			PatternCellViewModel.Create(
				_workspace.Document,
				_pattern,
				row,
				channel);

		TextBlock note =
			new()
			{
				Text = view.NoteText,
				FontWeight = rowWeight,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(5, 0),
				TextTrimming = TextTrimming.CharacterEllipsis,
			};
		Border noteField =
			new()
			{
				Height = RowHeight - 4,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Center,
				BorderBrush = Brushes.Transparent,
				BorderThickness = new Thickness(1),
				Child = note,
			};

		TextBlock volumeText =
			new()
			{
				Text = view.VolumeText,
				FontWeight = rowWeight,
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
			};
		Border volumeField =
			new()
			{
				Width = VolumeWidth,
				Height = RowHeight - 4,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Center,
				BorderBrush = Brushes.Transparent,
				BorderThickness = new Thickness(1),
				Child = volumeText,
			};

		PatternEffectStripControl effects =
			new(
				CellWidth - 2,
				EffectWidth,
				RowHeight - 2,
				() => ExpandVisualEffects(row, channel),
				(effectIndex, field) =>
					SelectExpandedEffect(
						row,
						channel,
						effectIndex,
						field),
				effectIndex =>
					DeleteEffectAt(
						row,
						channel,
						effectIndex),
				(effectIndex, after) =>
					InsertEffectAt(
						row,
						channel,
						effectIndex,
						after),
				(sourceIndex, targetIndex) =>
					ReorderEffectByDrag(
						row,
						channel,
						sourceIndex,
						targetIndex));
		effects.SetEffects(view.Effects);

		Grid content = new();
		content.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		content.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(VolumeWidth)));
		content.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(EffectWidth)));
		Grid.SetColumn(noteField, 0);
		Grid.SetColumn(volumeField, 1);
		Grid.SetColumn(effects, 0);
		Grid.SetColumnSpan(effects, 3);
		content.Children.Add(noteField);
		content.Children.Add(volumeField);
		content.Children.Add(effects);

		Border cell =
			new()
			{
				Width = CellWidth,
				Height = RowHeight,
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(1),
				ClipToBounds = true,
				Focusable = true,
				Child = content,
			};

		cell.PointerPressed += (_, e) =>
			OnCellPointerPressed(row, channel, cell, e);
		cell.KeyDown += async (_, e) =>
			await OnCellKeyDownAsync(row, channel, e);
		cell.TextInput += (_, e) =>
			OnCellTextInput(row, channel, e);

		_cellBorders[(row, channel)] = cell;
		_noteFields[(row, channel)] = noteField;
		_noteTexts[(row, channel)] = note;
		_volumeFields[(row, channel)] = volumeField;
		_volumeTexts[(row, channel)] = volumeText;
		_effectStrips[(row, channel)] = effects;
		return cell;
	}

	private void OnCellPointerPressed(
		int row,
		int channel,
		Border cell,
		PointerPressedEventArgs e)
	{
		if (e.Handled)
			return;

		Point point = e.GetPosition(cell);
		PatternCell? patternCell = _pattern.Grid[row, channel];
		int effectCount = patternCell?.Effects.Count ?? 0;
		bool singleTrackerStyle =
			effectCount != 1
				|| PatternEffectCodec.IsTrackerStyle(
					patternCell!.Effects[0]);
		PatternCellField field =
			PatternCellFieldGeometry.HitTest(
				point.X,
				CellWidth,
				VolumeWidth,
				EffectWidth,
				effectCount,
				singleTrackerStyle);

		if (_expandedCell == (row, channel))
			CollapseVisualEffects(collapseCursor: true);

		_volumeInput.Reset();
		_cursor.SetPosition(row, channel, field);
		cell.Focus();
		RefreshCursorVisuals();
		e.Handled = true;
	}

	private async Task OnCellKeyDownAsync(
		int row,
		int channel,
		KeyEventArgs e)
	{
		if (_cursor.Row != row || _cursor.Channel != channel)
			_cursor.SetPosition(row, channel, PatternCellField.Note);

		PatternCell? cell = _pattern.Grid[row, channel];
		(int Row, int Channel)? previouslyExpanded = _expandedCell;

		if ((e.KeyModifiers & KeyModifiers.Alt) != 0
			&& HandleAltEffectKey(row, channel, cell, e))
		{
			e.Handled = true;
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		KeyModifiers noteBlockingModifiers =
			KeyModifiers.Control
				| KeyModifiers.Alt
				| KeyModifiers.Meta;
		if (_cursor.Field == PatternCellField.Note
			&& (e.KeyModifiers & noteBlockingModifiers) == 0)
		{
			int editedRow = _cursor.Row;
			int editedChannel = _cursor.Channel;
			PatternNoteInputResult noteResult =
				PatternNoteKeyboardEditor.TypePhysical(
					_workspace,
					_pattern,
					_cursor,
					_noteInputState,
					e.PhysicalKey);

			if (noteResult.Handled)
			{
				if (noteResult.Rejected)
				{
					_message.Text =
						"Choose a current sound source before entering pitched notes.";
				}
				else if (noteResult.Changed)
				{
					RefreshCell(editedRow, editedChannel);
					_changed(
						$"Edited note at row {editedRow}, channel {editedChannel + 1}");
				}

				e.Handled = true;
				RefreshCursorVisuals();
				FocusCursorCell();
				return;
			}
		}

		switch (e.Key)
		{
			case Key.Left:
				_volumeInput.Reset();
				_cursor.MoveLeft(_pattern);
				e.Handled = true;
				break;

			case Key.Right:
				_volumeInput.Reset();
				_cursor.MoveRight(_pattern);
				e.Handled = true;
				break;

			case Key.Up:
				_volumeInput.Reset();
				_cursor.MoveUp(Math.Max(1, _pattern.RowCount));
				if (previouslyExpanded is not null)
					CollapseVisualEffects(collapseCursor: false);
				e.Handled = true;
				break;

			case Key.Down:
				_volumeInput.Reset();
				_cursor.MoveDown(Math.Max(1, _pattern.RowCount));
				if (previouslyExpanded is not null)
					CollapseVisualEffects(collapseCursor: false);
				e.Handled = true;
				break;

			case Key.Enter:
				if (_cursor.Field == PatternCellField.Note)
				{
					await EditCellAsync(row, channel);
					e.Handled = true;
					return;
				}

				if (_cursor.IsExpanded)
				{
					_cursor.HandleEnter(cell);
					CollapseVisualEffects(collapseCursor: false);
				}
				else if (cell is not null && cell.Effects.Count > 1)
				{
					_cursor.HandleEnter(cell);
					ExpandVisualEffects(row, channel);
				}
				else if (cell is not null
					&& cell.Effects.Count == 1
					&& !PatternEffectCodec.IsTrackerStyle(
						cell.Effects[0]))
				{
					// TODO: Enter and double-click should invoke the same
					// native-effect parameter editor command.
					_message.Text =
						"Native effect parameters are not directly editable here yet.";
				}
				e.Handled = true;
				break;

			default:
				return;
		}

		RefreshCursorVisuals();
		FocusCursorCell();
	}

	private void OnCellTextInput(
		int row,
		int channel,
		TextInputEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Text))
			return;

		if (_cursor.Row != row || _cursor.Channel != channel)
			_cursor.SetPosition(row, channel, PatternCellField.Note);

		char value = e.Text[0];
		int editedRow = _cursor.Row;
		int editedChannel = _cursor.Channel;

		if (_cursor.Field == PatternCellField.Note)
		{
			// Tracker notes are driven by KeyDown/PhysicalKey so their piano
			// geometry is independent of the active keyboard layout. Consuming
			// TextInput here also prevents a handled physical key from entering
			// the same note a second time through its produced text symbol.
			e.Handled = true;
			return;
		}

		if (_cursor.Field == PatternCellField.Volume)
		{
			PatternVolumeInputResult volumeResult =
				PatternVolumeKeyboardEditor.Type(
					_workspace,
					_pattern,
					_cursor,
					_volumeInput,
					value);

			if (!volumeResult.Handled)
				return;

			if (volumeResult.Rejected)
			{
				_message.Text =
					"Volume must be entered as a decimal tracker value from 00 through 64.";
			}
			else if (volumeResult.Changed)
			{
				RefreshCell(editedRow, editedChannel);
				_changed(
					$"Edited volume at row {editedRow}, channel {editedChannel + 1}");
			}

			RefreshCursorVisuals();
			FocusCursorCell();
			e.Handled = true;
			return;
		}

		PatternEffectInputResult result =
			PatternEffectKeyboardEditor.Type(
				_workspace,
				_pattern,
				_cursor,
				value);

		if (result.Rejected)
		{
			_message.Text =
				"That effect field is not directly editable in its current state. "
				+ "Expand stacked effects with Enter; native effects use a future parameter dialog.";
			e.Handled = true;
			return;
		}

		if (result.Changed)
		{
			RefreshCell(editedRow, editedChannel);
			_changed(
				$"Edited effect at row {editedRow}, channel {editedChannel + 1}");
		}

		RefreshCursorVisuals();
		FocusCursorCell();
		e.Handled = true;
	}

	private bool HandleAltEffectKey(
		int row,
		int channel,
		PatternCell? cell,
		KeyEventArgs e)
	{
		bool shift =
			(e.KeyModifiers & KeyModifiers.Shift) != 0;

		switch (e.Key)
		{
			case Key.Left:
				if (_cursor.IsExpanded)
				{
					FinishStackMutation(
						row,
						channel,
						PatternEffectStackEditor.MoveSelected(
							_workspace,
							_pattern,
							_cursor,
							delta: -1),
						"Moved effect left");
				}
				return true;

			case Key.Right:
				if (_cursor.IsExpanded)
				{
					FinishStackMutation(
						row,
						channel,
						PatternEffectStackEditor.MoveSelected(
							_workspace,
							_pattern,
							_cursor,
							delta: 1),
						"Moved effect right");
				}
				return true;

			case Key.Delete:
				if (_cursor.Field is
					PatternCellField.EffectCommand
					or PatternCellField.EffectParameter)
				{
					FinishStackMutation(
						row,
						channel,
						PatternEffectStackEditor.Delete(
							_workspace,
							_pattern,
							_cursor),
						"Deleted effect");
				}
				return true;

			case Key.Insert:
				if (_cursor.Field is
					PatternCellField.EffectCommand
					or PatternCellField.EffectParameter)
				{
					bool changed =
						shift
							? PatternEffectStackEditor.InsertAfter(
								_workspace,
								_pattern,
								_cursor)
							: PatternEffectStackEditor.InsertBefore(
								_workspace,
								_pattern,
								_cursor);
					FinishStackMutation(
						row,
						channel,
						changed,
						shift
							? "Inserted effect after"
							: "Inserted effect before");
				}
				return true;

			case Key.Home:
				if (_cursor.IsExpanded && cell is not null)
				{
					_cursor.MoveToFirstEffect(cell);
					_expandedCell = (row, channel);
				}
				return true;

			case Key.End:
				if (_cursor.IsExpanded && cell is not null)
				{
					_cursor.MoveToLastEffect(cell);
					_expandedCell = (row, channel);
				}
				return true;

			default:
				return false;
		}
	}

	private void DeleteEffectAt(
		int row,
		int channel,
		int effectIndex)
	{
		if (!SelectEffectForStackCommand(
			row,
			channel,
			effectIndex))
		{
			return;
		}

		FinishStackMutation(
			row,
			channel,
			PatternEffectStackEditor.Delete(
				_workspace,
				_pattern,
				_cursor),
			"Deleted effect");
	}

	private void InsertEffectAt(
		int row,
		int channel,
		int effectIndex,
		bool after)
	{
		if (!SelectEffectForStackCommand(
			row,
			channel,
			effectIndex))
		{
			return;
		}

		bool changed =
			after
				? PatternEffectStackEditor.InsertAfter(
					_workspace,
					_pattern,
					_cursor)
				: PatternEffectStackEditor.InsertBefore(
					_workspace,
					_pattern,
					_cursor);
		FinishStackMutation(
			row,
			channel,
			changed,
			after
				? "Inserted effect after"
				: "Inserted effect before");
	}

	private void ReorderEffectByDrag(
		int row,
		int channel,
		int sourceIndex,
		int targetIndex)
	{
		if (!SelectEffectForStackCommand(
			row,
			channel,
			sourceIndex))
		{
			return;
		}

		FinishStackMutation(
			row,
			channel,
			PatternEffectStackEditor.MoveSelectedTo(
				_workspace,
				_pattern,
				_cursor,
				targetIndex),
			"Reordered effect");
	}

	private bool SelectEffectForStackCommand(
		int row,
		int channel,
		int effectIndex)
	{
		PatternCell? cell =
			_pattern.Grid[row, channel];
		if (cell is null
			|| (uint)effectIndex >= (uint)cell.Effects.Count)
		{
			return false;
		}

		PatternEffect effect = cell.Effects[effectIndex];
		ExpandedEffectField field =
			PatternEffectCodec.IsTrackerStyle(effect)
				? ExpandedEffectField.Command
				: ExpandedEffectField.Native;
		_cursor.SetPosition(
			row,
			channel,
			PatternCellField.EffectCommand);
		_cursor.SetExpandedSelection(
			cell,
			effectIndex,
			field);
		ExpandVisualEffects(row, channel);
		FocusCursorCell();
		RefreshCursorVisuals();
		return true;
	}

	private void FinishStackMutation(
		int row,
		int channel,
		bool changed,
		string message)
	{
		if (!changed)
			return;

		if (_cursor.IsExpanded)
			_expandedCell = (row, channel);
		else if (_expandedCell == (row, channel))
			CollapseVisualEffects(collapseCursor: false);

		RefreshCell(row, channel);
		RefreshCursorVisuals();
		FocusCursorCell();
		_changed($"{message} at row {row}, channel {channel + 1}");
	}

	private void SelectExpandedEffect(
		int row,
		int channel,
		int effectIndex,
		ExpandedEffectField field)
	{
		PatternCell? cell = _pattern.Grid[row, channel];
		if (cell is null)
			return;

		_cursor.SetPosition(
			row,
			channel,
			field == ExpandedEffectField.Parameter
				? PatternCellField.EffectParameter
				: PatternCellField.EffectCommand);
		_cursor.SetExpandedSelection(
			cell,
			effectIndex,
			field);
		ExpandVisualEffects(row, channel);
		FocusCursorCell();
		RefreshCursorVisuals();
	}

	private void ExpandVisualEffects(int row, int channel)
	{
		PatternCell? cell = _pattern.Grid[row, channel];
		if (cell is null || cell.Effects.Count == 0)
			return;

		if (_expandedCell is (int oldRow, int oldChannel)
			&& (oldRow != row || oldChannel != channel)
			&& _effectStrips.TryGetValue(
				(oldRow, oldChannel),
				out PatternEffectStripControl? oldStrip))
		{
			oldStrip.SetVisualState(
				expanded: false,
				keyboardActive: false,
				PatternCellField.Note,
				-1,
				default);
		}

		_expandedCell = (row, channel);
		if (_cursor.Row == row
			&& _cursor.Channel == channel
			&& _cursor.Field is
				PatternCellField.EffectCommand
				or PatternCellField.EffectParameter
			&& !_cursor.IsExpanded)
		{
			_cursor.Expand(cell);
		}
		RefreshCellEffectState(row, channel);
	}

	private void CollapseVisualEffects(bool collapseCursor)
	{
		if (_expandedCell is not (int row, int channel))
			return;

		_expandedCell = null;
		if (collapseCursor
			&& _cursor.IsExpanded
			&& _cursor.Row == row
			&& _cursor.Channel == channel)
		{
			_cursor.Collapse();
		}

		if (_effectStrips.TryGetValue(
			(row, channel),
			out PatternEffectStripControl? strip))
		{
			strip.SetVisualState(
				expanded: false,
				keyboardActive:
					_cursor.Row == row
						&& _cursor.Channel == channel
						&& _cursor.Field != PatternCellField.Note,
				_cursor.Field,
				-1,
				default);
		}
	}

	private void OnRootPointerPressed(
		object? sender,
		PointerPressedEventArgs e)
	{
		if (_expandedCell is not (int row, int channel)
			|| !_effectStrips.TryGetValue(
				(row, channel),
				out PatternEffectStripControl? strip))
		{
			return;
		}

		Point? origin =
			strip.TranslatePoint(
				new Point(0, 0),
				this);
		if (origin is null)
			return;

		Point point = e.GetPosition(this);
		Rect bounds =
			new(
				origin.Value,
				strip.Bounds.Size);
		if (!bounds.Contains(point))
			CollapseVisualEffects(collapseCursor: true);
	}

	private void OnRootPointerMoved(
		object? sender,
		PointerEventArgs e)
	{
		if (_expandedCell is not (int row, int channel)
			|| _patternGrid is null
			|| !_cellBorders.TryGetValue(
				(row, channel),
				out Border? cell))
		{
			return;
		}

		Point? cellOrigin =
			cell.TranslatePoint(
				new Point(0, 0),
				this);
		Point? gridOrigin =
			_patternGrid.TranslatePoint(
				new Point(0, 0),
				this);
		if (cellOrigin is null || gridOrigin is null)
			return;

		Point pointer = e.GetPosition(this);
		EffectStripRect rowBounds =
			new(
				gridOrigin.Value.X,
				cellOrigin.Value.Y,
				_patternGrid.Bounds.Width,
				RowHeight);
		if (EffectStripLayout.ShouldCollapseForPointer(
			rowBounds,
			new EffectStripPoint(pointer.X, pointer.Y),
			RowHeight,
			distanceInRowHeights: 5))
		{
			CollapseVisualEffects(collapseCursor: true);
			RefreshCursorVisuals();
		}
	}

	private async Task EditCellAsync(int row, int channel)
	{
		PatternCell? cell = _pattern.Grid[row, channel];
		PatternNoteEditorDialog dialog =
			new(
				_workspace.Document,
				row,
				channel,
				cell?.Note);
		PatternNoteEditResult? result =
			await dialog.ShowDialog<PatternNoteEditResult?>(_owner);
		if (result is null)
			return;

		PatternDocumentEditor.SetNote(
			_workspace,
			_pattern,
			row,
			channel,
			result.Note);
		RefreshCell(row, channel);
		RefreshCursorVisuals();
		_changed($"Edited row {row}, channel {channel + 1}");
	}

	private void RefreshCell(int row, int channel)
	{
		if (!_noteTexts.TryGetValue(
			(row, channel),
			out TextBlock? note)
			|| !_volumeTexts.TryGetValue(
				(row, channel),
				out TextBlock? volume)
			|| !_effectStrips.TryGetValue(
				(row, channel),
				out PatternEffectStripControl? effects))
		{
			return;
		}

		PatternCellViewModel view =
			PatternCellViewModel.Create(
				_workspace.Document,
				_pattern,
				row,
				channel);
		note.Text = view.NoteText;
		volume.Text = view.VolumeText;
		effects.SetEffects(view.Effects);
		RefreshCellEffectState(row, channel);
	}

	private void RefreshCursorVisuals()
	{
		foreach (((int row, int channel), Border border) in _cellBorders)
		{
			bool active =
				_pattern.RowCount > 0
					&& row == _cursor.Row
					&& channel == _cursor.Channel;
			border.BorderBrush =
				active ? Brushes.DeepSkyBlue : Brushes.Gray;
			border.BorderThickness =
				active ? new Thickness(2) : new Thickness(1);
			if (_noteFields.TryGetValue(
				(row, channel),
				out Border? noteField))
			{
				bool noteActive =
					active && _cursor.Field == PatternCellField.Note;
				noteField.BorderBrush =
					noteActive ? Brushes.DeepSkyBlue : Brushes.Transparent;
				noteField.BorderThickness =
					noteActive ? new Thickness(2) : new Thickness(1);
			}
			if (_volumeFields.TryGetValue(
				(row, channel),
				out Border? volumeField))
			{
				bool volumeActive =
					active && _cursor.Field == PatternCellField.Volume;
				volumeField.BorderBrush =
					volumeActive ? Brushes.DeepSkyBlue : Brushes.Transparent;
				volumeField.BorderThickness =
					volumeActive ? new Thickness(2) : new Thickness(1);
			}
			RefreshCellEffectState(row, channel);
		}
	}

	private void RefreshCellEffectState(int row, int channel)
	{
		if (!_effectStrips.TryGetValue(
			(row, channel),
			out PatternEffectStripControl? strip))
		{
			return;
		}

		bool keyboardActive =
			_pattern.RowCount > 0
				&& _cursor.Row == row
				&& _cursor.Channel == channel
				&& _cursor.Field is
					PatternCellField.EffectCommand
					or PatternCellField.EffectParameter;
		bool expanded =
			_expandedCell == (row, channel);

		strip.SetVisualState(
			expanded,
			keyboardActive,
			_cursor.Field,
			keyboardActive && _cursor.IsExpanded
				? _cursor.ExpandedEffectIndex
				: -1,
			keyboardActive && _cursor.IsExpanded
				? _cursor.ExpandedField
				: default);
	}

	private void FocusCursorCell()
	{
		if (_pattern.RowCount == 0)
			return;

		if (_cellBorders.TryGetValue(
			(_cursor.Row, _cursor.Channel),
			out Border? border))
		{
			border.Focus();
		}
	}

	private FontWeight GetRowWeight(int row)
		=> _pattern.MajorHighlightRows > 0
			&& row % _pattern.MajorHighlightRows == 0
				? FontWeight.Bold
				: _pattern.MinorHighlightRows > 0
					&& row % _pattern.MinorHighlightRows == 0
					? FontWeight.SemiBold
					: FontWeight.Normal;

	private static TextBox NumberBox(int value)
		=> new()
		{
			Text = value.ToString(CultureInfo.CurrentCulture),
			Width = 60,
		};

	private static int ParseInt(TextBox box, string label)
	{
		if (!int.TryParse(
			box.Text,
			NumberStyles.Integer,
			CultureInfo.CurrentCulture,
			out int value))
		{
			throw new ArgumentException($"{label} is not a valid integer.");
		}

		return value;
	}

	private static void AddText(
		Grid grid,
		string text,
		int row,
		int column,
		FontWeight weight)
	{
		TextBlock block =
			new()
			{
				Text = text,
				FontWeight = weight,
				Margin = new Thickness(5, 4),
				VerticalAlignment = VerticalAlignment.Center,
			};
		Grid.SetRow(block, row);
		Grid.SetColumn(block, column);
		grid.Children.Add(block);
	}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Patterns;
using Heresy.Core.Sequencing;
using Heresy.Playback;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.ViewModels;

namespace Heresy.UserInterface.Views;

public sealed class PatternEditorControl : UserControl
{
	private const double CellWidth = 280;
	private const double RowHeaderWidth = 54;
	private const double RowHeight = 28;
	private const double SourceWidth = 96;
	private const double VolumeWidth = 34;
	private const double EffectWidth = 54;

	private readonly Window _owner;
	private readonly DocumentWorkspace _workspace;
	private readonly PatternEditorContext _context;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly Func<NoteSchedule, Task>? _audition;
	private readonly PatternLiveAuditionActions? _liveAudition;
	private readonly Action<PatternEditorSwitchRequest>? _switchPattern;
	private readonly HeldNotePreviewKeyState _heldPreviewKeys = new();
	private readonly string _backLabel;
	private readonly UserInterfaceConfiguration _configuration;
	private readonly TextBox _rowCount;
	private readonly TextBox _channelCount;
	private readonly TextBox _minorHighlight;
	private readonly TextBox _majorHighlight;
	private readonly PatternSourceOption[] _noteSources;
	private readonly ComboBox _noteSource;
	private readonly ComboBox _noteOctave;
	private readonly TextBlock _skipValueDisplay = new()
	{
		VerticalAlignment = VerticalAlignment.Center,
	};
	private readonly TextBlock _editMaskDisplay =
		new()
		{
			VerticalAlignment = VerticalAlignment.Center,
		};
	private readonly TextBlock _editVolumeDisplay =
		new()
		{
			VerticalAlignment = VerticalAlignment.Center,
		};
	private readonly PatternNoteInputState _noteInputState;
	private readonly PatternChordInputState _chordInputState = new();
	private readonly StackPanel _chordStatus =
		new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			Margin = new Thickness(10, 2),
			VerticalAlignment = VerticalAlignment.Center,
		};
	private readonly ScrollViewer _scroll;
	private readonly TextBlock _message;
	private readonly TextBlock _title =
		new()
		{
			FontSize = 20,
			FontWeight = FontWeight.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
		};
	private readonly Dictionary<(int Row, int Channel), Border> _cellBorders = [];
	private readonly Dictionary<int, Border> _rowHeaders = [];
	private readonly HashSet<int> _playbackDisplayRows = [];
	private readonly Dictionary<(int Row, int Channel), Border> _noteFields = [];
	private readonly Dictionary<(int Row, int Channel), TextBlock> _noteTexts = [];
	private readonly Dictionary<(int Row, int Channel), Border> _sourceFields = [];
	private readonly Dictionary<(int Row, int Channel), TextBlock> _sourceTexts = [];
	private readonly Dictionary<(int Row, int Channel), Border> _volumeFields = [];
	private readonly Dictionary<(int Row, int Channel), TextBlock> _volumeTexts = [];
	private readonly Dictionary<(int Row, int Channel), PatternEffectStripControl> _effectStrips = [];
	private readonly PatternVolumeInputState _volumeInput = new();
	private readonly PatternSelectionState _selection = new();

	private PatternEffectCursor _cursor =
		new(0, 0, PatternCellField.Note);
	private Grid? _patternGrid;
	private (int Row, int Channel)? _expandedCell;
	private PlaybackPatternPosition? _playbackPosition;

	public PatternEditorControl(
		Window owner,
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		Action close,
		Action<string> changed,
		string backLabel = "← Document",
		UserInterfaceConfiguration? configuration = null,
		Func<NoteSchedule, Task>? audition = null,
		PatternLiveAuditionActions? liveAudition = null,
		Action<PatternEditorSwitchRequest>? switchPattern = null,
		PatternEditorOpenState? initialState = null)
		: this(
			owner,
			workspace,
			PatternEditorContext.ForPattern(workspace.Document, pattern),
			close,
			changed,
			backLabel,
			configuration,
			audition,
			liveAudition,
			switchPattern,
			initialState)
	{
	}

	public PatternEditorControl(
		Window owner,
		DocumentWorkspace workspace,
		PatternEditorContext context,
		Action close,
		Action<string> changed,
		string backLabel = "← Document",
		UserInterfaceConfiguration? configuration = null,
		Func<NoteSchedule, Task>? audition = null,
		PatternLiveAuditionActions? liveAudition = null,
		Action<PatternEditorSwitchRequest>? switchPattern = null,
		PatternEditorOpenState? initialState = null)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		_context = context ?? throw new ArgumentNullException(nameof(context));
		_close = close ?? throw new ArgumentNullException(nameof(close));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		_audition = audition;
		_liveAudition = liveAudition;
		_switchPattern = switchPattern;
		_backLabel = backLabel ?? throw new ArgumentNullException(nameof(backLabel));
		_configuration = configuration ?? new UserInterfaceConfiguration();

		DataPatternDefinition pattern = GetInitialPattern(context);
		int initialDisplayRow =
			context.Rows.Count == 0
				? 0
				: context.InitialDisplayRow;
		int initialChannel = 0;
		PatternCellField initialField = PatternCellField.Note;
		if (initialState is not null
			&& !context.IsSequence
			&& context.Rows.Count != 0)
		{
			initialDisplayRow =
				Math.Clamp(
					initialState.PatternRow,
					0,
					context.Rows.Count - 1);
			DataPatternDefinition initialPattern =
				context.GetRow(initialDisplayRow).Pattern;
			initialChannel =
				Math.Clamp(
					initialState.Channel,
					0,
					initialPattern.ChannelCount - 1);
			initialField = initialState.Field;
		}
		_cursor.SetPosition(
			initialDisplayRow,
			initialChannel,
			initialField);

		_rowCount = NumberBox(pattern.RowCount);
		_channelCount = NumberBox(pattern.ChannelCount);
		_minorHighlight = NumberBox(pattern.MinorHighlightRows);
		_majorHighlight = NumberBox(pattern.MajorHighlightRows);

		_noteSources =
			PatternSourceCatalog.GetSources(workspace.Document);
		PatternSourceOption? defaultSource = null;
		if (initialState is not null)
		{
			foreach (PatternSourceOption source in _noteSources)
			{
				if (source.Id == initialState.SourceId)
				{
					defaultSource = source;
					break;
				}
			}
		}
		else
		{
			foreach (PatternSourceOption source in _noteSources)
			{
				if (source.Id != pattern.Id)
				{
					defaultSource = source;
					break;
				}
			}
		}

		int baseOctave =
			initialState?.BaseOctave ?? 4;
		_noteInputState =
			new(
				defaultSource?.Id ?? Heresy.Core.Objects.ObjectId.None,
				baseOctave)
			{
				EditMask =
					initialState?.EditMask
						?? PatternEditMask.Default,
				CurrentVolume =
					initialState?.CurrentVolume,
				SkipRows =
					initialState?.SkipRows ?? 1,
			};
		UpdateSkipValueDisplay();
		if (initialState?.Chord is PatternChordInputSnapshot chord)
			_chordInputState.Restore(chord);
		UpdateEditStateDisplay();
		_noteSource =
			new ComboBox
			{
				ItemsSource = _noteSources,
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
				SelectedItem = baseOctave,
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
					"Arrow keys move the tracker cursor. Shift+Arrow extends the marked block; Alt+B/Alt+E set its corners, Alt+D marks/expands by the major highlight, Alt+L marks the channel then pattern, and Alt+U unmarks. Ctrl+C/Ctrl+X copy or cut the marked block, Ctrl+V merge-pastes it, Shift+Ctrl+V overwrite-pastes it, and Ctrl+Delete clears it. Ctrl+Alt+Z/X/C/V/B/N/M chooses a chord; Ctrl+Alt+-/+ rotates it, Ctrl+Alt+Numpad */ changes its tone count, Ctrl+Alt+1..9 toggles tones and Ctrl+Alt+= enables all. Type notes directly in the note field; the edit mask controls which Note/Source/Volume fields are stamped, and comma toggles the mask bit for the current field. Hold Caps Lock while pressing tracker piano keys to preview without editing; repeats are ignored and key release sends Note Off. Alt+0–9 selects the note-entry skip (default 1; 0 stays on the row); ordinary held-key repeats enter notes at successive skipped rows. Top-row 4 auditions the current note and 8 auditions the current row, advancing one row. Enter opens detailed note editing or expands a stacked effect strip.",
				TextWrapping = TextWrapping.Wrap,
			};

		Content = BuildContent();
		UpdateChordStatus();
		_owner.Deactivated += OnOwnerDeactivated;
		RefreshGrid();
	}

	public void SetPlaybackPosition(
		PlaybackPatternPosition? position)
	{
		if (_playbackPosition == position)
			return;

		_playbackPosition = position;
		RemapPlaybackDisplayRows();
		RefreshCursorVisuals();
		RefreshPlaybackRowHeaders();
	}

	private void UpdateSkipValueDisplay()
		=> _skipValueDisplay.Text = $"Skip {_noteInputState.SkipRows}";

	private Control BuildContent()
	{
		Button back = new() { Content = _backLabel, MinWidth = 100 };
		back.Click += (_, _) => _close();

		UpdateCurrentPatternControls();

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
		layout.Children.Add(_skipValueDisplay);
		layout.Children.Add(new TextBlock
		{
			Text = "Mask",
			VerticalAlignment = VerticalAlignment.Center,
		});
		layout.Children.Add(_editMaskDisplay);
		layout.Children.Add(new TextBlock
		{
			Text = "Edit Vol",
			VerticalAlignment = VerticalAlignment.Center,
		});
		layout.Children.Add(_editVolumeDisplay);
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
		header.Children.Add(_title);

		DockPanel root = new();
		DockPanel.SetDock(header, Dock.Top);
		root.Children.Add(header);

		Border chordBorder =
			new()
			{
				Padding = new Thickness(0, 2),
				Child = _chordStatus,
			};
		DockPanel.SetDock(chordBorder, Dock.Top);
		root.Children.Add(chordBorder);

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
			DataPatternDefinition pattern = GetCurrentPattern();
			PatternEditorRow? preferred =
				_context.Rows.Count == 0
					? null
					: _context.GetRow(_cursor.Row);
			PatternCellField field = _cursor.Field;
			int channel = _cursor.Channel;

			int rows = ParseInt(_rowCount, "Rows");
			int channels = ParseInt(_channelCount, "Channels");
			int minor = ParseInt(_minorHighlight, "Minor highlight");
			int major = ParseInt(_majorHighlight, "Major highlight");

			if (PatternDocumentEditor.WouldDiscardCells(
				pattern,
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
				pattern,
				rows,
				channels,
				minor,
				major);

			int displayRow = _context.Refresh(preferred);
			if (_context.Rows.Count != 0)
			{
				DataPatternDefinition focusedPattern =
					_context.GetRow(displayRow).Pattern;
				channel =
					Math.Min(channel, focusedPattern.ChannelCount - 1);
			}
			else
			{
				channel = 0;
			}
			_cursor.SetPosition(displayRow, channel, field);
			_selection.Clear();
			RefreshGrid();
			_changed($"Pattern layout updated: {pattern.Name}");
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
		_rowHeaders.Clear();
		_noteFields.Clear();
		_noteTexts.Clear();
		_sourceFields.Clear();
		_sourceTexts.Clear();
		_volumeFields.Clear();
		_volumeTexts.Clear();
		_effectStrips.Clear();

		RemapPlaybackDisplayRows();

		Grid grid = new();
		_patternGrid = grid;
		int channelCount = _context.MaxChannelCount;
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(RowHeaderWidth)));
		for (int channel = 0; channel < channelCount; channel++)
			grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(CellWidth)));

		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		AddText(grid, "Row", 0, 0, FontWeight.SemiBold);
		for (int channel = 0; channel < channelCount; channel++)
		{
			AddText(
				grid,
				$"Channel {channel + 1}",
				0,
				channel + 1,
				FontWeight.SemiBold);
		}

		int gridRow = 1;
		foreach (PatternEditorSegment segment in _context.Segments)
		{
			if (_context.IsSequence)
			{
				grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
				AddSegmentHeader(
					grid,
					segment,
					gridRow,
					channelCount + 1);
				gridRow++;
			}

			for (int offset = 0; offset < segment.DisplayRowCount; offset++)
			{
				int displayRow = segment.FirstDisplayRow + offset;
				PatternEditorRow row = _context.GetRow(displayRow);
				grid.RowDefinitions.Add(
					new RowDefinition(new GridLength(RowHeight)));

				IBrush? rowBackground =
					GetDisplayRowBackground(
						displayRow,
						row.Pattern,
						row.PatternRow);
				AddPlaybackRowHeader(
					grid,
					displayRow,
					row.PatternRow,
					gridRow,
					rowBackground);

				for (int channel = 0; channel < channelCount; channel++)
				{
					Border cell =
						channel < row.Pattern.ChannelCount
							? BuildCell(
								displayRow,
								row,
								channel,
								rowBackground)
							: BuildUnavailableCell(rowBackground);
					Grid.SetRow(cell, gridRow);
					Grid.SetColumn(cell, channel + 1);
					grid.Children.Add(cell);
				}
				gridRow++;
			}
		}

		_scroll.Content = grid;
		UpdateCurrentPatternControls();
		RefreshCursorVisuals();
		FocusCursorCell();
	}

	private Border BuildCell(
		int displayRow,
		PatternEditorRow row,
		int channel,
		IBrush? rowBackground)
	{
		PatternCellViewModel view =
			PatternCellViewModel.Create(
				_workspace.Document,
				row.Pattern,
				row.PatternRow,
				channel);

		TextBlock note =
			new()
			{
				Text = view.NoteText,
				FontWeight = FontWeight.Normal,
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
				Background = Brushes.Transparent,
				Child = note,
			};

		TextBlock sourceText =
			new()
			{
				Text = view.SourceText,
				FontWeight = FontWeight.Normal,
				VerticalAlignment = VerticalAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis,
				Margin = new Thickness(4, 0),
			};
		Border sourceField =
			new()
			{
				Width = SourceWidth,
				Height = RowHeight - 4,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Center,
				BorderBrush = Brushes.Transparent,
				BorderThickness = new Thickness(1),
				Background = Brushes.Transparent,
				Child = sourceText,
			};
		ToolTip.SetTip(sourceField, view.SourceText);
		MenuItem clearSource = new() { Header = "Clear" };
		clearSource.Click += (_, _) =>
			ClearSource(displayRow, channel);
		sourceField.ContextMenu =
			new ContextMenu
			{
				ItemsSource = new object[] { clearSource },
			};

		TextBlock volumeText =
			new()
			{
				Text = view.VolumeText,
				FontWeight = FontWeight.Normal,
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
				Background = Brushes.Transparent,
				Child = volumeText,
			};

		PatternEffectStripControl effects =
			new(
				CellWidth - 2,
				EffectWidth,
				RowHeight - 2,
				() => ExpandVisualEffects(displayRow, channel),
				(effectIndex, field) =>
					SelectExpandedEffect(
						displayRow,
						channel,
						effectIndex,
						field),
				effectIndex =>
					_ = EditNativeEffectAtAsync(
						displayRow,
						channel,
						effectIndex),
				(effectIndex, after) =>
					_ = InsertNativeEffectAtAsync(
						displayRow,
						channel,
						effectIndex,
						after),
				() =>
					_ = InsertNativeEffectAtAsync(
						displayRow,
						channel,
						effectIndex: null,
						after: false),
				() =>
					_ = CopyEffectStackAsync(
						displayRow,
						channel),
				() =>
					_ = PasteEffectStackAsync(
						displayRow,
						channel),
				effectIndex =>
					DeleteEffectAt(
						displayRow,
						channel,
						effectIndex),
				(effectIndex, after) =>
					InsertEffectAt(
						displayRow,
						channel,
						effectIndex,
						after),
				(sourceIndex, targetIndex) =>
					ReorderEffectByDrag(
						displayRow,
						channel,
						sourceIndex,
						targetIndex));
		effects.SetEffects(view.Effects);

		Grid content = new();
		content.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		content.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(SourceWidth)));
		content.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(VolumeWidth)));
		content.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(EffectWidth)));
		Grid.SetColumn(noteField, 0);
		Grid.SetColumn(sourceField, 1);
		Grid.SetColumn(volumeField, 2);
		Grid.SetColumn(effects, 0);
		Grid.SetColumnSpan(effects, 4);
		content.Children.Add(noteField);
		content.Children.Add(sourceField);
		content.Children.Add(volumeField);
		content.Children.Add(effects);

		Border cell =
			new()
			{
				Width = CellWidth,
				Height = RowHeight,
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(1),
				Background = rowBackground ?? Brushes.Transparent,
				ClipToBounds = true,
				Focusable = true,
				Child = content,
			};

		cell.PointerPressed += (_, e) =>
			OnCellPointerPressed(displayRow, channel, cell, e);
		cell.KeyDown += async (_, e) =>
			await OnCellKeyDownAsync(displayRow, channel, e);
		cell.KeyUp += async (_, e) =>
			await OnCellKeyUpAsync(e);
		cell.TextInput += (_, e) =>
			OnCellTextInput(displayRow, channel, e);

		_cellBorders[(displayRow, channel)] = cell;
		_noteFields[(displayRow, channel)] = noteField;
		_noteTexts[(displayRow, channel)] = note;
		_sourceFields[(displayRow, channel)] = sourceField;
		_sourceTexts[(displayRow, channel)] = sourceText;
		_volumeFields[(displayRow, channel)] = volumeField;
		_volumeTexts[(displayRow, channel)] = volumeText;
		_effectStrips[(displayRow, channel)] = effects;
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

		PatternEditorRow editorRow = _context.GetRow(row);
		Point point = e.GetPosition(cell);
		PatternCell? patternCell =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel];
		bool sourceWasActive =
			_cursor.Row == row
				&& _cursor.Channel == channel
				&& _cursor.Field == PatternCellField.Source;
		int effectCount = patternCell?.Effects.Count ?? 0;
		bool singleTrackerStyle =
			effectCount != 1
				|| PatternEffectCodec.IsTrackerStyle(
					patternCell!.Effects[0]);
		PatternCellField field =
			PatternCellFieldGeometry.HitTest(
				point.X,
				CellWidth,
				SourceWidth,
				VolumeWidth,
				EffectWidth,
				effectCount,
				singleTrackerStyle);

		if (_expandedCell == (row, channel))
			CollapseVisualEffects(collapseCursor: true);

		_volumeInput.Reset();
		_cursor.SetPosition(row, channel, field);
		UpdateCurrentPatternControls();
		cell.Focus();
		RefreshCursorVisuals();
		if (field == PatternCellField.Source
			&& sourceWasActive
			&& e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed
			&& _sourceFields.TryGetValue((row, channel), out Border? sourceField))
		{
			OpenSourcePopup(row, channel, sourceField);
		}
		e.Handled = true;
	}

	private async Task OnCellKeyDownAsync(
		int row,
		int channel,
		KeyEventArgs e)
	{
		if (_cursor.Row != row || _cursor.Channel != channel)
			_cursor.SetPosition(row, channel, PatternCellField.Note);

		PatternEditorRow editorRow = _context.GetRow(row);

		if (PatternSkipKeyboard.TryGetValue(
			e.PhysicalKey, e.KeyModifiers, out int skipRows))
		{
			_noteInputState.SkipRows = skipRows;
			UpdateSkipValueDisplay();
			_message.Text = $"Tracker note skip set to {skipRows}.";
			e.Handled = true;
			return;
		}

		if (_cursor.Field == PatternCellField.Note
			&& e.PhysicalKey == PhysicalKey.CapsLock)
		{
			_heldPreviewKeys.KeyDown(
				e.PhysicalKey,
				_noteInputState.BaseOctave);
			e.Handled = true;
			return;
		}
		if (_cursor.Field == PatternCellField.Note
			&& PatternPreviewKeyRouting.IsPreviewOnly(
				_heldPreviewKeys, e.PhysicalKey))
		{
			// Auto-repeat remains preview-only, even when Caps Lock is
			// released before the still-held piano key.
			HeldNotePreviewAction? action = _heldPreviewKeys.KeyDown(
				e.PhysicalKey, _noteInputState.BaseOctave);
			e.Handled = true;
			if (_liveAudition is not null
				&& action is StartHeldNotePreviewAction preview)
			{
				await StartHeldPreviewAsync(editorRow, channel, preview);
			}
			return;
		}

		PatternCell? cell =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel];
		(int Row, int Channel)? previouslyExpanded = _expandedCell;

		if (PatternChordKeyboard.TryGetCommand(
			e.PhysicalKey,
			e.KeyModifiers,
			out PatternChordCommand chordCommand))
		{
			HandleChordCommand(chordCommand);
			UpdateChordStatus();
			e.Handled = true;
			FocusCursorCell();
			return;
		}

		if (PatternOctaveKeyboard.TryAdjust(
			e.PhysicalKey,
			e.KeyModifiers,
			_noteInputState.BaseOctave,
			out int octave))
		{
			_noteInputState.BaseOctave = octave;
			_noteOctave.SelectedItem = octave;
			_message.Text = $"Tracker octave set to {octave}.";
			e.Handled = true;
			FocusCursorCell();
			return;
		}

		if (PatternSourceNavigationKeyboard.TryGetDelta(
			e.Key,
			e.KeyModifiers,
			out int sourceDelta))
		{
			MoveCurrentSource(sourceDelta);
			e.Handled = true;
			FocusCursorCell();
			return;
		}

		if (PatternSelectionKeyboard.TryGetCommand(
			e.Key,
			e.KeyModifiers,
			out PatternSelectionCommand selectionCommand))
		{
			int originalRow = _cursor.Row;
			int originalChannel = _cursor.Channel;

			switch (selectionCommand)
			{
				case PatternSelectionCommand.SetStart:
					_selection.SetStart(
						_cursor.Row,
						_cursor.Channel);
					break;

				case PatternSelectionCommand.SetEnd:
					_selection.SetEnd(
						_cursor.Row,
						_cursor.Channel);
					break;

				case PatternSelectionCommand.SelectMajorBlock:
					PatternSelectionEditor.SelectMajorBlock(
						_context,
						_cursor,
						_selection);
					break;

				case PatternSelectionCommand.SelectColumnOrPattern:
					PatternSelectionEditor.SelectColumnOrPattern(
						_context,
						_cursor,
						_selection);
					break;

				case PatternSelectionCommand.Clear:
					_selection.Clear();
					break;

				case PatternSelectionCommand.ExtendLeft:
					PatternEditorContextCursor.MoveLeft(
						_context,
						_cursor);
					_selection.Extend(
						originalRow,
						originalChannel,
						_cursor.Row,
						_cursor.Channel);
					break;

				case PatternSelectionCommand.ExtendRight:
					PatternEditorContextCursor.MoveRight(
						_context,
						_cursor);
					_selection.Extend(
						originalRow,
						originalChannel,
						_cursor.Row,
						_cursor.Channel);
					break;

				case PatternSelectionCommand.ExtendUp:
					PatternEditorContextCursor.MoveUp(
						_context,
						_cursor);
					_selection.Extend(
						originalRow,
						originalChannel,
						_cursor.Row,
						_cursor.Channel);
					break;

				case PatternSelectionCommand.ExtendDown:
					PatternEditorContextCursor.MoveDown(
						_context,
						_cursor);
					_selection.Extend(
						originalRow,
						originalChannel,
						_cursor.Row,
						_cursor.Channel);
					break;

				default:
					throw new ArgumentOutOfRangeException(
						nameof(selectionCommand));
			}

			_volumeInput.Reset();
			e.Handled = true;
			UpdateCurrentPatternControls();
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		bool effectField =
			_cursor.Field is
				PatternCellField.EffectCommand
				or PatternCellField.EffectParameter;
		if (PatternRegionClipboardKeyboard.TryGetCommand(
			e.Key,
			e.KeyModifiers,
			out PatternRegionClipboardCommand clipboardCommand))
		{
			await HandlePatternRegionClipboardAsync(
				row,
				channel,
				effectField,
				clipboardCommand);
			e.Handled = true;
			return;
		}

		if ((e.KeyModifiers & KeyModifiers.Alt) != 0
			&& e.Key == Key.N
			&& _cursor.Field is
				PatternCellField.EffectCommand
				or PatternCellField.EffectParameter)
		{
			int? effectIndex =
				cell is null || cell.Effects.Count == 0
					? null
					: _cursor.IsExpanded
						? _cursor.ExpandedEffectIndex
						: 0;
			bool after =
				(e.KeyModifiers & KeyModifiers.Shift) != 0;
			await InsertNativeEffectAtAsync(
				row,
				channel,
				effectIndex,
				after);
			e.Handled = true;
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		KeyModifiers channelNavigationBlockers =
			KeyModifiers.Control
				| KeyModifiers.Meta
				| KeyModifiers.Shift;
		if (!_cursor.IsExpanded
			&& (e.KeyModifiers & KeyModifiers.Alt) != 0
			&& (e.KeyModifiers & channelNavigationBlockers) == 0
			&& e.Key is Key.Left or Key.Right)
		{
			_volumeInput.Reset();
			PatternEditorContextCursor.MoveChannel(
				_context,
				_cursor,
				e.Key == Key.Left ? -1 : 1);
			e.Handled = true;
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		if ((e.KeyModifiers & KeyModifiers.Alt) != 0
			&& HandleAltEffectKey(row, channel, cell, e))
		{
			e.Handled = true;
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		if (PatternRowMutationKeyboard.TryGet(
			e.Key,
			e.KeyModifiers,
			effectField,
			out PatternRowMutationKind rowMutation,
			out bool allChannels))
		{
			bool changed =
				PatternEditorRowMutation.Apply(
					_workspace,
					_context,
					_cursor,
					rowMutation,
					allChannels);
			_volumeInput.Reset();
			if (changed)
			{
				PatternEditorRow current =
					_context.GetRow(_cursor.Row);
				_changed(
					$"{(rowMutation == PatternRowMutationKind.Insert ? "Inserted" : "Deleted")} row data in {current.Pattern.Name} row {current.PatternRow}"
						+ (allChannels ? " across all channels" : $", channel {_cursor.Channel + 1}"));
				RefreshGrid();
			}
			else
			{
				_message.Text =
					"No populated row data was shifted.";
				RefreshCursorVisuals();
				FocusCursorCell();
			}
			e.Handled = true;
			return;
		}

		if (_cursor.Field == PatternCellField.Source)
		{
			if (e.Key == Key.Enter)
			{
				PatternSourceOption? selected =
					PatternSourceSelection.FindExplicitSource(
						cell,
						_noteSources);
				if (selected is null)
				{
					_message.Text =
						cell is null || cell.SourceId.IsNone
							? "This Source field is empty."
							: "This Source is no longer available.";
				}
				else
				{
					_noteSource.SelectedItem = selected;
					_message.Text =
						$"Current Source set to {selected.DisplayName}.";
				}

				e.Handled = true;
				FocusCursorCell();
				return;
			}

			if ((e.KeyModifiers & KeyModifiers.Alt) != 0
				&& e.Key == Key.Down
				&& _sourceFields.TryGetValue(
					(row, channel),
					out Border? sourceField))
			{
				OpenSourcePopup(row, channel, sourceField);
				e.Handled = true;
				return;
			}

			if (e.Key == Key.Space)
			{
				if (_noteSource.SelectedItem is PatternSourceOption option)
				{
					SetSource(row, channel, option.Id);
					_message.Text =
						$"Source set to {option.DisplayName}.";
				}
				else
				{
					_message.Text =
						"Choose a Source in the pattern toolbar before using Space.";
				}
				e.Handled = true;
				return;
			}
		}

		KeyModifiers noteBlockingModifiers =
			KeyModifiers.Control
				| KeyModifiers.Alt
				| KeyModifiers.Meta;
		if (_cursor.Field == PatternCellField.Note
			&& (e.KeyModifiers & noteBlockingModifiers) == 0
			&& PatternAuditionKeyboard.TryGetKind(
				e.PhysicalKey,
				out PatternAuditionKind auditionKind))
		{
			await AuditionCurrentAsync(
				auditionKind,
				editorRow,
				channel);
			e.Handled = true;
			return;
		}
		if (_cursor.Field == PatternCellField.Note
			&& (e.KeyModifiers & noteBlockingModifiers) == 0)
		{
			int editedRow = _cursor.Row;
			int editedChannel = _cursor.Channel;
			PatternEditorRow edited = _context.GetRow(editedRow);

			if (_chordInputState.IsActive)
			{
				PatternChordInputResult chordResult =
					PatternEditorContextCursor.EditCurrent(
						_context,
						_cursor,
						mapped =>
							PatternChordEditor.TypeRootPhysical(
								_workspace,
								mapped.Pattern,
								_cursor,
								_noteInputState,
								_chordInputState,
								e.PhysicalKey));

				if (chordResult.Handled)
				{
					if (chordResult.Changed)
					{
						foreach (int chordChannel in chordResult.Channels)
						{
							RefreshUnderlyingCell(
								edited.Pattern,
								edited.PatternRow,
								chordChannel);
						}
						_changed(
							$"Entered chord in {edited.Pattern.Name} row {edited.PatternRow}, starting at channel {editedChannel + 1}");
					}

					if (chordResult.NoteApplied)
					{
						foreach (int chordChannel in chordResult.Channels)
						{
							await PlayEnteredNoteAsync(
								edited,
								chordChannel);
						}
					}

					UpdateChordStatus();
					e.Handled = true;
					UpdateCurrentPatternControls();
					RefreshCursorVisuals();
					FocusCursorCell();
					return;
				}
			}

			PatternNoteInputResult noteResult =
				PatternEditorContextCursor.EditCurrent(
					_context,
					_cursor,
					mapped =>
						PatternNoteKeyboardEditor.TypePhysical(
							_workspace,
							mapped.Pattern,
							_cursor,
							_noteInputState,
							e.PhysicalKey));

			if (noteResult.Handled)
			{
				if (noteResult.Rejected)
				{
					_message.Text =
						"Choose a current sound source before entering pitched notes.";
				}
				else
				{
					if (noteResult.Changed)
					{
						RefreshUnderlyingCell(
							edited.Pattern,
							edited.PatternRow,
							editedChannel);
						_changed(
							$"Edited tracker entry in {edited.Pattern.Name} row {edited.PatternRow}, channel {editedChannel + 1}");
					}

					if (noteResult.NoteApplied)
					{
						await PlayEnteredNoteAsync(
							edited,
							editedChannel);
					}
				}

				e.Handled = true;
				UpdateCurrentPatternControls();
				RefreshCursorVisuals();
				FocusCursorCell();
				return;
			}
		}

		if (PatternNoteColumnNavigationKeyboard.TryGetDelta(
			e.Key,
			e.KeyModifiers,
			out int noteColumnDelta))
		{
			_volumeInput.Reset();
			bool moved =
				PatternEditorContextCursor.MoveToAdjacentNote(
					_context,
					_cursor,
					noteColumnDelta);
			if (moved && _expandedCell is not null)
				CollapseVisualEffects(collapseCursor: false);

			e.Handled = true;
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		if (PatternRowBoundaryNavigationKeyboard.TryGetFirst(
			e.Key,
			e.KeyModifiers,
			out bool firstRow))
		{
			_volumeInput.Reset();
			if (_expandedCell is not null)
				CollapseVisualEffects(collapseCursor: true);

			bool moved =
				firstRow
					? PatternEditorContextCursor.MoveToFirstRow(_context, _cursor)
					: PatternEditorContextCursor.MoveToLastRow(_context, _cursor);
			bool switched =
				!moved
					&& !_context.IsSequence
					&& SwitchStandalonePatternBoundary(firstRow);

			e.Handled = true;
			if (switched)
				return;

			UpdateCurrentPatternControls();
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		if (PatternCornerNavigationKeyboard.TryGetTopLeft(
			e.Key,
			e.KeyModifiers,
			out bool topLeft))
		{
			_volumeInput.Reset();
			if (_expandedCell is not null)
				CollapseVisualEffects(collapseCursor: true);

			if (topLeft)
				PatternEditorContextCursor.MoveToTopLeft(_context, _cursor);
			else
				PatternEditorContextCursor.MoveToBottomRight(_context, _cursor);

			e.Handled = true;
			UpdateCurrentPatternControls();
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		KeyModifiers homeEndBlockers =
			KeyModifiers.Control
				| KeyModifiers.Alt
				| KeyModifiers.Meta
				| KeyModifiers.Shift;
		if ((e.KeyModifiers & homeEndBlockers) == 0
			&& e.Key is Key.Home or Key.End)
		{
			_volumeInput.Reset();
			if (_expandedCell is not null)
				CollapseVisualEffects(collapseCursor: true);

			if (e.Key == Key.Home)
				PatternEditorContextCursor.MoveHome(_context, _cursor);
			else
				PatternEditorContextCursor.MoveEnd(_context, _cursor);

			e.Handled = true;
			UpdateCurrentPatternControls();
			RefreshCursorVisuals();
			FocusCursorCell();
			return;
		}

		switch (e.Key)
		{
			case Key.Left:
				_volumeInput.Reset();
				PatternEditorContextCursor.MoveLeft(_context, _cursor);
				e.Handled = true;
				break;

			case Key.Right:
				_volumeInput.Reset();
				PatternEditorContextCursor.MoveRight(_context, _cursor);
				e.Handled = true;
				break;

			case Key.Up:
				_volumeInput.Reset();
				PatternEditorContextCursor.MoveUp(_context, _cursor);
				if (previouslyExpanded is not null)
					CollapseVisualEffects(collapseCursor: false);
				e.Handled = true;
				break;

			case Key.Down:
				_volumeInput.Reset();
				PatternEditorContextCursor.MoveDown(_context, _cursor);
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

				if (_cursor.IsExpanded
					&& _cursor.ExpandedField == ExpandedEffectField.Native)
				{
					await EditNativeEffectAtAsync(
						row,
						channel,
						_cursor.ExpandedEffectIndex);
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
					await EditNativeEffectAtAsync(
						row,
						channel,
						0);
					e.Handled = true;
					return;
				}
				e.Handled = true;
				break;

			default:
				return;
		}

		UpdateCurrentPatternControls();
		RefreshCursorVisuals();
		FocusCursorCell();
	}

	private async Task PlayEnteredNoteAsync(
		PatternEditorRow editorRow,
		int channel)
	{
		if (_liveAudition is null)
			return;

		try
		{
			var liveEvent =
				PatternLiveEventCompiler.CompileEnteredNote(
					editorRow.Pattern,
					editorRow.PatternRow,
					channel);
			if (liveEvent is not null)
				await _liveAudition.SendEventAsync(liveEvent);
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Entered-note playback failed: {ex.Message}";
		}
	}

	private async Task AuditionCurrentAsync(
		PatternAuditionKind kind,
		PatternEditorRow editorRow,
		int channel)
	{
		NoteSchedule schedule =
			kind switch
			{
				PatternAuditionKind.Note =>
					PatternAuditionCompiler.CompileNote(
						editorRow.Pattern,
						editorRow.PatternRow,
						channel),
				PatternAuditionKind.Row =>
					PatternAuditionCompiler.CompileRow(
						editorRow.Pattern,
						editorRow.PatternRow),
				_ =>
					throw new ArgumentOutOfRangeException(
						nameof(kind)),
			};

		try
		{
			if (_audition is null)
			{
				_message.Text =
					"Realtime audition is not configured in this host.";
			}
			else
			{
				await _audition(schedule);
				_message.Text =
					kind == PatternAuditionKind.Note
						? "Auditioned current note."
						: "Auditioned current row.";
			}
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Audition failed: {ex.Message}";
		}
		finally
		{
			PatternEditorContextCursor.MoveDown(
				_context,
				_cursor);
			UpdateCurrentPatternControls();
			RefreshCursorVisuals();
			FocusCursorCell();
		}
	}

	private async void OnOwnerDeactivated(
		object? sender,
		EventArgs e)
	{
		_ = sender;
		_ = e;
		await ReleaseAllHeldPreviewsAsync();
	}

	protected override void OnDetachedFromVisualTree(
		Avalonia.VisualTreeAttachmentEventArgs e)
	{
		_owner.Deactivated -= OnOwnerDeactivated;
		_ = ReleaseAllHeldPreviewsAsync();
		base.OnDetachedFromVisualTree(e);
	}

	private async Task ReleaseAllHeldPreviewsAsync()
	{
		IReadOnlyList<ReleaseHeldNotePreviewAction> releases =
			_heldPreviewKeys.ReleaseAll();

		if (_liveAudition is null)
			return;

		foreach (ReleaseHeldNotePreviewAction release in releases)
		{
			try
			{
				await _liveAudition.SendEventAsync(
					PatternLiveEventCompiler.CompileHeldPreviewRelease(
						(uint)release.VoiceId));
			}
			catch (Exception ex)
			{
				_message.Text =
					$"Preview release failed: {ex.Message}";
			}
		}
	}

	private async Task OnCellKeyUpAsync(
		KeyEventArgs e)
	{
		HeldNotePreviewAction? action =
			_heldPreviewKeys.KeyUp(
				e.PhysicalKey);
		if (action is not ReleaseHeldNotePreviewAction release)
			return;

		e.Handled = true;
		if (_liveAudition is null)
			return;

		try
		{
			await _liveAudition.SendEventAsync(
				PatternLiveEventCompiler.CompileHeldPreviewRelease(
					(uint)release.VoiceId));
			_message.Text =
				"Released preview note.";
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Preview release failed: {ex.Message}";
		}
	}

	private async Task StartHeldPreviewAsync(
		PatternEditorRow editorRow,
		int channel,
		StartHeldNotePreviewAction preview)
	{
		if (_liveAudition is null)
			return;

		try
		{
			var liveEvent =
				PatternLiveEventCompiler.CompileHeldPreviewStart(
					editorRow.Pattern,
					editorRow.PatternRow,
					channel,
					(uint)preview.VoiceId,
					preview.PitchMultiplier);

			if (liveEvent is null)
			{
				_message.Text =
					"Preview note has no resolved Source.";
				return;
			}

			await _liveAudition.SendEventAsync(liveEvent);
			_message.Text =
				"Previewing tracker note; release the key for Note Off.";
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Preview failed: {ex.Message}";
		}
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
		if (value == ','
			&& PatternEditMaskEditor.TryToggle(
				_noteInputState.EditMask,
				_cursor.Field,
				out PatternEditMask editMask))
		{
			_noteInputState.EditMask = editMask;
			UpdateEditStateDisplay();
			_message.Text =
				$"Edit mask: {PatternEditMaskEditor.Describe(editMask)}.";
			e.Handled = true;
			FocusCursorCell();
			return;
		}

		if (PatternPatternNavigationKeyboard.TryGetDelta(
			value,
			out int patternDelta))
		{
			SwitchPattern(patternDelta);
			e.Handled = true;
			return;
		}

		if (PatternSourceNavigationKeyboard.TryGetDelta(
			value,
			out int sourceDelta))
		{
			MoveCurrentSource(sourceDelta);
			e.Handled = true;
			FocusCursorCell();
			return;
		}

		int editedRow = _cursor.Row;
		int editedChannel = _cursor.Channel;
		PatternEditorRow edited = _context.GetRow(editedRow);

		if (_cursor.Field == PatternCellField.Note)
		{
			// Tracker notes are driven by KeyDown/PhysicalKey so their piano
			// geometry is independent of the active keyboard layout. Consuming
			// TextInput here also prevents a handled physical key from entering
			// the same note a second time through its produced text symbol.
			e.Handled = true;
			return;
		}

		if (_cursor.Field == PatternCellField.Source)
		{
			if (value == '.')
			{
				ClearSource(row, channel);
				e.Handled = true;
			}
			return;
		}

		if (_cursor.Field == PatternCellField.Volume)
		{
			PatternVolumeInputResult volumeResult =
				PatternEditorContextCursor.EditCurrent(
					_context,
					_cursor,
					mapped =>
						PatternVolumeKeyboardEditor.Type(
							_workspace,
							mapped.Pattern,
							_cursor,
							_volumeInput,
							value));

			if (!volumeResult.Handled)
				return;

			if (volumeResult.Rejected)
			{
				_message.Text =
					"Volume must be entered as a decimal tracker value from 00 through 64.";
			}
			else if (volumeResult.Completed)
			{
				_noteInputState.CurrentVolume =
					edited.Pattern.Grid[
						edited.PatternRow,
						editedChannel]?.Volume;
				UpdateEditStateDisplay();
			}

			if (!volumeResult.Rejected && volumeResult.Changed)
			{
				RefreshUnderlyingCell(
					edited.Pattern,
					edited.PatternRow,
					editedChannel);
				_changed(
					$"Edited volume in {edited.Pattern.Name} row {edited.PatternRow}, channel {editedChannel + 1}");
			}

			UpdateCurrentPatternControls();
			RefreshCursorVisuals();
			FocusCursorCell();
			e.Handled = true;
			return;
		}

		PatternEffectInputResult result =
			PatternEditorContextCursor.EditCurrent(
				_context,
				_cursor,
				mapped =>
					PatternEffectKeyboardEditor.Type(
						_workspace,
						mapped.Pattern,
						_cursor,
						value));

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
			RefreshUnderlyingCell(
				edited.Pattern,
				edited.PatternRow,
				editedChannel);
			_changed(
				$"Edited effect in {edited.Pattern.Name} row {edited.PatternRow}, channel {editedChannel + 1}");
		}

		UpdateCurrentPatternControls();
		RefreshCursorVisuals();
		FocusCursorCell();
		e.Handled = true;
	}

	private bool SwitchStandalonePatternBoundary(
		bool firstRow)
	{
		if (_switchPattern is null
			|| _context.Rows.Count == 0)
		{
			return false;
		}

		_switchPattern(
			new PatternEditorSwitchRequest(
				firstRow ? -1 : 1,
				new PatternEditorOpenState(
					_noteInputState.SourceId,
					_noteInputState.BaseOctave,
					firstRow ? 0 : int.MaxValue,
					_cursor.Channel,
					_cursor.Field,
					_noteInputState.EditMask,
					_noteInputState.CurrentVolume,
					_chordInputState.IsActive
						? _chordInputState.CreateSnapshot()
						: null,
					_noteInputState.SkipRows)));
		return true;
	}

	private void SwitchPattern(
		int delta)
	{
		if (_context.IsSequence)
		{
			if (_context.Rows.Count == 0)
			{
				_message.Text =
					"This sequence view has no editable pattern rows.";
				return;
			}

			int? targetDisplayRow =
				_context.FindAdjacentPatternDisplayRow(
					_cursor.Row,
					delta);
			if (targetDisplayRow is not int target)
			{
				_message.Text =
					delta > 0
						? "There is no next editable pattern in this sequence."
						: "There is no previous editable pattern in this sequence.";
				return;
			}

			PatternCellField field = _cursor.Field;
			CollapseVisualEffects(collapseCursor: true);
			PatternEditorRow targetRow = _context.GetRow(target);
			int channel =
				Math.Min(
					_cursor.Channel,
					targetRow.Pattern.ChannelCount - 1);
			_cursor.SetPosition(
				target,
				channel,
				field);
			_volumeInput.Reset();
			UpdateCurrentPatternControls();
			RefreshCursorVisuals();
			FocusCursorCell();
			_message.Text =
				$"Switched to {targetRow.Pattern.Name}.";
			return;
		}

		if (_switchPattern is null)
		{
			_message.Text =
				"Pattern switching is not available in this editor context.";
			return;
		}

		PatternEditorRow currentRow =
			_context.GetRow(_cursor.Row);
		_switchPattern(
			new PatternEditorSwitchRequest(
				delta,
				new PatternEditorOpenState(
					_noteInputState.SourceId,
					_noteInputState.BaseOctave,
					currentRow.PatternRow,
					_cursor.Channel,
					_cursor.Field,
					_noteInputState.EditMask,
					_noteInputState.CurrentVolume,
					_chordInputState.IsActive
						? _chordInputState.CreateSnapshot()
						: null,
					_noteInputState.SkipRows)));
	}

	private void HandleChordCommand(
		PatternChordCommand command)
	{
		switch (command.Kind)
		{
			case PatternChordCommandKind.Exit:
				_chordInputState.Exit();
				_message.Text =
					"Exited chord mode; single-note entry is active.";
				break;

			case PatternChordCommandKind.Select:
				_chordInputState.Select(
					command.ChordType
						?? throw new InvalidOperationException(
							"Chord-selection command has no chord type."));
				_message.Text =
					$"Chord set to {FormatChordType(_chordInputState.Type!.Value)}.";
				break;

			case PatternChordCommandKind.RotateFirstToEnd:
				if (!RequireActiveChord())
					return;
				_chordInputState.RotateFirstToEnd();
				_message.Text =
					"Rotated first chord tone to the end.";
				break;

			case PatternChordCommandKind.RotateLastToBeginning:
				if (!RequireActiveChord())
					return;
				_chordInputState.RotateLastToBeginning();
				_message.Text =
					"Rotated last chord tone to the beginning.";
				break;

			case PatternChordCommandKind.AddTone:
				if (!RequireActiveChord())
					return;
				_chordInputState.AddTone();
				_message.Text =
					"Added one repeated chord tone.";
				break;

			case PatternChordCommandKind.RemoveTone:
				if (!RequireActiveChord())
					return;
				_chordInputState.RemoveTone();
				_message.Text =
					"Removed one chord tone.";
				break;

			case PatternChordCommandKind.ToggleTone:
				if (!RequireActiveChord())
					return;
				if (!_chordInputState.ToggleTone(
					command.ToneIndex))
				{
					_message.Text =
						$"Chord tone {command.ToneIndex + 1} does not currently exist.";
				}
				else
				{
					_message.Text =
						$"Toggled chord tone {command.ToneIndex + 1}.";
				}
				break;

			case PatternChordCommandKind.EnableAll:
				if (!RequireActiveChord())
					return;
				_chordInputState.EnableAll();
				_message.Text =
					"Enabled all current chord tones.";
				break;

			default:
				throw new ArgumentOutOfRangeException(
					nameof(command));
		}
	}

	private bool RequireActiveChord()
	{
		if (_chordInputState.IsActive)
			return true;

		_message.Text =
			"Choose a chord type first with Ctrl+Alt+Z/X/C/V/B/N/M.";
		return false;
	}

	private void UpdateChordStatus()
	{
		_chordStatus.Children.Clear();
		_chordStatus.Children.Add(
			new TextBlock
			{
				Text = "Chord:",
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			});

		if (!_chordInputState.IsActive)
		{
			_chordStatus.Children.Add(
				new TextBlock
				{
					Text = "—",
					VerticalAlignment = VerticalAlignment.Center,
				});
			return;
		}

		_chordStatus.Children.Add(
			new TextBlock
			{
				Text =
					FormatChordType(
						_chordInputState.Type!.Value),
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			});

		IReadOnlyList<PatternChordStatusTone> tones =
			_chordInputState.GetStatusTones();
		if (tones.Count == 0)
		{
			_chordStatus.Children.Add(
				new TextBlock
				{
					Text = "— press a tracker note for root —",
					Opacity = 0.65,
					VerticalAlignment = VerticalAlignment.Center,
				});
			return;
		}

		foreach (PatternChordStatusTone tone in tones)
		{
			_chordStatus.Children.Add(
				new TextBlock
				{
					Text = tone.NoteText,
					// Do not set Foreground locally: the TextBlock must inherit
					// the current theme/dialog foreground in both light and dark
					// modes. Disabled tones are distinguished by opacity only.
					Opacity =
						tone.Enabled
							? 1.0
							: 0.55,
					VerticalAlignment = VerticalAlignment.Center,
				});
		}
	}

	private static string FormatChordType(
		PatternChordType type)
		=> type switch
		{
			PatternChordType.Major =>
				"Major",
			PatternChordType.Minor =>
				"Minor",
			PatternChordType.DominantSeventh =>
				"Dominant 7",
			PatternChordType.MajorSeventh =>
				"Major 7",
			PatternChordType.MinorSeventh =>
				"Minor 7",
			PatternChordType.HalfDiminishedSeventh =>
				"Half-diminished 7",
			PatternChordType.DiminishedSeventh =>
				"Diminished 7",
			_ =>
				throw new ArgumentOutOfRangeException(
					nameof(type)),
		};

	private void UpdateEditStateDisplay()
	{
		_editMaskDisplay.Text =
			PatternEditMaskEditor.Describe(
				_noteInputState.EditMask);
		_editVolumeDisplay.Text =
			_noteInputState.CurrentVolume is double volume
				? Math.Round(
					volume * 64.0,
					MidpointRounding.AwayFromZero)
					.ToString(
						"00",
						CultureInfo.InvariantCulture)
				: "..";
	}

	private void MoveCurrentSource(
		int delta)
	{
		PatternSourceOption? current =
			_noteSource.SelectedItem as PatternSourceOption;
		PatternSourceOption? selected =
			PatternSourceNavigation.Move(
				_noteSources,
				current,
				delta);

		if (selected is null)
		{
			_message.Text =
				"No sound sources are available.";
			return;
		}

		_noteSource.SelectedItem = selected;
		_message.Text =
			$"Current Source set to {selected.DisplayName}.";
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
					bool changed =
						PatternEditorContextCursor.EditCurrent(
							_context,
							_cursor,
							mapped =>
								PatternEffectStackEditor.MoveSelected(
									_workspace,
									mapped.Pattern,
									_cursor,
									delta: -1));
					FinishStackMutation(
						row,
						channel,
						changed,
						"Moved effect left");
				}
				return true;

			case Key.Right:
				if (_cursor.IsExpanded)
				{
					bool changed =
						PatternEditorContextCursor.EditCurrent(
							_context,
							_cursor,
							mapped =>
								PatternEffectStackEditor.MoveSelected(
									_workspace,
									mapped.Pattern,
									_cursor,
									delta: 1));
					FinishStackMutation(
						row,
						channel,
						changed,
						"Moved effect right");
				}
				return true;

			case Key.Delete:
				if (_cursor.Field is not (
					PatternCellField.EffectCommand
						or PatternCellField.EffectParameter))
				{
					return false;
				}
				bool deleted =
					PatternEditorContextCursor.EditCurrent(
						_context,
						_cursor,
						mapped =>
							PatternEffectStackEditor.Delete(
								_workspace,
								mapped.Pattern,
								_cursor));
				FinishStackMutation(
					row,
					channel,
					deleted,
					"Deleted effect");
				return true;

			case Key.Insert:
				if (_cursor.Field is not (
					PatternCellField.EffectCommand
						or PatternCellField.EffectParameter))
				{
					return false;
				}
				bool inserted =
					PatternEditorContextCursor.EditCurrent(
						_context,
						_cursor,
						mapped =>
							shift
								? PatternEffectStackEditor.InsertAfter(
									_workspace,
									mapped.Pattern,
									_cursor)
								: PatternEffectStackEditor.InsertBefore(
									_workspace,
									mapped.Pattern,
									_cursor));
				FinishStackMutation(
					row,
					channel,
					inserted,
					shift
						? "Inserted effect after"
						: "Inserted effect before");
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

	private async Task HandlePatternRegionClipboardAsync(
		int row,
		int channel,
		bool effectField,
		PatternRegionClipboardCommand command)
	{
		switch (command)
		{
			case PatternRegionClipboardCommand.Copy:
				if (_selection.Region is PatternSelectionRegion copyRegion)
				{
					await CopyPatternRegionAsync(
						copyRegion,
						cut: false);
				}
				else if (effectField)
				{
					await CopyEffectStackAsync(
						row,
						channel);
				}
				else
				{
					_message.Text =
						"No pattern region is marked.";
					FocusCursorCell();
				}
				break;

			case PatternRegionClipboardCommand.Cut:
				if (_selection.Region is PatternSelectionRegion cutRegion)
				{
					await CopyPatternRegionAsync(
						cutRegion,
						cut: true);
				}
				else
				{
					_message.Text =
						"No pattern region is marked.";
					FocusCursorCell();
				}
				break;

			case PatternRegionClipboardCommand.PasteMerge:
				await PastePatternRegionAsync(
					row,
					channel,
					PatternRegionPasteMode.Merge,
					fallbackToEffectStack: effectField);
				break;

			case PatternRegionClipboardCommand.PasteOverwrite:
				await PastePatternRegionAsync(
					row,
					channel,
					PatternRegionPasteMode.Overwrite,
					fallbackToEffectStack: false);
				break;

			case PatternRegionClipboardCommand.Clear:
				ClearPatternRegion();
				break;

			default:
				throw new ArgumentOutOfRangeException(
					nameof(command));
		}
	}

	private async Task CopyPatternRegionAsync(
		PatternSelectionRegion region,
		bool cut)
	{
		try
		{
			if (_owner.Clipboard is null)
			{
				_message.Text =
					"The system clipboard is not available.";
				return;
			}

			PatternRegionClipboardData data =
				PatternRegionClipboardEditor.Capture(
					_context,
					region);
			string text =
				PatternRegionClipboardCodec.Serialize(data);
			await _owner.Clipboard.SetTextAsync(text);

			bool changed = false;
			if (cut)
			{
				changed =
					PatternRegionClipboardEditor.Clear(
						_workspace,
						_context,
						region);
				if (changed)
				{
					_changed(
						"Cut marked pattern region");
					RefreshGrid();
				}
			}

			_message.Text =
				cut
					? changed
						? $"Cut {data.RowCount}×{data.ChannelCount} pattern region."
						: $"Copied {data.RowCount}×{data.ChannelCount} empty pattern region; nothing needed clearing."
					: $"Copied {data.RowCount}×{data.ChannelCount} pattern region.";
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Could not {(cut ? "cut" : "copy")} pattern region: {ex.Message}";
		}
		finally
		{
			FocusCursorCell();
		}
	}

	private async Task PastePatternRegionAsync(
		int row,
		int channel,
		PatternRegionPasteMode mode,
		bool fallbackToEffectStack)
	{
		try
		{
			string? text =
				_owner.Clipboard is null
					? null
					: await _owner.Clipboard.TryGetTextAsync();
			if (text is null)
			{
				_message.Text =
					"The clipboard does not contain text.";
				return;
			}

			if (!PatternRegionClipboardCodec.TryDeserialize(
				text,
				out PatternRegionClipboardData? data))
			{
				if (fallbackToEffectStack)
				{
					await PasteEffectStackAsync(
						row,
						channel);
					return;
				}

				_message.Text =
					"The clipboard does not contain a Heresy pattern region.";
				return;
			}

			bool changed =
				PatternRegionClipboardEditor.Paste(
					_workspace,
					_context,
					_cursor,
					data!,
					mode);
			if (changed)
			{
				_changed(
					mode == PatternRegionPasteMode.Merge
						? "Merged pattern region"
						: "Overwrote pattern region");
				RefreshGrid();
			}
			else
			{
				RefreshCursorVisuals();
			}

			_message.Text =
				changed
					? mode == PatternRegionPasteMode.Merge
						? "Merged clipboard region at the tracker cursor."
						: "Overwrote from clipboard region at the tracker cursor."
					: "Clipboard region produced no changes at this position.";
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Could not paste pattern region: {ex.Message}";
		}
		finally
		{
			FocusCursorCell();
		}
	}

	private void ClearPatternRegion()
	{
		if (_selection.Region is not PatternSelectionRegion region)
		{
			_message.Text =
				"No pattern region is marked.";
			FocusCursorCell();
			return;
		}

		try
		{
			bool changed =
				PatternRegionClipboardEditor.Clear(
					_workspace,
					_context,
					region);
			if (changed)
			{
				_changed(
					"Cleared marked pattern region");
				RefreshGrid();
				_message.Text =
					"Cleared marked pattern region.";
			}
			else
			{
				_message.Text =
					"The marked pattern region is already empty.";
				RefreshCursorVisuals();
				FocusCursorCell();
			}
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Could not clear pattern region: {ex.Message}";
			FocusCursorCell();
		}
	}

	private async Task CopyEffectStackAsync(
		int row,
		int channel)
	{
		try
		{
			PatternEditorRow editorRow = _context.GetRow(row);
			PatternCell? cell =
				editorRow.Pattern.Grid[editorRow.PatternRow, channel];
			IEnumerable<PatternEffect> effects =
				cell is null
					? Array.Empty<PatternEffect>()
					: cell.Effects;
			string text =
				PatternEffectClipboardCodec.Serialize(effects);

			if (_owner.Clipboard != null)
				await _owner.Clipboard.SetTextAsync(text);
			_message.Text =
				$"Copied {cell?.Effects.Count ?? 0} effect(s) from {editorRow.Pattern.Name} row {editorRow.PatternRow}, channel {channel + 1}.";
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Could not copy effect stack: {ex.Message}";
		}
		finally
		{
			FocusCursorCell();
		}
	}

	private async Task PasteEffectStackAsync(
		int row,
		int channel)
	{
		try
		{
			string? text =
				_owner.Clipboard == null
				? null
				: await _owner.Clipboard.TryGetTextAsync();

			if (text is null)
			{
				_message.Text =
					"The clipboard does not contain text.";
				return;
			}

			PatternEffect[] effects =
				PatternEffectClipboardCodec.Deserialize(text);
			_cursor.SetPosition(
				row,
				channel,
				PatternCellField.EffectCommand);
			PatternEditorRow editorRow = _context.GetRow(row);
			bool changed =
				PatternEditorContextCursor.EditCurrent(
					_context,
					_cursor,
					mapped =>
						PatternEffectStackEditor.ReplaceAll(
							_workspace,
							mapped.Pattern,
							_cursor,
							effects));

			_expandedCell =
				_cursor.IsExpanded
					? (row, channel)
					: null;

			if (changed)
			{
				RefreshUnderlyingCell(
					editorRow.Pattern,
					editorRow.PatternRow,
					channel);
				_changed(
					$"Pasted effect stack into {editorRow.Pattern.Name} row {editorRow.PatternRow}, channel {channel + 1}");
			}

			_message.Text =
				changed
					? $"Pasted {effects.Length} effect(s)."
					: "The destination already has that effect stack.";
			RefreshCursorVisuals();
		}
		catch (Exception ex)
		{
			_message.Text =
				$"Could not paste effect stack: {ex.Message}";
		}
		finally
		{
			FocusCursorCell();
		}
	}

	private async Task InsertNativeEffectAtAsync(
		int row,
		int channel,
		int? effectIndex,
		bool after)
	{
		if (effectIndex.HasValue)
		{
			if (!SelectEffectForStackCommand(
				row,
				channel,
				effectIndex.Value))
			{
				return;
			}
		}
		else
		{
			_cursor.SetPosition(
				row,
				channel,
				PatternCellField.EffectCommand);
		}

		NativePatternEffectEditorDialog dialog = new();
		NativePatternEffectEditResult? result =
			await dialog.ShowDialog<NativePatternEffectEditResult?>(
				_owner);
		if (result is null)
		{
			FocusCursorCell();
			return;
		}

		PatternEditorRow editorRow = _context.GetRow(row);
		bool changed =
			PatternEditorContextCursor.EditCurrent(
				_context,
				_cursor,
				mapped =>
					after && effectIndex.HasValue
						? PatternEffectStackEditor.InsertAfter(
							_workspace,
							mapped.Pattern,
							_cursor,
							result.Effect)
						: PatternEffectStackEditor.InsertBefore(
							_workspace,
							mapped.Pattern,
							_cursor,
							result.Effect));

		if (changed)
		{
			RefreshUnderlyingCell(
				editorRow.Pattern,
				editorRow.PatternRow,
				channel);
			_changed(
				$"Inserted native effect in {editorRow.Pattern.Name} row {editorRow.PatternRow}, channel {channel + 1}");
		}

		RefreshCursorVisuals();
		FocusCursorCell();
	}

	private async Task EditNativeEffectAtAsync(
		int row,
		int channel,
		int effectIndex)
	{
		PatternEditorRow editorRow = _context.GetRow(row);
		PatternCell? cell =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel];
		if (cell is null
			|| (uint)effectIndex >= (uint)cell.Effects.Count)
		{
			return;
		}

		PatternEffect effect = cell.Effects[effectIndex];
		if (!NativePatternEffectEditor.CanEdit(effect))
			return;

		_cursor.SetPosition(
			row,
			channel,
			PatternCellField.EffectCommand);
		_cursor.SetExpandedSelection(
			cell,
			effectIndex,
			ExpandedEffectField.Native);
		ExpandVisualEffects(row, channel);
		RefreshCursorVisuals();

		NativePatternEffectEditorDialog dialog =
			new(effect);
		NativePatternEffectEditResult? result =
			await dialog.ShowDialog<NativePatternEffectEditResult?>(
				_owner);
		if (result is null)
		{
			FocusCursorCell();
			return;
		}

		bool changed =
			PatternEditorContextCursor.EditCurrent(
				_context,
				_cursor,
				mapped =>
					PatternEffectStackEditor.ReplaceSelected(
						_workspace,
						mapped.Pattern,
						_cursor,
						result.Effect));
		if (changed)
		{
			RefreshUnderlyingCell(
				editorRow.Pattern,
				editorRow.PatternRow,
				channel);
			_changed(
				$"Edited native effect in {editorRow.Pattern.Name} row {editorRow.PatternRow}, channel {channel + 1}");
		}

		RefreshCursorVisuals();
		FocusCursorCell();
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

		bool changed =
			PatternEditorContextCursor.EditCurrent(
				_context,
				_cursor,
				mapped =>
					PatternEffectStackEditor.Delete(
						_workspace,
						mapped.Pattern,
						_cursor));
		FinishStackMutation(
			row,
			channel,
			changed,
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
			PatternEditorContextCursor.EditCurrent(
				_context,
				_cursor,
				mapped =>
					after
						? PatternEffectStackEditor.InsertAfter(
							_workspace,
							mapped.Pattern,
							_cursor)
						: PatternEffectStackEditor.InsertBefore(
							_workspace,
							mapped.Pattern,
							_cursor));
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

		bool changed =
			PatternEditorContextCursor.EditCurrent(
				_context,
				_cursor,
				mapped =>
					PatternEffectStackEditor.MoveSelectedTo(
						_workspace,
						mapped.Pattern,
						_cursor,
						targetIndex));
		FinishStackMutation(
			row,
			channel,
			changed,
			"Reordered effect");
	}

	private bool SelectEffectForStackCommand(
		int row,
		int channel,
		int effectIndex)
	{
		PatternEditorRow editorRow = _context.GetRow(row);
		PatternCell? cell =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel];
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

		PatternEditorRow editorRow = _context.GetRow(row);
		if (_cursor.IsExpanded)
			_expandedCell = (row, channel);
		else if (_expandedCell == (row, channel))
			CollapseVisualEffects(collapseCursor: false);

		RefreshUnderlyingCell(
			editorRow.Pattern,
			editorRow.PatternRow,
			channel);
		RefreshCursorVisuals();
		FocusCursorCell();
		_changed(
			$"{message} in {editorRow.Pattern.Name} row {editorRow.PatternRow}, channel {channel + 1}");
	}

	private void SelectExpandedEffect(
		int row,
		int channel,
		int effectIndex,
		ExpandedEffectField field)
	{
		PatternEditorRow editorRow = _context.GetRow(row);
		PatternCell? cell =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel];
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
		PatternEditorRow editorRow = _context.GetRow(row);
		PatternCell? cell =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel];
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
		PatternEditorRow editorRow = _context.GetRow(row);
		PatternCell? cell =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel];
		PatternNoteEditorDialog dialog =
			new(
				_workspace.Document,
				editorRow.PatternRow,
				channel,
				cell?.Note);
		PatternNoteEditResult? result =
			await dialog.ShowDialog<PatternNoteEditResult?>(_owner);
		if (result is null)
			return;

		PatternDocumentEditor.SetNote(
			_workspace,
			editorRow.Pattern,
			editorRow.PatternRow,
			channel,
			result.Note);
		RefreshUnderlyingCell(
			editorRow.Pattern,
			editorRow.PatternRow,
			channel);
		RefreshCursorVisuals();
		_changed(
			$"Edited {editorRow.Pattern.Name} row {editorRow.PatternRow}, channel {channel + 1}");
	}

	private void SetSource(
		int row,
		int channel,
		Heresy.Core.Objects.ObjectId sourceId)
	{
		PatternEditorRow editorRow = _context.GetRow(row);
		Heresy.Core.Objects.ObjectId previous =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel]?.SourceId
				?? Heresy.Core.Objects.ObjectId.None;
		if (previous == sourceId)
		{
			FocusCursorCell();
			return;
		}

		PatternDocumentEditor.SetSource(
			_workspace,
			editorRow.Pattern,
			editorRow.PatternRow,
			channel,
			sourceId);
		RefreshUnderlyingCell(
			editorRow.Pattern,
			editorRow.PatternRow,
			channel);
		RefreshCursorVisuals();
		_changed(
			$"Edited source in {editorRow.Pattern.Name} row {editorRow.PatternRow}, channel {channel + 1}");
		FocusCursorCell();
	}

	private void ClearSource(int row, int channel)
	{
		SetSource(
			row,
			channel,
			Heresy.Core.Objects.ObjectId.None);
	}

	private void OpenSourcePopup(
		int row,
		int channel,
		Border placementTarget)
	{
		PatternEditorRow editorRow = _context.GetRow(row);
		PatternSourceOption[] options =
			PatternSourceCatalog.GetSources(_workspace.Document);
		ListBox list =
			new()
			{
				ItemsSource = options,
				Width = 300,
				MaxHeight = 320,
			};

		Heresy.Core.Objects.ObjectId current =
			editorRow.Pattern.Grid[editorRow.PatternRow, channel]?.SourceId
				?? Heresy.Core.Objects.ObjectId.None;
		foreach (PatternSourceOption option in options)
		{
			if (option.Id == current)
			{
				list.SelectedItem = option;
				break;
			}
		}

		Popup popup =
			new()
			{
				PlacementTarget = placementTarget,
				Placement = PlacementMode.Bottom,
				IsLightDismissEnabled = true,
				Child = new Border
				{
					Padding = new Thickness(4),
					Child = list,
				},
			};
		list.SelectionChanged += (_, _) =>
		{
			if (list.SelectedItem is not PatternSourceOption selected)
				return;

			SetSource(row, channel, selected.Id);
			popup.IsOpen = false;
		};
		popup.Closed += (_, _) => FocusCursorCell();
		popup.IsOpen = true;
		list.Focus();
	}

	private void RefreshCell(int row, int channel)
	{
		if (!_noteTexts.TryGetValue(
			(row, channel),
			out TextBlock? note)
			|| !_sourceTexts.TryGetValue(
				(row, channel),
				out TextBlock? source)
			|| !_volumeTexts.TryGetValue(
				(row, channel),
				out TextBlock? volume)
			|| !_effectStrips.TryGetValue(
				(row, channel),
				out PatternEffectStripControl? effects))
		{
			return;
		}

		PatternEditorRow editorRow = _context.GetRow(row);
		PatternCellViewModel view =
			PatternCellViewModel.Create(
				_workspace.Document,
				editorRow.Pattern,
				editorRow.PatternRow,
				channel);
		note.Text = view.NoteText;
		source.Text = view.SourceText;
		if (_sourceFields.TryGetValue((row, channel), out Border? sourceField))
			ToolTip.SetTip(sourceField, view.SourceText);
		volume.Text = view.VolumeText;
		effects.SetEffects(view.Effects);
		RefreshCellEffectState(row, channel);
	}

	private void RefreshUnderlyingCell(
		DataPatternDefinition pattern,
		int patternRow,
		int channel)
	{
		foreach (int displayRow in
			_context.FindDisplayRows(pattern, patternRow))
		{
			RefreshCell(displayRow, channel);
		}
	}

	private void RefreshCursorVisuals()
	{
		foreach (((int row, int channel), Border border) in _cellBorders)
		{
			bool active =
				_context.Rows.Count > 0
					&& row == _cursor.Row
					&& channel == _cursor.Channel;
			bool selected =
				_selection.Region is PatternSelectionRegion selection
					&& selection.Contains(row, channel);
			if (_context.Rows.Count > 0)
			{
				PatternEditorRow editorRow =
					_context.GetRow(row);
				border.Background =
					_playbackDisplayRows.Contains(row)
						? new SolidColorBrush(
							_configuration.PatternPlaybackRowHighlight)
						: selected
							? new SolidColorBrush(
								_configuration.PatternSelectionHighlight)
							: GetRowBackground(
								editorRow.Pattern,
								editorRow.PatternRow)
								?? Brushes.Transparent;
			}
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
			if (_sourceFields.TryGetValue(
				(row, channel),
				out Border? sourceField))
			{
				bool sourceActive =
					active && _cursor.Field == PatternCellField.Source;
				sourceField.BorderBrush =
					sourceActive ? Brushes.DeepSkyBlue : Brushes.Transparent;
				sourceField.BorderThickness =
					sourceActive ? new Thickness(2) : new Thickness(1);
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
			_context.Rows.Count > 0
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
		if (_context.Rows.Count == 0)
			return;

		if (_cellBorders.TryGetValue(
			(_cursor.Row, _cursor.Channel),
			out Border? border))
		{
			border.Focus();
		}
	}

	internal PatternEditorPlaybackCursor GetPlaybackCursor()
	{
		if (_context.Rows.Count == 0)
		{
			throw new InvalidOperationException(
				"The current pattern view has no playable row.");
		}

		return _context.GetPlaybackCursor(_cursor.Row);
	}

	private DataPatternDefinition GetCurrentPattern()
	{
		if (_context.Rows.Count != 0
			&& (uint)_cursor.Row < (uint)_context.Rows.Count)
		{
			return _context.GetRow(_cursor.Row).Pattern;
		}

		return GetInitialPattern(_context);
	}

	private static DataPatternDefinition GetInitialPattern(
		PatternEditorContext context)
	{
		if (context.Rows.Count != 0)
			return context.GetRow(context.InitialDisplayRow).Pattern;

		foreach (PatternEditorSegment segment in context.Segments)
		{
			if (segment.Pattern is not null)
				return segment.Pattern;
		}

		throw new InvalidOperationException(
			"The pattern editor context contains no data pattern to edit.");
	}

	private void UpdateCurrentPatternControls()
	{
		DataPatternDefinition pattern = GetCurrentPattern();
		if (_context.IsSequence
			&& _context.Rows.Count != 0
			&& _context.GetRow(_cursor.Row).SequenceEntryIndex is int order)
		{
			_title.Text =
				$"{_context.DisplayName} — {pattern.Name} (order {order:D2})";
		}
		else
		{
			_title.Text = pattern.Name;
		}

		_rowCount.Text =
			pattern.RowCount.ToString(CultureInfo.CurrentCulture);
		_channelCount.Text =
			pattern.ChannelCount.ToString(CultureInfo.CurrentCulture);
		_minorHighlight.Text =
			pattern.MinorHighlightRows.ToString(CultureInfo.CurrentCulture);
		_majorHighlight.Text =
			pattern.MajorHighlightRows.ToString(CultureInfo.CurrentCulture);
	}

	private void RemapPlaybackDisplayRows()
	{
		_playbackDisplayRows.Clear();
		if (_playbackPosition is not PlaybackPatternPosition position)
			return;

		foreach (int displayRow in
			_context.FindPlaybackDisplayRows(
				position.PatternId,
				position.PatternRow,
				position.SequenceId,
				position.SequenceEntryIndex))
		{
			_playbackDisplayRows.Add(displayRow);
		}
	}

	private void RefreshPlaybackRowHeaders()
	{
		foreach ((int displayRow, Border header) in _rowHeaders)
		{
			if ((uint)displayRow >= (uint)_context.Rows.Count)
				continue;

			PatternEditorRow row =
				_context.GetRow(displayRow);
			header.Background =
				GetDisplayRowBackground(
					displayRow,
					row.Pattern,
					row.PatternRow)
					?? Brushes.Transparent;
		}
	}

	private IBrush? GetDisplayRowBackground(
		int displayRow,
		DataPatternDefinition pattern,
		int row)
	{
		if (_playbackDisplayRows.Contains(displayRow))
		{
			return new SolidColorBrush(
				_configuration.PatternPlaybackRowHighlight);
		}

		return GetRowBackground(
			pattern,
			row);
	}

	private IBrush? GetRowBackground(
		DataPatternDefinition pattern,
		int row)
	{
		if (pattern.MajorHighlightRows > 0
			&& row % pattern.MajorHighlightRows == 0)
		{
			return new SolidColorBrush(
				_configuration.MajorPatternRowHighlight);
		}

		if (pattern.MinorHighlightRows > 0
			&& row % pattern.MinorHighlightRows == 0)
		{
			return new SolidColorBrush(
				_configuration.MinorPatternRowHighlight);
		}

		return null;
	}

	private void AddPlaybackRowHeader(
		Grid grid,
		int displayRow,
		int patternRow,
		int gridRow,
		IBrush? background)
	{
		Border header =
			new()
			{
				Background =
					background
						?? Brushes.Transparent,
				Child =
					new TextBlock
					{
						Text =
							patternRow.ToString(
								"X2",
								CultureInfo.InvariantCulture),
						FontWeight = FontWeight.Normal,
						Margin = new Thickness(5, 4),
						VerticalAlignment =
							VerticalAlignment.Center,
					},
			};
		Grid.SetRow(
			header,
			gridRow);
		Grid.SetColumn(
			header,
			0);
		grid.Children.Add(header);
		_rowHeaders[displayRow] = header;
	}

	private static Border BuildUnavailableCell(
		IBrush? rowBackground)
		=> new()
		{
			Width = CellWidth,
			Height = RowHeight,
			BorderBrush = Brushes.Gray,
			BorderThickness = new Thickness(1),
			Background = rowBackground ?? Brushes.Transparent,
			Opacity = 0.25,
		};

	private static void AddSegmentHeader(
		Grid grid,
		PatternEditorSegment segment,
		int row,
		int columnSpan)
	{
		string order =
			segment.SequenceEntryIndex.HasValue
				? segment.SequenceEntryIndex.Value.ToString("D2", CultureInfo.InvariantCulture)
				: "--";
		string text =
			$"Order {order} — {segment.DisplayName} <{segment.PatternId.Value}> — start row {segment.StartRow}";
		if (!string.IsNullOrWhiteSpace(segment.Status))
			text += $" — {segment.Status}";

		Border border =
			new()
			{
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(0, 1, 0, 1),
				Padding = new Thickness(6, 4),
				Child = new TextBlock
				{
					Text = text,
					FontWeight = FontWeight.SemiBold,
					TextWrapping = TextWrapping.Wrap,
				},
			};
		Grid.SetRow(border, row);
		Grid.SetColumn(border, 0);
		Grid.SetColumnSpan(border, columnSpan);
		grid.Children.Add(border);
	}

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
		FontWeight weight,
		IBrush? background = null)
	{
		TextBlock block =
			new()
			{
				Text = text,
				FontWeight = weight,
				Margin = new Thickness(5, 4),
				VerticalAlignment = VerticalAlignment.Center,
			};
		Control visual =
			background is null
				? block
				: new Border
				{
					Background = background,
					Child = block,
				};
		Grid.SetRow(visual, row);
		Grid.SetColumn(visual, column);
		grid.Children.Add(visual);
	}
}

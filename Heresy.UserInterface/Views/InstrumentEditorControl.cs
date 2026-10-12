using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Heresy.Core.Envelopes;
using Heresy.Core.Filters;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Eleven-column projected instrument table. The Core's shared specification
/// list remains an implementation detail of InstrumentToneGridModel.
/// </summary>
public sealed class InstrumentEditorControl : UserControl
{
	private static readonly int[] ColumnWidths =
		[66, 114, 196, 108, 140, 190, 145, 145, 145, 145, 250];
	private static readonly string[] Headings =
		["", "", "Source", "Pitch", "", "NNA / fade (ms)", "Volume", "Pitch", "Panning", "Filter", "Tone Filter"];

	private readonly DocumentWorkspace _workspace;
	private readonly InstrumentDefinition _instrument;
	private readonly InstrumentToneGridModel _model;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly TextBox _divisions;
	private readonly TextBox _offset;
	private readonly ComboBox _nnaOverride;
	private readonly TextBox _nnaFadeMilliseconds;
	private InstrumentNnaOverride _loadedNnaOverride;
	private TimeSpan _loadedNnaFade;
	private readonly ListBox _rows;
	private readonly InstrumentToneGridSelection _selection = new();
	private InstrumentToneGridRow[] _projectedRows = [];
	private readonly Dictionary<InstrumentToneGridSelection.Cell,
		WeakReference<Border>> _visibleCells = [];
	private bool _movingGridFocus;
	private readonly TextBlock _message = new()
	{
		TextWrapping = TextWrapping.Wrap,
	};
	private PatternSourceOption[] _sources = [];
	private EnvelopeChoice[] _envelopes = [];
	private FilterChoice[] _filters = [];

	public InstrumentEditorControl(DocumentWorkspace workspace,
		InstrumentDefinition instrument, Action close, Action<string> changed)
	{
		_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		_instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
		_close = close ?? throw new ArgumentNullException(nameof(close));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		_model = new InstrumentToneGridModel(workspace, instrument);
		_divisions = NumberBox(instrument.Divisions);
		_offset = NumberBox(instrument.Offset);
		_nnaOverride = new ComboBox { Width = 155, ItemsSource = Enum.GetValues<InstrumentNnaOverride>(), SelectedItem = instrument.NnaOverride };
		_nnaFadeMilliseconds = new TextBox { Width = 78, Text = InstrumentNnaEditorValues.FormatMilliseconds(instrument.NnaFadeDuration), IsVisible = instrument.NnaOverride == InstrumentNnaOverride.Fade };
		_loadedNnaOverride = instrument.NnaOverride;
		_loadedNnaFade = instrument.NnaFadeDuration;
		_nnaOverride.SelectionChanged += (_, _) =>
		{
			bool fade = _nnaOverride.SelectedItem is InstrumentNnaOverride.Fade;
			_nnaFadeMilliseconds.IsVisible = fade;
			// These choices are complete without a separate numeric input.
			// Commit on selection, just like individual tone NNA cells:
			// otherwise an uncommitted Continue appears selected while
			// playback still uses the previously persisted Cut policy.
			// Fade remains explicitly applied after its duration is valid.
			if (!fade && _nnaOverride.SelectedItem is InstrumentNnaOverride)
				ApplyNnaOverride();
		};
		_nnaOverride.ItemTemplate = new FuncDataTemplate<InstrumentNnaOverride>(
			(action, _) => new TextBlock { Text = action == InstrumentNnaOverride.UseToneNna
				? "Use Tone NNA" : action.ToString() });
		_rows = new ListBox
		{
			MinWidth = ColumnWidths.Sum(),
			// Avalonia may invoke the template with null while clearing or
			// recycling a virtualized ListBox item container.
			ItemTemplate = new FuncDataTemplate<InstrumentToneGridRow>(
				(row, _) => row is null ? null : BuildRow(row)),
		};
		// Tunnel before TextBox/ComboBox consume arrows, but leave
		// editing and open popups to their native handlers when appropriate.
		_rows.AddHandler(InputElement.KeyDownEvent, OnGridKeyDown,
			RoutingStrategies.Tunnel, handledEventsToo: true);
		Content = BuildContent();
		RefreshRows();
	}

	private Control BuildContent()
	{
		Button back = new() { Content = "← Document", MinWidth = 100 };
		back.Click += (_, _) => _close();
		TextBlock title = new()
		{
			Text = _instrument.Name,
			FontSize = 20,
			FontWeight = FontWeight.SemiBold,
			VerticalAlignment = VerticalAlignment.Center,
		};
		DockPanel top = new() { Margin = new Thickness(10, 8) };
		DockPanel.SetDock(back, Dock.Left);
		top.Children.Add(back);
		top.Children.Add(title);

		Button apply = new() { Content = "Apply lookup" };
		apply.Click += (_, _) => ApplyLookup();
		StackPanel lookup = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6,
			Margin = new Thickness(10, 6),
		};
		lookup.Children.Add(Label("Divisions"));
		lookup.Children.Add(_divisions);
		lookup.Children.Add(Label("Offset"));
		lookup.Children.Add(_offset);
		lookup.Children.Add(apply);
		StackPanel nnaHeader = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 2) };
		nnaHeader.Children.Add(Label("Instrument NNA"));
		nnaHeader.Children.Add(_nnaOverride);
		TextBlock nnaFadeLabel = Label("Fade (ms)");
		nnaFadeLabel.IsVisible = _nnaFadeMilliseconds.IsVisible;
		_nnaOverride.SelectionChanged += (_, _) =>
			nnaFadeLabel.IsVisible = _nnaFadeMilliseconds.IsVisible;
		nnaHeader.Children.Add(nnaFadeLabel);
		nnaHeader.Children.Add(_nnaFadeMilliseconds);
		Button applyNna = new() { Content = "Apply Fade" };
		applyNna.Click += (_, _) => ApplyNnaOverride();
		applyNna.IsVisible =
			_nnaOverride.SelectedItem is InstrumentNnaOverride.Fade;
		_nnaOverride.SelectionChanged += (_, _) =>
			applyNna.IsVisible =
				_nnaOverride.SelectedItem is InstrumentNnaOverride.Fade;
		nnaHeader.Children.Add(applyNna);

		Grid headers = NewColumns();
		headers.Margin = new Thickness(6, 2);
		for (int i = 0; i < Headings.Length; i++)
		{
			TextBlock label = Label(Headings[i]);
			label.FontWeight = FontWeight.SemiBold;
			Grid.SetColumn(label, i);
			headers.Children.Add(label);
		}

		Grid table = new();
		table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		table.RowDefinitions.Add(
			new RowDefinition(new GridLength(1, GridUnitType.Star)));
		Grid.SetRow(headers, 0);
		Grid.SetRow(_rows, 1);
		table.Children.Add(headers);
		table.Children.Add(_rows);

		ScrollViewer horizontal = new()
		{
			Content = table,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};

		TextBlock hint = new()
		{
			Text = "One grid, highest note first. Source-less rows stay as editor-only drafts. "
				+ "Pitch is the offset in Instrument divisions (Divisions × log₂(multiplier)). "
				+ "Instrument NNA changes immediately except Fade, which needs Apply Fade. "
				+ "A selected rectangle applies one field to all included data rows. "
				+ "The nearby-note dropdown snaps Pitch only when selected. "
				+ "Shift+Arrow or Shift+click selects a rectangular block; Tab/click within the block preserves it. "
				+ "Up/Down navigate cells, Left/Right leave text at the caret boundary. "
				+ "Focus a tone cell and press Delete to remove its row.",
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(10, 2),
		};

		DockPanel root = new();
		DockPanel.SetDock(top, Dock.Top);
		DockPanel.SetDock(lookup, Dock.Top);
		DockPanel.SetDock(nnaHeader, Dock.Top);
		DockPanel.SetDock(hint, Dock.Top);
		Border status = new()
		{
			Padding = new Thickness(10, 5),
			Child = _message,
		};
		DockPanel.SetDock(status, Dock.Bottom);
		root.Children.Add(top);
		root.Children.Add(lookup);
		root.Children.Add(nnaHeader);
		root.Children.Add(hint);
		root.Children.Add(status);
		root.Children.Add(horizontal);
		return root;
	}

	private static Grid NewColumns()
	{
		// Let controls determine the needed height; the previous 32px floor
		// needlessly expanded even the shortest row.
		Grid grid = new();
		foreach (int width in ColumnWidths)
			grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(width)));
		return grid;
	}

	private Control BuildRow(InstrumentToneGridRow projected)
	{
		InstrumentToneGridCells cells = projected.IsEntry
			? _model.EntryCells : projected.Cells;
		int? index = projected.Index;
		Grid grid = NewColumns();
		// Horizontal inset aligns data with headers; vertical breathing room
		// belongs inside each selectable cell, not as row-level margins.
		grid.Margin = new Thickness(6, 0);

		TextBox? entryIndex = null;
		if (projected.IsEntry)
		{
			entryIndex = new TextBox
			{
				Text = _model.EntryIndex?.ToString(CultureInfo.CurrentCulture) ?? "",
				PlaceholderText = "Index",
				Width = 60,
			};
			CompactRowTextBox(entryIndex);
			At(grid, entryIndex, 0);
		}
		else
		{
			At(grid, Label(index!.Value.ToString(CultureInfo.CurrentCulture)), 0);
			At(grid, Label(InstrumentToneNoteNotation.Format(index.Value,
				_instrument.Divisions, _instrument.Offset)), 1);
		}

		ComboBox source = BuildSourceBox(cells.SourceId);
		At(grid, source, 2);

		double initialLog = cells.PitchMultiplier > 0 && double.IsFinite(
			cells.PitchMultiplier)
			? InstrumentToneNoteNotation.LogarithmicOffset(cells.PitchMultiplier)
			: 0;
		TextBox pitch = new()
		{
			Text = cells.PitchMultiplier > 0 && double.IsFinite(cells.PitchMultiplier)
				? InstrumentToneNoteNotation.FormatDisplayPitch(
					cells.PitchMultiplier, _instrument.Divisions, CultureInfo.CurrentCulture)
				: "0",
			Width = 100,
		};
		CompactRowTextBox(pitch);
		string? lastCommittedPitchText = pitch.Text;
		At(grid, pitch, 3);

		ComboBox nearby = CompactRowComboBox(new ComboBox { Width = 135 });
		if (!projected.IsEntry)
			At(grid, nearby, 4);

		ComboBox volume = BuildEnvelopeBox(cells.VolumeEnvelopeId);
		ComboBox pitchEnvelope = BuildEnvelopeBox(cells.PitchEnvelopeId);
		ComboBox panning = BuildEnvelopeBox(cells.PanningEnvelopeId);
		ComboBox filter = BuildEnvelopeBox(cells.FilterEnvelopeId);
		At(grid, volume, 6);
		At(grid, pitchEnvelope, 7);
		At(grid, panning, 8);
		At(grid, filter, 9);

		ComboBox toneFilter = BuildFilterBox(cells.FilterConfiguration?.DefinitionId);
		Button editFilter = new()
		{
			Content = "...", Width = 38,
			IsEnabled = cells.FilterConfiguration is not null,
		};
		StackPanel toneFilterCell = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 3,
			Children = { toneFilter, editFilter },
		};
		CompactRowComboBox(toneFilter);
		CompactRowButton(editFilter);
		At(grid, toneFilterCell, 10);

		ComboBox toneNna = new()
		{
			Width = 96,
			ItemsSource = Enum.GetValues<InstrumentNnaAction>(),
			SelectedItem = cells.Nna.Action,
		};
		TextBox toneFade = new()
		{
			Width = 81,
			Text = InstrumentNnaEditorValues.FormatMilliseconds(
				cells.Nna.FadeDuration ?? TimeSpan.FromMilliseconds(100)),
			IsVisible = cells.Nna.Action == InstrumentNnaAction.Fade,
		};
		CompactRowComboBox(toneNna);
		CompactRowTextBox(toneFade);
		StackPanel toneNnaCell = new()
		{
			Orientation = Orientation.Horizontal, Spacing = 3,
			Children = { toneNna, toneFade },
		};
		At(grid, toneNnaCell, 5);

		bool editingFilter = false;
		bool changingNearest = false;

		void UpdateEntry()
		{
			int? entered = int.TryParse(entryIndex?.Text,
				NumberStyles.Integer, CultureInfo.CurrentCulture,
				out int i) ? i : null;
			_model.SetEntryDraft(entered, cells);
		}

		bool CommitField(
			int column,
			Func<InstrumentToneGridCells, InstrumentToneGridCells> update)
		{
			try
			{
				if (projected.IsEntry)
				{
					cells = update(_model.EntryCells);
					UpdateEntry();
					return true;
				}
				IReadOnlyList<int> targets = _selection.EditTargetRows(
					new InstrumentToneGridSelection.Cell(index!.Value, column),
					_projectedRows);
				if (targets.Count == 0)
					targets = [index.Value];
				bool changed = _model.EditRows(targets,
					(_, old) => update(old));
				cells = _model.Rows.Single(row => row.Index == index).Cells;
				if (changed)
				{
					_message.Text = targets.Count == 1
						? $"Updated tone {index.Value}."
						: $"Updated column {column} across {targets.Count} tones.";
					_changed($"Updated {targets.Count} tone(s) in {_instrument.Name}");
				}
				if (targets.Count > 1 && changed)
				{
					// Rebuild edited rows after the active control event
					// finishes, preserving the semantic rectangle and
					// avoiding transient ComboBox re-entrancy.
					Dispatcher.UIThread.Post(
						() => RefreshRows(index), DispatcherPriority.Loaded);
				}
				return true;
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
				// Core and all drafts stayed unchanged on failed batch.
				RefreshRows(index);
				return false;
			}
		}

		void RefreshNearest(double logOffset)
		{
			if (projected.IsEntry)
				return;
			try
			{
				var options = InstrumentToneNoteNotation.Options(
					index!.Value, _instrument.Divisions,
					_instrument.Offset, logOffset);
				InstrumentToneNoteChoice? nearest =
					InstrumentToneNoteNotation.Nearest(options, logOffset);
				changingNearest = true;
				nearby.ItemsSource = options;
				nearby.SelectedItem = nearest;
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
				changingNearest = true;
				nearby.ItemsSource = Array.Empty<InstrumentToneNoteChoice>();
			}
			finally
			{
				changingNearest = false;
			}
		}

		void CommitPitch()
		{
			if (pitch.Text == lastCommittedPitchText)
				return;
			if (!double.TryParse(pitch.Text, NumberStyles.Float,
				CultureInfo.CurrentCulture, out double displayOffset)
				|| !double.IsFinite(displayOffset))
			{
				_message.Text = "Pitch must be a finite offset in instrument divisions.";
				return;
			}
			try
			{
				// A *single constant* transposition across all selected
				// rows; never a ramp based on tone index or prior pitch.
				double multiplier =
					InstrumentToneNoteNotation.MultiplierFromDisplayPitch(
						displayOffset, _instrument.Divisions);
				if (!CommitField(3, old =>
					old with { PitchMultiplier = multiplier }))
					return;
				lastCommittedPitchText = pitch.Text;
				RefreshNearest(displayOffset / _instrument.Divisions);
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
		}

		void CommitToneNna()
		{
			try
			{
				InstrumentNnaAction action = (InstrumentNnaAction)toneNna.SelectedItem!;
				InstrumentNnaSetting setting = InstrumentNnaEditorValues.ToneSetting(
					action, toneFade.Text);
				CommitField(5, old => old with { Nna = setting });
			}
			catch (Exception ex) { _message.Text = ex.Message; }
		}

		toneNna.SelectionChanged += (_, _) =>
		{
			toneFade.IsVisible = toneNna.SelectedItem is InstrumentNnaAction.Fade;
			CommitToneNna();
		};
		toneFade.LostFocus += (_, _) =>
		{
			if (toneNna.SelectedItem is InstrumentNnaAction.Fade)
				CommitToneNna();
		};
		toneFade.KeyDown += (_, e) =>
		{
			if (e.Key != Key.Enter) return;
			if (toneNna.SelectedItem is InstrumentNnaAction.Fade)
				CommitToneNna();
			e.Handled = true;
		};

		if (entryIndex is not null)
			entryIndex.TextChanged += (_, _) => UpdateEntry();
		source.SelectionChanged += (_, _) =>
		{
			ObjectId? id = (source.SelectedItem as SourceChoice)?.Id;
			CommitField(2, old => old with { SourceId = id });
		};
		void BindEnvelope(ComboBox box, int column,
			Func<InstrumentToneGridCells, ObjectId?, InstrumentToneGridCells> update)
			=> box.SelectionChanged += (_, _) =>
			{
				ObjectId? id = (box.SelectedItem as EnvelopeChoice)?.Id;
				CommitField(column, old => update(old, id));
			};
		BindEnvelope(volume, 6, (old, id) => old with { VolumeEnvelopeId = id });
		BindEnvelope(pitchEnvelope, 7, (old, id) => old with { PitchEnvelopeId = id });
		BindEnvelope(panning, 8, (old, id) => old with { PanningEnvelopeId = id });
		BindEnvelope(filter, 9, (old, id) => old with { FilterEnvelopeId = id });
		toneFilter.SelectionChanged += (_, _) =>
		{
			ObjectId? definitionId = (toneFilter.SelectedItem as FilterChoice)?.Id;
			if (definitionId == cells.FilterConfiguration?.DefinitionId)
				return;
			FilterInstance? instance = definitionId is ObjectId id
				? new FilterInstance((FilterDefinition)_workspace.Document.Objects[id])
				: null;
			editFilter.IsEnabled = instance is not null;
			CommitField(10, old => old with { FilterConfiguration = instance });
		};
		editFilter.Click += (_, _) =>
		{
			try
			{
				if (cells.FilterConfiguration is null)
					return;
				Window? owner = TopLevel.GetTopLevel(this) as Window;
				if (owner is null)
					throw new InvalidOperationException(
						"Open the Instrument editor in a window to edit a tone Filter.");
				InstrumentToneGridCells CurrentCells()
					=> projected.IsEntry ? _model.EntryCells
						: _model.Rows.Single(row => row.Index == index).Cells;
				FilterInstanceEditSession session = new(
					() => CurrentCells().FilterConfiguration
						?? throw new InvalidOperationException(
							"This tone no longer has a Filter."),
					next =>
					{
						if (!CommitField(10, old => old with
							{ FilterConfiguration = next }))
							throw new InvalidOperationException(
								"Could not apply the selected tone Filter.");
					});
				Window window = new()
				{
					Title = "Tone Filter — " + _instrument.Name,
					Width = 910,
					Height = 600,
					Content = new FilterInstanceEditorControl(session,
						message => _message.Text = message),
				};
				editingFilter = true;
				window.Closed += (_, _) =>
				{
					editingFilter = false;
					RefreshRows(index);
				};
				window.Show(owner);
			}
			catch (Exception ex) { _message.Text = ex.Message; }
		};
		pitch.LostFocus += (_, _) => CommitPitch();
		pitch.KeyDown += (_, e) =>
		{
			if (e.Key != Key.Enter)
				return;
			CommitPitch();
			e.Handled = true;
		};
		nearby.SelectionChanged += (_, _) =>
		{
			if (changingNearest
				|| nearby.SelectedItem is not InstrumentToneNoteChoice selected)
				return;
			pitch.Text = InstrumentToneNoteNotation.FormatDisplayPitch(
				selected.Multiplier, _instrument.Divisions, CultureInfo.CurrentCulture);
			// Keep the choice's exact multiplier, not its rounded label.
			// Other selected rows receive precisely the same offset.
			if (!CommitField(3, old => old with
				{ PitchMultiplier = selected.Multiplier }))
				return;
			lastCommittedPitchText = pitch.Text;
			RefreshNearest(selected.LogarithmicOffset);
		};
		RefreshNearest(initialLog);

		WrapRowCells(grid, projected);
		Border border = new()
		{
			Child = grid,
			BorderThickness = new Thickness(0, 0, 0, 1),
			BorderBrush = Brushes.Gray,
			Background = projected.IsOutOfRange
				? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0, 0))
				: null,
		};
		// The ListBox may use a selected row for scrolling, but the
		// rectangular cell selection lives independently in _selection.
		if (projected.IsEntry)
		{
			// Only commit when focus leaves the *entire row*.
			// Tab between index/source/pitch/envelopes must not move it.
			border.AddHandler(InputElement.LostFocusEvent, (_, _) =>
			{
				Dispatcher.UIThread.Post(() =>
				{
					// ComboBox popups live in a separate visual tree,
					// so opening one can temporarily move keyboard focus
					// outside this row. It must not commit an insertion.
					if (border.IsKeyboardFocusWithin
						|| source.IsDropDownOpen
						|| volume.IsDropDownOpen
						|| pitchEnvelope.IsDropDownOpen
						|| panning.IsDropDownOpen
						|| filter.IsDropDownOpen
						|| toneFilter.IsDropDownOpen
						|| toneNna.IsDropDownOpen
						|| editingFilter)
						return;
					int? enteredIndex = _model.EntryIndex;
					if (enteredIndex is null)
						return;
					try
					{
						if (!_model.CommitEntry())
							return;
						_message.Text = "Inserted/overwrote tone table row.";
						RefreshRows(enteredIndex);
						_changed($"Inserted/overwrote tone {enteredIndex.Value} in {_instrument.Name}");
					}
					catch (Exception ex)
					{
						_message.Text = ex.Message;
					}
				}, DispatcherPriority.Loaded);
			}, RoutingStrategies.Bubble, handledEventsToo: true);
		}
		return border;
	}

	private void ApplyNnaOverride()
	{
		try
		{
			if (_instrument.NnaOverride != _loadedNnaOverride
				|| _instrument.NnaFadeDuration != _loadedNnaFade)
				throw new InvalidOperationException(
					"Instrument NNA changed elsewhere. Reopen this editor to reload before applying.");
			InstrumentNnaOverride action = (InstrumentNnaOverride)_nnaOverride.SelectedItem!;
			TimeSpan duration = action == InstrumentNnaOverride.Fade
				? InstrumentNnaEditorValues.ParseFadeMilliseconds(_nnaFadeMilliseconds.Text)
				: _loadedNnaFade;
			InstrumentDocumentEditor.SetNnaOverride(
				_workspace, _instrument, action, duration);
			_loadedNnaOverride = _instrument.NnaOverride;
			_loadedNnaFade = _instrument.NnaFadeDuration;
			_message.Text = "Instrument NNA updated.";
			_changed("Updated Instrument NNA in " + _instrument.Name);
		}
		catch (Exception ex) { _message.Text = ex.Message; }
	}

	private void ApplyLookup()
	{
		try
		{
			double divisions = double.Parse(_divisions.Text ?? "",
				CultureInfo.CurrentCulture);
			int offset = int.Parse(_offset.Text ?? "",
				CultureInfo.CurrentCulture);
			// Refuse a projection too large to render before touching Core.
			int range = InstrumentToneGridProjection.RegularExclusiveEnd(
				divisions, offset);
			if (range > InstrumentToneGridProjection.MaxRegularRows)
				throw new InvalidOperationException("The tone-grid range is too large.");

			bool reconfigured = _instrument.Divisions != divisions
				|| _instrument.Offset != offset;
			InstrumentDocumentEditor.UpdateLookup(
				_workspace, _instrument, divisions, offset);
			if (reconfigured)
				_model.OnDivisionsChanged();
			RefreshRows();
			_changed($"Updated lookup for {_instrument.Name}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void OnGridKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Handled)
			return;
		if (HandleGridNavigation(e))
		{
			e.Handled = true;
			return;
		}

		// Delete still acts on a tone row only when no editable child is
		// consuming it. Never delete the reserved entry row.
		if (e.Key != Key.Delete
			|| e.Source is TextBox or ComboBox
			|| _selection.Focused?.ToneIndex is not int index)
			return;
		try
		{
			_model.DeleteRow(index);
			RefreshRows();
			_changed($"Removed tone {index} from {_instrument.Name}");
			e.Handled = true;
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void RefreshRows(int? selectedIndex = null)
	{
		try
		{
			_sources = PatternSourceCatalog.GetSources(_workspace.Document);
			_filters = _workspace.Document.Objects.Values
				.OfType<FilterDefinition>()
				.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(x => x.Id.Value)
				.Select(x => new FilterChoice(x.Id,
					$"{x.Name} <{x.Id.Value}>")).ToArray();
			_envelopes = _workspace.Document.Objects.Values
				.OfType<EnvelopeDefinition>()
				.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(x => x.Id.Value)
				.Select(x => new EnvelopeChoice(x.Id,
					$"{x.Name} <{x.Id.Value}>"))
				.ToArray();
			_divisions.Text = _instrument.Divisions.ToString(
				CultureInfo.CurrentCulture);
			_offset.Text = _instrument.Offset.ToString(
				CultureInfo.CurrentCulture);
			InstrumentToneGridRow[] projected = _model.Rows.ToArray();
			_projectedRows = projected;
			_selection.Reconcile(projected);
			_visibleCells.Clear();
			_rows.ItemsSource = projected;
			if (selectedIndex.HasValue)
				_rows.SelectedItem = projected.FirstOrDefault(
					r => r.Index == selectedIndex);
			RefreshSelectionVisuals();
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	/// <summary>Wrap each projected visual column in a selection surface.
	/// The wrapper is created by the item template, but selection identity
	/// is the tone index and column: recycled controls never own the range.</summary>
	private void WrapRowCells(Grid grid, InstrumentToneGridRow projected)
	{
		Control[] contents = grid.Children.ToArray();
		grid.Children.Clear();
		for (int column = 0; column < InstrumentToneGridSelection.ColumnCount; column++)
		{
			Control? content = contents.FirstOrDefault(
				control => Grid.GetColumn(control) == column);
			var cell = new InstrumentToneGridSelection.Cell(projected.Index, column);
			Border surface = new()
			{
				Child = content,
				Focusable = true,
				BorderThickness = new Thickness(1),
				// The 1px top/bottom padding preserves a visible gap
				// around the entire *actual* control template, including
				// dropdowns and keyboard focus decorations.
				Padding = new Thickness(0, 1),
				BorderBrush = Brushes.Transparent,
			};
			Grid.SetColumn(surface, column);
			grid.Children.Add(surface);
			// The event source can be a nested TextBox or ComboBox. Handling
			// the bubbled GotFocus captures Tab and mouse focus traversal
			// without relying on the ListBox.SelectedItem.
			surface.AddHandler(InputElement.GotFocusEvent, (_, _) =>
			{
				if (_movingGridFocus)
					return;
				if (InstrumentToneGridSelection.RowPosition(
					_projectedRows, projected.Index) < 0)
					return;
				_selection.Focus(cell, _projectedRows);
				RefreshSelectionVisuals();
			}, RoutingStrategies.Bubble, handledEventsToo: true);
			surface.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
			{
				if (InstrumentToneGridSelection.RowPosition(
					_projectedRows, projected.Index) < 0)
					return;
				if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
					_selection.Extend(cell, _projectedRows);
				else
					_selection.Focus(cell, _projectedRows);
				RefreshSelectionVisuals();
			}, RoutingStrategies.Tunnel, handledEventsToo: true);
			surface.AttachedToVisualTree += (_, _) =>
			{
				_visibleCells[cell] = new WeakReference<Border>(surface);
				PaintSelection(surface, cell);
			};
			surface.DetachedFromVisualTree += (_, _) =>
			{
				if (_visibleCells.TryGetValue(cell, out var reference)
					&& reference.TryGetTarget(out Border? active)
					&& ReferenceEquals(active, surface))
					_visibleCells.Remove(cell);
			};
			// Templates built in unit tests (or prior to layout) must
			// already reflect the semantic selection state.
			PaintSelection(surface, cell);
		}
	}

	private void RefreshSelectionVisuals()
	{
		foreach (var pair in _visibleCells.ToArray())
		{
			if (!pair.Value.TryGetTarget(out Border? surface))
			{
				_visibleCells.Remove(pair.Key);
				continue;
			}
			PaintSelection(surface, pair.Key);
		}
	}

	private void PaintSelection(Border surface,
		InstrumentToneGridSelection.Cell cell)
	{
		bool selected = _selection.Contains(cell, _projectedRows);
		bool focused = selected && _selection.Focused == cell;
		surface.Background = selected
			? new SolidColorBrush(Color.FromArgb(78, 45, 131, 245))
			: Brushes.Transparent;
		surface.BorderBrush = focused ? Brushes.DeepSkyBlue
			: selected ? Brushes.SteelBlue : Brushes.Transparent;
	}

	private static Control PreferredFocusControl(Border cell)
	{
		if (cell.Child is TextBox or ComboBox or Button)
			return (Control)cell.Child;
		if (cell.Child is StackPanel panel)
			return panel.Children.OfType<Control>()
				.FirstOrDefault(control => control.Focusable && control.IsVisible)
				?? cell;
		return cell;
	}

	private void FocusSelectedCell(InstrumentToneGridSelection.Cell cell)
	{
		if (_visibleCells.TryGetValue(cell, out var reference)
			&& reference.TryGetTarget(out Border? target))
		{
			PreferredFocusControl(target).Focus();
		}
	}

	private bool HandleGridNavigation(KeyEventArgs e)
	{
		if (_selection.Focused is not InstrumentToneGridSelection.Cell current)
			return false;
		if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt |
			KeyModifiers.Meta)) != 0)
			return false;
		if (e.Source is ComboBox box && box.IsDropDownOpen)
			return false;

		int rowDelta = 0, columnDelta = 0;
		switch (e.Key)
		{
			case Key.Up: rowDelta = -1; break;
			case Key.Down: rowDelta = 1; break;
			case Key.Left: columnDelta = -1; break;
			case Key.Right: columnDelta = 1; break;
			default: return false;
		}

		bool boundaryShiftLeft = false;
		if (e.Source is TextBox input && columnDelta != 0)
		{
			// Do not hijack normal caret movement or Shift+Arrow text
			// selection. Only a zero-length selection at the boundary
			// can transfer keyboard focus to the neighbouring cell.
			if (!InstrumentToneGridSelection.CanTransferTextBoundary(
				input.CaretIndex, input.Text?.Length ?? 0,
				input.SelectionStart != input.SelectionEnd, columnDelta))
				return false;
			boundaryShiftLeft =
				columnDelta < 0 &&
				(e.KeyModifiers & KeyModifiers.Shift) != 0;
		}
		InstrumentToneGridSelection.Cell? neighbor =
			_selection.Neighbor(rowDelta, columnDelta, _projectedRows);
		if (neighbor is not InstrumentToneGridSelection.Cell next)
			return false;

		_movingGridFocus = true;
		try
		{
			if (boundaryShiftLeft)
				_selection.BeginAt(next, _projectedRows);
			else if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
				_selection.Extend(next, _projectedRows);
			else
				_selection.BeginAt(next, _projectedRows);
			int position = InstrumentToneGridSelection.RowPosition(
				_projectedRows, next.ToneIndex);
			// Selecting the projected item requests its realization in the
			// virtualizing ListBox. If not realized yet, focus after layout.
			_rows.SelectedItem = _projectedRows[position];
			_rows.ScrollIntoView(position);
			RefreshSelectionVisuals();
			Dispatcher.UIThread.Post(() =>
				FocusSelectedCell(next), DispatcherPriority.Loaded);
		}
		finally
		{
			_movingGridFocus = false;
		}
		return true;
	}

	private ComboBox BuildSourceBox(ObjectId? selectedId)
	{
		List<SourceChoice> options =
			[new SourceChoice(null, "")];
		options.AddRange(_sources.Select(x =>
			new SourceChoice(x.Id, x.DisplayName)));
		if (selectedId is ObjectId id && !id.IsNone
			&& !options.Any(x => x.Id == id))
		{
			options.Insert(1, new SourceChoice(id, $"⚠ Missing <{id.Value}>"));
		}
		return CompactRowComboBox(new ComboBox
		{
			Width = 186,
			ItemsSource = options,
			SelectedItem = options.FirstOrDefault(x => x.Id == selectedId)
				?? options[0],
		});
	}

	private ComboBox BuildEnvelopeBox(ObjectId? selectedId)
	{
		List<EnvelopeChoice> options = [new EnvelopeChoice(null, "")];
		options.AddRange(_envelopes);
		if (selectedId is ObjectId id && !id.IsNone
			&& !options.Any(x => x.Id == id))
			options.Insert(1, new EnvelopeChoice(id,
				$"⚠ Missing <{id.Value}>"));
		return CompactRowComboBox(new ComboBox
		{
			Width = 135,
			ItemsSource = options,
			SelectedItem = options.FirstOrDefault(x => x.Id == selectedId)
				?? options[0],
		});
	}

	private ComboBox BuildFilterBox(ObjectId? selectedId)
	{
		List<FilterChoice> options = [new FilterChoice(null, "(None)")];
		options.AddRange(_filters);
		if (selectedId is ObjectId id && !id.IsNone
			&& !options.Any(x => x.Id == id))
			options.Insert(1, new FilterChoice(id, $"⚠ Missing <{id.Value}>"));
		return CompactRowComboBox(new ComboBox
		{
			Width = 196,
			ItemsSource = options,
			SelectedItem = options.FirstOrDefault(x => x.Id == selectedId)
				?? options[0],
		});
	}

	/// <summary>
	/// Compact only the projected tone-row editors. These are minimum-free,
	/// content-sized controls with reduced *internal* vertical padding;
	/// they never use a forced Height, so localized text, focus rings and
	/// native templates retain their requested size.
	/// </summary>
	private static TextBox CompactRowTextBox(TextBox control)
	{
		control.MinHeight = 0;
		control.Padding = new Thickness(4, 1);
		return control;
	}

	private static ComboBox CompactRowComboBox(ComboBox control)
	{
		control.MinHeight = 0;
		control.Padding = new Thickness(4, 1);
		return control;
	}

	private static Button CompactRowButton(Button control)
	{
		control.MinHeight = 0;
		control.Padding = new Thickness(4, 1);
		return control;
	}

	private static TextBox NumberBox(double x) => new()
	{
		Text = x.ToString(CultureInfo.CurrentCulture),
		Width = 88,
	};
	private static TextBox NumberBox(int x) => new()
	{
		Text = x.ToString(CultureInfo.CurrentCulture),
		Width = 70,
	};
	private static TextBlock Label(string text) => new()
	{
		Text = text,
		Margin = new Thickness(4, 0),
		VerticalAlignment = VerticalAlignment.Center,
	};

	private static void At(Grid grid, Control control, int column)
	{
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}

	private sealed record SourceChoice(ObjectId? Id, string Name)
	{
		public override string ToString() => Name;
	}
	private sealed record FilterChoice(ObjectId? Id, string Name)
	{
		public override string ToString() => Name;
	}
	private sealed record EnvelopeChoice(ObjectId? Id, string Name)
	{
		public override string ToString() => Name;
	}
}

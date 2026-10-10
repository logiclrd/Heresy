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
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.InstrumentEditing;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Nine-column projected instrument table. The Core's shared specification
/// list remains an implementation detail of InstrumentToneGridModel.
/// </summary>
public sealed class InstrumentEditorControl : UserControl
{
	private static readonly int[] ColumnWidths =
		[66, 114, 196, 108, 140, 145, 145, 145, 145];
	private static readonly string[] Headings =
		["", "", "Source", "Pitch", "", "Volume", "Pitch", "Panning", "Filter"];

	private readonly DocumentWorkspace _workspace;
	private readonly InstrumentDefinition _instrument;
	private readonly InstrumentToneGridModel _model;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly TextBox _divisions;
	private readonly TextBox _offset;
	private readonly ListBox _rows;
	private readonly TextBlock _message = new()
	{
		TextWrapping = TextWrapping.Wrap,
	};
	private PatternSourceOption[] _sources = [];
	private EnvelopeChoice[] _envelopes = [];

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
		_rows = new ListBox
		{
			MinWidth = ColumnWidths.Sum(),
			ItemTemplate = new FuncDataTemplate<InstrumentToneGridRow>(
				(row, _) => BuildRow(row)),
		};
		_rows.KeyDown += OnGridKeyDown;
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
				+ "Pitch is log₂(multiplier). The nearby-note dropdown snaps Pitch only when selected. "
				+ "Select a row and press Delete to remove it.",
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(10, 2),
		};

		DockPanel root = new();
		DockPanel.SetDock(top, Dock.Top);
		DockPanel.SetDock(lookup, Dock.Top);
		DockPanel.SetDock(hint, Dock.Top);
		Border status = new()
		{
			Padding = new Thickness(10, 5),
			Child = _message,
		};
		DockPanel.SetDock(status, Dock.Bottom);
		root.Children.Add(top);
		root.Children.Add(lookup);
		root.Children.Add(hint);
		root.Children.Add(status);
		root.Children.Add(horizontal);
		return root;
	}

	private static Grid NewColumns()
	{
		Grid grid = new() { MinHeight = 32 };
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
		grid.Margin = new Thickness(6, 2);

		TextBox? entryIndex = null;
		if (projected.IsEntry)
		{
			entryIndex = new TextBox
			{
				Text = _model.EntryIndex?.ToString(CultureInfo.CurrentCulture) ?? "",
				Watermark = "Index",
				Width = 60,
			};
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
			Text = initialLog.ToString("G17", CultureInfo.CurrentCulture),
			Width = 100,
		};
		string? lastCommittedPitchText = pitch.Text;
		At(grid, pitch, 3);

		ComboBox nearby = new() { Width = 135 };
		if (!projected.IsEntry)
			At(grid, nearby, 4);

		ComboBox volume = BuildEnvelopeBox(cells.VolumeEnvelopeId);
		ComboBox pitchEnvelope = BuildEnvelopeBox(cells.PitchEnvelopeId);
		ComboBox panning = BuildEnvelopeBox(cells.PanningEnvelopeId);
		ComboBox filter = BuildEnvelopeBox(cells.FilterEnvelopeId);
		At(grid, volume, 5);
		At(grid, pitchEnvelope, 6);
		At(grid, panning, 7);
		At(grid, filter, 8);

		bool changingNearest = false;

		void UpdateEntry()
		{
			int? entered = int.TryParse(entryIndex?.Text,
				NumberStyles.Integer, CultureInfo.CurrentCulture,
				out int i) ? i : null;
			_model.SetEntryDraft(entered, cells);
		}

		void CommitCells()
		{
			try
			{
				if (projected.IsEntry)
				{
					UpdateEntry();
					return;
				}
				_model.SetRow(index!.Value, cells);
				_message.Text = $"Updated tone {index.Value}.";
				_changed($"Updated tone {index.Value} in {_instrument.Name}");
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
				// The song and editor drafts are unchanged after a rejected
				// edit. Restore controls from the last accepted model row.
				RefreshRows(index);
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
				CultureInfo.CurrentCulture, out double logarithm)
				|| !double.IsFinite(logarithm))
			{
				_message.Text = "Pitch must be a finite logarithmic offset.";
				return;
			}
			try
			{
				cells = cells with
				{
					PitchMultiplier =
						InstrumentToneNoteNotation.MultiplierFromOffset(logarithm),
				};
				CommitCells();
				lastCommittedPitchText = pitch.Text;
				RefreshNearest(logarithm);
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
		}

		if (entryIndex is not null)
			entryIndex.TextChanged += (_, _) => UpdateEntry();
		source.SelectionChanged += (_, _) =>
		{
			cells = cells with
			{
				SourceId = (source.SelectedItem as SourceChoice)?.Id,
			};
			CommitCells();
		};
		void BindEnvelope(ComboBox box, Func<ObjectId?, InstrumentToneGridCells> update)
			=> box.SelectionChanged += (_, _) =>
			{
				cells = update((box.SelectedItem as EnvelopeChoice)?.Id);
				CommitCells();
			};
		BindEnvelope(volume, id => cells with { VolumeEnvelopeId = id });
		BindEnvelope(pitchEnvelope, id => cells with { PitchEnvelopeId = id });
		BindEnvelope(panning, id => cells with { PanningEnvelopeId = id });
		BindEnvelope(filter, id => cells with { FilterEnvelopeId = id });
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
			pitch.Text = selected.LogarithmicOffset.ToString(
				"G17", CultureInfo.CurrentCulture);
			// Use the exact precomputed multiplier. Re-evaluating an
			// approximate string representation on focus loss would
			// introduce an unintended second, inexact update.
			cells = cells with { PitchMultiplier = selected.Multiplier };
			CommitCells();
			lastCommittedPitchText = pitch.Text;
			RefreshNearest(selected.LogarithmicOffset);
		};
		RefreshNearest(initialLog);

		Border border = new()
		{
			Child = grid,
			BorderThickness = new Thickness(0, 0, 0, 1),
			BorderBrush = Brushes.Gray,
			Background = projected.IsOutOfRange
				? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0, 0))
				: null,
		};
		border.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
			_rows.SelectedItem = projected,
			RoutingStrategies.Tunnel, handledEventsToo: true);
		if (projected.IsEntry)
		{
			// Only commit when focus leaves the *entire row*.
			// Tab between index/source/pitch/envelopes must not move it.
			border.AddHandler(InputElement.LostFocusEvent, (_, _) =>
			{
				Dispatcher.UIThread.Post(() =>
				{
					if (border.IsKeyboardFocusWithin)
						return;
					int? enteredIndex = _model.EntryIndex;
					if (enteredIndex is null || !_model.CommitEntry())
						return;
					_message.Text = "Inserted/overwrote tone table row.";
					RefreshRows(enteredIndex);
				}, DispatcherPriority.Loaded);
			}, RoutingStrategies.Bubble, handledEventsToo: true);
		}
		return border;
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
		// Avoid eating the Delete key when a TextBox is actively editing
		// text or a dropdown is manipulating its own selection.
		if (e.Key != Key.Delete
			|| e.Source is TextBox or ComboBox
			|| _rows.SelectedItem is not InstrumentToneGridRow selected
			|| selected.Index is not int index)
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
			_rows.ItemsSource = projected;
			if (selectedIndex.HasValue)
				_rows.SelectedItem = projected.FirstOrDefault(
					r => r.Index == selectedIndex);
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
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
		return new ComboBox
		{
			Width = 186,
			ItemsSource = options,
			SelectedItem = options.FirstOrDefault(x => x.Id == selectedId)
				?? options[0],
		};
	}

	private ComboBox BuildEnvelopeBox(ObjectId? selectedId)
	{
		List<EnvelopeChoice> options = [new EnvelopeChoice(null, "")];
		options.AddRange(_envelopes);
		if (selectedId is ObjectId id && !id.IsNone
			&& !options.Any(x => x.Id == id))
			options.Insert(1, new EnvelopeChoice(id,
				$"⚠ Missing <{id.Value}>"));
		return new ComboBox
		{
			Width = 135,
			ItemsSource = options,
			SelectedItem = options.FirstOrDefault(x => x.Id == selectedId)
				?? options[0],
		};
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
	private sealed record EnvelopeChoice(ObjectId? Id, string Name)
	{
		public override string ToString() => Name;
	}
}

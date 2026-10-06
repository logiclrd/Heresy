using System;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Objects;
using Heresy.Core.Sequences;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Main-workspace editor for a data-driven sequence. Sequence entries remain
/// stable ObjectId references; this view only projects names and editing controls.
/// </summary>
public sealed class SequenceEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly DataSequenceDefinition _sequence;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly Action<int, ObjectId> _openPattern;
	private readonly StackPanel _rows = new() { Spacing = 4 };
	private readonly TextBlock _rootStatus = new();
	private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
	private readonly ComboBox _addPattern;
	private readonly TextBox _addStartRow =
		new()
		{
			Text = "0",
			Width = 70,
		};

	private SequencePatternOption[] _patterns;

	public SequenceEditorControl(
		DocumentWorkspace workspace,
		DataSequenceDefinition sequence,
		Action close,
		Action<string> changed,
		Action<int, ObjectId> openPattern)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_sequence = sequence
			?? throw new ArgumentNullException(nameof(sequence));
		_close = close
			?? throw new ArgumentNullException(nameof(close));
		_changed = changed
			?? throw new ArgumentNullException(nameof(changed));
		_openPattern = openPattern
			?? throw new ArgumentNullException(nameof(openPattern));

		_patterns = SequencePatternCatalog.GetPatterns(_workspace.Document);
		_addPattern =
			new ComboBox
			{
				ItemsSource = _patterns,
				SelectedIndex = _patterns.Length == 0 ? -1 : 0,
				Width = 260,
			};

		Content = BuildContent();
		RefreshRows();
	}

	private Control BuildContent()
	{
		Button back =
			new()
			{
				Content = "← Document",
				MinWidth = 100,
			};
		back.Click += (_, _) => _close();

		TextBlock title =
			new()
			{
				Text = _sequence.Name,
				FontSize = 20,
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			};

		Button setRoot =
			new()
			{
				Content = "Set as Song Root",
			};
		setRoot.Click += (_, _) =>
		{
			SequenceDocumentEditor.SetRootSequence(
				_workspace,
				_sequence);
			RefreshRootStatus(setRoot);
			_changed($"Set {_sequence.Name} as song root sequence");
		};

		StackPanel rootControls =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8,
				VerticalAlignment = VerticalAlignment.Center,
			};
		rootControls.Children.Add(_rootStatus);
		rootControls.Children.Add(setRoot);

		DockPanel header =
			new()
			{
				Margin = new Thickness(10, 8),
			};
		DockPanel.SetDock(back, Dock.Left);
		DockPanel.SetDock(rootControls, Dock.Right);
		header.Children.Add(back);
		header.Children.Add(rootControls);
		header.Children.Add(title);

		Button add =
			new()
			{
				Content = "Add Pattern",
			};
		add.Click += (_, _) => AddEntry();

		StackPanel addControls =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
				Margin = new Thickness(10, 4),
				VerticalAlignment = VerticalAlignment.Center,
			};
		addControls.Children.Add(new TextBlock
		{
			Text = "Pattern",
			VerticalAlignment = VerticalAlignment.Center,
		});
		addControls.Children.Add(_addPattern);
		addControls.Children.Add(new TextBlock
		{
			Text = "Start row",
			VerticalAlignment = VerticalAlignment.Center,
		});
		addControls.Children.Add(_addStartRow);
		addControls.Children.Add(add);

		Grid headings = new()
		{
			Margin = new Thickness(10, 6, 10, 2),
		};
		ConfigureColumns(headings);
		AddHeading(headings, "Order", 0);
		AddHeading(headings, "Pattern", 1);
		AddHeading(headings, "Replace with", 2);
		AddHeading(headings, "Start row", 3);
		AddHeading(headings, "Actions", 4);

		ScrollViewer scroll =
			new()
			{
				Content = _rows,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				Margin = new Thickness(10, 0),
			};

		Border messageBorder =
			new()
			{
				Padding = new Thickness(10, 5),
				Child = _message,
			};

		DockPanel root = new();
		DockPanel.SetDock(header, Dock.Top);
		DockPanel.SetDock(addControls, Dock.Top);
		DockPanel.SetDock(headings, Dock.Top);
		DockPanel.SetDock(messageBorder, Dock.Bottom);
		root.Children.Add(header);
		root.Children.Add(addControls);
		root.Children.Add(headings);
		root.Children.Add(messageBorder);
		root.Children.Add(scroll);

		RefreshRootStatus(setRoot);
		return root;
	}

	private void RefreshRows()
	{
		_patterns = SequencePatternCatalog.GetPatterns(_workspace.Document);
		_addPattern.ItemsSource = _patterns;
		if (_addPattern.SelectedItem is SequencePatternOption selected
			&& !_patterns.Any(option => option.Id == selected.Id))
		{
			_addPattern.SelectedIndex = _patterns.Length == 0 ? -1 : 0;
		}
		else if (_addPattern.SelectedIndex < 0 && _patterns.Length != 0)
		{
			_addPattern.SelectedIndex = 0;
		}

		_rows.Children.Clear();
		for (int index = 0; index < _sequence.Entries.Count; index++)
			_rows.Children.Add(BuildEntryRow(index));

		if (_sequence.Entries.Count == 0)
		{
			_rows.Children.Add(
				new TextBlock
				{
					Text = "This sequence has no pattern entries yet.",
					Margin = new Thickness(5, 10),
				});
		}
	}

	private Control BuildEntryRow(int index)
	{
		SequenceEntryViewModel view =
			SequenceEntryViewModel.Create(
				_workspace.Document,
				_sequence,
				index);

		TextBlock order =
			new()
			{
				Text = index.ToString("D2", CultureInfo.InvariantCulture),
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(4),
			};

		TextBlock current =
			new()
			{
				Text = view.PatternText,
				VerticalAlignment = VerticalAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis,
				Margin = new Thickness(4),
			};

		ComboBox replacement =
			new()
			{
				ItemsSource = _patterns,
				Width = 230,
			};
		replacement.SelectedItem =
			_patterns.FirstOrDefault(option => option.Id == view.PatternId);

		TextBox startRow =
			new()
			{
				Text = view.StartRow.ToString(CultureInfo.CurrentCulture),
				Width = 70,
			};

		Button apply = new() { Content = "Apply" };
		apply.Click += (_, _) =>
			ApplyEntry(index, view.PatternId, replacement, startRow);

		Button open = new()
		{
			Content = "Open",
			IsEnabled = !view.IsMissing,
		};
		open.Click += (_, _) => _openPattern(index, view.PatternId);

		Button up = new()
		{
			Content = "↑",
			IsEnabled = index > 0,
			MinWidth = 36,
		};
		up.Click += (_, _) =>
			MoveEntry(index, index - 1);

		Button down = new()
		{
			Content = "↓",
			IsEnabled = index + 1 < _sequence.Entries.Count,
			MinWidth = 36,
		};
		down.Click += (_, _) =>
			MoveEntry(index, index + 1);

		Button remove = new() { Content = "Remove" };
		remove.Click += (_, _) =>
		{
			SequenceDocumentEditor.RemoveEntry(
				_workspace,
				_sequence,
				index);
			RefreshRows();
			_changed($"Removed sequence order {index}");
		};

		StackPanel actions =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 4,
			};
		actions.Children.Add(apply);
		actions.Children.Add(open);
		actions.Children.Add(up);
		actions.Children.Add(down);
		actions.Children.Add(remove);

		Grid row = new()
		{
			MinHeight = 36,
		};
		ConfigureColumns(row);
		AddAt(row, order, 0);
		AddAt(row, current, 1);
		AddAt(row, replacement, 2);
		AddAt(row, startRow, 3);
		AddAt(row, actions, 4);

		return new Border
		{
			BorderBrush = Brushes.Gray,
			BorderThickness = new Thickness(0, 0, 0, 1),
			Child = row,
		};
	}

	private void AddEntry()
	{
		if (_addPattern.SelectedItem is not SequencePatternOption pattern)
		{
			_message.Text =
				"Create a pattern before adding sequence entries.";
			return;
		}

		if (!TryParseStartRow(_addStartRow, out int startRow))
			return;

		SequenceDocumentEditor.InsertEntry(
			_workspace,
			_sequence,
			_sequence.Entries.Count,
			pattern.Id,
			startRow);
		RefreshRows();
		_changed($"Added {pattern.Name} to {_sequence.Name}");
	}

	private void ApplyEntry(
		int index,
		ObjectId currentPatternId,
		ComboBox replacement,
		TextBox startRowBox)
	{
		if (!TryParseStartRow(startRowBox, out int startRow))
			return;

		ObjectId patternId =
			replacement.SelectedItem is SequencePatternOption option
				? option.Id
				: currentPatternId;

		try
		{
			SequenceDocumentEditor.UpdateEntry(
				_workspace,
				_sequence,
				index,
				patternId,
				startRow);
			RefreshRows();
			_changed($"Updated sequence order {index}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void MoveEntry(int fromIndex, int toIndex)
	{
		SequenceDocumentEditor.MoveEntry(
			_workspace,
			_sequence,
			fromIndex,
			toIndex);
		RefreshRows();
		_changed($"Moved sequence order {fromIndex} to {toIndex}");
	}

	private bool TryParseStartRow(TextBox box, out int startRow)
	{
		if (!int.TryParse(
			box.Text,
			NumberStyles.Integer,
			CultureInfo.CurrentCulture,
			out startRow)
			|| startRow < 0)
		{
			_message.Text =
				"Start row must be a non-negative integer.";
			return false;
		}

		return true;
	}

	private void RefreshRootStatus(Button setRoot)
	{
		bool isRoot =
			_workspace.Document.RootSequenceId == _sequence.Id;
		_rootStatus.Text =
			isRoot ? "● Song root" : "○ Not song root";
		setRoot.IsEnabled = !isRoot;
	}

	private static void ConfigureColumns(Grid grid)
	{
		grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(58)));
		grid.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));
		grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90)));
		grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(260)));
	}

	private static void AddHeading(
		Grid grid,
		string text,
		int column)
	{
		AddAt(
			grid,
			new TextBlock
			{
				Text = text,
				FontWeight = FontWeight.SemiBold,
				Margin = new Thickness(4),
			},
			column);
	}

	private static void AddAt(
		Grid grid,
		Control control,
		int column)
	{
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}
}

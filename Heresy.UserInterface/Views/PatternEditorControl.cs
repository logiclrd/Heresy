using System;
using System.Globalization;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Patterns;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.ViewModels;

namespace Heresy.UserInterface.Views;

public sealed class PatternEditorControl : UserControl
{
	private readonly Window _owner;
	private readonly DocumentWorkspace _workspace;
	private readonly DataPatternDefinition _pattern;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly TextBox _rowCount;
	private readonly TextBox _channelCount;
	private readonly TextBox _minorHighlight;
	private readonly TextBox _majorHighlight;
	private readonly ScrollViewer _scroll;
	private readonly TextBlock _message;

	public PatternEditorControl(
		Window owner,
		DocumentWorkspace workspace,
		DataPatternDefinition pattern,
		Action close,
		Action<string> changed)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		_pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
		_close = close ?? throw new ArgumentNullException(nameof(close));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));

		_rowCount = NumberBox(pattern.RowCount);
		_channelCount = NumberBox(pattern.ChannelCount);
		_minorHighlight = NumberBox(pattern.MinorHighlightRows);
		_majorHighlight = NumberBox(pattern.MajorHighlightRows);
		_scroll =
			new ScrollViewer
			{
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			};
		_message =
			new TextBlock
			{
				TextWrapping = TextWrapping.Wrap,
			};

		Content = BuildContent();
		RefreshGrid();
	}

	private Control BuildContent()
	{
		Button back = new() { Content = "← Document", MinWidth = 100 };
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
		Grid grid = new();
		grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(54)));
		for (int channel = 0; channel < _pattern.ChannelCount; channel++)
			grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(190)));

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
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

			FontWeight rowWeight =
				_pattern.MajorHighlightRows > 0
					&& row % _pattern.MajorHighlightRows == 0
					? FontWeight.Bold
					: _pattern.MinorHighlightRows > 0
						&& row % _pattern.MinorHighlightRows == 0
						? FontWeight.SemiBold
						: FontWeight.Normal;
			AddText(
				grid,
				row.ToString("X2", CultureInfo.InvariantCulture),
				gridRow,
				0,
				rowWeight);

			for (int channel = 0; channel < _pattern.ChannelCount; channel++)
			{
				int cellRow = row;
				int cellChannel = channel;
				PatternCellViewModel cell =
					PatternCellViewModel.Create(
						_workspace.Document,
						_pattern,
						row,
						channel);

				Button button =
					new()
					{
						Content = cell.DisplayText,
						HorizontalContentAlignment = HorizontalAlignment.Left,
						MinHeight = 28,
						Margin = new Thickness(1),
						FontWeight = rowWeight,
					};
				button.Click += async (_, _) =>
					await EditCellAsync(cellRow, cellChannel);
				Grid.SetRow(button, gridRow);
				Grid.SetColumn(button, channel + 1);
				grid.Children.Add(button);
			}
		}

		_scroll.Content = grid;
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
		RefreshGrid();
		_changed($"Edited row {row}, channel {channel + 1}");
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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Temporary, functional node-list editor shared by the standalone Envelope
/// page and FM Envelope inspector. The interactive graph is rebuilt in the
/// subsequent node-graph milestones; there are no ADSR-style sliders here.
/// </summary>
public sealed class NodeEnvelopeEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly NodeEnvelopeDefinition _envelope;
	private readonly Action<string> _changed;
	private readonly NodeEnvelopeGraphControl _graph;
	private readonly TextBox _nodesInput = new()
	{
		AcceptsReturn = true,
		TextWrapping = TextWrapping.NoWrap,
		MinHeight = 125,
		MinWidth = 245,
		FontFamily = FontFamily.Default,
	};
	private readonly TextBox _sustainStart = new() { Width = 90 };
	private readonly TextBox _sustainEnd = new() { Width = 90 };
	private readonly ComboBox _loopMode = new()
	{
		ItemsSource = Enum.GetValues<EnvelopeSustainLoopMode>(),
		MinWidth = 140,
	};
	private readonly TextBlock _message = new()
	{
		TextWrapping = TextWrapping.Wrap,
	};

	public NodeEnvelopeEditorControl(
		DocumentWorkspace workspace,
		NodeEnvelopeDefinition envelope,
		Action<string> changed)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_envelope = envelope
			?? throw new ArgumentNullException(nameof(envelope));
		_changed = changed
			?? throw new ArgumentNullException(nameof(changed));
		if (!workspace.Document.TryGet(envelope.Id, out SongObject? stored)
			|| !ReferenceEquals(stored, envelope))
			throw new InvalidOperationException(
				"The envelope is not part of the current song document.");

		_graph = new NodeEnvelopeGraphControl(workspace, envelope);
		_graph.EnvelopeChanged += (_, _) =>
		{
			RefreshFromEnvelope();
			_message.Text = "Envelope updated from graph.";
			_changed($"Updated envelope {_envelope.Name}");
		};

		StackPanel form = new() { Spacing = 8, Margin = new Thickness(14) };
		form.Children.Add(new TextBlock
		{
			Text = "Envelope graph",
			FontWeight = FontWeight.SemiBold,
		});
		form.Children.Add(new Border
		{
			BorderBrush = Brushes.Gray,
			BorderThickness = new Thickness(1),
			Child = _graph,
		});
		form.Children.Add(new TextBlock
		{
			Text = "Click within 4 pixels of a node to select it, drag to "
				+ "adjust its time/value, double-click a node to delete it, "
				+ "or double-click a line to insert a node. The first node "
				+ "remains at time 0. Sustain guide dragging and Match "
				+ "will follow in the next milestone.",
			TextWrapping = TextWrapping.Wrap,
		});
		form.Children.Add(new TextBlock
		{
			Text = "Envelope nodes (time in seconds, value)",
			FontWeight = FontWeight.SemiBold,
		});
		form.Children.Add(new TextBlock
		{
			Text = "One time,value pair per line; start at time 0, with strictly "
				+ "increasing times. Values may be negative or greater than 1.",
			TextWrapping = TextWrapping.Wrap,
		});
		form.Children.Add(_nodesInput);
		form.Children.Add(new TextBlock
		{
			Text = "The node list remains available for precise numeric editing "
				+ "while graphical editing is being completed.",
			FontStyle = FontStyle.Italic,
			TextWrapping = TextWrapping.Wrap,
			Foreground = Brushes.Gray,
		});
		Grid controls = new()
		{
			ColumnSpacing = 10, RowSpacing = 6,
			ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
			RowDefinitions = new RowDefinitions("Auto,Auto"),
		};
		AddField(controls, "Sustain start index", _sustainStart, 0, 0);
		AddField(controls, "Sustain end index", _sustainEnd, 0, 2);
		AddField(controls, "Loop mode", _loopMode, 1, 0);
		form.Children.Add(controls);
		Button apply = new() { Content = "Apply envelope", MinWidth = 140 };
		apply.Click += (_, _) => Apply();
		form.Children.Add(apply);
		form.Children.Add(_message);
		Content = form;
		RefreshFromEnvelope();
	}

	private static void AddField(
		Grid grid, string label, Control editor, int row, int col)
	{
		TextBlock text = new() { Text = label,
			VerticalAlignment = VerticalAlignment.Center };
		Grid.SetRow(text, row);
		Grid.SetColumn(text, col);
		grid.Children.Add(text);
		Grid.SetRow(editor, row);
		Grid.SetColumn(editor, col + 1);
		grid.Children.Add(editor);
	}

	public void RefreshFromEnvelope()
	{
		_graph.RefreshFromEnvelope();
		_nodesInput.Text = string.Join("\n", _envelope.Nodes.Select(node =>
			node.Time.TotalSeconds.ToString("G17", CultureInfo.InvariantCulture)
			+ ", "
			+ node.Value.ToString("G17", CultureInfo.InvariantCulture)));
		_sustainStart.Text = _envelope.SustainStartIndex.ToString(
			CultureInfo.InvariantCulture);
		_sustainEnd.Text = _envelope.SustainEndIndex.ToString(
			CultureInfo.InvariantCulture);
		_loopMode.SelectedItem = _envelope.SustainLoopMode;
	}

	private void Apply()
	{
		try
		{
			List<EnvelopeNode> nodes = ParseNodes(_nodesInput.Text);
			int start = ParseIndex(_sustainStart, "Sustain start");
			int end = ParseIndex(_sustainEnd, "Sustain end");
			if (_loopMode.SelectedItem is not EnvelopeSustainLoopMode mode)
				throw new ArgumentException("Select a sustain loop mode.");
			EnvelopeDocumentEditor.UpdateNodeEnvelope(
				_workspace, _envelope, nodes, start, end, mode);
			RefreshFromEnvelope();
			_message.Text = "Node envelope updated.";
			_changed($"Updated envelope {_envelope.Name}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private static int ParseIndex(TextBox box, string label)
	{
		if (!int.TryParse(box.Text, NumberStyles.Integer,
			CultureInfo.InvariantCulture, out int index))
			throw new ArgumentException($"{label} must be a node index.");
		return index;
	}

	private static List<EnvelopeNode> ParseNodes(string? input)
	{
		List<EnvelopeNode> nodes = [];
		foreach (string line in (input ?? "").Split('\n'))
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;
			string[] parts = line.Trim().Split(',');
			if (parts.Length != 2
				|| !double.TryParse(parts[0].Trim(), NumberStyles.Float,
					CultureInfo.InvariantCulture, out double seconds)
				|| !double.IsFinite(seconds)
				|| seconds < 0
				|| !double.TryParse(parts[1].Trim(), NumberStyles.Float,
					CultureInfo.InvariantCulture, out double value)
				|| !double.IsFinite(value))
				throw new ArgumentException(
					$"Invalid node {nodes.Count}: expected finite time,value pair.");
			TimeSpan time;
			try { time = TimeSpan.FromSeconds(seconds); }
			catch (OverflowException ex)
			{
				throw new ArgumentException(
					$"Node {nodes.Count} time is out of range.", ex);
			}
			nodes.Add(new EnvelopeNode(time, value));
		}
		return nodes;
	}
}

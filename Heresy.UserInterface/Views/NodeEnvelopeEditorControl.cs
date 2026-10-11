using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using Heresy.Core.Envelopes;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Shared interactive node graph and precision node-list editor for both
/// standalone Envelopes and FM Envelope nodes. Protects unsaved numeric edits
/// from concurrent changes to the same shared Envelope object.
/// </summary>
public sealed class NodeEnvelopeEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly NodeEnvelopeDefinition _envelope;
	private readonly Action<string> _changed;
	private readonly NodeEnvelopeGraphControl _graph;
	private bool _loadingFields;
	private bool _draftDirty;
	private string? _loadedNodesText;
	private string? _loadedStartText;
	private string? _loadedEndText;
	private string? _loadedVminText;
	private string? _loadedVmaxText;
	private EnvelopeNode[] _baselineNodes = [];
	private int _baselineStart;
	private int _baselineEnd;
	private EnvelopeSustainLoopMode _baselineMode;
	private double _baselineVmin;
	private double _baselineVmax;
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
	private readonly TextBox _vmin = new() { Width = 90 };
	private readonly TextBox _vmax = new() { Width = 90 };
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
		_nodesInput.TextChanged += (_, _) => MarkDraftDirty();
		_sustainStart.TextChanged += (_, _) => MarkDraftDirty();
		_sustainEnd.TextChanged += (_, _) => MarkDraftDirty();
		_vmin.TextChanged += (_, _) => MarkDraftDirty();
		_vmax.TextChanged += (_, _) => MarkDraftDirty();
		_loopMode.SelectionChanged += (_, _) => MarkDraftDirty();
		AttachedToVisualTree += OnAttached;
		DetachedFromVisualTree += OnDetached;

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
			Text = "Click a node to select it, drag the dot to move it, "
				+ "double-click a dot to delete it, or double-click a segment "
				+ "to add a node. Drag a sustain endpoint's vertical guide "
				+ "to move that boundary; drag any guide outside the sustain "
				+ "region to select a new range (either direction). "
				+ "Double-click a guide away from its dot to sustain one node. "
				+ "A loop mode of None plays straight through the curve. "
				+ "Tab/Shift-Tab selects nodes; arrows nudge them; "
				+ "Ctrl-Tab exits the graph. "
				+ "Select a sustain endpoint to use Match below the graph. "
				+ "Node zero remains at time 0.",
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
			RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
		};
		AddField(controls, "Sustain start index", _sustainStart, 0, 0);
		AddField(controls, "Sustain end index", _sustainEnd, 0, 2);
		AddField(controls, "Loop mode", _loopMode, 1, 0);
		AddField(controls, "Vmin", _vmin, 2, 0);
		AddField(controls, "Vmax", _vmax, 2, 2);
		form.Children.Add(controls);
		Button apply = new() { Content = "Apply envelope", MinWidth = 140 };
		apply.Click += (_, _) => Apply();
		Button reload = new() { Content = "Reload shared envelope" };
		reload.Click += (_, _) =>
		{
			RefreshFromEnvelope();
			_message.Text = "Numeric editor reloaded from the shared envelope.";
		};
		StackPanel actions = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
		};
		actions.Children.Add(apply);
		actions.Children.Add(reload);
		form.Children.Add(actions);
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

	private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
	{
		_workspace.Document.Changed += OnDocumentChanged;
		if (!BaselineIsCurrent())
			OnSharedEnvelopeChanged();
	}

	private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
		=> _workspace.Document.Changed -= OnDocumentChanged;

	private void OnDocumentChanged(object? sender, SongDocumentChangedEventArgs e)
	{
		_ = sender;
		_ = e;
		if (!BaselineIsCurrent())
			OnSharedEnvelopeChanged();
	}

	private void OnSharedEnvelopeChanged()
	{
		if (HasDraftChanges())
		{
			_message.Text = "This shared envelope changed in another editor. "
				+ "Your unsaved numbers are preserved but cannot overwrite the "
				+ "new curve. Reload the shared envelope before applying.";
			return;
		}
		RefreshFromEnvelope();
		_message.Text = "Shared envelope updated.";
	}

	private bool BaselineIsCurrent()
		=> _baselineStart == _envelope.SustainStartIndex
			&& _baselineEnd == _envelope.SustainEndIndex
			&& _baselineMode == _envelope.SustainLoopMode
			&& _baselineVmin == _envelope.Vmin
			&& _baselineVmax == _envelope.Vmax
			&& _baselineNodes.SequenceEqual(_envelope.Nodes);

	private void MarkDraftDirty()
	{
		if (!_loadingFields)
			_draftDirty = true;
	}

	private bool HasDraftChanges()
		=> _draftDirty
			|| _nodesInput.Text != _loadedNodesText
			|| _sustainStart.Text != _loadedStartText
			|| _sustainEnd.Text != _loadedEndText
			|| _vmin.Text != _loadedVminText
			|| _vmax.Text != _loadedVmaxText
			|| _loopMode.SelectedItem is not EnvelopeSustainLoopMode mode
			|| mode != _baselineMode;

	public void RefreshFromEnvelope()
	{
		_loadingFields = true;
		try
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
			_vmin.Text = _envelope.Vmin.ToString("G17", CultureInfo.InvariantCulture);
			_vmax.Text = _envelope.Vmax.ToString("G17", CultureInfo.InvariantCulture);
			_loadedNodesText = _nodesInput.Text;
			_loadedStartText = _sustainStart.Text;
			_loadedEndText = _sustainEnd.Text;
			_loadedVminText = _vmin.Text;
			_loadedVmaxText = _vmax.Text;
			_baselineNodes = _envelope.Nodes.ToArray();
			_baselineStart = _envelope.SustainStartIndex;
			_baselineEnd = _envelope.SustainEndIndex;
			_baselineMode = _envelope.SustainLoopMode;
			_baselineVmin = _envelope.Vmin;
			_baselineVmax = _envelope.Vmax;
			_draftDirty = false;
		}
		finally
		{
			_loadingFields = false;
		}
	}

	private void Apply()
	{
		try
		{
			if (!BaselineIsCurrent())
				throw new InvalidOperationException(
					"The shared envelope changed since this draft began. "
					+ "Reload the shared envelope before applying.");
			// Preserve exact TimeSpan ticks and floating-point values
			// when only display settings or sustain controls change.
			// Re-parsing an unchanged G17 time in seconds can round a
			// TimeSpan and accidentally create an audio revision.
			List<EnvelopeNode> nodes = _nodesInput.Text == _loadedNodesText
				? _baselineNodes.ToList()
				: ParseNodes(_nodesInput.Text);
			int start = ParseIndex(_sustainStart, "Sustain start");
			int end = ParseIndex(_sustainEnd, "Sustain end");
			if (_loopMode.SelectedItem is not EnvelopeSustainLoopMode mode)
				throw new ArgumentException("Select a sustain loop mode.");
			double vmin = ParseDisplayBound(_vmin, "Vmin");
			double vmax = ParseDisplayBound(_vmax, "Vmax");
			EnvelopeDocumentEditor.UpdateNodeEnvelope(
				_workspace, _envelope, nodes, start, end, mode, vmin, vmax);
			RefreshFromEnvelope();
			_message.Text = "Node envelope updated.";
			_changed($"Updated envelope {_envelope.Name}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private static double ParseDisplayBound(TextBox box, string label)
	{
		if (!double.TryParse(box.Text, NumberStyles.Float,
			CultureInfo.InvariantCulture, out double number)
			|| !double.IsFinite(number))
			throw new ArgumentException($"{label} must be finite.");
		return number;
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

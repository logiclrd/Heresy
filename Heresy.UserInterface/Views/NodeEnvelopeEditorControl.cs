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
/// The same live, shared node Envelope can be edited in the standalone
/// document view, Synth inspector, or an independent Instrument Volume
/// window. Each accepted edit notifies every mounted view. The latest
/// committed state replaces any older unfinished draft in other views.
/// </summary>
public sealed class NodeEnvelopeEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly NodeEnvelopeDefinition _envelope;
	private readonly Action<string> _changed;
	private readonly NodeEnvelopeGraphControl _graph;
	private bool _loadingFields;
	private bool _publishing;
	private string? _loadedNodesText;
	private string? _loadedStartText;
	private string? _loadedEndText;
	private string? _loadedVminText;
	private string? _loadedVmaxText;
	private EnvelopeSustainLoopMode _loadedMode;

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
	private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };

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

		// The raw node textbox is intentionally the *only* explicit batch.
		// Every other control commits its own value on focus loss.
		_sustainStart.LostFocus += (_, _) => TryCommitField(_sustainStart);
		_sustainEnd.LostFocus += (_, _) => TryCommitField(_sustainEnd);
		_loopMode.LostFocus += (_, _) => TryCommitField(_loopMode);
		_vmin.LostFocus += (_, _) => TryCommitField(_vmin);
		_vmax.LostFocus += (_, _) => TryCommitField(_vmax);
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
		Button setNodes = new() { Content = "Set nodes", MinWidth = 115,
			HorizontalAlignment = HorizontalAlignment.Left };
		setNodes.Click += (_, _) => SetNodes();
		form.Children.Add(setNodes);
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
			ColumnSpacing = 10,
			RowSpacing = 6,
			MaxWidth = 800,
			HorizontalAlignment = HorizontalAlignment.Left,
			ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
			RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
		};
		AddField(controls, "Sustain start index", _sustainStart, 0, 0);
		AddField(controls, "Sustain end index", _sustainEnd, 0, 2);
		AddField(controls, "Loop mode", _loopMode, 1, 0);
		AddField(controls, "Vmin", _vmin, 2, 0);
		AddField(controls, "Vmax", _vmax, 2, 2);
		form.Children.Add(controls);
		form.Children.Add(_message);
		Content = form;
		RefreshFromEnvelope();
	}

	private static void AddField(
		Grid grid, string label, Control editor, int row, int col)
	{
		TextBlock text = new()
		{
			Text = label,
			VerticalAlignment = VerticalAlignment.Center,
		};
		Grid.SetRow(text, row);
		Grid.SetColumn(text, col);
		grid.Children.Add(text);
		Grid.SetRow(editor, row);
		Grid.SetColumn(editor, col + 1);
		grid.Children.Add(editor);
	}

	private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
	{
		_envelope.Changed += OnEnvelopeChanged;
		// Reattachment must always see the latest accepted shared state.
		RefreshFromEnvelope();
	}

	private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
	{
		// Commit independent parameter fields before a view is removed,
		// whether or not the focus system sent each field LostFocus.
		TryCommitPendingFields();
		_envelope.Changed -= OnEnvelopeChanged;
	}

	private void OnEnvelopeChanged(object? sender, EventArgs e)
	{
		if (_publishing)
			return; // This editor updates only its own committed field.
		bool discarded = HasPendingChanges();
		RefreshFromEnvelope();
		_message.Text = discarded
			? "The shared envelope changed. Uncommitted edits in this view "
				+ "were replaced by the newly accepted state."
			: "Shared envelope updated.";
	}

	private bool HasPendingChanges()
		=> _nodesInput.Text != _loadedNodesText
			|| _sustainStart.Text != _loadedStartText
			|| _sustainEnd.Text != _loadedEndText
			|| _vmin.Text != _loadedVminText
			|| _vmax.Text != _loadedVmaxText
			|| _loopMode.SelectedItem is not EnvelopeSustainLoopMode mode
			|| mode != _loadedMode;

	/// <summary>Commit focus-loss parameters independently before a view
	/// transition or song Save. A second pass permits related valid bounds
	/// or sustain indices to be entered in either order.</summary>
	public bool TryCommitPendingFields()
	{
		bool valid = true;
		Control[] fields = [_sustainStart, _sustainEnd, _loopMode, _vmin, _vmax];
		foreach (Control field in fields)
			valid = TryCommitField(field) && valid;
		if (!valid)
		{
			valid = true;
			foreach (Control field in fields)
				valid = TryCommitField(field) && valid;
		}
		return valid;
	}

	/// <summary>Each field commits against the currently accepted shared
	/// Envelope, without reparsing raw node text or applying unrelated
	/// pending inputs from this editor.</summary>
	private bool TryCommitField(Control field)
	{
		if (_loadingFields || _publishing)
			return true;
		bool changedText = field switch
		{
			_ when ReferenceEquals(field, _sustainStart) =>
				_sustainStart.Text != _loadedStartText,
			_ when ReferenceEquals(field, _sustainEnd) =>
				_sustainEnd.Text != _loadedEndText,
			_ when ReferenceEquals(field, _vmin) =>
				_vmin.Text != _loadedVminText,
			_ when ReferenceEquals(field, _vmax) =>
				_vmax.Text != _loadedVmaxText,
			_ when ReferenceEquals(field, _loopMode) =>
				_loopMode.SelectedItem is not EnvelopeSustainLoopMode selected
				|| selected != _loadedMode,
			_ => false,
		};
		if (!changedText)
			return true;

		try
		{
			int start = ReferenceEquals(field, _sustainStart)
				? ParseIndex(_sustainStart, "Sustain start")
				: _envelope.SustainStartIndex;
			int end = ReferenceEquals(field, _sustainEnd)
				? ParseIndex(_sustainEnd, "Sustain end")
				: _envelope.SustainEndIndex;
			EnvelopeSustainLoopMode mode = ReferenceEquals(field, _loopMode)
				? _loopMode.SelectedItem is EnvelopeSustainLoopMode selected
					? selected
					: throw new ArgumentException("Select a sustain loop mode.")
				: _envelope.SustainLoopMode;
			double vmin = ReferenceEquals(field, _vmin)
				? ParseDisplayBound(_vmin, "Vmin")
				: _envelope.Vmin;
			double vmax = ReferenceEquals(field, _vmax)
				? ParseDisplayBound(_vmax, "Vmax")
				: _envelope.Vmax;

			uint before = _workspace.Document.DocumentRevision;
			_publishing = true;
			try
			{
				EnvelopeDocumentEditor.UpdateNodeEnvelope(
					_workspace, _envelope, _envelope.Nodes, start, end,
					mode, vmin, vmax);
			}
			finally { _publishing = false; }
			SyncFieldFromEnvelope(field);
			_message.Text = "Envelope parameter updated.";
			if (_workspace.Document.DocumentRevision != before)
				_changed($"Updated envelope {_envelope.Name}");
			return true;
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
			return false;
		}
	}

	private void SyncFieldFromEnvelope(Control field)
	{
		_loadingFields = true;
		try
		{
			if (ReferenceEquals(field, _sustainStart))
				_loadedStartText = _sustainStart.Text =
					_envelope.SustainStartIndex.ToString(CultureInfo.InvariantCulture);
			else if (ReferenceEquals(field, _sustainEnd))
				_loadedEndText = _sustainEnd.Text =
					_envelope.SustainEndIndex.ToString(CultureInfo.InvariantCulture);
			else if (ReferenceEquals(field, _vmin))
				_loadedVminText = _vmin.Text =
					_envelope.Vmin.ToString("G17", CultureInfo.InvariantCulture);
			else if (ReferenceEquals(field, _vmax))
				_loadedVmaxText = _vmax.Text =
					_envelope.Vmax.ToString("G17", CultureInfo.InvariantCulture);
			else if (ReferenceEquals(field, _loopMode))
			{
				_loadedMode = _envelope.SustainLoopMode;
				_loopMode.SelectedItem = _loadedMode;
			}
		}
		finally { _loadingFields = false; }
	}

	public void RefreshFromEnvelope()
	{
		_loadingFields = true;
		try
		{
			_graph.RefreshFromEnvelope();
			_loadedNodesText = _nodesInput.Text = string.Join("\n",
				_envelope.Nodes.Select(node =>
					node.Time.TotalSeconds.ToString("G17",
						CultureInfo.InvariantCulture)
					+ ", "
					+ node.Value.ToString("G17", CultureInfo.InvariantCulture)));
			_loadedStartText = _sustainStart.Text =
				_envelope.SustainStartIndex.ToString(CultureInfo.InvariantCulture);
			_loadedEndText = _sustainEnd.Text =
				_envelope.SustainEndIndex.ToString(CultureInfo.InvariantCulture);
			_loadedMode = _envelope.SustainLoopMode;
			_loopMode.SelectedItem = _loadedMode;
			_loadedVminText = _vmin.Text =
				_envelope.Vmin.ToString("G17", CultureInfo.InvariantCulture);
			_loadedVmaxText = _vmax.Text =
				_envelope.Vmax.ToString("G17", CultureInfo.InvariantCulture);
		}
		finally { _loadingFields = false; }
	}

	/// <summary>Explicitly accept *only* the raw node list, never the
	/// Sustain, Loop, Vmin or Vmax drafts adjacent to it.</summary>
	private void SetNodes()
	{
		if (_nodesInput.Text == _loadedNodesText)
			return;
		try
		{
			List<EnvelopeNode> nodes = ParseNodes(_nodesInput.Text);
			uint before = _workspace.Document.DocumentRevision;
			_publishing = true;
			try
			{
				EnvelopeDocumentEditor.UpdateNodeEnvelope(
					_workspace, _envelope, nodes,
					_envelope.SustainStartIndex, _envelope.SustainEndIndex,
					_envelope.SustainLoopMode);
			}
			finally { _publishing = false; }
			_loadingFields = true;
			try
			{
				_loadedNodesText = _nodesInput.Text = string.Join("\n",
					_envelope.Nodes.Select(node =>
						node.Time.TotalSeconds.ToString("G17",
							CultureInfo.InvariantCulture)
						+ ", "
						+ node.Value.ToString("G17", CultureInfo.InvariantCulture)));
			}
			finally { _loadingFields = false; }
			_message.Text = "Envelope nodes updated.";
			if (_workspace.Document.DocumentRevision != before)
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

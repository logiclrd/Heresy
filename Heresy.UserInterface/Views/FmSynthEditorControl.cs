using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Main-workspace editor for persistent FM-synthesized sound sources. Semantic
/// graph mutations flow through FmSynthDocumentEditor; the canvas owns only
/// transient drag state.
/// </summary>
public sealed class FmSynthEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly FmSynthDefinition _synth;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly FmSynthGraphCanvas _canvas;
	private readonly StackPanel _inspector =
		new()
		{
			Spacing = 8,
			Margin = new Thickness(10),
		};
	private readonly TextBlock _message =
		new()
		{
			TextWrapping = TextWrapping.Wrap,
		};

	private int? _selectedNodeId;

	public FmSynthEditorControl(
		DocumentWorkspace workspace,
		FmSynthDefinition synth,
		Action close,
		Action<string> changed)
	{
		_workspace =
			workspace
				?? throw new ArgumentNullException(nameof(workspace));
		_synth =
			synth
				?? throw new ArgumentNullException(nameof(synth));
		_close =
			close
				?? throw new ArgumentNullException(nameof(close));
		_changed =
			changed
				?? throw new ArgumentNullException(nameof(changed));

		_canvas =
			new FmSynthGraphCanvas(
				synth,
				SelectNode,
				MoveNode);
		_selectedNodeId =
			synth.Graph.OutputNodeId;
		_canvas.SetSelectedNode(
			_selectedNodeId);
		Content = BuildContent();
		RebuildInspector();
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
				Text = _synth.Name,
				FontSize = 20,
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			};

		DockPanel header =
			new()
			{
				Margin = new Thickness(10, 8),
			};
		DockPanel.SetDock(back, Dock.Left);
		header.Children.Add(back);
		header.Children.Add(title);

		StackPanel addButtons =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
				Margin = new Thickness(10, 4, 10, 8),
			};
		addButtons.Children.Add(
			Button(
				"+ Constant",
				AddConstant));
		addButtons.Children.Add(
			Button(
				"+ Oscillator",
				AddOscillator));
		addButtons.Children.Add(
			Button(
				"+ Envelope",
				AddEnvelope));
		addButtons.Children.Add(
			Button(
				"+ Operator",
				AddOperator));

		StackPanel top = new();
		top.Children.Add(header);
		top.Children.Add(addButtons);

		ScrollViewer graphScroll =
			new()
			{
				Content = _canvas,
				HorizontalScrollBarVisibility =
					ScrollBarVisibility.Auto,
				VerticalScrollBarVisibility =
					ScrollBarVisibility.Auto,
			};

		ScrollViewer inspectorScroll =
			new()
			{
				Content = _inspector,
				Width = 350,
				HorizontalScrollBarVisibility =
					ScrollBarVisibility.Disabled,
				VerticalScrollBarVisibility =
					ScrollBarVisibility.Auto,
			};

		Border inspectorBorder =
			new()
			{
				BorderBrush = Brushes.Gray,
				BorderThickness =
					new Thickness(
						1,
						0,
						0,
						0),
				Child = inspectorScroll,
			};

		Grid body = new();
		body.ColumnDefinitions.Add(
			new ColumnDefinition(
				new GridLength(
					1,
					GridUnitType.Star)));
		body.ColumnDefinitions.Add(
			new ColumnDefinition(
				GridLength.Auto));
		Grid.SetColumn(
			graphScroll,
			0);
		Grid.SetColumn(
			inspectorBorder,
			1);
		body.Children.Add(graphScroll);
		body.Children.Add(inspectorBorder);

		Border messageBorder =
			new()
			{
				Padding =
					new Thickness(
						10,
						5),
				Child = _message,
			};

		DockPanel root = new();
		DockPanel.SetDock(
			top,
			Dock.Top);
		DockPanel.SetDock(
			messageBorder,
			Dock.Bottom);
		root.Children.Add(top);
		root.Children.Add(messageBorder);
		root.Children.Add(body);
		return root;
	}

	private void AddConstant()
	{
		try
		{
			(double X, double Y) =
				NextSpawnPosition();
			int id =
				FmSynthDocumentEditor.AddConstantNode(
					_workspace,
					_synth,
					value: 0.0,
					X,
					Y);
			AfterGraphChange(
				id,
				$"Added FM constant node #{id}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void AddOscillator()
	{
		try
		{
			(double X, double Y) =
				NextSpawnPosition();
			int id =
				FmSynthDocumentEditor.AddOscillatorNode(
					_workspace,
					_synth,
					FmOscillatorWaveform.Sine,
					frequencyHz: 440.0,
					x: X,
					y: Y);
			AfterGraphChange(
				id,
				$"Added FM oscillator node #{id}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void AddEnvelope()
	{
		try
		{
			EnvelopeDefinition? envelope =
				_workspace.Document.Objects.Values
					.OfType<EnvelopeDefinition>()
					.OrderBy(
						item => item.Name,
						StringComparer.OrdinalIgnoreCase)
					.ThenBy(
						item => item.Id.Value)
					.FirstOrDefault();
			if (envelope is null)
			{
				_message.Text =
					"Create an envelope before adding an FM envelope node.";
				return;
			}

			(double X, double Y) =
				NextSpawnPosition();
			int id =
				FmSynthDocumentEditor.AddEnvelopeNode(
					_workspace,
					_synth,
					envelope.Id,
					X,
					Y);
			AfterGraphChange(
				id,
				$"Added FM envelope node #{id}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void AddOperator()
	{
		try
		{
			int input =
				_selectedNodeId
					?? _synth.Graph.OutputNodeId;
			(double X, double Y) =
				NextSpawnPosition();
			int id =
				FmSynthDocumentEditor.AddOperatorNode(
					_workspace,
					_synth,
					FmOperatorKind.Add,
					[input],
					X,
					Y);
			AfterGraphChange(
				id,
				$"Added FM operator node #{id}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void SelectNode(
		int nodeId)
	{
		_selectedNodeId = nodeId;
		_canvas.SetSelectedNode(
			nodeId);
		RebuildInspector();
	}

	private void MoveNode(
		int nodeId,
		double x,
		double y)
	{
		try
		{
			FmSynthDocumentEditor.MoveNode(
				_workspace,
					_synth,
					nodeId,
					x,
					y);
			_changed(
				$"Moved FM node #{nodeId}");
			_canvas.Refresh();
			_canvas.SetSelectedNode(
				_selectedNodeId);
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
			_canvas.Refresh();
			_canvas.SetSelectedNode(
				_selectedNodeId);
		}
	}

	private void RebuildInspector()
	{
		_inspector.Children.Clear();

		FmSynthNode? node =
			_selectedNodeId.HasValue
				? _synth.Graph.Nodes
					.FirstOrDefault(candidate =>
						candidate.Id == _selectedNodeId.Value)
				: null;
		if (node is null)
		{
			_inspector.Children.Add(
				new TextBlock
				{
					Text = "Select an FM node to edit its parameters.",
					TextWrapping = TextWrapping.Wrap,
				});
			return;
		}

		_inspector.Children.Add(
			new TextBlock
			{
				Text =
					$"Node #{node.Id}: {NodeName(node)}",
				FontSize = 17,
				FontWeight = FontWeight.SemiBold,
			});

		if (node.Id == _synth.Graph.OutputNodeId)
		{
			_inspector.Children.Add(
				new TextBlock
				{
					Text = "Current graph output",
					FontWeight = FontWeight.SemiBold,
				});
		}
		else
		{
			_inspector.Children.Add(
				Button(
					"Make output",
					() =>
					{
						try
						{
							FmSynthDocumentEditor.SetOutputNode(
								_workspace,
								_synth,
								node.Id);
							AfterGraphChange(
								node.Id,
								$"Set FM output to node #{node.Id}");
						}
						catch (Exception ex)
						{
							_message.Text = ex.Message;
						}
					}));
		}

		switch (node)
		{
			case FmConstantNode constant:
				AddConstantInspector(
					constant);
				break;

			case FmOscillatorNode oscillator:
				AddOscillatorInspector(
					oscillator);
				break;

			case FmEnvelopeNode envelope:
				AddEnvelopeInspector(
					envelope);
				break;

			case FmOperatorNode op:
				AddOperatorInspector(
					op);
				break;
		}

		AddRoutingInspector(node);

		Button remove =
			Button(
				"Remove node",
				() =>
				{
					try
					{
						FmSynthDocumentEditor.RemoveNode(
							_workspace,
							_synth,
							node.Id);
						_selectedNodeId =
							_synth.Graph.OutputNodeId;
						AfterGraphChange(
							_selectedNodeId,
							$"Removed FM node #{node.Id}");
					}
					catch (Exception ex)
					{
						_message.Text = ex.Message;
					}
				});
		_inspector.Children.Add(
			new Separator());
		_inspector.Children.Add(remove);
	}

	private void AddConstantInspector(
		FmConstantNode node)
	{
		TextBox value =
			NumberBox(
				node.Value);
		AddField(
			"Value",
			value);
		_inspector.Children.Add(
			Button(
				"Apply",
				() =>
					ApplyNode(
						new FmConstantNode(
							node.Id,
							ParseDouble(
								value,
								"Value")))));
	}

	private void AddOscillatorInspector(
		FmOscillatorNode node)
	{
		ComboBox waveform =
			new()
			{
				ItemsSource =
					Enum.GetValues<FmOscillatorWaveform>(),
				SelectedItem = node.Waveform,
			};
		TextBox frequency =
			NumberBox(
				node.FrequencyHz);
		TextBox minimum =
			NumberBox(
				node.Minimum);
		TextBox maximum =
			NumberBox(
				node.Maximum);
		NodeChoice[] multiplierChoices =
			new[]
			{
				new NodeChoice(
					null,
					"— none"),
			}
			.Concat(
				_synth.Graph.Nodes
					.Where(candidate =>
						candidate.Id != node.Id)
					.Select(candidate =>
						new NodeChoice(
							candidate.Id,
							$"#{candidate.Id} {NodeName(candidate)}")))
			.ToArray();
		ComboBox multiplier =
			new()
			{
				ItemsSource = multiplierChoices,
				SelectedItem =
					multiplierChoices.FirstOrDefault(
						choice =>
							choice.NodeId
								== node.MultiplierNodeId)
					?? multiplierChoices[0],
			};
		CheckBox exponential =
			new()
			{
				Content = "Exponential multiplier (semitones)",
				IsChecked =
					node.ExponentialMultiplier,
			};

		AddField(
			"Waveform",
			waveform);
		AddField(
			"Frequency Hz",
			frequency);
		AddField(
			"Vmin",
			minimum);
		AddField(
			"Vmax",
			maximum);
		AddField(
			"Multiplier input",
			multiplier);
		_inspector.Children.Add(exponential);
		_inspector.Children.Add(
			Button(
				"Apply",
				() =>
				{
					if (waveform.SelectedItem
						is not FmOscillatorWaveform selectedWaveform)
					{
						return;
					}

					int? multiplierId =
						(multiplier.SelectedItem as NodeChoice)
							?.NodeId;
					ApplyNode(
						new FmOscillatorNode(
							node.Id,
							selectedWaveform,
							ParsePositiveDouble(
								frequency,
								"Frequency"),
							ParseDouble(
								minimum,
								"Vmin"),
							ParseDouble(
								maximum,
								"Vmax"),
							multiplierId,
							exponential.IsChecked
								== true));
				}));
	}

	private void AddEnvelopeInspector(
		FmEnvelopeNode node)
	{
		EnvelopeChoice[] envelopes =
			_workspace.Document.Objects.Values
				.OfType<EnvelopeDefinition>()
				.OrderBy(
					envelope => envelope.Name,
					StringComparer.OrdinalIgnoreCase)
				.ThenBy(
					envelope => envelope.Id.Value)
				.Select(envelope =>
					new EnvelopeChoice(
						envelope.Id,
						$"{envelope.Name} <{envelope.Id.Value}>"))
				.ToArray();
		ComboBox envelopeBox =
			new()
			{
				ItemsSource = envelopes,
				SelectedItem =
					envelopes.FirstOrDefault(
						choice =>
							choice.Id
								== node.EnvelopeId),
			};
		AddField(
			"Envelope",
			envelopeBox);
		_inspector.Children.Add(
			Button(
				"Apply",
				() =>
				{
					if (envelopeBox.SelectedItem
						is not EnvelopeChoice selected)
					{
						_message.Text =
							"Select a live envelope.";
						return;
					}

					ApplyNode(
						new FmEnvelopeNode(
							node.Id,
							selected.Id));
				}));
	}

	private void AddOperatorInspector(
		FmOperatorNode node)
	{
		ComboBox operation =
			new()
			{
				ItemsSource =
					Enum.GetValues<FmOperatorKind>(),
				SelectedItem = node.Operation,
			};
		TextBox inputs =
			new()
			{
				Text =
					string.Join(
						", ",
						node.InputNodeIds),
			};
		AddField(
			"Operation",
			operation);
		AddField(
			"Input node IDs",
			inputs);
		_inspector.Children.Add(
			Button(
				"Apply",
				() =>
				{
					if (operation.SelectedItem
						is not FmOperatorKind selectedOperation)
					{
						return;
					}

					ApplyNode(
						new FmOperatorNode(
							node.Id,
							selectedOperation,
							ParseNodeIds(
								inputs.Text)));
				}));
	}

	private void AddRoutingInspector(
		FmSynthNode node)
	{
		if (node.InputNodeIds.Count == 0)
			return;

		_inspector.Children.Add(
			new Separator());
		_inspector.Children.Add(
			new TextBlock
			{
				Text = "Connection routing",
				FontWeight = FontWeight.SemiBold,
			});
		_inspector.Children.Add(
			new TextBlock
			{
				Text =
					"Optional waypoints use invariant x,y pairs separated by semicolons. Empty means automatic routing.",
				TextWrapping = TextWrapping.Wrap,
				FontSize = 11,
			});

		for (int inputIndex = 0;
			inputIndex < node.InputNodeIds.Count;
			inputIndex++)
		{
			int capturedIndex = inputIndex;
			int sourceId =
				node.InputNodeIds[inputIndex];
			FmSynthConnectionRoutingHint? hint =
				_synth.ConnectionRoutingHints
					.FirstOrDefault(candidate =>
						candidate.SourceNodeId
							== sourceId
						&& candidate.TargetNodeId
							== node.Id
						&& candidate.TargetInputIndex
							== inputIndex);
			TextBox route =
				new()
				{
					Text =
						hint is null
							? string.Empty
							: FormatRoutePoints(
								hint.RoutePoints),
				};
			AddField(
				$"Input {inputIndex} from #{sourceId}",
				route);

			StackPanel actions =
				new()
				{
					Orientation = Orientation.Horizontal,
					Spacing = 6,
				};
			actions.Children.Add(
				Button(
					"Apply waypoints",
					() =>
					{
						try
						{
							FmSynthRoutePoint[] points =
								ParseRoutePoints(
									route.Text);
							if (points.Length == 0)
							{
								FmSynthDocumentEditor.ClearRoutingHint(
									_workspace,
									_synth,
									node.Id,
									capturedIndex);
							}
							else
							{
								FmSynthDocumentEditor.SetRoutingHint(
									_workspace,
									_synth,
									sourceId,
									node.Id,
									capturedIndex,
									points);
							}
							_canvas.Refresh();
							_canvas.SetSelectedNode(
								_selectedNodeId);
							_changed(
								$"Updated route into FM node #{node.Id}");
						}
						catch (Exception ex)
						{
							_message.Text = ex.Message;
						}
					}));
			actions.Children.Add(
				Button(
					"Auto",
					() =>
					{
						FmSynthDocumentEditor.ClearRoutingHint(
							_workspace,
							_synth,
							node.Id,
							capturedIndex);
						route.Text = string.Empty;
						_canvas.Refresh();
						_canvas.SetSelectedNode(
							_selectedNodeId);
						_changed(
							$"Reset route into FM node #{node.Id}");
					}));
			_inspector.Children.Add(actions);
		}
	}

	private void ApplyNode(
		FmSynthNode replacement)
	{
		try
		{
			FmSynthDocumentEditor.UpdateNode(
				_workspace,
				_synth,
				replacement);
			AfterGraphChange(
				replacement.Id,
				$"Updated FM node #{replacement.Id}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void AfterGraphChange(
		int? selectedNodeId,
		string message)
	{
		_selectedNodeId = selectedNodeId;
		_canvas.Refresh();
		_canvas.SetSelectedNode(
			selectedNodeId);
		RebuildInspector();
		_message.Text = string.Empty;
		_changed(message);
	}

	private (double X, double Y) NextSpawnPosition()
	{
		int index =
			_synth.Graph.Nodes.Count;
		return (
			80.0 + ((index % 4) * 220.0),
			80.0 + ((index / 4) * 130.0));
	}

	private void AddField(
		string label,
		Control control)
	{
		_inspector.Children.Add(
			new TextBlock
			{
				Text = label,
				FontWeight = FontWeight.SemiBold,
			});
		_inspector.Children.Add(control);
	}

	private static Button Button(
		string label,
		Action action)
	{
		Button button =
			new()
			{
				Content = label,
			};
		button.Click += (_, _) => action();
		return button;
	}

	private static TextBox NumberBox(
		double value)
		=> new()
		{
			Text =
				value.ToString(
					"R",
					CultureInfo.InvariantCulture),
		};

	private static double ParseDouble(
		TextBox box,
		string label)
	{
		if (!double.TryParse(
			box.Text,
			NumberStyles.Float,
			CultureInfo.InvariantCulture,
			out double value)
			|| !double.IsFinite(value))
		{
			throw new ArgumentException(
				$"{label} must be a finite invariant-culture number.");
		}
		return value;
	}

	private static double ParsePositiveDouble(
		TextBox box,
		string label)
	{
		double value =
			ParseDouble(
				box,
				label);
		if (!(value > 0.0))
		{
			throw new ArgumentException(
				$"{label} must be greater than zero.");
		}
		return value;
	}

	private static int[] ParseNodeIds(
		string? text)
	{
		string[] components =
			(text ?? string.Empty)
				.Split(
					[',', ';', ' ', '\t', '\r', '\n'],
					StringSplitOptions.RemoveEmptyEntries
						| StringSplitOptions.TrimEntries);
		if (components.Length == 0)
		{
			throw new ArgumentException(
				"An operator must have at least one input node ID.");
		}

		int[] result =
			new int[components.Length];
		for (int index = 0;
			index < components.Length;
			index++)
		{
			if (!int.TryParse(
				components[index],
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out result[index])
				|| result[index] < 0)
			{
				throw new ArgumentException(
					$"'{components[index]}' is not a valid non-negative node ID.");
			}
		}
		return result;
	}

	private static string FormatRoutePoints(
		IReadOnlyList<FmSynthRoutePoint> points)
		=> string.Join(
			"; ",
			points.Select(point =>
				$"{point.X.ToString("R", CultureInfo.InvariantCulture)},"
				+ point.Y.ToString(
					"R",
					CultureInfo.InvariantCulture)));

	private static FmSynthRoutePoint[] ParseRoutePoints(
		string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return [];

		string[] waypoints =
			text.Split(
				';',
				StringSplitOptions.RemoveEmptyEntries
					| StringSplitOptions.TrimEntries);
		List<FmSynthRoutePoint> result = [];
		foreach (string waypoint in waypoints)
		{
			string[] components =
				waypoint.Split(
					',',
					StringSplitOptions.TrimEntries);
			if (components.Length != 2
				|| !double.TryParse(
					components[0],
					NumberStyles.Float,
					CultureInfo.InvariantCulture,
					out double x)
				|| !double.TryParse(
					components[1],
					NumberStyles.Float,
					CultureInfo.InvariantCulture,
					out double y)
				|| !double.IsFinite(x)
				|| !double.IsFinite(y))
			{
				throw new ArgumentException(
					$"Routing waypoint '{waypoint}' must be written as finite invariant x,y.");
			}

			result.Add(
				new FmSynthRoutePoint(
					x,
					y));
		}
		return [.. result];
	}

	private static string NodeName(
		FmSynthNode node)
		=> node switch
		{
			FmConstantNode => "Constant",
			FmOscillatorNode => "Oscillator",
			FmEnvelopeNode => "Envelope",
			FmOperatorNode => "Operator",
			_ => node.GetType().Name,
		};

	private sealed record NodeChoice(
		int? NodeId,
		string DisplayName)
	{
		public override string ToString()
			=> DisplayName;
	}

	private sealed record EnvelopeChoice(
		ObjectId Id,
		string DisplayName)
	{
		public override string ToString()
			=> DisplayName;
	}
}

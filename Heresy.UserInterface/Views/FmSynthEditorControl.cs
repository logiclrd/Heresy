using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Filters;
using Heresy.Core.Objects;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.PatternEditing;

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
	private readonly PatternLiveAuditionActions? _liveAudition;
	private readonly FmSynthAuditionKeyboard _auditionKeyboard = new();
	private readonly Border _testArea = new()
	{
		Focusable = true,
		Background = Brushes.DimGray,
		BorderBrush = Brushes.Gray,
		BorderThickness = new Thickness(2),
		CornerRadius = new CornerRadius(5),
		Padding = new Thickness(22, 9),
		Child = new TextBlock
		{
			Text = "Test",
			FontSize = 17,
			FontWeight = FontWeight.SemiBold,
			Foreground = Brushes.White,
		},
	};
	private readonly TextBlock _auditionOctave = new();
	private Window? _auditionOwner;
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
		Action<string> changed,
		PatternLiveAuditionActions? liveAudition = null)
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
		_liveAudition = liveAudition;

		_testArea.PointerPressed += (_, e) =>
		{
			_testArea.Focus();
			e.Handled = true;
		};
		_testArea.KeyDown += async (_, e) =>
			await OnTestKeyDownAsync(e);
		_testArea.KeyUp += async (_, e) =>
			await OnTestKeyUpAsync(e);
		_testArea.GotFocus += (_, _) =>
			_testArea.BorderBrush = Brushes.LightSkyBlue;
		_testArea.LostFocus += async (_, _) =>
		{
			_testArea.BorderBrush = Brushes.Gray;
			await ReleaseAllTestNotesAsync();
		};
		UpdateAuditionOctave();

		_canvas =
			new FmSynthGraphCanvas(
				synth,
				SelectNode,
				MoveNode,
				ConnectNodes,
				EditConnectionWaypoints);
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
		addButtons.Children.Add(Button("+ Filter", AddFilter));

		StackPanel top = new();
		top.Children.Add(header);
		top.Children.Add(addButtons);
		top.Children.Add(BuildAuditionArea());

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
				Width = 470,
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

	private Control BuildAuditionArea()
	{
		StackPanel text = new()
		{
			Spacing = 4,
			VerticalAlignment = VerticalAlignment.Center,
		};
		text.Children.Add(_auditionOctave);
		text.Children.Add(
			new TextBlock
			{
				Text = "Click Test, then use tracker piano keys (Z S X D C ...). Keypad * / change octave. Release keys to stop.",
				FontSize = 11,
				TextWrapping = TextWrapping.Wrap,
			});

		StackPanel row = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12,
			Margin = new Thickness(10, 0, 10, 10),
		};
		row.Children.Add(_testArea);
		row.Children.Add(text);
		return row;
	}

	private void UpdateAuditionOctave()
		=> _auditionOctave.Text =
			$"Audition octave: {_auditionKeyboard.BaseOctave}";

	private async Task OnTestKeyDownAsync(KeyEventArgs e)
	{
		int previousOctave = _auditionKeyboard.BaseOctave;
		StartFmSynthAuditionNote? note =
			_auditionKeyboard.KeyDown(e.PhysicalKey, e.KeyModifiers);

		bool octaveCommand =
			PatternOctaveKeyboard.TryAdjust(
				e.PhysicalKey,
				e.KeyModifiers,
				previousOctave,
				out _);
		if (octaveCommand)
		{
			UpdateAuditionOctave();
			e.Handled = true;
			return;
		}

		bool pianoKey =
			(e.KeyModifiers & (KeyModifiers.Control
				| KeyModifiers.Alt | KeyModifiers.Meta)) == 0
			&& PatternNoteKeyboard.TryGetSemitoneOffset(
				e.PhysicalKey,
				out _);
		if (!pianoKey)
			return;

		e.Handled = true;
		if (note is null)
			return; // Keyboard auto-repeat never retriggers a held note.

		if (_liveAudition is null)
		{
			_message.Text = "Realtime FM audition is unavailable in this host.";
			return;
		}

		try
		{
			await _liveAudition.SendEventAsync(
				FmSynthAuditionCompiler.Start(_synth.Id, note));
			_message.Text = $"FM audition: octave {_auditionKeyboard.BaseOctave}.";
		}
		catch (Exception ex)
		{
			_message.Text = $"FM audition failed: {ex.Message}";
		}
	}

	private async Task OnTestKeyUpAsync(KeyEventArgs e)
	{
		ReleaseFmSynthAuditionNote? release =
			_auditionKeyboard.KeyUp(e.PhysicalKey);
		if (release is null)
			return;
		e.Handled = true;
		await ReleaseTestNoteAsync(release);
	}

	private async Task ReleaseTestNoteAsync(
		ReleaseFmSynthAuditionNote release)
	{
		if (_liveAudition is null)
			return;

		try
		{
			await _liveAudition.SendEventAsync(
				FmSynthAuditionCompiler.Release(release));
		}
		catch (Exception ex)
		{
			_message.Text = $"FM audition release failed: {ex.Message}";
		}
	}

	private async Task ReleaseAllTestNotesAsync()
	{
		foreach (ReleaseFmSynthAuditionNote release
			in _auditionKeyboard.ReleaseAll())
		{
			await ReleaseTestNoteAsync(release);
		}
	}

	private async void OnAuditionOwnerDeactivated(
		object? sender,
		EventArgs e)
	{
		_ = sender;
		_ = e;
		await ReleaseAllTestNotesAsync();
	}

	protected override void OnAttachedToVisualTree(
		VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_auditionOwner = TopLevel.GetTopLevel(this) as Window;
		if (_auditionOwner is not null)
			_auditionOwner.Deactivated += OnAuditionOwnerDeactivated;
	}

	protected override void OnDetachedFromVisualTree(
		VisualTreeAttachmentEventArgs e)
	{
		if (_auditionOwner is not null)
		{
			_auditionOwner.Deactivated -= OnAuditionOwnerDeactivated;
			_auditionOwner = null;
		}
		_ = ReleaseAllTestNotesAsync();
		base.OnDetachedFromVisualTree(e);
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
			// New envelope nodes intentionally begin unassigned, even if
			// this song already contains reusable envelope definitions.
			(double X, double Y) = NextSpawnPosition();
			int id = FmSynthDocumentEditor.AddEnvelopeNode(
				_workspace, _synth, ObjectId.None, X, Y);
			AfterGraphChange(id,
				$"Added unassigned FM envelope node #{id}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void AddFilter()
	{
		try
		{
			(double x, double y) = NextSpawnPosition();
			int id = FmSynthDocumentEditor.AddFilterNode(
				_workspace, _synth, x, y);
			AfterGraphChange(id, $"Added unassigned FM Filter node #{id}");
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

	private void ConnectNodes(
		int sourceNodeId,
		int targetNodeId,
		int targetInputIndex)
	{
		try
		{
			uint previousRevision = _workspace.Document.AudioRevision;
			FmSynthDocumentEditor.ConnectNodes(
				_workspace,
				_synth,
				sourceNodeId,
				targetNodeId,
				targetInputIndex);
			if (_workspace.Document.AudioRevision != previousRevision)
			{
				AfterGraphChange(
					targetNodeId,
					$"Connected FM node #{sourceNodeId} to #{targetNodeId}, input {targetInputIndex + 1}");
			}
		}
		catch (Exception ex)
		{
			_message.Text = $"Cannot connect FM nodes: {ex.Message}";
			_canvas.Refresh();
			_canvas.SetSelectedNode(_selectedNodeId);
		}
	}

	private void EditConnectionWaypoints(
		int sourceNodeId,
		int targetNodeId,
		int targetInputIndex,
		IReadOnlyList<FmSynthRoutePoint> waypoints)
	{
		try
		{
			if (waypoints.Count == 0)
			{
				FmSynthDocumentEditor.ClearRoutingHint(
					_workspace, _synth, targetNodeId, targetInputIndex);
			}
			else
			{
				FmSynthDocumentEditor.SetRoutingHint(
					_workspace, _synth, sourceNodeId,
					targetNodeId, targetInputIndex, waypoints);
			}

			_canvas.Refresh();
			_canvas.SetSelectedNode(_selectedNodeId);
			_changed($"Updated waypoints into FM node #{targetNodeId}");
		}
		catch (Exception ex)
		{
			_message.Text = $"Could not edit FM connection: {ex.Message}";
			_canvas.Refresh();
			_canvas.SetSelectedNode(_selectedNodeId);
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

			case FmFilterNode filter:
				AddFilterInspector(filter);
				break;

			case FmOperatorNode op:
				AddOperatorInspector(
					op);
				break;
		}

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

	private void AddConstantInspector(FmConstantNode node)
	{
		TextBox value = NumberBox(node.Value);
		AddField("Value", value);
		BindParameterText(
			value,
			text =>
			{
				double parsed = ParseDouble(text, "Value");
				UpdateParameter(
					node.Id,
					current =>
					{
						FmConstantNode constant = (FmConstantNode)current;
						return constant.Value == parsed
							? null
							: new FmConstantNode(node.Id, parsed);
					});
			});
	}

	private void AddOscillatorInspector(FmOscillatorNode node)
	{
		ComboBox waveform =
			new()
			{
				ItemsSource = Enum.GetValues<FmOscillatorWaveform>(),
				SelectedItem = node.Waveform,
			};
		TextBox frequency = NumberBox(node.FrequencyHz);
		CheckBox constant = new()
		{
			Content = "Constant",
			IsChecked = node.Constant,
		};
		TextBox minimum = NumberBox(node.Minimum);
		TextBox maximum = NumberBox(node.Maximum);

		NodeChoice[] multiplierChoices =
			new[] { new NodeChoice(null, "— none") }
			.Concat(
				_synth.Graph.Nodes
					.Where(candidate => candidate.Id != node.Id)
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
					multiplierChoices.FirstOrDefault(choice =>
						choice.NodeId == node.MultiplierNodeId)
					?? multiplierChoices[0],
			};
		CheckBox exponential =
			new()
			{
				Content = "Exponential multiplier (semitones)",
				IsChecked = node.ExponentialMultiplier,
			};

		AddField("Waveform", waveform);
		AddField("Frequency Hz", frequency);
		_inspector.Children.Add(constant);
		AddField("Vmin", minimum);
		AddField("Vmax", maximum);
		AddField("Multiplier input", multiplier);
		_inspector.Children.Add(exponential);

		BindParameterSelection(
			waveform,
			() => ((FmOscillatorNode)CurrentNode(node.Id)).Waveform,
			item => UpdateOscillator(node.Id, waveform: (FmOscillatorWaveform)item));

		BindParameterText(
			frequency,
			text => UpdateOscillator(
				node.Id,
				frequency: ParsePositiveDouble(text, "Frequency")));
		BindParameterToggle(
			constant,
			() => ((FmOscillatorNode)CurrentNode(node.Id)).Constant,
			value => UpdateOscillator(node.Id, constant: value));
		BindParameterText(
			minimum,
			text => UpdateOscillator(
				node.Id,
				minimum: ParseDouble(text, "Vmin")));
		BindParameterText(
			maximum,
			text => UpdateOscillator(
				node.Id,
				maximum: ParseDouble(text, "Vmax")));

		BindParameterSelection(
			multiplier,
			() =>
			{
				int? id = ((FmOscillatorNode)CurrentNode(node.Id))
					.MultiplierNodeId;
				return multiplierChoices.First(choice => choice.NodeId == id);
			},
			item => UpdateOscillator(
				node.Id,
				multiplierId: ((NodeChoice)item).NodeId,
				replaceMultiplier: true));

		BindParameterToggle(
			exponential,
			() => ((FmOscillatorNode)CurrentNode(node.Id)).ExponentialMultiplier,
			value => UpdateOscillator(node.Id, exponential: value));
	}

	private void AddEnvelopeInspector(FmEnvelopeNode node)
	{
		// The New... option is first and italic; (None) can clear an
		// assignment later. An unassigned node initially has *no selected
		// ComboBox value*, rather than implicitly selecting a catalogue
		// envelope or even the (None) choice.
		ComboBoxItem newEnvelopeItem = new()
		{
			Content = new TextBlock
			{
				Text = "New...",
				FontStyle = FontStyle.Italic,
			},
		};
		ComboBoxItem clearEnvelopeItem = new()
		{
			Content = "(None)",
		};
		EnvelopeChoice[] envelopes =
			_workspace.Document.Objects.Values
				.OfType<EnvelopeDefinition>()
				.OrderBy(envelope => envelope.Name,
					StringComparer.OrdinalIgnoreCase)
				.ThenBy(envelope => envelope.Id.Value)
				.Select(envelope => new EnvelopeChoice(
					envelope.Id,
					$"{envelope.Name} <{envelope.Id.Value}>"))
				.ToArray();
		List<object> options = [newEnvelopeItem, clearEnvelopeItem];
		options.AddRange(envelopes);
		ComboBox envelopeBox = new()
		{
			ItemsSource = options,
			SelectedItem = node.EnvelopeId.IsNone
				? null
				: envelopes.FirstOrDefault(choice =>
					choice.Id == node.EnvelopeId),
		};
		AddField("Envelope", envelopeBox);

		bool choosing = false;
		envelopeBox.SelectionChanged += async (_, _) =>
		{
			if (choosing || envelopeBox.SelectedItem is not object selection)
				return;
			choosing = true;
			try
			{
				if (ReferenceEquals(selection, newEnvelopeItem))
				{
					Window? owner = TopLevel.GetTopLevel(this) as Window;
					if (owner is null)
						throw new InvalidOperationException(
							"An open FM editor window is required to create an envelope.");

					TextPromptDialog prompt = new(
						"New envelope", "Envelope name:", "New Envelope");
					string? name = await prompt.ShowDialog<string?>(owner);
					if (name is not null)
					{
						// New is created in the document's shared Envelopes
						// section, then assigned by its stable ObjectId.
						NodeEnvelopeDefinition created =
							EnvelopeDocumentEditor.CreateNodeEnvelope(
								_workspace, name);
						AssignEnvelope(node.Id, created.Id);
						AfterGraphChange(node.Id,
							$"Created and assigned envelope {created.Name}");
						return;
					}
				}
				else
				{
					ObjectId selectedId =
						ReferenceEquals(selection, clearEnvelopeItem)
							? ObjectId.None
							: ((EnvelopeChoice)selection).Id;
					AssignEnvelope(node.Id, selectedId);
					AfterGraphChange(node.Id,
						selectedId.IsNone
							? $"Cleared FM envelope node #{node.Id}"
							: $"Assigned envelope to FM node #{node.Id}");
					return;
				}
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
			finally
			{
				choosing = false;
			}

			// A canceled/failed New... action restores the previous
			// selection, including null for an unassigned FM node. Guard
			// this programmatic restoration: selecting an existing option
			// must not accidentally fire an additional user edit.
			choosing = true;
			try
			{
				ObjectId committedId =
					((FmEnvelopeNode)CurrentNode(node.Id)).EnvelopeId;
				envelopeBox.SelectedItem = committedId.IsNone
					? null
					: envelopes.FirstOrDefault(choice =>
						choice.Id == committedId);
			}
			finally
			{
				choosing = false;
			}
		};

		if (node.EnvelopeId.IsNone)
		{
			_inspector.Children.Add(new TextBlock
			{
				Text = "No envelope selected. Choose an existing envelope or New... to create one in this song.",
				TextWrapping = TextWrapping.Wrap,
			});
			return;
		}

		if (_workspace.Document.TryGet(
			node.EnvelopeId, out SongObject? scriptObject)
			&& scriptObject is ScriptEnvelopeDefinition scripted)
		{
			_inspector.Children.Add(new TextBlock
			{
				Text = "Script Envelope (shared object)",
				FontWeight = FontWeight.SemiBold,
			});
			_inspector.Children.Add(new ScriptEnvelopeEditorControl(
				_workspace, scripted,
				() => SelectNode(node.Id),
				message =>
				{
					_message.Text = message;
					_changed(message);
				}));
			return;
		}

		if (!_workspace.Document.TryGet(
			node.EnvelopeId, out SongObject? selected)
			|| selected is not NodeEnvelopeDefinition nodes)
		{
			_inspector.Children.Add(new TextBlock
			{
				Text = $"Envelope <{node.EnvelopeId.Value}> is missing or is not a supported Envelope type.",
				TextWrapping = TextWrapping.Wrap,
			});
			return;
		}

		_inspector.Children.Add(new TextBlock
		{
			Text = "Node envelope (shared object)",
			FontWeight = FontWeight.SemiBold,
		});
		_inspector.Children.Add(new NodeEnvelopeEditorControl(
			_workspace, nodes, message =>
			{
				_message.Text = message;
				_changed(message);
			}));
	}

	private void AssignEnvelope(int nodeId, ObjectId envelopeId)
	{
		UpdateParameter(nodeId, current =>
		{
			FmEnvelopeNode envelope = (FmEnvelopeNode)current;
			return envelope.EnvelopeId == envelopeId
				? null
				: new FmEnvelopeNode(nodeId, envelopeId);
		});
	}

	private void AddFilterInspector(FmFilterNode node)
	{
		ComboBoxItem newItem = new()
		{
			Content = new TextBlock
			{
				Text = "New...",
				FontStyle = FontStyle.Italic,
			},
		};
		ComboBoxItem noneItem = new() { Content = "(None)" };
		FilterChoice[] definitions = _workspace.Document.Objects.Values
			.OfType<FilterDefinition>()
			.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(item => item.Id.Value)
			.Select(item => new FilterChoice(
				item.Id, $"{item.Name} <{item.Id.Value}>"))
			.ToArray();
		List<object> options = [newItem, noneItem];
		options.AddRange(definitions);
		ComboBox selected = new()
		{
			ItemsSource = options,
			SelectedItem = node.Configuration is FilterInstance current
				? definitions.FirstOrDefault(x => x.Id == current.DefinitionId)
				: null,
			MinWidth = 235,
		};
		Button editDefinition = Button("Edit Definition...", () =>
		{
			try
			{
				FmFilterNode active = (FmFilterNode)CurrentNode(node.Id);
				if (active.Configuration is not FilterInstance configuration)
					return;
				Window? owner = TopLevel.GetTopLevel(this) as Window;
				if (owner is null)
					throw new InvalidOperationException(
						"Open the FM editor in a window to edit a definition.");
				Window popup = new()
				{
					Title = $"Edit Filter — {configuration.Definition.Name}",
					Width = 950,
					Height = 690,
				};
				popup.Content = new FilterEditorControl(
					_workspace, configuration.Definition,
					() => popup.Close(), message =>
					{
						_message.Text = message;
						_changed(message);
						_canvas.Refresh();
						_canvas.SetSelectedNode(_selectedNodeId);
					});
				popup.Closed += (_, _) =>
				{
					_canvas.Refresh();
					RebuildInspector();
				};
				popup.Show(owner);
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
		});
		editDefinition.IsEnabled = node.Configuration is not null;

		StackPanel selector = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6,
		};
		selector.Children.Add(selected);
		selector.Children.Add(editDefinition);
		AddField("Shared Filter definition", selector);
		bool choosing = false;
		selected.SelectionChanged += async (_, _) =>
		{
			if (choosing || selected.SelectedItem is not object selection)
				return;
			choosing = true;
			try
			{
				if (ReferenceEquals(selection, newItem))
				{
					Window? owner = TopLevel.GetTopLevel(this) as Window;
					if (owner is null)
						throw new InvalidOperationException(
							"Open the FM editor in a window to create a Filter.");
					TextPromptDialog prompt = new(
						"New Filter", "Filter name:", "New Filter");
					string? name = await prompt.ShowDialog<string?>(owner);
					if (name is not null)
					{
						FilterDefinition created = FilterDocumentEditor.CreateFilter(
							_workspace, name);
						FmSynthDocumentEditor.SetFilterConfiguration(
							_workspace, _synth, node.Id, new FilterInstance(created));
						AfterGraphChange(node.Id,
							$"Created and assigned Filter {created.Name}");
						return;
					}
				}
				else
				{
					FilterInstance? proposed = ReferenceEquals(selection, noneItem)
						? null
						: new FilterInstance(
							(FilterDefinition)_workspace.Document.Objects[
								((FilterChoice)selection).Id]);
					FmSynthDocumentEditor.SetFilterConfiguration(
						_workspace, _synth, node.Id, proposed);
					AfterGraphChange(node.Id, "Updated FM Filter selection");
					return;
				}
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
			}
			finally { choosing = false; }

			choosing = true;
			try
			{
				FilterInstance? committed =
					((FmFilterNode)CurrentNode(node.Id)).Configuration;
				selected.SelectedItem = committed is null ? null
					: definitions.FirstOrDefault(x =>
						x.Id == committed.DefinitionId);
			}
			finally { choosing = false; }
		};

		TextBox tail = NumberBox(node.TailSeconds);
		AddField("Tail (seconds, nonnegative)", tail);
		BindParameterText(tail, value =>
		{
			double seconds = ParseDouble(value, "Tail");
			UpdateParameter(node.Id, live =>
			{
				FmFilterNode filter = (FmFilterNode)live;
				return filter.TailSeconds == seconds ? null
					: filter.WithTail(seconds);
			});
		});
		_inspector.Children.Add(new TextBlock
		{
			Text = "Audio enters the Input port. Only nonstructural numeric "
				+ "stage parameters have modulation ports. Disconnected ports "
				+ "use the per-use static configuration. Until FM Filter "
				+ "rendering is implemented, this node passes audio unchanged; "
				+ "Tail is stored but not yet applied.",
			TextWrapping = TextWrapping.Wrap,
		});
		if (node.StaleConnections.Count > 0)
			_inspector.Children.Add(new TextBlock
			{
				Text = $"{node.StaleConnections.Count} modulation connection(s) "
					+ "no longer match the shared Filter stage kind/generation. "
					+ "They are inactive and cannot silently move to another parameter; "
					+ "reconnect the intended port explicitly.",
				TextWrapping = TextWrapping.Wrap,
				Foreground = Brushes.Orange,
			});
		if (node.Configuration is not FilterInstance configured)
			return;

		foreach (ConfiguredFilterStage stage in configured.GetStages())
		{
			FilterParameterDescriptor[] inputs = stage.Parameters.Parameters
				.Where(descriptor => descriptor.IsModulatable).ToArray();
			if (inputs.Length > 0)
				_inspector.Children.Add(new TextBlock
				{
					Text = $"{stage.Name} (stage #{stage.StageId}): "
						+ string.Join(", ", inputs.Select(x => x.Name)),
					TextWrapping = TextWrapping.Wrap,
				});
		}
		_inspector.Children.Add(new Separator());
		_inspector.Children.Add(new TextBlock
		{
			Text = "This node's independent Filter parameter overrides",
			FontWeight = FontWeight.SemiBold,
		});
		FilterInstanceEditSession session = new(
			() => ((FmFilterNode)CurrentNode(node.Id)).Configuration
				?? throw new InvalidOperationException(
					"FM Filter configuration has been cleared."),
			proposed =>
			{
				UpdateParameter(node.Id, current =>
					((FmFilterNode)current).WithConfiguration(proposed));
			});
		_inspector.Children.Add(new FilterInstanceEditorControl(
			session, message =>
			{
				_message.Text = message;
				_changed(message);
			}));
	}

	private void AddOperatorInspector(FmOperatorNode node)
	{
		ComboBox operation =
			new()
			{
				ItemsSource = Enum.GetValues<FmOperatorKind>(),
				SelectedItem = node.Operation,
			};
		TextBox inputs =
			new()
			{
				Text = string.Join(", ", node.InputNodeIds),
			};
		AddField("Operation", operation);
		AddField("Input node IDs", inputs);

		BindParameterSelection(
			operation,
			() => ((FmOperatorNode)CurrentNode(node.Id)).Operation,
			item =>
				UpdateParameter(
					node.Id,
					current =>
					{
						FmOperatorNode live = (FmOperatorNode)current;
						FmOperatorKind selected = (FmOperatorKind)item;
						return live.Operation == selected
							? null
							: new FmOperatorNode(
								live.Id, selected, live.InputNodeIds);
					}));

		BindParameterText(
			inputs,
			text =>
			{
				int[] parsed = ParseNodeIds(text);
				UpdateParameter(
					node.Id,
					current =>
					{
						FmOperatorNode live = (FmOperatorNode)current;
						return live.InputNodeIds.SequenceEqual(parsed)
							? null
							: new FmOperatorNode(
								live.Id, live.Operation, parsed);
					});
			});
	}

	/// <summary>
	/// Enter/focus loss commit just this field. Escape resets the current
	/// draft to the last successful commit. Rebuilding the inspector while
	/// another field gains focus would steal that focus, so model commits only
	/// refresh the graph canvas; the existing controls remain mounted.
	/// </summary>
	private void BindParameterText(TextBox box, Action<string> apply)
	{
		FmSynthParameterTextField field = new(box.Text ?? string.Empty);

		void Commit(bool focusLost)
		{
			try
			{
				field.Commit(box.Text, apply);
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
				if (focusLost)
					box.Text = field.Revert();
			}
		}

		box.KeyDown += (_, e) =>
		{
			if (e.Key == Key.Escape)
			{
				box.Text = field.Revert();
				e.Handled = true;
			}
			else if (e.Key == Key.Enter)
			{
				Commit(focusLost: false);
				e.Handled = true;
			}
		};
		box.LostFocus += (_, _) => Commit(focusLost: true);
	}

	private void BindParameterSelection(
		ComboBox box,
		Func<object?> committedSelection,
		Action<object> apply)
	{
		bool reverting = false;
		box.SelectionChanged += (_, _) =>
		{
			if (reverting || box.SelectedItem is not object item)
				return;

			try
			{
				apply(item);
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
				reverting = true;
				try
				{
					box.SelectedItem = committedSelection();
				}
				finally
				{
					reverting = false;
				}
			}
		};
	}

	private void BindParameterToggle(
		CheckBox box,
		Func<bool> committedValue,
		Action<bool> apply)
	{
		bool reverting = false;
		void Changed()
		{
			if (reverting)
				return;
			try
			{
				apply(box.IsChecked == true);
			}
			catch (Exception ex)
			{
				_message.Text = ex.Message;
				reverting = true;
				try
				{
					box.IsChecked = committedValue();
				}
				finally
				{
					reverting = false;
				}
			}
		}

		box.PropertyChanged += (_, args) =>
		{
			if (args.Property == ToggleButton.IsCheckedProperty)
				Changed();
		};
	}

	private FmSynthNode CurrentNode(int nodeId)
		=> _synth.Graph.Nodes.FirstOrDefault(node => node.Id == nodeId)
			?? throw new InvalidOperationException(
					$"FM node #{nodeId} no longer exists.");

	private void UpdateOscillator(
		int nodeId,
		FmOscillatorWaveform? waveform = null,
		double? frequency = null,
		double? minimum = null,
		double? maximum = null,
		int? multiplierId = null,
		bool replaceMultiplier = false,
		bool? exponential = null,
		bool? constant = null)
	{
		UpdateParameter(
			nodeId,
			current =>
			{
				FmOscillatorNode live = (FmOscillatorNode)current;
				FmOscillatorWaveform wave = waveform ?? live.Waveform;
				double freq = frequency ?? live.FrequencyHz;
				double min = minimum ?? live.Minimum;
				double max = maximum ?? live.Maximum;
				int? multiplier = replaceMultiplier
					? multiplierId : live.MultiplierNodeId;
				bool exp = exponential ?? live.ExponentialMultiplier;
				bool fixedFrequency = constant ?? live.Constant;
				if (wave == live.Waveform
					&& freq == live.FrequencyHz
					&& min == live.Minimum
					&& max == live.Maximum
					&& multiplier == live.MultiplierNodeId
					&& exp == live.ExponentialMultiplier
					&& fixedFrequency == live.Constant)
				{
					return null;
				}
				return new FmOscillatorNode(
					live.Id, wave, freq, min, max, multiplier, exp, fixedFrequency);
			});
	}

	private void UpdateParameter(
		int nodeId,
		Func<FmSynthNode, FmSynthNode?> createReplacement)
	{
		FmSynthNode current = CurrentNode(nodeId);
		FmSynthNode? replacement = createReplacement(current);
		if (replacement is null)
			return;

		FmSynthDocumentEditor.UpdateNode(
			_workspace,
			_synth,
			replacement);
		_canvas.Refresh();
		_canvas.SetSelectedNode(_selectedNodeId);
		_message.Text = string.Empty;
		_changed($"Updated FM node #{nodeId}");
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
		string? text,
		string label)
	{
		if (!double.TryParse(
			text,
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
		string? text,
		string label)
	{
		double value =
			ParseDouble(
				text,
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

	private static string NodeName(
		FmSynthNode node)
		=> node switch
		{
			FmConstantNode => "Constant",
			FmOscillatorNode => "Oscillator",
			FmEnvelopeNode => "Envelope",
			FmFilterNode => "Filter",
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

	private sealed record FilterChoice(ObjectId Id, string DisplayName)
	{
		public override string ToString() => DisplayName;
	}

	private sealed record EnvelopeChoice(
		ObjectId Id,
		string DisplayName)
	{
		public override string ToString()
			=> DisplayName;
	}
}

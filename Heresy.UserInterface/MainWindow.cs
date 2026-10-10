using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using Heresy.Core.Diagnostics;
using Heresy.Core.Envelopes;
using Heresy.Core.FmSynthesis;
using Heresy.Core.Instruments;
using Heresy.Core.Objects;
using Heresy.Core.Patterns;
using Heresy.Core.Persistence;
using Heresy.Core.Samples;
using Heresy.Core.Sequences;
using Heresy.Playback;
using Heresy.Render.Configuration;
using Heresy.Render.File;
using Heresy.Render.Realtime;
using Heresy.UserInterface.Dialogs;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.Exporting;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.ViewModels;
using Heresy.UserInterface.Views;

namespace Heresy.UserInterface;

public sealed class MainWindow : Window
{
	private static readonly FilePickerFileType SongFileType =
		new("Heresy song")
		{
			Patterns = new[] { "*.hm", "*.hm.json" },
		};

	private static readonly FilePickerFileType PackageFileType =
		new("Heresy module")
		{
			Patterns = new[] { "*.hm" },
		};

	private static readonly FilePickerFileType JsonFileType =
		new("Heresy JSON")
		{
			Patterns = new[] { "*.hm.json" },
		};

	private static readonly FilePickerFileType AbsoluteJsonFileType =
		new("Heresy JSON (absolute paths)")
		{
			Patterns = new[] { "*.hm.json" },
		};

	private static readonly FilePickerFileType SampleFileType =
		new("Audio sample")
		{
			Patterns = new[]
			{
				"*.wav",
				"*.flac",
				"*.mp3",
				"*.ogg",
				"*.aif",
				"*.aiff",
			},
		};

	private static readonly FilePickerFileType FlacRenderFileType =
		new("FLAC audio")
		{
			Patterns = new[] { "*.flac" },
		};

	private static readonly FilePickerFileType Mp3RenderFileType =
		new("MP3 audio")
		{
			Patterns = new[] { "*.mp3" },
		};

	private static readonly FilePickerFileType WaveRenderFileType =
		new("WAV audio")
		{
			Patterns = new[] { "*.wav" },
		};

	private static readonly DataFormat<SongTreeNode> TreeNodeFormat =
		DataFormat.CreateInProcessFormat<SongTreeNode>("Heresy.SongTreeNode");

	private readonly DocumentWorkspace _workspace;
	private readonly ISongPlaybackTransport? _playbackTransport;
	private readonly SongExportService _exportService;
	private readonly AudioOutputSettings _audioOutputSettings;
	private readonly IPlaybackPositionTransport? _playbackPositionTransport;
	private readonly IPlaybackRuntimeDiagnosticsTransport?
		_runtimeDiagnosticsTransport;
	private readonly IPlaybackAudioHealthTransport? _audioHealthTransport;
	private readonly TextBlock _underrunIndicator = new()
	{
		IsVisible = false,
		VerticalAlignment = VerticalAlignment.Center,
		Margin = new Thickness(12, 0),
	};
	private long _lastAudioHealthSessionId = -1;
	private readonly Button _diagnosticsButton =
		new() { Content = "Warnings", IsVisible = false };
	private readonly List<string> _runtimeDiagnosticMessages = [];
	private Window? _runtimeDiagnosticsWindow;
	private ListBox? _runtimeDiagnosticsList;
	private bool _windowClosed;
	private const int MaximumVisibleRuntimeDiagnostics = 500;
	private readonly UserInterfaceConfiguration _uiConfiguration = new();
	private readonly Dictionary<SongTreeSection, TreeView> _trees = [];
	private readonly TextBlock _status;
	private readonly ContentControl _mainContent = new();
	private Control? _documentView;

	private SongTreeItemViewModel? _selectedItem;
	private TreeView? _selectedTree;
	private SongTreeItemViewModel? _dragCandidate;
	private PointerPressedEventArgs? _dragTrigger;
	private Point _dragStart;
	private bool _dragInProgress;
	private bool _closeApproved;
	private bool _closePromptInProgress;
	private CancellationTokenSource? _exportCancellation;
	private bool _renderAudioPending;

	public MainWindow()
		: this(new DocumentWorkspace(), null, null) { }

	public MainWindow(
		ISongPlaybackTransport playbackTransport)
		: this(new DocumentWorkspace(), playbackTransport, null) { }

	public MainWindow(
		ISongPlaybackTransport playbackTransport,
		AudioOutputSettings settings)
		: this(new DocumentWorkspace(), playbackTransport, null, settings) { }

	public MainWindow(
		ISongPlaybackTransport playbackTransport,
		SongExportService exportService)
		: this(
			new DocumentWorkspace(),
			playbackTransport,
			exportService)
	{
	}

	internal MainWindow(
		DocumentWorkspace workspace)
		: this(workspace, null, null) { }

	internal MainWindow(
		DocumentWorkspace workspace,
		ISongPlaybackTransport? playbackTransport)
		: this(workspace, playbackTransport, null) { }

	internal MainWindow(
		DocumentWorkspace workspace,
		ISongPlaybackTransport? playbackTransport,
		SongExportService? exportService,
		AudioOutputSettings? audioOutputSettings = null)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_playbackTransport = playbackTransport;
		_audioOutputSettings = audioOutputSettings ?? new AudioOutputSettings();
		_exportService = exportService ??
			new SongExportService(new OfflineSongRenderPlanFactory(
				() => _audioOutputSettings.Current));
		_playbackPositionTransport =
			playbackTransport as IPlaybackPositionTransport;
		_runtimeDiagnosticsTransport =
			playbackTransport as IPlaybackRuntimeDiagnosticsTransport;
		_audioHealthTransport =
			playbackTransport as IPlaybackAudioHealthTransport;
		if (_audioHealthTransport is not null)
			_audioHealthTransport.AudioHealthChanged += OnAudioHealthChanged;
		if (_runtimeDiagnosticsTransport is not null)
			_runtimeDiagnosticsTransport.RuntimeDiagnostics +=
				OnRuntimeDiagnostics;
		_diagnosticsButton.Click += (_, _) => ShowRuntimeDiagnostics();
		if (_playbackPositionTransport is not null)
		{
			_playbackPositionTransport.PlaybackPositionChanged +=
				OnPlaybackPositionChanged;
		}

		Width = 1200;
		Height = 760;
		MinWidth = 760;
		MinHeight = 480;

		_status =
			new TextBlock
			{
				VerticalAlignment = VerticalAlignment.Center,
			};

		Content = BuildShell();
		Closing += OnClosing;
		Closed += (_, _) =>
		{
			_windowClosed = true;
			_exportCancellation?.Cancel();
			if (_runtimeDiagnosticsTransport is not null)
				_runtimeDiagnosticsTransport.RuntimeDiagnostics -=
					OnRuntimeDiagnostics;
			if (_audioHealthTransport is not null)
				_audioHealthTransport.AudioHealthChanged -= OnAudioHealthChanged;
			_runtimeDiagnosticsWindow?.Close();
			if (_playbackPositionTransport is not null)
			{
				_playbackPositionTransport.PlaybackPositionChanged -=
					OnPlaybackPositionChanged;
			}
			_playbackTransport?.Dispose();
		};
		RefreshDocumentView("New song");
	}

	private Control BuildShell()
	{
		DockPanel root = new();

		Menu menu = BuildMenu();
		DockPanel.SetDock(menu, Dock.Top);
		root.Children.Add(menu);

		Border statusBar =
			new()
			{
				BorderThickness = new Thickness(0, 1, 0, 0),
				BorderBrush = Brushes.Gray,
				Padding = new Thickness(10, 5),
				Child = new DockPanel
				{
					Children =
					{
						_underrunIndicator,
						_diagnosticsButton,
						_status,
					},
				},
			};
		DockPanel.SetDock(_underrunIndicator, Dock.Right);
		DockPanel.SetDock(_diagnosticsButton, Dock.Right);
		DockPanel.SetDock(statusBar, Dock.Bottom);
		root.Children.Add(statusBar);

		_documentView = BuildDocumentView();
		_mainContent.Content = _documentView;
		root.Children.Add(_mainContent);
		return root;
	}

	private Control BuildDocumentView()
	{
		Grid body = new();
		body.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		body.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
		body.RowDefinitions.Add(
			new RowDefinition(new GridLength(1, GridUnitType.Star)));
		body.RowDefinitions.Add(
			new RowDefinition(new GridLength(1, GridUnitType.Star)));

		AddSectionPane(body, SongTreeSection.Sequences, row: 0, column: 0);
		AddSectionPane(body, SongTreeSection.Patterns, row: 0, column: 1);
		AddSectionPane(body, SongTreeSection.Samples, row: 1, column: 0);
		AddSectionPane(body, SongTreeSection.Instruments, row: 1, column: 1);
		return body;
	}

	private void AddSectionPane(
		Grid body,
		SongTreeSection section,
		int row,
		int column)
	{
		TreeView tree = new();
		_trees.Add(section, tree);
		tree.SelectionChanged += (_, _) =>
		{
			if (tree.SelectedItem is TreeViewItem treeItem
				&& treeItem.Tag is SongTreeItemViewModel item)
			{
				SelectTreeItem(item, tree);
			}
		};

		TextBlock title =
			new()
			{
				Text = SongTreeSections.GetName(section),
				FontSize = 17,
				FontWeight = FontWeight.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
			};

		Button newFolder = new() { Content = "+ Folder" };
		newFolder.Click += async (_, _) => await CreateFolderAsync(section);

		StackPanel actions =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
			};
		if (section == SongTreeSection.Sequences)
		{
			Button newSequence = new() { Content = "+ Sequence" };
			newSequence.Click += async (_, _) => await CreateSequenceAsync();
			actions.Children.Add(newSequence);

			Button newScriptSequence = new() { Content = "+ Script" };
			newScriptSequence.Click += async (_, _) =>
				await CreateScriptSequenceAsync();
			actions.Children.Add(newScriptSequence);
		}
		if (section == SongTreeSection.Patterns)
		{
			Button newPattern = new() { Content = "+ Pattern" };
			newPattern.Click += async (_, _) => await CreatePatternAsync();
			actions.Children.Add(newPattern);

			Button newScriptPattern = new() { Content = "+ Script" };
			newScriptPattern.Click += async (_, _) =>
				await CreateScriptPatternAsync();
			actions.Children.Add(newScriptPattern);
		}
		if (section == SongTreeSection.Instruments)
		{
			Button newInstrument = new() { Content = "+ Instrument" };
			newInstrument.Click += async (_, _) => await CreateInstrumentAsync();
			actions.Children.Add(newInstrument);

			Button newEnvelope = new() { Content = "+ Envelope" };
			newEnvelope.Click += async (_, _) => await CreateEnvelopeAsync();
			actions.Children.Add(newEnvelope);
		}
		if (section == SongTreeSection.Samples)
		{
			Button newFmSynth = new() { Content = "+ FM Synth" };
			newFmSynth.Click += async (_, _) => await CreateFmSynthAsync();
			actions.Children.Add(newFmSynth);

			Button importFmSynth = new() { Content = "+ Import FM" };
			importFmSynth.Click += async (_, _) => await ImportFmSynthsAsync();
			actions.Children.Add(importFmSynth);

			Button import = new() { Content = "+ Import" };
			import.Click += async (_, _) => await ImportSamplesAsync();
			actions.Children.Add(import);
		}
		actions.Children.Add(newFolder);

		DockPanel header =
			new()
			{
				Margin = new Thickness(8, 6),
			};
		DockPanel.SetDock(actions, Dock.Right);
		header.Children.Add(actions);
		header.Children.Add(title);

		Grid pane = new();
		pane.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		pane.RowDefinitions.Add(
			new RowDefinition(new GridLength(1, GridUnitType.Star)));
		Grid.SetRow(header, 0);
		pane.Children.Add(header);
		Grid.SetRow(tree, 1);
		pane.Children.Add(tree);

		Border border =
			new()
			{
				BorderThickness = new Thickness(1),
				BorderBrush = Brushes.Gray,
				Margin = new Thickness(4),
				Child = pane,
			};
		Grid.SetRow(border, row);
		Grid.SetColumn(border, column);
		body.Children.Add(border);
	}

	private Menu BuildMenu()
	{
		MenuItem newItem =
			new()
			{
				Header = "_New",
				InputGesture = new KeyGesture(Key.N, KeyModifiers.Control),
			};
		newItem.Click += async (_, _) => await NewDocumentAsync();

		MenuItem openItem =
			new()
			{
				Header = "_Open...",
				InputGesture = new KeyGesture(Key.O, KeyModifiers.Control),
			};
		openItem.Click += async (_, _) => await OpenDocumentAsync();

		MenuItem saveItem =
			new()
			{
				Header = "_Save",
				InputGesture = new KeyGesture(Key.S, KeyModifiers.Control),
			};
		saveItem.Click += async (_, _) => await SaveDocumentAsync();

		MenuItem saveAsItem = new() { Header = "Save _As..." };
		saveAsItem.Click += async (_, _) => await SaveDocumentAsAsync();

		MenuItem renderItem = new() { Header = "_Render Audio..." };
		renderItem.Click += async (_, _) => await RenderAudioAsync();

		MenuItem exitItem =
			new()
			{
				Header = "E_xit",
				InputGesture = new KeyGesture(Key.Q, KeyModifiers.Control),
			};
		exitItem.Click += (_, _) => Close();

		MenuItem file =
			new()
			{
				Header = "_File",
				ItemsSource = new object[]
				{
					newItem,
					openItem,
					new Separator(),
					saveItem,
					saveAsItem,
					new Separator(),
					renderItem,
					new Separator(),
					exitItem,
				},
			};

		MenuItem audioOutputItem =
			new() { Header = "_Audio Output..." };
		audioOutputItem.Click += async (_, _) =>
			await ConfigureAudioOutputAsync();
		MenuItem options = new()
		{
			Header = "_Options",
			ItemsSource = new object[] { audioOutputItem },
		};
		MenuItem diagnosticsItem =
			new() { Header = "Runtime _Diagnostics..." };
		diagnosticsItem.Click += (_, _) => ShowRuntimeDiagnostics();
		MenuItem view = new()
		{
			Header = "_View",
			ItemsSource = new object[] { diagnosticsItem },
		};
		return new Menu
		{
			ItemsSource = new object[] { file, view, options },
		};
	}

	private async Task NewDocumentAsync()
	{
		if (!await ConfirmCanReplaceDocumentAsync())
			return;

		_workspace.New();
		RefreshDocumentView("New song");
	}

	private async Task OpenDocumentAsync()
	{
		if (!await ConfirmCanReplaceDocumentAsync())
			return;

		if (!StorageProvider.CanOpen)
		{
			SetStatus("This platform does not provide an open-file picker.");
			return;
		}

		IReadOnlyList<IStorageFile> files =
			await StorageProvider.OpenFilePickerAsync(
				new FilePickerOpenOptions
				{
					Title = "Open Heresy song",
					AllowMultiple = false,
					FileTypeFilter = new[] { SongFileType },
				});

		if (files.Count == 0)
			return;

		string? path = files[0].TryGetLocalPath();
		if (path is null)
		{
			SetStatus("The selected file does not expose a local filesystem path.");
			return;
		}

		try
		{
			_workspace.Open(path);
			RefreshDocumentView($"Opened {_workspace.DisplayName}");
		}
		catch (Exception ex)
		{
			// TODO: when a .hm.json asset cannot be resolved, offer a workflow
			// for locating replacement files/directories and retry the load.
			SetStatus($"Open failed: {ex.Message}");
		}
	}

	private async Task<bool> SaveDocumentAsync()
	{
		if (_workspace.FilePath is null)
			return await SaveDocumentAsAsync();

		try
		{
			_workspace.Save();
			RefreshAfterPersistence($"Saved {_workspace.DisplayName}", _selectedItem?.Node);
			return true;
		}
		catch (Exception ex)
		{
			SetStatus($"Save failed: {ex.Message}");
			return false;
		}
	}

	private async Task<bool> SaveDocumentAsAsync()
	{
		if (!StorageProvider.CanSave)
		{
			SetStatus("This platform does not provide a save-file picker.");
			return false;
		}

		SaveFilePickerResult result =
			await StorageProvider.SaveFilePickerWithResultAsync(
				new FilePickerSaveOptions
				{
					Title = "Save Heresy song",
					SuggestedFileName =
						_workspace.FilePath is null
							? "song.hm"
							: Path.GetFileName(_workspace.FilePath),
					DefaultExtension = "hm",
					FileTypeChoices =
						new[]
						{
							PackageFileType,
							JsonFileType,
							AbsoluteJsonFileType,
						},
				});

		IStorageFile? file = result.File;
		if (file is null)
			return false;

		string? path = file.TryGetLocalPath();
		if (path is null)
		{
			SetStatus("The selected destination does not expose a local filesystem path.");
			return false;
		}

		try
		{
			JsonAssetPathMode jsonPathMode =
				string.Equals(
					result.SelectedFileType?.Name,
					AbsoluteJsonFileType.Name,
					StringComparison.Ordinal)
					? JsonAssetPathMode.Absolute
					: JsonAssetPathMode.Relative;

			_workspace.SaveAs(path, jsonPathMode);
			RefreshAfterPersistence($"Saved {_workspace.DisplayName}", _selectedItem?.Node);
			return true;
		}
		catch (Exception ex)
		{
			// TODO: when a relative JSON save identifies an offending asset,
			// navigate directly to that sample before presenting the error.
			SetStatus($"Save failed: {ex.Message}");
			return false;
		}
	}

	private async Task RenderAudioAsync()
	{
		if (_renderAudioPending)
		{
			SetStatus("An audio export is already being prepared or rendered.");
			return;
		}
		_renderAudioPending = true;
		try
		{
			await RenderAudioCoreAsync();
		}
		finally
		{
			_renderAudioPending = false;
		}
	}

	private async Task RenderAudioCoreAsync()
	{
		if (_exportCancellation is not null)
		{
			SetStatus("An audio export is already running.");
			return;
		}
		if (!StorageProvider.CanSave)
		{
			SetStatus("This platform does not provide a save-file picker.");
			return;
		}

		if (_workspace.Document.RootSequenceId.IsNone)
		{
			SetStatus("Render failed: the song does not have a root sequence.");
			return;
		}

		SaveFilePickerResult result =
			await StorageProvider.SaveFilePickerWithResultAsync(
				new FilePickerSaveOptions
				{
					Title = "Render Heresy song",
					SuggestedFileName =
						GetSuggestedRenderFileName(),
					DefaultExtension = "flac",
					FileTypeChoices =
						new[]
						{
							FlacRenderFileType,
							Mp3RenderFileType,
							WaveRenderFileType,
						},
				});

		IStorageFile? file = result.File;
		if (file is null)
			return;

		string? path = file.TryGetLocalPath();
		if (path is null)
		{
			SetStatus("The selected render destination does not expose a local filesystem path.");
			return;
		}

		OfflineAudioFileFormat format =
			ResolveRenderFormat(
				result.SelectedFileType?.Name,
				path);

		using CancellationTokenSource cancellation = new();
		_exportCancellation = cancellation;
		ExportProgressDialog progressWindow = new(
			Path.GetFileName(path), cancellation);
		// The dialog coalesces worker progress into at most one pending
		// UI dispatcher update, even if offline PCM runs much faster than
		// realtime. The renderer never touches Avalonia controls.
		IProgress<OfflineRenderProgress> progress =
			progressWindow.CreateProgressReporter();
		// Export reports are delivered on the UI synchronization context,
		// not from the coroutine worker. Reuse realtime's bounded history.
		IProgress<SequencingDiagnostic[]> exportDiagnostics =
			new Progress<SequencingDiagnostic[]>(batch =>
			{
				if (!_windowClosed)
					AppendRuntimeDiagnostics(batch, "Export");
			});
		SetStatus($"Rendering {Path.GetFileName(path)}...");
		try
		{
			progressWindow.Show(this);
			OfflineRenderResult render =
				await _exportService.ExportAsync(
					_workspace.Document,
					path,
					format,
					progress,
					cancellation.Token,
					exportDiagnostics);
			if (!_windowClosed)
				SetStatus(
					$"Rendered {Path.GetFileName(path)} ({render.TotalFrameCount:N0} frames).");
		}
		catch (OperationCanceledException) when (
			cancellation.IsCancellationRequested)
		{
			if (!_windowClosed)
				SetStatus($"Canceled audio export: {Path.GetFileName(path)}. "
					+ "Existing destination preserved.");
		}
		catch (PlaybackSourceCompilationException ex)
		{
			if (!_windowClosed)
				SetStatus(
					$"Render failed while compiling the song: {ex.Message}");
		}
		catch (Exception ex)
		{
			if (!_windowClosed)
				SetStatus($"Render failed: {ex.Message}");
		}
		finally
		{
			progressWindow.Close();
			_exportCancellation = null;
		}
	}

	private string GetSuggestedRenderFileName()
	{
		string sourceName =
			_workspace.FilePath is null
				? "song"
				: Path.GetFileName(_workspace.FilePath);
		string baseName =
			sourceName.EndsWith(
					".hm.json",
					StringComparison.OrdinalIgnoreCase)
				? sourceName[..^8]
				: Path.GetFileNameWithoutExtension(sourceName);
		if (string.IsNullOrWhiteSpace(baseName))
			baseName = "song";
		return baseName + ".flac";
	}

	private static OfflineAudioFileFormat ResolveRenderFormat(
		string? selectedFileTypeName,
		string path)
	{
		if (string.Equals(
			selectedFileTypeName,
			Mp3RenderFileType.Name,
			StringComparison.Ordinal))
		{
			return OfflineAudioFileFormat.Mp3;
		}
		if (string.Equals(
			selectedFileTypeName,
			WaveRenderFileType.Name,
			StringComparison.Ordinal))
		{
			return OfflineAudioFileFormat.Wave;
		}
		if (string.Equals(
			selectedFileTypeName,
			FlacRenderFileType.Name,
			StringComparison.Ordinal))
		{
			return OfflineAudioFileFormat.Flac;
		}

		return Path.GetExtension(path).ToLowerInvariant() switch
		{
			".mp3" => OfflineAudioFileFormat.Mp3,
			".wav" => OfflineAudioFileFormat.Wave,
			_ => OfflineAudioFileFormat.Flac,
		};
	}

	private async Task ConfigureAudioOutputAsync()
	{
		AudioOutputSettingsDialog dialog = new(_audioOutputSettings.Current);
		RenderConfiguration? configuration =
			await dialog.ShowDialog<RenderConfiguration?>(this);
		if (configuration is null)
			return;
		try
		{
			// SDL output sessions cannot have their sample rate/channel
			// format changed while callbacks are running. The transport
			// fully stops the old session before the new configuration
			// becomes visible to the next playback request.
			if (_playbackTransport is not null)
				await _playbackTransport.StopAsync();
			_audioOutputSettings.Set(configuration);
			SetStatus($"Audio output: {configuration.SampleRate:N0} Hz, "
				+ $"{configuration.OutputChannelCount} speaker feeds. "
				+ "Changes apply to the next playback and export.");
		}
		catch (Exception ex)
		{
			SetStatus($"Could not change audio output: {ex.Message}");
		}
	}

	private async Task<bool> ConfirmCanReplaceDocumentAsync()
		=> await UnsavedChangesGuard.CanProceedAsync(
			_workspace,
			async () =>
			{
				UnsavedChangesDialog dialog =
					new(_workspace.DisplayName);
				UnsavedChangesChoice? choice =
					await dialog.ShowDialog<UnsavedChangesChoice?>(
						this);
				return choice
					?? UnsavedChangesChoice.Cancel;
			},
			SaveDocumentAsync);

	private async void OnClosing(
		object? sender,
		WindowClosingEventArgs e)
	{
		_ = sender;

		if (_closeApproved
			|| !_workspace.IsModified)
		{
			return;
		}

		e.Cancel = true;
		if (_closePromptInProgress)
			return;

		_closePromptInProgress = true;
		try
		{
			if (!await ConfirmCanReplaceDocumentAsync())
				return;

			_closeApproved = true;
			Close();
		}
		finally
		{
			_closePromptInProgress = false;
		}
	}

	private async Task CreateSequenceAsync()
	{
		TextPromptDialog dialog =
			new("New sequence", "Sequence name:", "New Sequence");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		try
		{
			DataSequenceDefinition sequence =
				SequenceDocumentEditor.CreateDataSequence(
					_workspace,
					name);
			SongTreeObject? node =
				FindTreeObject(
					_workspace.Document.GetSectionRoot(SongTreeSection.Sequences),
					sequence.Id);
			RefreshDocumentView($"Created sequence {sequence.Name}", node);
			ShowSequenceEditor(sequence, node);
		}
		catch (Exception ex)
		{
			SetStatus($"Could not create sequence: {ex.Message}");
		}
	}

	private async Task CreateScriptSequenceAsync()
	{
		TextPromptDialog dialog =
			new(
				"New script sequence",
				"Sequence name:",
				"New Script Sequence");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		try
		{
			ScriptSequenceDefinition sequence =
				ScriptDocumentEditor.CreateScriptSequence(
					_workspace,
					name);
			SongTreeObject? node =
				FindTreeObject(
					_workspace.Document.GetSectionRoot(
						SongTreeSection.Sequences),
					sequence.Id);
			RefreshDocumentView(
				$"Created script sequence {sequence.Name}",
				node);
			ShowScriptEditor(sequence, node);
		}
		catch (Exception ex)
		{
			SetStatus(
				$"Could not create script sequence: {ex.Message}");
		}
	}

	private async Task CreatePatternAsync()
	{
		TextPromptDialog dialog =
			new("New pattern", "Pattern name:", "New Pattern");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		try
		{
			DataPatternDefinition pattern =
				PatternDocumentEditor.CreateDataPattern(
					_workspace,
					name);
			SongTreeObject? node =
				FindTreeObject(
					_workspace.Document.GetSectionRoot(SongTreeSection.Patterns),
					pattern.Id);
			RefreshDocumentView($"Created pattern {pattern.Name}", node);
			ShowPatternEditor(pattern, node);
		}
		catch (Exception ex)
		{
			SetStatus($"Could not create pattern: {ex.Message}");
		}
	}

	private async Task CreateScriptPatternAsync()
	{
		TextPromptDialog dialog =
			new(
				"New script pattern",
				"Pattern name:",
				"New Script Pattern");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		try
		{
			ScriptPatternDefinition pattern =
				ScriptDocumentEditor.CreateScriptPattern(
					_workspace,
					name);
			SongTreeObject? node =
				FindTreeObject(
					_workspace.Document.GetSectionRoot(
						SongTreeSection.Patterns),
					pattern.Id);
			RefreshDocumentView(
				$"Created script pattern {pattern.Name}",
				node);
			ShowScriptEditor(pattern, node);
		}
		catch (Exception ex)
		{
			SetStatus(
				$"Could not create script pattern: {ex.Message}");
		}
	}

	private async Task CreateInstrumentAsync()
	{
		TextPromptDialog dialog =
			new("New instrument", "Instrument name:", "New Instrument");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		try
		{
			InstrumentDefinition instrument =
				InstrumentDocumentEditor.CreateInstrument(
					_workspace,
					name);
			SongTreeObject? node =
				FindTreeObject(
					_workspace.Document.GetSectionRoot(SongTreeSection.Instruments),
					instrument.Id);
			RefreshDocumentView($"Created instrument {instrument.Name}", node);
			ShowInstrumentEditor(instrument, node);
		}
		catch (Exception ex)
		{
			SetStatus($"Could not create instrument: {ex.Message}");
		}
	}

	private async Task CreateEnvelopeAsync()
	{
		TextPromptDialog dialog =
			new("New envelope", "Envelope name:", "New Envelope");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		try
		{
			AdsrEnvelopeDefinition envelope =
				EnvelopeDocumentEditor.CreateAdsrEnvelope(
					_workspace,
					name);
			SongTreeObject? node =
				FindTreeObject(
					_workspace.Document.GetSectionRoot(SongTreeSection.Instruments),
					envelope.Id);
			RefreshDocumentView($"Created envelope {envelope.Name}", node);
			ShowEnvelopeEditor(envelope, node);
		}
		catch (Exception ex)
		{
			SetStatus($"Could not create envelope: {ex.Message}");
		}
	}

	private async Task CreateFmSynthAsync()
	{
		TextPromptDialog dialog =
			new("New FM synth", "FM synth name:", "New FM Synth");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		try
		{
			FmSynthDefinition synth =
				FmSynthDocumentEditor.CreateFmSynth(
					_workspace,
					name);
			SongTreeObject? node =
				FindTreeObject(
					_workspace.Document.GetSectionRoot(
						SongTreeSection.Samples),
					synth.Id);
			RefreshDocumentView(
				$"Created FM synth {synth.Name}",
				node);
			ShowFmSynthEditor(
				synth,
				node);
		}
		catch (Exception ex)
		{
			SetStatus(
				$"Could not create FM synth: {ex.Message}");
		}
	}

	private async Task ImportFmSynthsAsync()
	{
		if (!StorageProvider.CanOpen)
		{
			SetStatus("This platform does not provide an open-file picker.");
			return;
		}

		IReadOnlyList<IStorageFile> files =
			await StorageProvider.OpenFilePickerAsync(
				new FilePickerOpenOptions
				{
					Title = "Import FM synths",
					AllowMultiple = true,
					FileTypeFilter =
						new[]
						{
							SongFileType,
							FilePickerFileTypes.All,
						},
				});
		if (files.Count == 0)
			return;

		int imported = 0;
		SongTreeNode? selectNode = null;
		FmSynthDefinition? firstSynth = null;
		foreach (IStorageFile file in files)
		{
			string? path =
				file.TryGetLocalPath();
			if (path is null)
				continue;

			try
			{
				SongFmSynthImportSource source =
					FmSynthDocumentEditor.LoadImportSource(
						path);
				if (source.Synths.Count == 0)
				{
					SetStatus(
						$"{file.Name} contains no FM synths to import.");
					continue;
				}

				FmSynthImportSelectionDialog dialog =
					new(source);
				ObjectId[]? selected =
					await dialog.ShowDialog<ObjectId[]?>(
						this);
				if (selected is null
					|| selected.Length == 0)
				{
					continue;
				}

				IReadOnlyList<FmSynthDefinition> synths =
					FmSynthDocumentEditor.ImportFromSong(
						_workspace,
						source,
						selected);
				foreach (FmSynthDefinition synth in synths)
				{
					firstSynth ??= synth;
					selectNode ??=
						FindTreeObject(
							_workspace.Document.GetSectionRoot(
								SongTreeSection.Samples),
							synth.Id);
					imported++;
				}
			}
			catch (Exception ex)
			{
				SetStatus(
					$"FM synth import failed for {file.Name}: {ex.Message}");
			}
		}

		if (imported == 0)
		{
			SetStatus("No FM synths were imported.");
			return;
		}

		RefreshDocumentView(
			imported == 1
				? "Imported 1 FM synth"
				: $"Imported {imported} FM synths",
			selectNode);

		if (imported == 1
			&& firstSynth is not null)
		{
			ShowFmSynthEditor(
				firstSynth,
				selectNode);
		}
	}

	private async Task ImportSamplesAsync()
	{
		if (!StorageProvider.CanOpen)
		{
			SetStatus("This platform does not provide an open-file picker.");
			return;
		}

		IReadOnlyList<IStorageFile> files =
			await StorageProvider.OpenFilePickerAsync(
				new FilePickerOpenOptions
				{
					Title = "Import samples",
					AllowMultiple = true,
					FileTypeFilter =
						new[]
						{
							SampleFileType,
							SongFileType,
							FilePickerFileTypes.All,
						},
				});
		if (files.Count == 0)
			return;

		int imported = 0;
		SongTreeNode? selectNode = null;
		SampleDefinition? firstSample = null;
		foreach (IStorageFile file in files)
		{
			string? path = file.TryGetLocalPath();
			if (path is null)
				continue;

			try
			{
				if (SongDocumentStorage.IsPackagePath(path)
					|| SongDocumentStorage.IsJsonPath(path))
				{
					SongSampleImportSource source =
						SampleDocumentEditor.LoadImportSource(path);
					if (source.Samples.Count == 0)
					{
						SetStatus(
							$"{file.Name} contains no samples to import.");
						continue;
					}

					SampleImportSelectionDialog dialog =
						new(source);
					ObjectId[]? selected =
						await dialog.ShowDialog<ObjectId[]?>(this);
					if (selected is null || selected.Length == 0)
						continue;

					IReadOnlyList<SampleDefinition> songSamples =
						SampleDocumentEditor.ImportFromSong(
							_workspace,
							source,
							selected);
					foreach (SampleDefinition songSample in songSamples)
					{
						firstSample ??= songSample;
						selectNode ??= FindTreeObject(
							_workspace.Document.GetSectionRoot(
								SongTreeSection.Samples),
							songSample.Id);
						imported++;
					}
					continue;
				}

				SampleDefinition sample =
					SampleDocumentEditor.Import(_workspace, path);
				firstSample ??= sample;
				selectNode ??= FindTreeObject(
					_workspace.Document.GetSectionRoot(SongTreeSection.Samples),
					sample.Id);
				imported++;
			}
			catch (Exception ex)
			{
				SetStatus($"Import failed for {file.Name}: {ex.Message}");
			}
		}

		if (imported == 0)
		{
			SetStatus("No sample files were imported.");
			return;
		}

		RefreshDocumentView(
			imported == 1 ? "Imported 1 sample" : $"Imported {imported} samples",
			selectNode);

		if (imported == 1 && firstSample is not null)
			await ShowSampleEditorAsync(firstSample, selectNode);
	}

	private async Task CreateFolderAsync(SongTreeSection section)
	{
		SongTreeFolder parent = GetFolderForNewChild(section);
		TextPromptDialog dialog =
			new("New folder", "Folder name:", "New Folder");
		string? name = await dialog.ShowDialog<string?>(this);
		if (name is null)
			return;

		SongTreeFolder folder =
			SongTreeEditor.CreateFolder(_workspace.Document, parent, name);
		RefreshDocumentView($"Created folder {folder.Name}", folder);
	}

	private async Task RenameSelectedAsync()
	{
		SongTreeItemViewModel? selected = _selectedItem;
		if (selected is null)
			return;

		if (selected.Node is SongTreeFolder folder)
		{
			TextPromptDialog dialog =
				new("Rename folder", "Folder name:", folder.Name);
			string? name = await dialog.ShowDialog<string?>(this);
			if (name is null)
				return;

			SongTreeEditor.RenameFolder(_workspace.Document, folder, name);
			RefreshDocumentView($"Renamed folder to {folder.Name}", folder);
			return;
		}

		if (selected.ObjectId is not ObjectId objectId
			|| selected.IsMissingReference)
		{
			SetStatus("Missing object references cannot be renamed.");
			return;
		}

		TextPromptDialog objectDialog =
			new("Rename object", "Object name:", selected.DisplayName);
		string? objectName = await objectDialog.ShowDialog<string?>(this);
		if (objectName is null)
			return;

		SongTreeEditor.RenameObject(_workspace.Document, objectId, objectName);
		RefreshDocumentView($"Renamed object to {objectName.Trim()}", selected.Node);
	}

	private async Task DeleteSelectedAsync()
	{
		SongTreeItemViewModel? selected = _selectedItem;
		if (selected?.Node is not SongTreeObject objectNode)
			return;

		SongTreeFolder parent =
			SongTreeEditor.GetParent(_workspace.Document, objectNode)
			?? _workspace.Document.GetSectionRoot(
				SongTreeSections.ForKind(selected.Kind));

		if (selected.IsMissingReference)
		{
			ConfirmDialog missingDialog =
				new(
					"Remove missing tree entry",
					$"Remove the missing reference '{selected.DisplayName}' from this folder? "
					+ "Other unresolved references to the same object ID are not changed.",
					"Remove");
			if (!await missingDialog.ShowDialog<bool>(this))
				return;

			SongTreeEditor.RemoveNode(_workspace.Document, objectNode);
			RefreshDocumentView("Removed missing tree entry", parent);
			return;
		}

		ObjectId objectId = objectNode.ObjectId;
		SongReferenceAnalysis analysis =
			SongReferenceAnalyzer.Analyze(_workspace.Document);
		SongReference[] meaningfulReferences =
			analysis.References
				.Where(reference =>
					reference.TargetId == objectId
					&& reference.Kind != SongReferenceKind.Tree)
				.ToArray();

		string message = BuildDeleteMessage(
			selected.DisplayName,
			meaningfulReferences,
			analysis.HasOpaqueScriptReferences);
		ConfirmDialog deleteDialog =
			new("Delete object", message, "Delete");
		if (!await deleteDialog.ShowDialog<bool>(this))
			return;

		SongTreeEditor.DeleteObject(_workspace.Document, objectId);
		RefreshDocumentView($"Deleted {selected.DisplayName}", parent);
	}

	private SongTreeFolder GetFolderForNewChild(SongTreeSection section)
	{
		SongTreeFolder sectionRoot = _workspace.Document.GetSectionRoot(section);
		if (_selectedItem is null
			|| SongTreeEditor.GetSection(_workspace.Document, _selectedItem.Node) != section)
		{
			return sectionRoot;
		}

		if (_selectedItem.Node is SongTreeFolder selectedFolder)
			return selectedFolder;

		return SongTreeEditor.GetParent(_workspace.Document, _selectedItem.Node)
			?? sectionRoot;
	}

	private string BuildDeleteMessage(
		string displayName,
		IReadOnlyList<SongReference> references,
		bool hasOpaqueScriptReferences)
	{
		StringBuilder message = new();
		message.Append("Delete '");
		message.Append(displayName);
		message.Append("' from the song?");

		if (references.Count != 0)
		{
			message.Append("\n\nThis object is still referenced by:\n");
			int shown = Math.Min(references.Count, 8);
			for (int index = 0; index < shown; index++)
			{
				message.Append(" • ");
				message.Append(DescribeReference(references[index]));
				message.Append('\n');
			}
			if (shown < references.Count)
			{
				message.Append(" • ");
				message.Append(references.Count - shown);
				message.Append(" more reference(s)\n");
			}
		}

		if (hasOpaqueScriptReferences)
		{
			message.Append(
				"\nOne or more script objects exist. Script references cannot yet be analyzed exactly, so they may also refer to this ID.\n");
		}

		if (references.Count != 0 || hasOpaqueScriptReferences)
		{
			message.Append(
				"\nDeletion will not rewrite those references. They will remain as unresolved object IDs until the object is restored or the references are edited.");
		}
		else
		{
			message.Append("\n\nNo musical references to this object were found.");
		}

		return message.ToString();
	}

	private string DescribeReference(SongReference reference)
	{
		string sourceName = "Song";
		if (reference.SourceObjectId is ObjectId sourceId)
		{
			if (_workspace.Document.TryGet(sourceId, out SongObject? source)
				&& source is not null)
			{
				sourceName = source.Name;
			}
			else
			{
				sourceName = $"Object <{sourceId.Value}>";
			}
		}

		return reference.Kind switch
		{
			SongReferenceKind.RootSequence => "the song root sequence",
			SongReferenceKind.PatternNoteSource => $"{sourceName}: pattern note source",
			SongReferenceKind.SequencePattern => $"{sourceName}: sequence pattern entry",
			SongReferenceKind.InstrumentSource => $"{sourceName}: instrument tone source",
			SongReferenceKind.InstrumentVolumeEnvelope => $"{sourceName}: volume envelope",
			SongReferenceKind.InstrumentPitchEnvelope => $"{sourceName}: pitch envelope",
			SongReferenceKind.InstrumentPanningEnvelope => $"{sourceName}: panning envelope",
			SongReferenceKind.InstrumentFilterEnvelope => $"{sourceName}: filter envelope",
			SongReferenceKind.Tree => $"{sourceName}: tree placement",
			_ => sourceName,
		};
	}

	private void RefreshDocumentView(
		string status,
		SongTreeNode? selectNode = null)
	{
		if (_documentView is not null)
			_mainContent.Content = _documentView;
		UpdateWindowTitle();

		_selectedItem = null;
		_selectedTree = null;
		TreeView? selectedTree = null;
		TreeViewItem? selectedControl = null;
		SongTreeItemViewModel? selectedItem = null;

		foreach (SongTreeSection section in SongTreeSections.DocumentOrder)
		{
			TreeView tree = _trees[section];
			tree.SelectedItem = null;

			SongTreeItemViewModel sectionRoot =
				SongTreeItemViewModel.Create(
					_workspace.Document,
					_workspace.Document.GetSectionRoot(section));

			TreeViewItem? localSelectedControl = null;
			SongTreeItemViewModel? localSelectedItem = null;
			object[] children = new object[sectionRoot.Children.Count];
			for (int index = 0; index < sectionRoot.Children.Count; index++)
			{
				children[index] = BuildTreeItem(
					tree,
					section,
					sectionRoot.Children[index],
					selectNode,
					ref localSelectedControl,
					ref localSelectedItem);
			}
			tree.ItemsSource = children;

			if (localSelectedControl is not null && localSelectedItem is not null)
			{
				selectedTree = tree;
				selectedControl = localSelectedControl;
				selectedItem = localSelectedItem;
			}
		}

		if (selectedTree is not null
			&& selectedControl is not null
			&& selectedItem is not null)
		{
			selectedTree.SelectedItem = selectedControl;
			SelectTreeItem(selectedItem, selectedTree);
		}

		SetStatus(status);
	}

	private TreeViewItem BuildTreeItem(
		TreeView tree,
		SongTreeSection section,
		SongTreeItemViewModel item,
		SongTreeNode? selectNode,
		ref TreeViewItem? selectedControl,
		ref SongTreeItemViewModel? selectedItem)
	{
		TextBlock header =
			new()
			{
				Text = item.IsMissingReference
					? $"⚠ {item.DisplayName}"
					: item.DisplayName,
				FontStyle = item.IsMissingReference
					? FontStyle.Italic
					: FontStyle.Normal,
			};

		TreeViewItem result =
			new()
			{
				Header = header,
				IsExpanded = item.IsFolder,
				Tag = item,
			};

		if (selectNode is not null && ReferenceEquals(item.Node, selectNode))
		{
			selectedControl = result;
			selectedItem = item;
		}

		object[] children = new object[item.Children.Count];
		for (int index = 0; index < item.Children.Count; index++)
		{
			children[index] = BuildTreeItem(
				tree,
				section,
				item.Children[index],
				selectNode,
				ref selectedControl,
				ref selectedItem);
		}
		result.ItemsSource = children;

		result.PointerPressed += (_, e) =>
			OnTreePointerPressed(
				tree,
				result,
				item,
				e);
		result.DoubleTapped += async (_, e) =>
			await OnTreeDoubleTappedAsync(
				section,
				tree,
				result,
				item,
				e);
		result.PointerMoved += async (_, e) =>
			await OnTreePointerMovedAsync(tree, item, e);
		result.PointerReleased += (_, _) => ClearDragCandidate(item);

		DragDrop.SetAllowDrop(result, true);
		DragDrop.AddDragOverHandler(
			result,
			(_, e) => OnTreeDragOver(item, e));
		DragDrop.AddDropHandler(
			result,
			(_, e) => OnTreeDrop(item, e));

		result.ContextMenu = BuildTreeContextMenu(section, tree, item, result);
		return result;
	}

	private ContextMenu BuildTreeContextMenu(
		SongTreeSection section,
		TreeView tree,
		SongTreeItemViewModel item,
		TreeViewItem control)
	{
		MenuItem newFolder = new() { Header = "New Folder..." };
		newFolder.Click += async (_, _) =>
		{
			tree.SelectedItem = control;
			SelectTreeItem(item, tree);
			await CreateFolderAsync(section);
		};

		MenuItem rename =
			new()
			{
				Header = "Rename...",
				IsEnabled = item.IsFolder || !item.IsMissingReference,
			};
		rename.Click += async (_, _) =>
		{
			tree.SelectedItem = control;
			SelectTreeItem(item, tree);
			await RenameSelectedAsync();
		};

		MenuItem delete =
			new()
			{
				Header = item.IsMissingReference ? "Remove from Tree..." : "Delete Object...",
				IsEnabled = item.Node is SongTreeObject,
			};
		delete.Click += async (_, _) =>
		{
			tree.SelectedItem = control;
			SelectTreeItem(item, tree);
			await DeleteSelectedAsync();
		};

		List<object> items = [];
		SongTreeActivationKind activation =
			SongTreeDefaultActivation.Resolve(
				section,
				item);
		if (activation != SongTreeActivationKind.None)
		{
			MenuItem edit =
				new()
				{
					Header =
						SongTreeDefaultActivation.GetMenuHeader(
							activation),
					FontWeight =
						FontWeight.Bold,
				};
			edit.Click += async (_, _) =>
			{
				tree.SelectedItem = control;
				SelectTreeItem(item, tree);
				await ActivateTreeItemAsync(
					activation,
					item);
			};
			items.Add(edit);
			items.Add(new Separator());
		}
		items.Add(newFolder);
		items.Add(rename);
		items.Add(new Separator());
		items.Add(delete);

		return new ContextMenu
		{
			ItemsSource = items,
		};
	}

	private async Task ActivateTreeItemAsync(
		SongTreeActivationKind activation,
		SongTreeItemViewModel item)
	{
		switch (activation)
		{
			case SongTreeActivationKind.Sequence:
				ShowSequenceEditor(item);
				break;
			case SongTreeActivationKind.Pattern:
				ShowPatternEditor(item);
				break;
			case SongTreeActivationKind.Instrument:
				ShowInstrumentEditor(item);
				break;
			case SongTreeActivationKind.Envelope:
				ShowEnvelopeEditor(item);
				break;
			case SongTreeActivationKind.Sample:
				await ShowSampleEditorAsync(item);
				break;
			case SongTreeActivationKind.FmSynth:
				ShowFmSynthEditor(item);
				break;
			case SongTreeActivationKind.None:
				break;
			default:
				throw new ArgumentOutOfRangeException(
					nameof(activation));
		}
	}

	private void ShowSequenceEditor(SongTreeItemViewModel item)
	{
		if (item.ObjectId is not ObjectId id
			|| !_workspace.Document.TryGet(id, out SongObject? songObject))
		{
			SetStatus("The selected sequence is not available.");
			return;
		}

		if (songObject is DataSequenceDefinition sequence)
		{
			ShowSequenceEditor(sequence, item.Node);
			return;
		}

		if (songObject is ScriptSequenceDefinition scriptSequence)
		{
			ShowScriptEditor(scriptSequence, item.Node);
			return;
		}

		SetStatus("The selected sequence type is not editable.");
	}

	private void ShowSequenceEditor(
		DataSequenceDefinition sequence,
		SongTreeNode? selectNode)
	{
		SequenceEditorControl editor =
			new(
				_workspace,
				sequence,
				() => RefreshDocumentView(
					$"Edited sequence {sequence.Name}",
					selectNode),
				message =>
				{
					UpdateWindowTitle();
					SetStatus(message);
				},
				(entryIndex, patternId) =>
					ShowSequencePattern(
						sequence,
						selectNode,
						entryIndex,
						patternId));
		_mainContent.Content = editor;
		UpdateWindowTitle();
		SetStatus($"Editing sequence {sequence.Name}");
	}

	private void ShowSequencePattern(
		DataSequenceDefinition sequence,
		SongTreeNode? sequenceNode,
		int entryIndex,
		ObjectId patternId)
	{
		if (!_workspace.Document.TryGet(
			patternId,
			out SongObject? songObject)
			|| songObject is not PatternDefinition)
		{
			SetStatus(
				$"Sequence order {entryIndex} references a missing pattern.");
			return;
		}

		if (songObject is ScriptPatternDefinition scriptPattern)
		{
			ShowScriptEditor(
				scriptPattern,
				sequenceNode,
				() => ShowSequenceEditor(sequence, sequenceNode),
				"← Sequence");
			return;
		}

		if (songObject is not DataPatternDefinition pattern)
		{
			SetStatus("The referenced pattern type is not editable.");
			return;
		}

		PatternEditorContext context =
			PatternEditorContext.ForSequence(
				_workspace.Document,
				sequence,
				entryIndex);
		ShowPatternEditor(
			context,
			() => ShowSequenceEditor(sequence, sequenceNode),
			"← Sequence",
			$"Editing sequence {sequence.Name} from order {entryIndex}");
	}

	private void ShowPatternEditor(SongTreeItemViewModel item)
	{
		if (item.ObjectId is not ObjectId id
			|| !_workspace.Document.TryGet(id, out SongObject? songObject))
		{
			SetStatus("The selected pattern is not available.");
			return;
		}

		if (songObject is DataPatternDefinition pattern)
		{
			ShowPatternEditor(pattern, item.Node);
			return;
		}

		if (songObject is ScriptPatternDefinition scriptPattern)
		{
			ShowScriptEditor(scriptPattern, item.Node);
			return;
		}

		SetStatus("The selected pattern type is not editable.");
	}

	private void ShowPatternEditor(
		DataPatternDefinition pattern,
		SongTreeNode? selectNode,
		Action? closeOverride = null,
		string backLabel = "← Document",
		PatternEditorOpenState? initialState = null)
	{
		PatternEditorContext context =
			PatternEditorContext.ForPattern(
				_workspace.Document,
				pattern);
		Action close =
			closeOverride
				?? (() => RefreshDocumentView(
					$"Edited pattern {pattern.Name}",
					selectNode));
		Action<PatternEditorSwitchRequest>? switchPattern =
			selectNode is SongTreeObject currentNode
				? request =>
					SwitchTreePattern(
						currentNode,
						request,
						closeOverride,
						backLabel)
				: null;
		ShowPatternEditor(
			context,
			close,
			backLabel,
			$"Editing pattern {pattern.Name}",
			switchPattern,
			initialState);
	}

	private void ShowPatternEditor(
		PatternEditorContext context,
		Action close,
		string backLabel,
		string status,
		Action<PatternEditorSwitchRequest>? switchPattern = null,
		PatternEditorOpenState? initialState = null)
	{
		PatternEditorControl editor =
			new(
				this,
				_workspace,
				context,
				close,
				message =>
				{
					UpdateWindowTitle();
					SetStatus(message);
				},
				backLabel,
				_uiConfiguration,
				_playbackTransport is null
					? null
					: schedule =>
						_playbackTransport.PlayAdHocAsync(
							_workspace.Document,
							schedule),
				_playbackTransport is null
					? null
					: new PatternLiveAuditionActions(
						liveEvent =>
							_playbackTransport.SendLiveEventAsync(
								_workspace.Document,
								liveEvent.Target,
								liveEvent.Commands)),
				switchPattern,
				initialState);
		_mainContent.Content = editor;
		editor.SetPlaybackPosition(
			_playbackPositionTransport
				?.CurrentPlaybackPosition);
		UpdateWindowTitle();
		SetStatus(status);
	}

	private void SwitchTreePattern(
		SongTreeObject currentNode,
		PatternEditorSwitchRequest request,
		Action? closeOverride,
		string backLabel)
	{
		int delta = request.Delta;
		SongTreeObject? targetNode =
			PatternTreeNavigation.FindAdjacent(
				_workspace.Document,
				currentNode,
				delta);
		if (targetNode is null)
		{
			SetStatus(
				delta > 0
					? "There is no next tracker-editable pattern in the Patterns tree."
					: "There is no previous tracker-editable pattern in the Patterns tree.");
			return;
		}

		if (!_workspace.Document.TryGet(
				targetNode.ObjectId,
				out SongObject? songObject)
			|| songObject is not DataPatternDefinition pattern)
		{
			SetStatus(
				"The adjacent pattern is no longer available.");
			return;
		}

		ShowPatternEditor(
			pattern,
			targetNode,
			closeOverride,
			backLabel,
			request.State);
	}

	private void ShowScriptEditor(
		ScriptPatternDefinition pattern,
		SongTreeNode? selectNode,
		Action? closeOverride = null,
		string backLabel = "← Document")
	{
		ScriptEditorControl editor =
			new(
				_workspace,
				pattern,
				closeOverride
					?? (() => RefreshDocumentView(
						$"Edited script pattern {pattern.Name}",
						selectNode)),
				message =>
				{
					UpdateWindowTitle();
					SetStatus(message);
				},
				backLabel);
		_mainContent.Content = editor;
		UpdateWindowTitle();
		SetStatus($"Editing script pattern {pattern.Name}");
	}

	private void ShowScriptEditor(
		ScriptSequenceDefinition sequence,
		SongTreeNode? selectNode,
		Action? closeOverride = null,
		string backLabel = "← Document")
	{
		ScriptEditorControl editor =
			new(
				_workspace,
				sequence,
				closeOverride
					?? (() => RefreshDocumentView(
						$"Edited script sequence {sequence.Name}",
						selectNode)),
				message =>
				{
					UpdateWindowTitle();
					SetStatus(message);
				},
				backLabel);
		_mainContent.Content = editor;
		UpdateWindowTitle();
		SetStatus($"Editing script sequence {sequence.Name}");
	}

	private void ShowInstrumentEditor(SongTreeItemViewModel item)
	{
		if (item.ObjectId is not ObjectId id
			|| !_workspace.Document.TryGet(id, out SongObject? songObject)
			|| songObject is not InstrumentDefinition instrument)
		{
			SetStatus("The selected instrument is not available.");
			return;
		}

		ShowInstrumentEditor(instrument, item.Node);
	}

	private void ShowInstrumentEditor(
		InstrumentDefinition instrument,
		SongTreeNode? selectNode)
	{
		InstrumentEditorControl editor =
			new(
				_workspace,
				instrument,
				() => RefreshDocumentView(
					$"Edited instrument {instrument.Name}",
					selectNode),
				message =>
				{
					UpdateWindowTitle();
					SetStatus(message);
				});
		_mainContent.Content = editor;
		UpdateWindowTitle();
		SetStatus($"Editing instrument {instrument.Name}");
	}

	private void ShowEnvelopeEditor(SongTreeItemViewModel item)
	{
		if (item.ObjectId is not ObjectId id
			|| !_workspace.Document.TryGet(id, out SongObject? songObject)
			|| songObject is not AdsrEnvelopeDefinition envelope)
		{
			SetStatus("The selected envelope is not available.");
			return;
		}

		ShowEnvelopeEditor(envelope, item.Node);
	}

	private void ShowEnvelopeEditor(
		AdsrEnvelopeDefinition envelope,
		SongTreeNode? selectNode)
	{
		EnvelopeEditorControl editor =
			new(
				_workspace,
				envelope,
				() => RefreshDocumentView(
					$"Edited envelope {envelope.Name}",
					selectNode),
				message =>
				{
					UpdateWindowTitle();
					SetStatus(message);
				});
		_mainContent.Content = editor;
		UpdateWindowTitle();
		SetStatus($"Editing envelope {envelope.Name}");
	}

	private void ShowFmSynthEditor(SongTreeItemViewModel item)
	{
		if (item.ObjectId is not ObjectId id
			|| !_workspace.Document.TryGet(id, out SongObject? songObject)
			|| songObject is not FmSynthDefinition synth)
		{
			SetStatus("The selected FM synth is not available.");
			return;
		}

		ShowFmSynthEditor(
			synth,
			item.Node);
	}

	private void ShowFmSynthEditor(
		FmSynthDefinition synth,
		SongTreeNode? selectNode)
	{
		FmSynthEditorControl editor =
			new(
				_workspace,
				synth,
				() => RefreshDocumentView(
					$"Edited FM synth {synth.Name}",
					selectNode),
				message =>
				{
					UpdateWindowTitle();
					SetStatus(message);
				},
				_playbackTransport is null
					? null
					: new PatternLiveAuditionActions(
						liveEvent =>
							_playbackTransport.SendLiveEventAsync(
								_workspace.Document,
								liveEvent.Target,
								liveEvent.Commands)));
		_mainContent.Content = editor;
		UpdateWindowTitle();
		SetStatus($"Editing FM synth {synth.Name}");
	}

	private async Task ShowSampleEditorAsync(SongTreeItemViewModel item)
	{
		if (item.ObjectId is not ObjectId id
			|| !_workspace.Document.TryGet(id, out SongObject? songObject)
			|| songObject is not SampleDefinition sample)
		{
			SetStatus("The selected sample is not available.");
			return;
		}

		await ShowSampleEditorAsync(sample, item.Node);
	}

	private async Task ShowSampleEditorAsync(
		SampleDefinition sample,
		SongTreeNode? selectNode)
	{
		SampleEditorDialog dialog = new(_workspace, sample);
		await dialog.ShowDialog(this);
		RefreshDocumentView($"Updated sample {sample.Name}", selectNode);
	}

	private static SongTreeObject? FindTreeObject(
		SongTreeFolder folder,
		ObjectId id)
	{
		foreach (SongTreeNode child in folder.Children)
		{
			if (child is SongTreeObject songObject && songObject.ObjectId == id)
				return songObject;
			if (child is SongTreeFolder childFolder)
			{
				SongTreeObject? nested = FindTreeObject(childFolder, id);
				if (nested is not null)
					return nested;
			}
		}

		return null;
	}

	private void OnTreePointerPressed(
		TreeView tree,
		TreeViewItem control,
		SongTreeItemViewModel item,
		PointerPressedEventArgs e)
	{
		tree.SelectedItem = control;
		SelectTreeItem(item, tree);

		if (!e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
		{
			ClearDragCandidate(item);
			return;
		}

		_dragCandidate = item;
		_dragTrigger = e;
		_dragStart = e.GetPosition(tree);
	}

	private async Task OnTreeDoubleTappedAsync(
		SongTreeSection section,
		TreeView tree,
		TreeViewItem control,
		SongTreeItemViewModel item,
		TappedEventArgs e)
	{
		tree.SelectedItem = control;
		SelectTreeItem(item, tree);
		ClearDragCandidate(item);

		SongTreeActivationKind activation =
			SongTreeDefaultActivation.Resolve(
				section,
				item);
		if (activation == SongTreeActivationKind.None)
			return;

		e.Handled = true;
		await ActivateTreeItemAsync(
			activation,
			item);
	}

	private async Task OnTreePointerMovedAsync(
		TreeView tree,
		SongTreeItemViewModel item,
		PointerEventArgs e)
	{
		if (_dragInProgress
			|| !ReferenceEquals(_dragCandidate, item)
			|| _dragTrigger is null)
		{
			return;
		}

		if (!e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
		{
			ClearDragCandidate(item);
			return;
		}

		Point current = e.GetPosition(tree);
		double dx = current.X - _dragStart.X;
		double dy = current.Y - _dragStart.Y;
		if ((dx * dx) + (dy * dy) < 36.0)
			return;

		PointerPressedEventArgs trigger = _dragTrigger;
		_dragCandidate = null;
		_dragTrigger = null;
		_dragInProgress = true;

		DataTransfer data = new();
		data.Add(DataTransferItem.Create(TreeNodeFormat, item.Node));
		try
		{
			await DragDrop.DoDragDropAsync(
				trigger,
				data,
				DragDropEffects.Move);
		}
		finally
		{
			_dragInProgress = false;
		}
	}

	private void ClearDragCandidate(SongTreeItemViewModel item)
	{
		if (ReferenceEquals(_dragCandidate, item))
		{
			_dragCandidate = null;
			_dragTrigger = null;
		}
	}

	private void OnTreeDragOver(
		SongTreeItemViewModel target,
		DragEventArgs e)
	{
		SongTreeNode? node = e.DataTransfer.TryGetValue(TreeNodeFormat);
		if (node is null)
		{
			e.DragEffects = DragDropEffects.None;
			return;
		}

		bool canMove = target.Node switch
		{
			SongTreeFolder folder =>
				SongTreeEditor.CanMoveInto(_workspace.Document, node, folder),
			_ =>
				SongTreeEditor.CanMoveBefore(_workspace.Document, node, target.Node),
		};
		e.DragEffects = canMove
			? DragDropEffects.Move
			: DragDropEffects.None;
	}

	private void OnTreeDrop(
		SongTreeItemViewModel target,
		DragEventArgs e)
	{
		SongTreeNode? node = e.DataTransfer.TryGetValue(TreeNodeFormat);
		if (node is null)
		{
			e.DragEffects = DragDropEffects.None;
			return;
		}

		try
		{
			if (target.Node is SongTreeFolder folder)
				SongTreeEditor.MoveInto(_workspace.Document, node, folder);
			else
				SongTreeEditor.MoveBefore(_workspace.Document, node, target.Node);

			e.DragEffects = DragDropEffects.Move;
			RefreshDocumentView("Reorganized song tree", node);
		}
		catch (InvalidOperationException ex)
		{
			e.DragEffects = DragDropEffects.None;
			SetStatus(ex.Message);
		}
	}

	private void SelectTreeItem(
		SongTreeItemViewModel item,
		TreeView tree)
	{
		if (!ReferenceEquals(_selectedTree, tree))
		{
			foreach (TreeView other in _trees.Values)
			{
				if (!ReferenceEquals(other, tree))
					other.SelectedItem = null;
			}
		}

		_selectedItem = item;
		_selectedTree = tree;
		SetStatus(DescribeSelection(item));
	}

	private static string DescribeSelection(SongTreeItemViewModel item)
	{
		if (item.IsFolder)
		{
			return $"{item.DisplayName} — folder, {item.Children.Count} item{(item.Children.Count == 1 ? string.Empty : "s")}";
		}

		string id = item.ObjectId?.Value.ToString() ?? "—";
		return item.IsMissingReference
			? $"{item.DisplayName} — missing {item.Kind} reference, Object ID {id}"
			: $"{item.DisplayName} — {item.Kind}, Object ID {id}";
	}

	private void RefreshAfterPersistence(
		string status,
		SongTreeNode? selectNode)
	{
		UpdateWindowTitle();
		if (_documentView is not null
			&& ReferenceEquals(_mainContent.Content, _documentView))
		{
			RefreshDocumentView(status, selectNode);
		}
		else
		{
			SetStatus(status);
		}
	}

	private void UpdateWindowTitle()
	{
		Title =
			_workspace.IsModified
				? $"{_workspace.DisplayName} * — Heresy"
				: $"{_workspace.DisplayName} — Heresy";
	}

	protected override void OnKeyDown(
		KeyEventArgs e)
	{
		if (e.KeyModifiers == KeyModifiers.None
			&& e.Key is
				Key.F5
				or Key.F6
				or Key.F7
				or Key.F8)
		{
			e.Handled = true;
			_ = HandlePlaybackKeyAsync(e.Key);
			return;
		}

		if (FileMenuKeyboard.TryGetCommand(
			e.Key,
			e.KeyModifiers,
			e.Handled,
			out FileMenuCommand command))
		{
			e.Handled = true;
			_ = HandleFileMenuCommandAsync(command);
			return;
		}

		base.OnKeyDown(e);
	}

	private async Task HandleFileMenuCommandAsync(
		FileMenuCommand command)
	{
		switch (command)
		{
			case FileMenuCommand.New:
				await NewDocumentAsync();
				break;

			case FileMenuCommand.Open:
				await OpenDocumentAsync();
				break;

			case FileMenuCommand.Save:
				await SaveDocumentAsync();
				break;

			case FileMenuCommand.Exit:
				Close();
				break;

			default:
				throw new ArgumentOutOfRangeException(nameof(command));
		}
	}

	private async Task HandlePlaybackKeyAsync(
		Key key)
	{
		if (_playbackTransport is null)
		{
			SetStatus(
				"Realtime playback is not configured in this host.");
			return;
		}

		try
		{
			switch (key)
			{
				case Key.F5:
					await _playbackTransport.PlaySongAsync(
						_workspace.Document);
					SetStatus("Playing song from the root sequence.");
					break;

				case Key.F6:
					await PlayCurrentPatternAsync();
					break;

				case Key.F7:
					await PlayFromCurrentRowAsync();
					break;

				case Key.F8:
					await _playbackTransport.StopAsync();
					SetStatus("Playback stopped.");
					break;
			}
		}
		catch (Exception ex)
		{
			SetStatus(
				$"Playback failed: {ex.Message}");
		}
	}

	private async Task PlayCurrentPatternAsync()
	{
		if (_playbackTransport is null)
			return;

		if (_mainContent.Content
			is not PatternEditorControl editor)
		{
			SetStatus(
				"F6 requires an open tracker pattern view.");
			return;
		}

		PatternEditorPlaybackCursor cursor =
			editor.GetPlaybackCursor();

		await _playbackTransport.PlayPatternAsync(
			_workspace.Document,
			cursor.PatternId,
			startRow: 0,
			repeat: true);

		string name =
			_workspace.Document.TryGet(
				cursor.PatternId,
				out SongObject? songObject)
				&& songObject is not null
					? songObject.Name
					: $"Pattern <{cursor.PatternId.Value}>";
		SetStatus(
			$"Playing {name} repeatedly.");
	}

	private async Task PlayFromCurrentRowAsync()
	{
		if (_playbackTransport is null)
			return;

		if (_mainContent.Content
			is not PatternEditorControl editor)
		{
			SetStatus(
				"F7 requires an open tracker pattern view.");
			return;
		}

		PatternEditorPlaybackCursor cursor =
			editor.GetPlaybackCursor();
		PlaybackStartLocation start =
			PlaybackStartResolver.ResolveFromPattern(
				_workspace.Document,
				cursor.PatternId,
				cursor.PatternRow,
				cursor.SequenceId,
				cursor.SequenceEntryIndex);

		switch (start)
		{
			case SequencePlaybackStartLocation sequence:
				await _playbackTransport.PlaySequenceAsync(
					_workspace.Document,
					sequence.SequenceId,
					new SequencePlaybackPosition(
						sequence.Order,
						sequence.Row));
				SetStatus(
					$"Playing from sequence <{sequence.SequenceId.Value}> "
						+ $"order {sequence.Order}, row {sequence.Row}.");
				break;

			case PatternPlaybackStartLocation pattern:
				await _playbackTransport.PlayPatternAsync(
					_workspace.Document,
					pattern.PatternId,
					pattern.Row,
					repeat: false);
				SetStatus(
					$"Playing pattern <{pattern.PatternId.Value}> "
						+ $"from row {pattern.Row}.");
				break;

			default:
				throw new InvalidOperationException(
					$"Unsupported playback start location {start.GetType().Name}.");
		}
	}

	private void OnPlaybackPositionChanged(
		object? sender,
		PlaybackPositionChangedEventArgs e)
	{
		_ = sender;
		PlaybackPatternPosition? position =
			e.Position;
		Dispatcher.UIThread.Post(
			() =>
			{
				if (_mainContent.Content
					is PatternEditorControl editor)
				{
					editor.SetPlaybackPosition(
						position);
				}
			});
	}

	/// <summary>Only UI-thread delivery updates labels or warning history.
	/// In particular, no UI work is performed on SDL's audio callback.</summary>
	private void OnAudioHealthChanged(
		object? sender,
		PlaybackAudioHealthChangedEventArgs e)
	{
		_ = sender;
		Dispatcher.UIThread.Post(() =>
		{
			if (_windowClosed || e.SessionId < _lastAudioHealthSessionId)
				return;
			_lastAudioHealthSessionId = e.SessionId;
			_underrunIndicator.IsVisible = e.IsActive && e.UnderrunCount > 0;
			if (_underrunIndicator.IsVisible)
				_underrunIndicator.Text = $"Audio underruns: {e.UnderrunCount:N0}";

			if (e.IsNewFault && e.Fault is not null)
			{
				_runtimeDiagnosticMessages.Add(
					$"[Playback] Audio worker failure: {e.Fault.GetType().Name}: {e.Fault.Message}");
				if (_runtimeDiagnosticMessages.Count > MaximumVisibleRuntimeDiagnostics)
					_runtimeDiagnosticMessages.RemoveRange(
						0, _runtimeDiagnosticMessages.Count - MaximumVisibleRuntimeDiagnostics);
				UpdateRuntimeDiagnosticsView();
				SetStatus($"Audio playback failed: {e.Fault.Message}");
			}
		});
	}

	private void OnRuntimeDiagnostics(
		object? sender,
		PlaybackRuntimeDiagnosticsEventArgs e)
	{
		_ = sender;
		// Source compilation and transport callbacks can complete on a
		// worker thread. Never touch Avalonia controls until dispatched.
		SequencingDiagnostic[] snapshot = e.Diagnostics.ToArray();
		Dispatcher.UIThread.Post(() =>
		{
			if (!_windowClosed)
				AppendRuntimeDiagnostics(snapshot);
		});
	}

	/// <summary>Both realtime and offline reports use the same bounded
	/// in-app warning history and existing clear/open commands. Call only
	/// from Avalonia's UI dispatcher.</summary>
	private void AppendRuntimeDiagnostics(
		IReadOnlyList<SequencingDiagnostic> batch,
		string? origin = null)
	{
		string prefix = origin is null ? string.Empty : $"[{origin}] ";
		foreach (SequencingDiagnostic warning in batch)
			_runtimeDiagnosticMessages.Add(
				$"{prefix}{warning.Code}: {warning.Message}");
		if (_runtimeDiagnosticMessages.Count > MaximumVisibleRuntimeDiagnostics)
			_runtimeDiagnosticMessages.RemoveRange(
				0,
				_runtimeDiagnosticMessages.Count - MaximumVisibleRuntimeDiagnostics);
		UpdateRuntimeDiagnosticsView();
	}

	private void UpdateRuntimeDiagnosticsView()
	{
		int count = _runtimeDiagnosticMessages.Count;
		_diagnosticsButton.IsVisible = count != 0;
		_diagnosticsButton.Content = $"Warnings ({count})";
		if (_runtimeDiagnosticsList is not null)
			_runtimeDiagnosticsList.ItemsSource =
				_runtimeDiagnosticMessages.ToArray();
	}

	private void ShowRuntimeDiagnostics()
	{
		if (_runtimeDiagnosticsWindow is not null)
		{
			_runtimeDiagnosticsWindow.Activate();
			return;
		}

		_runtimeDiagnosticsList = new ListBox
		{
			ItemsSource = _runtimeDiagnosticMessages.ToArray(),
		};
		Button clear = new() { Content = "Clear" };
		clear.Click += (_, _) =>
		{
			_runtimeDiagnosticMessages.Clear();
			UpdateRuntimeDiagnosticsView();
		};
		DockPanel layout = new();
		DockPanel.SetDock(clear, Dock.Bottom);
		layout.Children.Add(clear);
		layout.Children.Add(_runtimeDiagnosticsList);
		Window dialog = new()
		{
			Title = "Heresy — Runtime Diagnostics",
			Width = 850,
			Height = 380,
			MinWidth = 450,
			MinHeight = 230,
			Content = layout,
		};
		_runtimeDiagnosticsWindow = dialog;
		dialog.Closed += (_, _) =>
		{
			_runtimeDiagnosticsWindow = null;
			_runtimeDiagnosticsList = null;
		};
		dialog.Show(this);
	}

	private void SetStatus(string text)
	{
		_status.Text = text;
	}
}

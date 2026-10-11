using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using Heresy.Core.Assets;
using Heresy.Core.Samples;
using Heresy.UserInterface.FmEditing;
using Heresy.UserInterface.PatternEditing;
using Heresy.UserInterface.Documents;
using Heresy.UserInterface.SampleEditing;
using Heresy.UserInterface.Views;

namespace Heresy.UserInterface.Dialogs;

public sealed class SampleEditorDialog : Window
{
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

	private readonly DocumentWorkspace _workspace;
	private readonly SampleDefinition _sample;
	private readonly TextBox _name;
	private readonly ComboBox _loopMode;
	private readonly TextBox _loopStart;
	private readonly TextBox _loopEnd;
	private readonly TextBox _loopAssistantRadiusMs;
	private readonly TextBox _relativePath;
	private readonly TextBox _resolvedPath;
	private readonly TextBlock _integrityStatus;
	private readonly TextBox _expectedHash;
	private readonly TextBox _actualHash;
	private readonly SampleWaveformControl _waveform;
	private readonly TextBlock _waveformSummary;
	private readonly TextBlock _message;
	private readonly PatternLiveAuditionActions? _liveAudition;
	private readonly Func<Task>? _stopAuditionAsync;
	private readonly FmSynthAuditionKeyboard _auditionKeyboard =
		new(SampleAuditionCompiler.VoiceIdBase);
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

	public SampleEditorDialog(
		DocumentWorkspace workspace,
		SampleDefinition sample,
		PatternLiveAuditionActions? liveAudition = null,
		Func<Task>? stopAuditionAsync = null)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_sample = sample
			?? throw new ArgumentNullException(nameof(sample));
		_liveAudition = liveAudition;
		_stopAuditionAsync = stopAuditionAsync;

		Title = $"Sample — {sample.Name}";
		Width = 700;
		Height = 650;
		MinWidth = 560;
		MinHeight = 520;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		_name = new TextBox { Text = sample.Name };
		_loopMode =
			new ComboBox
			{
				ItemsSource = Enum.GetValues<SampleLoopMode>(),
				SelectedItem = sample.Loop.Mode,
			};
		_loopStart =
			new TextBox
			{
				Text = sample.Loop.StartFrame.ToString(CultureInfo.CurrentCulture),
			};
		_loopEnd =
			new TextBox
			{
				Text = sample.Loop.EndFrameExclusive.ToString(CultureInfo.CurrentCulture),
			};
		_loopAssistantRadiusMs =
			new TextBox
			{
				Text = "20",
				Width = 70,
			};

		_relativePath = ReadOnlyTextBox();
		_resolvedPath = ReadOnlyTextBox();
		_integrityStatus =
			new TextBlock
			{
				FontWeight = FontWeight.SemiBold,
			};
		_expectedHash = ReadOnlyTextBox();
		_actualHash = ReadOnlyTextBox();
		_waveform =
			new SampleWaveformControl
			{
				Height = 180,
				HorizontalAlignment = HorizontalAlignment.Stretch,
			};
		_waveform.LoopPreviewChanged +=
			(_, e) => PreviewWaveformLoop(e.Loop);
		_waveform.LoopCommitted +=
			(_, e) => CommitWaveformLoop(e.Loop);
		_waveformSummary =
			new TextBlock
			{
				TextWrapping = TextWrapping.Wrap,
			};
		_message =
			new TextBlock
			{
				TextWrapping = TextWrapping.Wrap,
			};

		_testArea.PointerPressed += (_, e) =>
		{
			_testArea.Focus();
			e.Handled = true;
		};
		_testArea.KeyDown += async (_, e) => await OnTestKeyDownAsync(e);
		_testArea.GotFocus += (_, _) =>
			_testArea.BorderBrush = Brushes.LightSkyBlue;
		_testArea.LostFocus += (_, _) =>
			_testArea.BorderBrush = Brushes.Gray;
		// Editing a loop field must not release a held note.
		AddHandler(InputElement.KeyUpEvent, OnWindowKeyUp,
			RoutingStrategies.Tunnel, handledEventsToo: true);
		AddHandler(InputElement.KeyDownEvent, OnWindowStopKeyDown,
			RoutingStrategies.Tunnel, handledEventsToo: true);
		Deactivated += async (_, _) => await ReleaseAllTestNotesAsync();
		Closed += async (_, _) => await ReleaseAllTestNotesAsync();
		_loopStart.LostFocus += (_, _) => CommitLoopFields();
		_loopEnd.LostFocus += (_, _) => CommitLoopFields();
		UpdateAuditionOctave();

		Content = BuildContent();
		RefreshWaveform();
		RefreshDiagnostics();
	}

	private Control BuildContent()
	{
		Grid form = new();
		form.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
		form.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

		int row = 0;

		TextBlock waveformHeading =
			new()
			{
				Text = "Decoded waveform",
				FontSize = 18,
				FontWeight = FontWeight.SemiBold,
				Margin = new Thickness(0, 0, 0, 4),
			};
		AddFullWidth(form, ref row, waveformHeading);
		AddFullWidth(
			form,
			ref row,
			new Border
			{
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(1),
				Child = _waveform,
			});
		AddFullWidth(form, ref row, _waveformSummary);
		AddFullWidth(form, ref row, BuildAuditionArea());

		AddField(form, ref row, "Name", _name);
		AddField(form, ref row, "Loop mode", _loopMode);
		AddField(form, ref row, "Loop start frame", _loopStart);
		AddField(form, ref row, "Loop end frame (exclusive)", _loopEnd);

		Button findNaturalLoop =
			new()
			{
				Content = "Find natural loop",
			};
		findNaturalLoop.Click +=
			(_, _) => FindNaturalLoop();

		StackPanel loopAssistant =
			new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 6,
				VerticalAlignment = VerticalAlignment.Center,
			};
		loopAssistant.Children.Add(
			new TextBlock
			{
				Text = "Search ±",
				VerticalAlignment = VerticalAlignment.Center,
			});
		loopAssistant.Children.Add(_loopAssistantRadiusMs);
		loopAssistant.Children.Add(
			new TextBlock
			{
				Text = "ms",
				VerticalAlignment = VerticalAlignment.Center,
			});
		loopAssistant.Children.Add(findNaturalLoop);
		AddField(form, ref row, "Loop assistant", loopAssistant);

		TextBlock assetHeading =
			new()
			{
				Text = "Encoded storage",
				FontSize = 18,
				FontWeight = FontWeight.SemiBold,
				Margin = new Thickness(0, 14, 0, 2),
			};
		AddFullWidth(form, ref row, assetHeading);
		AddField(form, ref row, "Full path", _relativePath);
		AddField(form, ref row, "Storage", _resolvedPath);
		AddField(form, ref row, "Integrity", _integrityStatus);
		AddField(form, ref row, "Expected SHA-256", _expectedHash);
		AddField(form, ref row, "Actual SHA-256", _actualHash);
		AddFullWidth(form, ref row, _message);

		Button relink = new() { Content = "Relink..." };
		relink.Click += async (_, _) => await RelinkAsync();

		Button refreshHash = new() { Content = "Accept Current File" };
		refreshHash.Click += (_, _) => RefreshHash();

		Button apply = new() { Content = "Apply" };
		apply.Click += (_, _) => ApplyMetadata();

		Button close = new() { Content = "Close" };
		close.Click += (_, _) => Close();
		DialogActionLayout.ConfigureButtons(apply, close);

		StackPanel buttons =
			new()
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Spacing = 8,
			};
		buttons.Children.Add(relink);
		buttons.Children.Add(refreshHash);
		buttons.Children.Add(apply);
		buttons.Children.Add(close);

		return DialogActionLayout.Create(
			new ScrollViewer
			{
				Content = form,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			},
			buttons,
			new Thickness(18));
	}

	private Control BuildAuditionArea()
	{
		StackPanel description = new()
		{
			Spacing = 4,
			VerticalAlignment = VerticalAlignment.Center,
		};
		description.Children.Add(_auditionOctave);
		description.Children.Add(new TextBlock
		{
			Text = "Click Test, then use tracker piano keys (Z S X D C ...). "
				+ "Keypad * / change octave; release keys to stop. "
				+ "Editing the loop restarts held notes. F8 stops preview.",
			FontSize = 11,
			TextWrapping = TextWrapping.Wrap,
		});
		StackPanel row = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12,
			Margin = new Thickness(0, 6, 0, 10),
		};
		row.Children.Add(_testArea);
		row.Children.Add(description);
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
		if (PatternOctaveKeyboard.TryAdjust(e.PhysicalKey,
			e.KeyModifiers, previousOctave, out _))
		{
			UpdateAuditionOctave();
			e.Handled = true;
			return;
		}
		if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt
			| KeyModifiers.Meta)) != 0
			|| !PatternNoteKeyboard.TryGetSemitoneOffset(
				e.PhysicalKey, out _))
			return;
		e.Handled = true;
		if (note is null)
			return;

		if (_liveAudition is null)
		{
			_message.Text = "Realtime Sample audition is unavailable in this host.";
			_auditionKeyboard.KeyUp(note.Key);
			return;
		}
		try
		{
			await _liveAudition.SendEventAsync(
				SampleAuditionCompiler.Start(_sample.Id, note));
			_message.Text =
				$"Sample audition: octave {_auditionKeyboard.BaseOctave}.";
		}
		catch (Exception ex)
		{
			_auditionKeyboard.KeyUp(note.Key);
			_message.Text = $"Sample audition failed: {ex.Message}";
		}
	}

	private async void OnWindowKeyUp(object? sender, KeyEventArgs e)
	{
		ReleaseFmSynthAuditionNote? note =
			_auditionKeyboard.KeyUp(e.PhysicalKey);
		if (note is null)
			return;
		e.Handled = true;
		await ReleaseTestNoteAsync(note);
	}

	private async void OnWindowStopKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.F8)
			return;
		e.Handled = true;
		_auditionKeyboard.ReleaseAll();
		try
		{
			if (_stopAuditionAsync is not null)
				await _stopAuditionAsync();
			else
				_message.Text = "Playback stop is unavailable in this host.";
		}
		catch (Exception ex)
		{
			_message.Text = $"Could not stop audition: {ex.Message}";
		}
	}

	private async Task ReleaseTestNoteAsync(
		ReleaseFmSynthAuditionNote note)
	{
		if (_liveAudition is null)
			return;
		try
		{
			await _liveAudition.SendEventAsync(
				SampleAuditionCompiler.Release(note));
		}
		catch (Exception ex)
		{
			_message.Text = $"Sample audition release failed: {ex.Message}";
		}
	}

	private async Task ReleaseAllTestNotesAsync()
	{
		foreach (ReleaseFmSynthAuditionNote note
			in _auditionKeyboard.ReleaseAll())
			await ReleaseTestNoteAsync(note);
	}

	/// <summary>Restart exact held pitches and voice IDs, including the
	/// octaves captured at key-down. Document AudioRevision invalidates the
	/// live audition snapshot at the next Start note.</summary>
	private async Task RestartHeldNotesAsync()
	{
		if (_liveAudition is null)
			return;
		foreach (StartFmSynthAuditionNote note in _auditionKeyboard.ActiveNotes)
		{
			try
			{
				await _liveAudition.SendEventAsync(
					SampleAuditionCompiler.Start(_sample.Id, note,
						restart: true));
			}
			catch (Exception ex)
			{
				_message.Text = $"Sample preview restart failed: {ex.Message}";
			}
		}
	}

	/// <summary>Commit the waveform's frame-range on focus loss, without
	/// forcing the user to click Apply for a loop-audition adjustment.</summary>
	private async void CommitLoopFields()
	{
		try
		{
			SampleLoop loop = ReadLoopFromFields();
			if (loop == _sample.Loop)
				return;
			SampleDocumentEditor.UpdateMetadata(_workspace, _sample, loop);
			_waveform.SetLoop(loop);
			await RestartHeldNotesAsync();
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void ApplyMetadata()
	{
		try
		{
			string name = (_name.Text ?? string.Empty).Trim();
			if (name.Length == 0)
				throw new ArgumentException("Sample name must not be empty.");

			SampleLoop loop =
				ReadLoopFromFields();

			if (!string.Equals(name, _sample.Name, StringComparison.Ordinal))
				SongTreeEditor.RenameObject(_workspace.Document, _sample.Id, name);
			bool changed = loop != _sample.Loop;
			SampleDocumentEditor.UpdateMetadata(_workspace, _sample, loop);
			_waveform.SetLoop(loop);
			PreviewWaveformLoop(loop);
			if (changed)
				_ = RestartHeldNotesAsync();

			Title = $"Sample — {_sample.Name}";
			_message.Text = "Sample metadata updated.";
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private async Task RelinkAsync()
	{
		if (!StorageProvider.CanOpen)
		{
			_message.Text = "This platform does not provide an open-file picker.";
			return;
		}

		IReadOnlyList<IStorageFile> files =
			await StorageProvider.OpenFilePickerAsync(
				new FilePickerOpenOptions
				{
					Title = "Relink sample asset",
					AllowMultiple = false,
					FileTypeFilter = new[] { SampleFileType, FilePickerFileTypes.All },
				});
		if (files.Count == 0)
			return;

		string? path = files[0].TryGetLocalPath();
		if (path is null)
		{
			_message.Text = "The selected file does not expose a local filesystem path.";
			return;
		}

		try
		{
			SampleDocumentEditor.Relink(_workspace, _sample, path);
			RefreshWaveform();
			RefreshDiagnostics();
			_message.Text = "Replacement sample imported into the song and decoded.";
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void RefreshHash()
	{
		try
		{
			SampleDocumentEditor.RefreshHash(_workspace, _sample);
			RefreshWaveform();
			RefreshDiagnostics();
			_message.Text = "The persisted encoding was reloaded into memory and accepted.";
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private SampleLoop ReadLoopFromFields()
	{
		if (!long.TryParse(
			_loopStart.Text,
			NumberStyles.Integer,
			CultureInfo.CurrentCulture,
			out long loopStart)
			|| !long.TryParse(
				_loopEnd.Text,
				NumberStyles.Integer,
				CultureInfo.CurrentCulture,
				out long loopEnd))
		{
			throw new ArgumentException(
				"Loop frame positions must be integers.");
		}

		SampleLoopMode loopMode =
			_loopMode.SelectedItem is SampleLoopMode selectedMode
				? selectedMode
				: SampleLoopMode.None;
		return new SampleLoop(
			loopMode,
			loopStart,
			loopEnd);
	}

	private void FindNaturalLoop()
	{
		try
		{
			SamplePcmData pcm =
				_sample.PcmData
					?? throw new InvalidOperationException(
						"The sample has no decoded PCM to analyze.");
			SampleLoop loop =
				ReadLoopFromFields();

			if (!double.TryParse(
				_loopAssistantRadiusMs.Text,
				NumberStyles.Float,
				CultureInfo.CurrentCulture,
				out double radiusMs)
				|| !(radiusMs > 0.0)
				|| !double.IsFinite(radiusMs))
			{
				throw new ArgumentException(
					"Loop assistant search radius must be a finite number greater than zero.");
			}

			double radiusFramesExact =
				radiusMs
					* pcm.SampleRate
					/ 1000.0;
			if (radiusFramesExact > long.MaxValue)
			{
				throw new ArgumentOutOfRangeException(
					nameof(radiusMs),
					"Loop assistant search radius is too large.");
			}

			long radiusFrames =
				Math.Max(
					1,
					(long)Math.Round(
						radiusFramesExact,
						MidpointRounding.AwayFromZero));
			SampleLoopAssistantResult result =
				SampleLoopAssistant.Find(
					pcm,
					loop,
					radiusFrames);

			_waveform.SetLoop(
				result.SuggestedLoop);
			_loopMode.SelectedItem =
				result.SuggestedLoop.Mode;
			PreviewWaveformLoop(
				result.SuggestedLoop);

			string radiusDescription =
				$"±{radiusMs:0.###} ms ({radiusFrames} frames)";
			if (!result.Changed)
			{
				_message.Text =
					$"The current loop is already the best candidate within {radiusDescription}.";
				return;
			}

			string improvement;
			if (double.IsFinite(result.OriginalScore)
				&& result.OriginalScore > 0.0
				&& double.IsFinite(result.SuggestedScore))
			{
				double percent =
					Math.Clamp(
						(1.0
							- (result.SuggestedScore
								/ result.OriginalScore))
							* 100.0,
						0.0,
						100.0);
				improvement =
					$" Estimated discontinuity reduced by {percent:0.#}%.";
			}
			else if (!double.IsFinite(result.OriginalScore)
				&& double.IsFinite(result.SuggestedScore))
			{
				improvement =
					" Found a finite seam where the original candidate contained non-finite PCM.";
			}
			else
			{
				improvement = string.Empty;
			}

			_message.Text =
				$"Suggested loop: frames {result.SuggestedLoop.StartFrame}..{result.SuggestedLoop.EndFrameExclusive} within {radiusDescription}.{improvement} Click Apply to commit.";
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private void RefreshWaveform()
	{
		_waveform.SetPcmData(_sample.PcmData);
		_waveform.SetLoop(_sample.Loop);
		PreviewWaveformLoop(_sample.Loop);

		if (_sample.PcmData is not SamplePcmData pcm)
		{
			_waveform.Height = 120;
			_waveformSummary.Text =
				"No decoded PCM is available for this sample.";
			return;
		}

		_waveform.Height =
			Math.Clamp(
				pcm.ChannelCount * 72.0,
				140.0,
				360.0);

		double durationSeconds =
			pcm.FrameCount / (double)pcm.SampleRate;
		_waveformSummary.Text =
			$"{pcm.ChannelCount} channel{(pcm.ChannelCount == 1 ? string.Empty : "s")} · "
			+ $"{pcm.SampleRate} Hz · "
			+ $"{pcm.FrameCount} frame{(pcm.FrameCount == 1 ? string.Empty : "s")} · "
			+ $"{durationSeconds:0.###} s";
	}

	private void PreviewWaveformLoop(
		SampleLoop loop)
	{
		_loopStart.Text =
			loop.StartFrame.ToString(
				CultureInfo.CurrentCulture);
		_loopEnd.Text =
			loop.EndFrameExclusive.ToString(
				CultureInfo.CurrentCulture);
	}

	private async void CommitWaveformLoop(SampleLoop loop)
	{
		try
		{
			bool changed = loop != _sample.Loop;
			SampleDocumentEditor.UpdateMetadata(_workspace, _sample, loop);
			_loopMode.SelectedItem = loop.Mode;
			PreviewWaveformLoop(loop);
			_message.Text =
				$"Loop range updated to frames {loop.StartFrame}..{loop.EndFrameExclusive}.";
			if (changed)
				await RestartHeldNotesAsync();
		}
		catch (Exception ex)
		{
			_waveform.SetLoop(_sample.Loop);
			PreviewWaveformLoop(_sample.Loop);
			_message.Text = ex.Message;
		}
	}

	private void RefreshDiagnostics()
	{
		_relativePath.Text =
			_sample.Asset?.FullPath
				?? "(pending save into current song)";
		_expectedHash.Text =
			_sample.Asset?.Sha256
				?? _sample.PendingAsset?.Sha256
				?? "(not recorded)";

		try
		{
			ExternalAssetCheck check =
				SampleDocumentEditor.CheckAsset(_workspace, _sample);
			if (_sample.Asset is ExternalAssetReference asset
				&& HeresyModulePath.TrySplit(
					asset.FullPath,
					out string archivePath,
					out string entryPath))
			{
				_resolvedPath.Text = $"{archivePath} → {entryPath}";
			}
			else
			{
				_resolvedPath.Text = "Filesystem";
			}
			_actualHash.Text = check.ActualSha256 ?? "(unavailable)";
			_integrityStatus.Text = check.Status switch
			{
				ExternalAssetStatus.Pending => "Pending — encoded bytes are owned in memory until the song is saved",
				ExternalAssetStatus.Match => "OK — persisted encoding matches recorded SHA-256",
				ExternalAssetStatus.Missing => "Missing — referenced file was not found",
				ExternalAssetStatus.Unhashed => "Unhashed — file exists but no SHA-256 is recorded",
				ExternalAssetStatus.HashMismatch => "Changed — file content differs from recorded SHA-256",
				_ => check.Status.ToString(),
			};
		}
		catch (Exception ex)
		{
			_resolvedPath.Text = "(unavailable)";
			_actualHash.Text = "(unavailable)";
			_integrityStatus.Text = "Unavailable";
			_message.Text = ex.Message;
		}
	}

	private static TextBox ReadOnlyTextBox()
		=> new()
		{
			IsReadOnly = true,
			TextWrapping = TextWrapping.Wrap,
		};

	private static void AddField(
		Grid grid,
		ref int row,
		string label,
		Control control)
	{
		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		TextBlock labelBlock =
			new()
			{
				Text = label,
				Margin = new Thickness(0, 5, 12, 5),
				VerticalAlignment = VerticalAlignment.Center,
			};
		Grid.SetRow(labelBlock, row);
		Grid.SetColumn(labelBlock, 0);
		grid.Children.Add(labelBlock);

		control.Margin = new Thickness(0, 3);
		Grid.SetRow(control, row);
		Grid.SetColumn(control, 1);
		grid.Children.Add(control);
		row++;
	}

	private static void AddFullWidth(
		Grid grid,
		ref int row,
		Control control)
	{
		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		Grid.SetRow(control, row);
		Grid.SetColumn(control, 0);
		Grid.SetColumnSpan(control, 2);
		grid.Children.Add(control);
		row++;
	}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using Heresy.Core.Assets;
using Heresy.Core.Samples;
using Heresy.UserInterface.Documents;
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
	private readonly TextBox _referenceFrequency;
	private readonly ComboBox _loopMode;
	private readonly TextBox _loopStart;
	private readonly TextBox _loopEnd;
	private readonly TextBox _relativePath;
	private readonly TextBox _resolvedPath;
	private readonly TextBlock _integrityStatus;
	private readonly TextBox _expectedHash;
	private readonly TextBox _actualHash;
	private readonly SampleWaveformControl _waveform;
	private readonly TextBlock _waveformSummary;
	private readonly TextBlock _message;

	public SampleEditorDialog(
		DocumentWorkspace workspace,
		SampleDefinition sample)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_sample = sample
			?? throw new ArgumentNullException(nameof(sample));

		Title = $"Sample — {sample.Name}";
		Width = 700;
		Height = 650;
		MinWidth = 560;
		MinHeight = 520;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		_name = new TextBox { Text = sample.Name };
		_referenceFrequency =
			new TextBox
			{
				Text = sample.ReferenceFrequencyHz.ToString(
					"G17",
					CultureInfo.CurrentCulture),
			};
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

		AddField(form, ref row, "Name", _name);
		AddField(form, ref row, "Reference frequency (Hz)", _referenceFrequency);
		AddField(form, ref row, "Loop mode", _loopMode);
		AddField(form, ref row, "Loop start frame", _loopStart);
		AddField(form, ref row, "Loop end frame (exclusive)", _loopEnd);

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

		StackPanel content =
			new()
			{
				Margin = new Thickness(18),
				Spacing = 12,
			};
		content.Children.Add(
			new ScrollViewer
			{
				Content = form,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			});
		content.Children.Add(buttons);
		return content;
	}

	private void ApplyMetadata()
	{
		try
		{
			string name = (_name.Text ?? string.Empty).Trim();
			if (name.Length == 0)
				throw new ArgumentException("Sample name must not be empty.");

			if (!double.TryParse(
				_referenceFrequency.Text,
				NumberStyles.Float,
				CultureInfo.CurrentCulture,
				out double referenceFrequency))
			{
				throw new ArgumentException("Reference frequency is not a valid number.");
			}

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
				throw new ArgumentException("Loop frame positions must be integers.");
			}

			SampleLoopMode loopMode =
				_loopMode.SelectedItem is SampleLoopMode selectedMode
					? selectedMode
					: SampleLoopMode.None;
			SampleLoop loop = new(loopMode, loopStart, loopEnd);

			if (!string.Equals(name, _sample.Name, StringComparison.Ordinal))
				SongTreeEditor.RenameObject(_workspace.Document, _sample.Id, name);
			SampleDocumentEditor.UpdateMetadata(
				_workspace,
				_sample,
				referenceFrequency,
				loop);
			_waveform.SetLoop(loop);
			PreviewWaveformLoop(loop);

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

	private void CommitWaveformLoop(
		SampleLoop loop)
	{
		try
		{
			SampleDocumentEditor.UpdateMetadata(
				_workspace,
				_sample,
				_sample.ReferenceFrequencyHz,
				loop);
			_loopMode.SelectedItem = loop.Mode;
			PreviewWaveformLoop(loop);
			_message.Text =
				$"Loop range updated to frames {loop.StartFrame}..{loop.EndFrameExclusive}.";
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

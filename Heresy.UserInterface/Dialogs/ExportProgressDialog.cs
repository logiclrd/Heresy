using System;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using Heresy.Render.File;

namespace Heresy.UserInterface.Dialogs;

/// <summary>
/// Modeless export progress. Reported durations are rendered musical time,
/// never a guessed wall-clock ETA or a precomputed duration of a script.
/// The Cancel button only signals a token; the PCM worker stops at its
/// next safe block and the owning service deletes its temporary file.
/// </summary>
public sealed class ExportProgressDialog : Window
{
	private readonly CancellationTokenSource _cancellation;
	private readonly TextBlock _message = new();
	private readonly ProgressBar _bar = new()
	{
		IsIndeterminate = true,
		Minimum = 0,
		Maximum = 1,
		Height = 15,
	};
	private readonly Button _cancel = new()
	{
		Content = "Cancel",
		MinWidth = 100,
		IsCancel = true,
	};

	public ExportProgressDialog(string filename,
		CancellationTokenSource cancellation)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filename);
		_cancellation = cancellation
			?? throw new ArgumentNullException(nameof(cancellation));
		Title = "Rendering Audio";
		Width = 485;
		Height = 200;
		MinWidth = 400;
		CanResize = false;
		ShowInTaskbar = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		_message.Text = $"Rendering {filename} — 00:00:00.000 musical time";
		_message.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
		_cancel.Click += (_, _) => Cancel();
		Closing += (_, _) => Cancel();

		StackPanel actions = new()
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Children = { _cancel },
		};
		Content = DialogActionLayout.Create(
			new StackPanel
			{
				Spacing = 16,
				Children = { _message, _bar },
			},
			actions,
			new Thickness(18));
	}

	/// <summary>Bound UI work to one outstanding dispatcher post, even
	/// when the offline PCM worker emits thousands of fast render blocks.
	/// The most recent musical-time position wins; no per-block UI queue
	/// can grow with the length or playback rate of a song.</summary>
	public IProgress<OfflineRenderProgress> CreateProgressReporter()
		=> new CoalescingProgress(this);

	private sealed class CoalescingProgress(ExportProgressDialog window)
		: IProgress<OfflineRenderProgress>
	{
		private readonly object _gate = new();
		private OfflineRenderProgress _latest;
		private bool _scheduled;

		public void Report(OfflineRenderProgress update)
		{
			bool post;
			lock (_gate)
			{
				_latest = update;
				post = !_scheduled;
				_scheduled = true;
			}
			if (post)
				Dispatcher.UIThread.Post(Publish);
		}

		private void Publish()
		{
			OfflineRenderProgress update;
			lock (_gate)
			{
				update = _latest;
				_scheduled = false;
			}
			if (window.IsVisible)
				window.Update(update);
		}
	}

	public void Update(OfflineRenderProgress update)
	{
		if (_cancellation.IsCancellationRequested)
			return;

		string time = update.RenderedMusicalTime.ToString(@"hh\:mm\:ss\.fff");
		switch (update.Phase)
		{
			case OfflineRenderPhase.LogicalBody:
				_message.Text = $"Rendered {time} of musical time";
				if (update.KnownLogicalFrameCount is long total && total > 0)
				{
					_bar.IsIndeterminate = false;
					_bar.Value = Math.Clamp(
						(double)update.LogicalFramesRendered / total, 0, 1);
				}
				else
					_bar.IsIndeterminate = true;
				break;
			case OfflineRenderPhase.ReleaseTail:
				_message.Text = $"Rendered {time}; finishing release tail "
					+ update.RenderedTailTime.ToString(@"hh\:mm\:ss\.fff");
				_bar.IsIndeterminate = true;
				break;
			case OfflineRenderPhase.Completed:
				_message.Text = $"Completed {time} musical time";
				_bar.IsIndeterminate = false;
				_bar.Value = 1;
				break;
		}
	}

	private void Cancel()
	{
		if (_cancellation.IsCancellationRequested)
			return;
		_cancellation.Cancel();
		_cancel.IsEnabled = false;
		_message.Text = "Canceling after the current render block…";
		_bar.IsIndeterminate = true;
	}
}

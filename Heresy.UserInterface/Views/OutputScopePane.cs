using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Heresy.Render.Realtime;

namespace Heresy.UserInterface.Views;

/// <summary>Selectable speaker-channel display for the post-SDL-queue
/// scope. Multi-speaker sessions start with FL+FR; the other speakers
/// remain individually accessible without a confusing 8-line overlay.</summary>
public sealed record OutputScopeChannelChoice(
	string Name, int FirstChannel, int SecondChannel = -1)
{
	public override string ToString() => Name;
}

public sealed class OutputScopePane : UserControl
{
	private readonly AudioOutputWaveformCapture _capture;
	private readonly DispatcherTimer _timer;
	private readonly ComboBox _channels = new()
	{
		MinWidth = 125,
		MaxWidth = 230,
		HorizontalAlignment = HorizontalAlignment.Right,
	};
	private readonly TextBlock _status = new()
	{
		Text = "Waiting for SDL audio...",
		VerticalAlignment = VerticalAlignment.Center,
		Margin = new Thickness(8, 0),
	};
	private long _lastSessionId = -1;
	private int _lastChannelCount = -1;

	public OutputScopePane(AudioOutputWaveformCapture capture)
	{
		_capture = capture ?? throw new ArgumentNullException(nameof(capture));
		Graph = new OutputScopeWaveformGraph(capture);
		DragHandle = new Border
		{
			Padding = new Thickness(10, 5),
			Background = new SolidColorBrush(Color.FromRgb(48, 55, 65)),
			Child = new TextBlock
			{
				Text = "Output scope  •  drag to a window edge",
				FontWeight = FontWeight.SemiBold,
				Foreground = Brushes.White,
			},
		};
		Button close = new()
		{
			Content = "×",
			Padding = new Thickness(8, 2),
		};
		close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

		DockPanel header = new()
		{
			LastChildFill = true,
			Background = Brushes.DimGray,
		};
		DockPanel.SetDock(close, Dock.Right);
		header.Children.Add(close);
		DockPanel.SetDock(_channels, Dock.Right);
		header.Children.Add(_channels);
		header.Children.Add(DragHandle);

		_channels.SelectionChanged += (_, _) =>
		{
			if (_channels.SelectedItem is OutputScopeChannelChoice selection)
				Graph.SetChannels(selection.FirstChannel, selection.SecondChannel);
		};

		Grid content = new();
		content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		content.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
		content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		content.Children.Add(header);
		Grid.SetRow(Graph, 1);
		content.Children.Add(Graph);
		Grid.SetRow(_status, 2);
		content.Children.Add(_status);
		Content = content;

		_timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33),
			DispatcherPriority.Background, (_, _) => RefreshScope());
		UpdateChoices();
	}

	public Border DragHandle { get; }
	public OutputScopeWaveformGraph Graph { get; }
	public event EventHandler? CloseRequested;

	public void SetActive(bool active)
	{
		_capture.SetEnabled(active);
		if (active)
		{
			RefreshScope();
			_timer.Start();
		}
		else
		{
			_timer.Stop();
			Graph.InvalidateVisual();
		}
	}

	public void Shutdown() => SetActive(false);

	private void RefreshScope()
	{
		long sessionId = _capture.CurrentSessionId;
		int channels = _capture.CurrentChannelCount;
		if (sessionId != _lastSessionId || channels != _lastChannelCount)
			UpdateChoices();

		_status.Text = sessionId == 0
			? "Waiting for SDL audio..."
			: $"{channels} output channel(s)  •  newest PCM frames, 1 sample/pixel";
		Graph.InvalidateVisual();
	}

	private void UpdateChoices()
	{
		_lastSessionId = _capture.CurrentSessionId;
		_lastChannelCount = _capture.CurrentChannelCount;
		int channels = _lastChannelCount;

		List<OutputScopeChannelChoice> choices = [];
		if (channels == 1)
			choices.Add(new("Mono", 0));
		else if (channels >= 2)
		{
			choices.Add(new("Front L + R", 0, 1));
			for (int i = 0; i < channels; i++)
				choices.Add(new(SpeakerName(channels, i), i));
		}
		else
			choices.Add(new("Awaiting output", 0));
		_channels.ItemsSource = choices;
		_channels.SelectedIndex = 0;
	}

	public static string SpeakerName(int channels, int channel)
	{
		if (channel < 0 || channel >= channels)
			throw new ArgumentOutOfRangeException(nameof(channel));
		if (channels == 1)
			return "Mono";
		if (channel == 0)
			return "Front L";
		if (channel == 1)
			return "Front R";
		if (channels is 6 or 8)
		{
			return channel switch
			{
				2 => "Center",
				3 => "LFE",
				4 => "Rear L",
				5 => "Rear R",
				6 => "Side L",
				7 => "Side R",
				_ => $"Channel {channel + 1}",
			};
		}
		return $"Channel {channel + 1}";
	}
}

/// <summary>
/// Direct time-domain view, precisely one actual output frame for each
/// physical horizontal pixel column. No min/max aggregation or envelope.
/// </summary>
public sealed class OutputScopeWaveformGraph : Control
{
	private static readonly Pen LeftPen =
		new(Brushes.LightSkyBlue, 1.0);
	private static readonly Pen RightPen =
		new(Brushes.Orange, 1.0);
	private static readonly Pen AxisPen =
		new(new SolidColorBrush(Color.FromRgb(65, 69, 77)), 1.0);
	private readonly AudioOutputWaveformCapture _capture;
	private float[] _first = [];
	private float[] _second = [];
	private int _firstChannel;
	private int _secondChannel = -1;

	public OutputScopeWaveformGraph(AudioOutputWaveformCapture capture)
	{
		_capture = capture ?? throw new ArgumentNullException(nameof(capture));
		MinHeight = 60;
		ClipToBounds = true;
	}

	public int FirstChannel => _firstChannel;
	public int SecondChannel => _secondChannel;

	public void SetChannels(int first, int second)
	{
		_firstChannel = first;
		_secondChannel = second;
		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		if (Bounds.Width <= 0 || Bounds.Height <= 0)
			return;

		double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
		if (!double.IsFinite(scaling) || scaling <= 0.0)
			scaling = 1.0;
		int columns = Math.Clamp(
			(int)Math.Ceiling(Bounds.Width * scaling),
			1, AudioOutputWaveformCapture.CapacityFrames);
		if (_first.Length != columns)
		{
			_first = new float[columns];
			_second = new float[columns];
		}
		_capture.ReadLatest(_first, _second, _firstChannel, _secondChannel);

		context.DrawRectangle(Brushes.Black, null, Bounds);
		double center = Bounds.Height / 2.0;
		context.DrawLine(AxisPen, new Point(0, center),
			new Point(Bounds.Width, center));
		DrawTrace(context, _first, LeftPen, center, scaling);
		DrawTrace(context, _second, RightPen, center, scaling);
	}

	private void DrawTrace(DrawingContext context, float[] samples,
		Pen pen, double center, double scaling)
	{
		bool hasPrevious = false;
		Point previous = default;
		double amplitude = Math.Max(1.0, center - 4.0);
		for (int i = 0; i < samples.Length; i++)
		{
			if (!float.IsFinite(samples[i]))
			{
				hasPrevious = false;
				continue;
			}
			double clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
			Point next = new((i + 0.5) / scaling,
				center - (clamped * amplitude));
			if (hasPrevious)
				context.DrawLine(pen, previous, next);
			previous = next;
			hasPrevious = true;
		}
	}
}

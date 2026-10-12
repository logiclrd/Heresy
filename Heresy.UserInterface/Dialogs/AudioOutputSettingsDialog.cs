using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Render.Configuration;
using Heresy.Core.Filters;

namespace Heresy.UserInterface.Dialogs;

/// <summary>Per-session output settings. Accept produces an immutable
/// render snapshot, never changing an active audio callback or song document.
/// Physical speaker order is the order of rows in the dialog.</summary>
public sealed class AudioOutputSettingsDialog : Window
{
	private sealed record SpeakerRow(
		TextBox X, TextBox Y, TextBox Z, TextBox Importance,
		ComboBox Filter, TextBox Cutoff, Button Edit)
    {
        public FilterInstance? Inline { get; set; }
    }

	private readonly TextBox _sampleRate;
	private readonly ComboBox _layout;
	private readonly StackPanel _speakers = new() { Spacing = 7 };
	private readonly TextBlock _error = new()
	{
		Foreground = Brushes.IndianRed,
		TextWrapping = TextWrapping.Wrap,
	};
	private readonly List<SpeakerRow> _rows = [];

	public AudioOutputSettingsDialog(RenderConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		Title = "Audio Output Configuration";
		Width = 820;
		Height = 620;
		MinWidth = 710;
		MinHeight = 430;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		_sampleRate = new TextBox
		{
			Text = configuration.SampleRate.ToString(CultureInfo.InvariantCulture),
			Width = 110,
		};
		_layout = new ComboBox
		{
			ItemsSource = new[]
			{
				"Mono (1 speaker)", "Stereo (2 speakers)",
				"Surround 5.1 (6 feeds)", "Surround 7.1 (8 feeds)",
			},
			Width = 230,
			SelectedIndex = configuration.OutputChannelCount switch
			{
				1 => 0, 2 => 1, 6 => 2, 8 => 3, _ => -1,
			},
		};

		StackPanel header = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 12,
			Children =
			{
				new TextBlock
				{
					Text = "Sample rate (Hz)",
					VerticalAlignment = VerticalAlignment.Center,
				},
				_sampleRate,
				new TextBlock
				{
					Text = "Speaker layout",
					VerticalAlignment = VerticalAlignment.Center,
				},
				_layout,
			},
		};
		TextBlock note = new()
		{
			Text = "Rows are in physical output order. Position is X/Y/Z. "
				+ "Importance weights positional routing. "
				+ "LFE is a normal output feed (no automatic bass management). "
				+ "Legacy RC low/high-pass preserves saved output settings exactly; "
                + "choose Filter chain and Edit chain to use the reusable serial DSP engine.",
			TextWrapping = TextWrapping.Wrap,
		};
		// Give the speaker table the remaining space instead of
		// placing an unbounded ScrollViewer inside a StackPanel. This
		// keeps 7.1 controls and the action footer reachable on a short
		// desktop window or when the application font is enlarged.
		Grid main = new()
		{
			RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
			RowSpacing = 12,
		};
		ScrollViewer scroll = new()
		{
			Content = _speakers,
			VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
		};
		Grid.SetRow(header, 0);
		Grid.SetRow(note, 1);
		Grid.SetRow(scroll, 2);
		Grid.SetRow(_error, 3);
		main.Children.Add(header);
		main.Children.Add(note);
		main.Children.Add(scroll);
		main.Children.Add(_error);

		Button accept = new()
		{
			Content = "Apply",
			MinWidth = 95,
		};
		Button cancel = new()
		{
			Content = "Cancel",
			MinWidth = 95,
		};
		accept.Click += (_, _) => Accept();
		cancel.Click += (_, _) => Close((RenderConfiguration?)null);
		DialogActionLayout.ConfigureButtons(accept, cancel);
		StackPanel actions = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			Children = { cancel, accept },
		};
		Content = DialogActionLayout.Create(main, actions, new Thickness(16));
		FillRows(configuration);

		_layout.SelectionChanged += (_, _) =>
		{
			int channels = _layout.SelectedIndex switch
			{
				0 => 1, 1 => 2, 2 => 6, 3 => 8, _ => 0,
			};
			if (channels == 0)
				return;
			// Only changing the layout discards the old speaker rows.
			// Editing rate or any individual position does not.
			FillRows(AudioOutputSettings.Preset(
				ReadSampleRateOrDefault(), channels));
		};
	}

	private int ReadSampleRateOrDefault()
		=> int.TryParse(_sampleRate.Text, NumberStyles.Integer,
			CultureInfo.InvariantCulture, out int rate)
			&& rate >= 8000 && rate <= 384000 ? rate : 48000;

	private static TextBox Number(double value, int width = 64)
		=> new()
		{
			Text = value.ToString("G9", CultureInfo.InvariantCulture),
			Width = width,
		};

	private void FillRows(RenderConfiguration configuration)
	{
		_speakers.Children.Clear();
		_rows.Clear();
		_speakers.Children.Add(new TextBlock
		{
			Text = "Speaker                 X           Y          Z       Importance     Filter           Cutoff Hz     Chain",
		});
		for (int index = 0; index < configuration.OutputChannelCount; index++)
		{
			int speakerIndex = index;
			OutputChannelConfiguration channel = configuration.OutputChannels[index];
			TextBox x = Number(channel.Position.X);
			TextBox y = Number(channel.Position.Y);
			TextBox z = Number(channel.Position.Z);
			TextBox importance = Number(channel.PositionalImportance, 82);
			ComboBox filter = new()
			{
				ItemsSource = new[] { "None", "LowPass (RC)", "HighPass (RC)", "Filter chain" },
				SelectedIndex = (int)channel.FilterType,
				Width = 138,
			};
			TextBox cutoff = Number(channel.CutoffHz ?? 1000, 82);
            Button edit = new() { Content = "Edit chain...", MinWidth = 93 };
            SpeakerRow state = new(x, y, z, importance, filter, cutoff, edit)
            {
                Inline = channel.Filter,
            };
            _rows.Add(state);
            void Refresh()
            {
                bool custom = filter.SelectedIndex == (int)OutputFilterType.Chain;
                edit.IsEnabled = custom;
                cutoff.IsEnabled = filter.SelectedIndex is 1 or 2;
            }
            filter.SelectionChanged += (_, _) => Refresh();
            edit.Click += async (_, _) =>
            {
                try
                {
                    FilterInstance? proposed = await new SpeakerFilterChainDialog(
                        state.Inline, ReadSampleRateOrDefault(), speakerIndex)
                        .ShowDialog<FilterInstance?>(this);
                    if (proposed is not null)
                        state.Inline = proposed;
                }
                catch (Exception ex) when (ex is ArgumentException
                    or InvalidOperationException)
                {
                    _error.Text = ex.Message;
                }
            };
            Refresh();
			StackPanel row = new()
			{
				Orientation = Orientation.Horizontal,
				Spacing = 8,
				Children =
				{
					new TextBlock
					{
						Text = AudioOutputSettings.SpeakerName(
							configuration.OutputChannelCount, index),
						Width = 100,
						VerticalAlignment = VerticalAlignment.Center,
					},
					x, y, z, importance, filter, cutoff, edit,
				},
			};
			_speakers.Children.Add(row);
		}
	}

	private void Accept()
	{
		try
		{
			if (!int.TryParse(_sampleRate.Text, NumberStyles.Integer,
				CultureInfo.InvariantCulture, out int rate)
				|| rate < 8000 || rate > 384000)
				throw new ArgumentException(
					"Sample rate must be between 8000 and 384000 Hz.");

			static double Read(TextBox input, string field)
			{
				if (!double.TryParse(input.Text, NumberStyles.Float,
					CultureInfo.InvariantCulture, out double value)
					|| !double.IsFinite(value))
					throw new ArgumentException(
						$"{field} must be a finite number (use '.' for decimals).");
				return value;
			}

			List<OutputChannelConfiguration> outputs = [];
			foreach ((SpeakerRow row, int index) in EnumerateRows())
			{
				double x = Read(row.X, $"Speaker {index + 1} X");
				double y = Read(row.Y, $"Speaker {index + 1} Y");
				double z = Read(row.Z, $"Speaker {index + 1} Z");
				double importance = Read(row.Importance,
					$"Speaker {index + 1} importance");
				if (importance < 0)
					throw new ArgumentException("Positional importance cannot be negative.");
				OutputFilterType filter = row.Filter.SelectedIndex switch
				{
					0 => OutputFilterType.None,
					1 => OutputFilterType.LowPass,
					2 => OutputFilterType.HighPass,
                    3 => OutputFilterType.Chain,
					_ => throw new ArgumentException("Choose a speaker filter."),
				};
				double? cutoff = null;
				if (filter is OutputFilterType.LowPass or OutputFilterType.HighPass)
				{
					cutoff = Read(row.Cutoff, $"Speaker {index + 1} cutoff");
					if (cutoff <= 0 || cutoff >= rate / 2.0)
						throw new ArgumentException(
							$"Speaker {index + 1} cutoff must be above zero "
							+ "and below the Nyquist frequency.");
				}
				FilterInstance? chain = filter == OutputFilterType.Chain
                    ? SpeakerFilterChainText.Parse(
                        SpeakerFilterChainText.Format(row.Inline ??
                            SpeakerFilterChainText.Parse(
                                SpeakerFilterChainText.Default, rate, index)),
                        rate, index)
                    : null;
				outputs.Add(new OutputChannelConfiguration(
					new Vector3((float)x, (float)y, (float)z),
					importance, filter, cutoff, chain));
			}
			Close(new RenderConfiguration(rate, outputs));
		}
		catch (Exception ex) when (ex is ArgumentException
			or OverflowException)
		{
			_error.Text = ex.Message;
		}
	}

	private IEnumerable<(SpeakerRow Row, int Index)> EnumerateRows()
	{
		for (int i = 0; i < _rows.Count; i++)
			yield return (_rows[i], i);
	}
}

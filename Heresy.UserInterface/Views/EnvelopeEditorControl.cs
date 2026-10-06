using System;
using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Envelopes;
using Heresy.UserInterface.Documents;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Main-workspace editor for an ADSR envelope. Duration fields are presented in
/// seconds while Core continues to store TimeSpan values. Sustain is deliberately
/// an unrestricted finite scalar rather than a 0..1 percentage.
/// </summary>
public sealed class EnvelopeEditorControl : UserControl
{
	private readonly DocumentWorkspace _workspace;
	private readonly AdsrEnvelopeDefinition _envelope;
	private readonly Action _close;
	private readonly Action<string> _changed;
	private readonly TextBox _attack;
	private readonly TextBox _decay;
	private readonly TextBox _sustain;
	private readonly TextBox _release;
	private readonly TextBlock _message =
		new()
		{
			TextWrapping = TextWrapping.Wrap,
		};

	public EnvelopeEditorControl(
		DocumentWorkspace workspace,
		AdsrEnvelopeDefinition envelope,
		Action close,
		Action<string> changed)
	{
		_workspace = workspace
			?? throw new ArgumentNullException(nameof(workspace));
		_envelope = envelope
			?? throw new ArgumentNullException(nameof(envelope));
		_close = close
			?? throw new ArgumentNullException(nameof(close));
		_changed = changed
			?? throw new ArgumentNullException(nameof(changed));

		_attack = DurationBox(envelope.Attack);
		_decay = DurationBox(envelope.Decay);
		_sustain = ScalarBox(envelope.SustainLevel);
		_release = DurationBox(envelope.Release);

		Content = BuildContent();
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
				Text = _envelope.Name,
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

		Button apply =
			new()
			{
				Content = "Apply envelope",
				MinWidth = 130,
			};
		apply.Click += (_, _) => Apply();

		Grid fields =
			new()
			{
				Margin = new Thickness(18, 14),
				ColumnSpacing = 10,
				RowSpacing = 10,
				MaxWidth = 640,
				HorizontalAlignment = HorizontalAlignment.Left,
			};
		fields.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(180)));
		fields.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(170)));
		fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

		AddField(fields, "Attack (seconds)", _attack, row: 0);
		AddField(fields, "Decay (seconds)", _decay, row: 1);
		AddField(fields, "Sustain scalar", _sustain, row: 2);
		AddField(fields, "Release (seconds)", _release, row: 3);
		Grid.SetRow(apply, 4);
		Grid.SetColumn(apply, 1);
		fields.Children.Add(apply);

		TextBlock explanation =
			new()
			{
				Text =
					"Attack, decay and release must be non-negative. Sustain is a finite scalar and is intentionally not limited to 0…1; negative or above-unity values remain valid Heresy envelope data.",
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 720,
				Margin = new Thickness(18, 0, 18, 12),
			};

		StackPanel body =
			new()
			{
				Children =
				{
					fields,
					explanation,
				},
			};

		Border messageBorder =
			new()
			{
				Padding = new Thickness(10, 5),
				Child = _message,
			};

		DockPanel root = new();
		DockPanel.SetDock(header, Dock.Top);
		DockPanel.SetDock(messageBorder, Dock.Bottom);
		root.Children.Add(header);
		root.Children.Add(messageBorder);
		root.Children.Add(
			new ScrollViewer
			{
				Content = body,
			});
		return root;
	}

	private void Apply()
	{
		try
		{
			TimeSpan attack =
				ParseDuration(_attack, "Attack");
			TimeSpan decay =
				ParseDuration(_decay, "Decay");
			double sustain =
				ParseFiniteScalar(_sustain, "Sustain");
			TimeSpan release =
				ParseDuration(_release, "Release");

			EnvelopeDocumentEditor.UpdateAdsrEnvelope(
				_workspace,
				_envelope,
				attack,
				decay,
				sustain,
				release);

			_attack.Text = FormatDuration(_envelope.Attack);
			_decay.Text = FormatDuration(_envelope.Decay);
			_sustain.Text = FormatScalar(_envelope.SustainLevel);
			_release.Text = FormatDuration(_envelope.Release);
			_message.Text = "Envelope updated.";
			_changed($"Updated envelope {_envelope.Name}");
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private static TextBox DurationBox(TimeSpan value)
		=> new()
		{
			Text = FormatDuration(value),
			Width = 160,
		};

	private static TextBox ScalarBox(double value)
		=> new()
		{
			Text = FormatScalar(value),
			Width = 160,
		};

	private static string FormatDuration(TimeSpan value)
		=> value.TotalSeconds.ToString(
			"G17",
			CultureInfo.CurrentCulture);

	private static string FormatScalar(double value)
		=> value.ToString(
			"G17",
			CultureInfo.CurrentCulture);

	private static TimeSpan ParseDuration(
		TextBox box,
		string label)
	{
		double seconds =
			ParseFiniteScalar(box, label);
		if (seconds < 0.0)
		{
			throw new ArgumentException(
				$"{label} must be non-negative.");
		}

		try
		{
			return TimeSpan.FromSeconds(seconds);
		}
		catch (OverflowException ex)
		{
			throw new ArgumentException(
				$"{label} is too large to represent as a duration.",
				ex);
		}
	}

	private static double ParseFiniteScalar(
		TextBox box,
		string label)
	{
		if (!double.TryParse(
			box.Text,
			NumberStyles.Float,
			CultureInfo.CurrentCulture,
			out double value)
			|| double.IsNaN(value)
			|| double.IsInfinity(value))
		{
			throw new ArgumentException(
				$"{label} must be a finite number.");
		}

		return value;
	}

	private static void AddField(
		Grid grid,
		string label,
		Control field,
		int row)
	{
		TextBlock text =
			new()
			{
				Text = label,
				VerticalAlignment = VerticalAlignment.Center,
			};
		Grid.SetRow(text, row);
		Grid.SetColumn(text, 0);
		grid.Children.Add(text);

		Grid.SetRow(field, row);
		Grid.SetColumn(field, 1);
		grid.Children.Add(field);
	}
}

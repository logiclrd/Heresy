using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Heresy.Core.Objects;
using Heresy.Core.Patterns;

namespace Heresy.UserInterface.Dialogs;

public sealed record PatternNoteEditResult(PatternNoteEntry? Note);

public sealed class PatternNoteEditorDialog : Window
{
	private enum NoteKind
	{
		Empty,
		Start,
		Off,
		Cut,
	}

	private sealed record SourceOption(ObjectId Id, string DisplayName)
	{
		public override string ToString() => DisplayName;
	}

	private readonly ComboBox _kind;
	private readonly ComboBox _source;
	private readonly TextBox _pitch;
	private readonly TextBox _speed;
	private readonly CheckBox _mixdown;
	private readonly TextBlock _message;

	public PatternNoteEditorDialog(
		SongDocument document,
		int row,
		int channel,
		PatternNoteEntry? note)
	{
		ArgumentNullException.ThrowIfNull(document);

		Title = $"Pattern cell — row {row}, channel {channel + 1}";
		Width = 500;
		Height = 390;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		_kind =
			new ComboBox
			{
				ItemsSource = Enum.GetValues<NoteKind>(),
				SelectedItem = GetKind(note),
			};

		List<SourceOption> sources =
			document.Objects.Values
				.Where(IsSoundSource)
				.OrderBy(songObject => songObject.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(songObject => songObject.Id.Value)
				.Select(songObject =>
					new SourceOption(
						songObject.Id,
						$"{songObject.Name} <{songObject.Id.Value}>"))
				.ToList();

		if (note is StartPatternNote start
			&& sources.All(option => option.Id != start.SourceId))
		{
			string fallback =
				document.Tombstones.TryGetValue(
					start.SourceId,
					out ObjectTombstone? tombstone)
					? $"⚠ {tombstone.LastKnownName} <{start.SourceId.Value}>"
					: $"⚠ <{start.SourceId.Value}>";
			sources.Add(new SourceOption(start.SourceId, fallback));
		}

		_source =
			new ComboBox
			{
				ItemsSource = sources,
				SelectedItem = note is StartPatternNote selectedStart
					? sources.FirstOrDefault(option => option.Id == selectedStart.SourceId)
					: sources.FirstOrDefault(),
			};
		_pitch =
			new TextBox
			{
				Text = (note as StartPatternNote)?.PitchMultiplier.ToString(
					"G17",
					CultureInfo.CurrentCulture)
					?? "1",
			};
		_speed =
			new TextBox
			{
				Text = (note as StartPatternNote)?.PlaybackSpeedMultiplier.ToString(
					"G17",
					CultureInfo.CurrentCulture)
					?? "1",
			};
		_mixdown =
			new CheckBox
			{
				Content = "Mix down nested source",
				IsChecked = (note as StartPatternNote)?.Mixdown ?? false,
			};
		_message = new TextBlock();

		_kind.SelectionChanged += (_, _) => UpdateStartFields();
		UpdateStartFields();

		Content = BuildContent();
	}

	private Control BuildContent()
	{
		Grid form = new();
		form.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
		form.ColumnDefinitions.Add(
			new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

		int row = 0;
		AddField(form, ref row, "Note", _kind);
		AddField(form, ref row, "Source", _source);
		AddField(form, ref row, "Pitch multiplier", _pitch);
		AddField(form, ref row, "Playback-speed multiplier", _speed);
		AddField(form, ref row, string.Empty, _mixdown);

		Button cancel = new() { Content = "Cancel", MinWidth = 90 };
		cancel.Click += (_, _) => Close(null);

		Button apply = new() { Content = "Apply", MinWidth = 90 };
		apply.Click += (_, _) => Apply();

		StackPanel buttons =
			new()
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				Spacing = 8,
			};
		buttons.Children.Add(cancel);
		buttons.Children.Add(apply);

		StackPanel content =
			new()
			{
				Margin = new Thickness(18),
				Spacing = 12,
			};
		content.Children.Add(form);
		content.Children.Add(_message);
		content.Children.Add(buttons);
		return content;
	}

	private void Apply()
	{
		try
		{
			NoteKind kind =
				_kind.SelectedItem is NoteKind selected
					? selected
					: NoteKind.Empty;

			PatternNoteEntry? note = kind switch
			{
				NoteKind.Empty => null,
				NoteKind.Off => new PatternNoteOff(),
				NoteKind.Cut => new PatternNoteCut(),
				NoteKind.Start => BuildStartNote(),
				_ => throw new InvalidOperationException(),
			};

			Close(new PatternNoteEditResult(note));
		}
		catch (Exception ex)
		{
			_message.Text = ex.Message;
		}
	}

	private StartPatternNote BuildStartNote()
	{
		if (_source.SelectedItem is not SourceOption source)
			throw new InvalidOperationException("Choose a source object.");

		if (!double.TryParse(
			_pitch.Text,
			NumberStyles.Float,
			CultureInfo.CurrentCulture,
			out double pitch))
		{
			throw new ArgumentException("Pitch multiplier is not a valid number.");
		}

		if (!double.TryParse(
			_speed.Text,
			NumberStyles.Float,
			CultureInfo.CurrentCulture,
			out double speed))
		{
			throw new ArgumentException(
				"Playback-speed multiplier is not a valid number.");
		}

		return new StartPatternNote(
			source.Id,
			pitch,
			speed,
			_mixdown.IsChecked == true);
	}

	private void UpdateStartFields()
	{
		bool enabled =
			_kind.SelectedItem is NoteKind kind
				&& kind == NoteKind.Start;
		_source.IsEnabled = enabled;
		_pitch.IsEnabled = enabled;
		_speed.IsEnabled = enabled;
		_mixdown.IsEnabled = enabled;
	}

	private static NoteKind GetKind(PatternNoteEntry? note)
		=> note switch
		{
			StartPatternNote => NoteKind.Start,
			PatternNoteOff => NoteKind.Off,
			PatternNoteCut => NoteKind.Cut,
			_ => NoteKind.Empty,
		};

	private static bool IsSoundSource(SongObject songObject)
		=> songObject.Kind is
			SongObjectKind.Sample
			or SongObjectKind.Instrument
			or SongObjectKind.Pattern
			or SongObjectKind.Sequence;

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
}

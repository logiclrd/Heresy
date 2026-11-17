using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Heresy.UserInterface.Styling;

/// <summary>
/// One presentation of a song-object name. The colour is attached to the
/// name alone, rather than a selectable row or the ComboBox background.
/// Null means an unassigned/missing reference and stays uncoloured.
/// </summary>
public static class SongObjectTypeNameLabel
{
	public static Control Create(
		string text, Color? background,
		bool compact = false)
	{
		TextBlock label = new()
		{
			Text = text,
			Foreground = background is null ? null : Brushes.Black,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center,
		};
		if (background is null)
			return label;
		return new Border
		{
			Background = new SolidColorBrush(background.Value),
			CornerRadius = new CornerRadius(2),
			// Compact selection templates must not inflate Instrument row
			// heights. Selection and popup use exactly this same label.
			Padding = compact ? new Thickness(3, 0) : new Thickness(3, 1),
			Child = label,
			HorizontalAlignment = HorizontalAlignment.Left,
		};
	}
}

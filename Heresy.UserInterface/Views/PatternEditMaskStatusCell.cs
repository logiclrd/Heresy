using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Heresy.Core.Objects;
using Heresy.UserInterface.PatternEditing;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Miniature, fixed-text tracker-cell depiction of the current edit mask.
/// The field bits only affect opacity; no song cell values are displayed.
/// </summary>
public sealed class PatternEditMaskStatusCell : Border
{
	// At 14 DIP font size, the complete 16-DIP right gutter is comfortably
	// larger than one en, and it is measured even when the toolbar scrolls.
	public const double RightGutter = 16;
	public const double DisabledOpacity = 0.6;
	public const double EnabledOpacity = 1.0;

	public TextBlock NoteSegment { get; } = Segment("C-5");
	public TextBlock SourceSegment { get; } = Segment("src");
	public TextBlock VolumeSegment { get; } = Segment("48");
	public TextBlock EffectSegment { get; } = Segment("...");

	public PatternEditMaskStatusCell()
	{
		uint argb = SongSourceColour.FromHue(280);
		Background = new SolidColorBrush(Color.FromArgb(
			(byte)(argb >> 24), (byte)(argb >> 16),
			(byte)(argb >> 8), (byte)argb));
		Padding = new Thickness(5, 2);
		Margin = new Thickness(0, 0, RightGutter, 0);
		VerticalAlignment = VerticalAlignment.Center;
		IsHitTestVisible = false;

		StackPanel cell = new()
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6,
			VerticalAlignment = VerticalAlignment.Center,
		};
		cell.Children.Add(NoteSegment);
		cell.Children.Add(SourceSegment);
		cell.Children.Add(VolumeSegment);
		cell.Children.Add(EffectSegment);
		Child = cell;
		SetMask(PatternEditMask.Default);
	}

	public void SetMask(PatternEditMask mask)
	{
		NoteSegment.Opacity = OpacityFor(mask, PatternEditMask.Note);
		SourceSegment.Opacity = OpacityFor(mask, PatternEditMask.Source);
		VolumeSegment.Opacity = OpacityFor(mask, PatternEditMask.Volume);
		// The effect portion is a fixed visual example, never a mask bit.
		EffectSegment.Opacity = DisabledOpacity;
	}

	private static double OpacityFor(PatternEditMask mask, PatternEditMask bit)
		=> (mask & bit) != 0 ? EnabledOpacity : DisabledOpacity;

	private static TextBlock Segment(string text)
		=> new()
		{
			Text = text,
			FontSize = 14,
			VerticalAlignment = VerticalAlignment.Center,
		};
}

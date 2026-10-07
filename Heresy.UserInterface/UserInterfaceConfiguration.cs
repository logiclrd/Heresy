using Avalonia.Media;

namespace Heresy.UserInterface;

/// <summary>
/// User-interface presentation settings. These defaults intentionally use
/// translucent neutral row bands so the same values blend lighter in dark
/// themes and darker in light themes.
/// </summary>
public sealed class UserInterfaceConfiguration
{
	public Color MajorPatternRowHighlight { get; init; } =
		Color.FromArgb(0x80, 0x80, 0x80, 0x80);

	public Color MinorPatternRowHighlight { get; init; } =
		Color.FromArgb(0x40, 0x80, 0x80, 0x80);

	public Color PatternSelectionHighlight { get; init; } =
		Color.FromArgb(0x60, 0x40, 0x80, 0xFF);
}

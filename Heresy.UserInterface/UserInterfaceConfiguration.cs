using Avalonia.Media;

namespace Heresy.UserInterface;

/// <summary>
/// User-interface presentation settings. These defaults intentionally use
/// translucent neutral row bands so the same values blend lighter in dark
/// themes and darker in light themes.
/// </summary>
public sealed class UserInterfaceConfiguration
{
	/// <summary>Session-only preference. Hides advisory seek hints in
	/// Pattern editor and UI runtime reports, never changes rendering.</summary>
	public bool ShowReplayRequiredSeekHints { get; set; } = true;

	public Color MajorPatternRowHighlight { get; init; } =
		Color.FromArgb(0x80, 0x80, 0x80, 0x80);

	public Color MinorPatternRowHighlight { get; init; } =
		Color.FromArgb(0x40, 0x80, 0x80, 0x80);

	public Color PatternSelectionHighlight { get; init; } =
		Color.FromArgb(0x60, 0x40, 0x80, 0xFF);

	public Color PatternPlaybackRowHighlight { get; init; } =
		Color.FromArgb(0x80, 0x00, 0x80, 0x00);
}

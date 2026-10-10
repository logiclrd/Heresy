using Avalonia.Input;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Tracker playback-follow shortcuts. Physical keypad Decimal (Renoise),
/// Ctrl+F (OpenMPT), and Scroll Lock (MilkyTracker) toggle the same flag.
/// Backquote remains Note Off; the main Period key remains a tracker input.
/// </summary>
public static class PatternPlaybackFollowKeyboard
{
	public static bool IsToggle(
		PhysicalKey key,
		KeyModifiers modifiers)
		=> (key == PhysicalKey.NumPadDecimal
				&& modifiers == KeyModifiers.None)
			|| (key == PhysicalKey.ScrollLock
				&& modifiers == KeyModifiers.None)
			|| (key == PhysicalKey.F
				&& modifiers == KeyModifiers.Control);

	public static bool IsNumpadDecimalText(
		bool numpadDecimalHeld,
		string? text)
		=> numpadDecimalHeld && (text is "." or ",");
}

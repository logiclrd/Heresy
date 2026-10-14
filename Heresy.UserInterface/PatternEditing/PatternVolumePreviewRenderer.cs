using System;
using System.Globalization;

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Heresy.UserInterface.PatternEditing;

/// <summary>
/// Presentation-only two-digit Volume entry. The pending tens digit is drawn
/// with the active field's own focus-outline brush; the provisional units
/// digit retains the committed units digit (or zero for an empty cell).
/// This never mutates a PatternCell or the keyboard input state.
/// </summary>
public static class PatternVolumePreviewRenderer
{
    public static string PendingText(int tensDigit, string committedText)
    {
        if ((uint)tensDigit > 6)
            throw new ArgumentOutOfRangeException(nameof(tensDigit));

        char previousUnits = committedText.Length == 2
            && committedText[1] is >= '0' and <= '9'
                ? committedText[1]
                : '0';
        return tensDigit.ToString(CultureInfo.InvariantCulture)
            + previousUnits;
    }

    public static void ShowPending(
        TextBlock display, int tensDigit, string committedText,
        IBrush focusOutlineBrush)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(committedText);
        ArgumentNullException.ThrowIfNull(focusOutlineBrush);

        string digits = PendingText(tensDigit, committedText);
        // TextBlock's inline model clears plain Text automatically when
        // the first Run is inserted. The unstyled second Run inherits the
        // normal theme foreground (including light/dark variants).
        display.Inlines!.Clear();
        display.Inlines.Add(new Run(digits[..1])
        {
            Foreground = focusOutlineBrush,
        });
        display.Inlines.Add(new Run(digits[1..]));
    }

    public static void Restore(TextBlock display, string committedText)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(committedText);

        display.Inlines!.Clear();
        display.Text = committedText;
    }
}

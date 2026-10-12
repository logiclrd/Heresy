using System;
using System.Globalization;

using Heresy.Core.Instruments;

namespace Heresy.UserInterface.InstrumentEditing;

/// <summary>
/// Shared NNA editor conversion. Form fields specify duration in milliseconds;
/// no draft can reach the document unless duration and policy both validate.
/// </summary>
public static class InstrumentNnaEditorValues
{
    public static string FormatMilliseconds(TimeSpan duration)
        => duration.TotalMilliseconds.ToString("G17", CultureInfo.CurrentCulture);

    public static TimeSpan ParseFadeMilliseconds(string? text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture,
                out double milliseconds)
            || !double.IsFinite(milliseconds)
            || milliseconds <= 0
            || milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
            throw new ArgumentException("NNA Fade duration must be a positive finite number of milliseconds.");
        TimeSpan duration = TimeSpan.FromMilliseconds(milliseconds);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentException("NNA Fade duration must be at least one tick.");
        return duration;
    }

    public static InstrumentNnaSetting ToneSetting(
        InstrumentNnaAction action, string? fadeMilliseconds)
    {
        InstrumentNnaSetting setting = action == InstrumentNnaAction.Fade
            ? InstrumentNnaSetting.Fade(ParseFadeMilliseconds(fadeMilliseconds))
            : new InstrumentNnaSetting(action);
        setting.Validate();
        return setting;
    }
}

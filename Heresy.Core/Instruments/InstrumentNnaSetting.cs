using System;

namespace Heresy.Core.Instruments;

/// <summary>Explicit NNA selection at an Instrument layer; Inherit keeps
/// the child's note-start policy, including its previously captured fade.</summary>
public enum InstrumentNnaAction
{
    Inherit = 0,
    Cut = 1,
    Continue = 2,
    Off = 3,
    Fade = 4,
}

/// <summary>Data-driven Instrument's outermost blanket override.
— UseToneNna delegates to its selected ToneSpecification.</summary>
public enum InstrumentNnaOverride
{
    UseToneNna = 0,
    Cut = 1,
    Continue = 2,
    Off = 3,
    Fade = 4,
}

/// <summary>Validated, immutable action with optional positive Fade duration.
/// The default struct and old song documents mean Inherit.</summary>
public readonly record struct InstrumentNnaSetting(
    InstrumentNnaAction Action = InstrumentNnaAction.Inherit,
    TimeSpan? FadeDuration = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Action))
            throw new ArgumentOutOfRangeException(nameof(Action));
        if (Action == InstrumentNnaAction.Fade)
        {
            if (FadeDuration is not TimeSpan duration || duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(FadeDuration),
                    "NNA Fade requires a positive duration.");
        }
        else if (FadeDuration is not null)
            throw new ArgumentException(
                "Only an explicit Fade NNA may specify its duration.", nameof(FadeDuration));
    }

    public static InstrumentNnaSetting Fade(TimeSpan duration)
    {
        InstrumentNnaSetting setting = new(InstrumentNnaAction.Fade, duration);
        setting.Validate();
        return setting;
    }
}

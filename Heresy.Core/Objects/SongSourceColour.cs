using System;
using System.Security.Cryptography;

namespace Heresy.Core.Objects;

/// <summary>
/// One portable ARGB source tint. A is fixed at 20% (0x33), HSV saturation
/// at 75% and value at 100%; hue is independently uniform at creation.
/// This Core value is visual metadata only. UI brushes are not persisted.
/// </summary>
public static class SongSourceColour
{
    public const byte Alpha = 0x33;

    /// <summary>Cryptographically uniform discrete hue, without seeding
    /// from ObjectId or touching the playback random generator.</summary>
    public static uint CreateRandom()
        => FromHue(RandomNumberGenerator.GetInt32(0, 360_000_000) / 1_000_000.0);

    /// <summary>Pure conversion for tests and any later colour picker.</summary>
    public static uint FromHue(double degrees)
    {
        if (!double.IsFinite(degrees) || degrees < 0 || degrees >= 360)
            throw new ArgumentOutOfRangeException(nameof(degrees));
        double sector = degrees / 60.0;
        double x = 0.75 * (1.0 - Math.Abs(sector % 2.0 - 1.0));
        (double r, double g, double b) = sector switch
        {
            < 1 => (0.75, x, 0.0),
            < 2 => (x, 0.75, 0.0),
            < 3 => (0.0, 0.75, x),
            < 4 => (0.0, x, 0.75),
            < 5 => (x, 0.0, 0.75),
            _ => (0.75, 0.0, x),
        };
        static uint Byte(double component)
            => (uint)Math.Round((component + 0.25) * 255,
                MidpointRounding.AwayFromZero);
        return (uint)Alpha << 24 | Byte(r) << 16 | Byte(g) << 8 | Byte(b);
    }

    public static void Validate(uint argb)
    {
        if ((argb >> 24) != Alpha)
            throw new ArgumentOutOfRangeException(nameof(argb),
                "Source colour must use 20% alpha (0x33).");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Heresy.UserInterface.Views;

/// <summary>
/// Selects one installed fixed-width family for authored script source and
/// copyable API reference text. Do not set a local font when none is usable:
/// the ordinary theme/UI font must remain the final fallback.
/// </summary>
public static class ScriptEditorFonts
{
    private static readonly string[] Preferred =
    [
        "Commit Mono", "Hack", "Fira Mono", "Noto Mono",
        "Liberation Mono", "IBM Plex Mono", "Ubuntu Mono",
        "Cascadia Mono", "Consolas", "Lucida Console",
        "SF Mono", "Menlo", "Monaco",
    ];

    private static readonly string[] LastResort = ["Courier New", "Courier"];

    // Font discovery belongs to the initialized Avalonia UI, not startup or
    // static initialization of a document model. Resolve at first editor use.
    private static readonly Lazy<FontFamily?> Selected =
        new(ResolveInstalledFamily);

    public static FontFamily? SelectedFamily => Selected.Value;

    public static void ApplyTo(Control editor)
    {
        if (SelectedFamily is { } family)
            TextElement.SetFontFamily(editor, family);
    }

    /// <summary>
    /// Pure selection policy so every priority/fallback tier is testable
    /// without assuming a particular test machine's font installation.
    /// A candidate is never chosen solely because its name sounds monospaced.
    /// </summary>
    public static string? SelectFamilyName(
        IEnumerable<string> installedFamilies,
        Func<string, bool> isFixedWidth,
        Func<string, bool> isSansSerif)
    {
        string[] names = installedFamilies
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string preferred in Preferred)
        {
            string? match = names.FirstOrDefault(name =>
                string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null && isFixedWidth(match))
                return match;
        }

        // Do not let the explicitly last-resort Courier families bypass the
        // two generic monospace tiers, even when their metrics are fixed.
        string[] other = names.Where(name =>
            !LastResort.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();

        foreach (string name in other)
        {
            if (isFixedWidth(name) && isSansSerif(name))
                return name;
        }

        foreach (string name in other)
        {
            if (isFixedWidth(name))
                return name;
        }

        foreach (string fallback in LastResort)
        {
            string? match = names.FirstOrDefault(name =>
                string.Equals(name, fallback, StringComparison.OrdinalIgnoreCase));
            if (match is not null && isFixedWidth(match))
                return match;
        }

        return null;
    }

    private static FontFamily? ResolveInstalledFamily()
    {
        var fonts = FontManager.Current.SystemFonts;
        FontFamily[] installed = fonts.ToArray();
        var glyphFaces = new Dictionary<string, IGlyphTypeface?>(
            StringComparer.OrdinalIgnoreCase);

        IGlyphTypeface? FindFace(string name)
        {
            if (!glyphFaces.TryGetValue(name, out IGlyphTypeface? face))
            {
                fonts.TryGetGlyphTypeface(name, FontStyle.Normal,
                    FontWeight.Normal, FontStretch.Normal, out face);
                glyphFaces.Add(name, face);
            }
            return face;
        }

        string? selectedName = SelectFamilyName(
            installed.Select(family => family.Name),
            name => FindFace(name) is { } face && HasUniformAdvance(face),
            name => FindFace(name) is { } face && IsSansSerif(name, face));

        return selectedName is null ? null : new FontFamily(selectedName);
    }

    private static bool HasUniformAdvance(IGlyphTypeface face)
    {
        int advance = -1;
        foreach (char character in "iWm0. ")
        {
            ushort glyph = face.GetGlyph(character);
            if (glyph == 0)
                return false;

            int width = face.GetGlyphAdvance(glyph);
            if (width <= 0 || (advance >= 0 && width != advance))
                return false;
            advance = width;
        }
        return true;
    }

    private static bool IsSansSerif(string name, IGlyphTypeface face)
    {
        // The OS/2 PANOSE serif-style byte (index 33) distinguishes Latin
        // text sans-serif styles 11..15 from serif styles 2..10.
        const uint os2Tag = 0x4F532F32; // 'OS/2'
        if (face.TryGetTable(os2Tag, out byte[] table)
            && table.Length > 33 && table[32] == 2
            && table[33] is >= 2 and <= 15)
        {
            return table[33] >= 11;
        }

        // Older/underspecified fonts may have no meaningful PANOSE data.
        // Name hints are a best effort only; equal glyph advances are still
        // mandatory before any fallback font can be selected.
        return name.Contains("Sans", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Code", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Console", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gothic", StringComparison.OrdinalIgnoreCase)
            || (name.Contains("Mono", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Serif", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Courier", StringComparison.OrdinalIgnoreCase));
    }
}

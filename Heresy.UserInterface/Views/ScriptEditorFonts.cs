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
        var glyphFaces = new Dictionary<string, GlyphTypeface?>(
            StringComparer.OrdinalIgnoreCase);

        GlyphTypeface? FindFace(string name)
        {
            if (!glyphFaces.TryGetValue(name, out GlyphTypeface? face))
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
            name => IsSansSerif(name));

        return selectedName is null ? null : new FontFamily(selectedName);
    }

    private static bool HasUniformAdvance(GlyphTypeface face)
    {
        int advance = -1;
        foreach (char character in "iWm0. ")
        {
            if (!face.CharacterToGlyphMap.TryGetGlyph(character, out ushort glyph)
                || !face.TryGetHorizontalGlyphAdvance(glyph, out ushort width)
                || width == 0 || (advance >= 0 && width != advance))
            {
                return false;
            }
            advance = width;
        }
        return true;
    }

    private static bool IsSansSerif(string name)
    {
        // Avalonia 12.1 exposes glyph metrics, not PANOSE classification.
        // Use conservative family-name hints for the sans-serif tier;
        // a verified uniform glyph advance is still required for selection.
        return name.Contains("Sans", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Code", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Console", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gothic", StringComparison.OrdinalIgnoreCase)
            || (name.Contains("Mono", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Serif", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Courier", StringComparison.OrdinalIgnoreCase));
    }
}

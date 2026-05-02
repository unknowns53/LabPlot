using System;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;

namespace LabPlot.Core.Wpf;

/// <summary>
/// Static utility methods shared by the WPF apps for parsing user input,
/// formatting numbers, normalising hex colours, and reading / writing
/// ComboBox tags. Apps consume this with `using static
/// LabPlot.Core.Wpf.FormatHelpers;` so the call sites read like the
/// previous private helpers but the implementation lives in one place.
/// </summary>
public static class FormatHelpers
{
    // ===== Numeric parsing =====

    /// <summary>
    /// Tries to parse a double from user input. Accepts CurrentCulture
    /// first (so locales using "," as decimal separator work natively)
    /// then falls back to InvariantCulture (matches files / sessions
    /// stored with "." regardless of locale). Thousands separators are
    /// allowed in either culture.
    /// </summary>
    public static bool TryParseDouble(string? text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Tries to parse an integer from user input using InvariantCulture
    /// (e.g. for run indices). NumberStyles.Integer rejects fractional
    /// input.
    /// </summary>
    public static bool TryParseInt(string? text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Like <see cref="TryParseDouble"/> but additionally requires the
    /// parsed value to be finite and strictly positive.
    /// </summary>
    public static bool TryParsePositiveDouble(string? text, out double value)
    {
        return TryParseDouble(text, out value) && double.IsFinite(value) && value > 0;
    }

    /// <summary>
    /// Like <see cref="TryParseDouble"/> but additionally requires the
    /// parsed value to be finite and non-negative (zero is allowed).
    /// </summary>
    public static bool TryParseNonNegativeDouble(string? text, out double value)
    {
        return TryParseDouble(text, out value) && double.IsFinite(value) && value >= 0;
    }

    // ===== Number formatting =====

    /// <summary>
    /// Formats a double for UI display using the "0.###" pattern (up to
    /// three fractional digits, trailing zeros trimmed) in
    /// InvariantCulture so the rendered string round-trips through
    /// <see cref="TryParseDouble"/> regardless of locale.
    /// </summary>
    public static string FormatDouble(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // ===== Colour helpers =====

    /// <summary>
    /// Returns true when <paramref name="text"/> is null / whitespace
    /// or equals "Auto" (case-insensitive). Used by the line-colour
    /// pickers to keep the auto-palette branch separate from explicit
    /// hex entries.
    /// </summary>
    public static bool IsAutoColorText(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            || text.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the canonical "#RRGGBB" form of <paramref name="text"/>
    /// (or "#000000" if it is not a valid hex code). Use
    /// <see cref="TryNormalizeHexColorCode"/> when you need to
    /// distinguish "invalid" from "valid but black".
    /// </summary>
    public static string NormalizeHexColorCode(string text)
    {
        return TryNormalizeHexColorCode(text, out var hex) ? hex : "#000000";
    }

    /// <summary>
    /// Tries to canonicalise a hex colour code. Accepts both "#RRGGBB"
    /// and "RRGGBB" forms; rejects 3-digit shorthand and any non-hex
    /// characters. On success <paramref name="hex"/> is set to the
    /// uppercase "#RRGGBB" form.
    /// </summary>
    public static bool TryNormalizeHexColorCode(string? text, out string hex)
    {
        hex = string.Empty;

        var value = text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length != 6 || !value.All(Uri.IsHexDigit))
        {
            return false;
        }

        hex = $"#{value.ToUpperInvariant()}";
        return true;
    }

    /// <summary>
    /// Converts a "#RRGGBB" hex string into a WPF
    /// <see cref="System.Windows.Media.Color"/>. Returns
    /// <see cref="Colors.Gray"/> on parse failure so preview borders
    /// still render something instead of throwing.
    /// </summary>
    public static Color HexToMediaColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Gray;
        }
    }

    // ===== ComboBox helpers =====

    /// <summary>
    /// Reads the <see cref="ComboBoxItem.Tag"/> (cast to string) of the
    /// currently selected item, or null if the selection is not a
    /// tagged ComboBoxItem. Useful for ComboBoxes whose Tags carry
    /// enum-like keys ("Auto", "Manual", etc.).
    /// </summary>
    public static string? GetComboBoxTag(ComboBox combo)
    {
        if (combo.SelectedItem is not ComboBoxItem item) return null;
        return item.Tag as string;
    }

    /// <summary>
    /// Selects the ComboBoxItem whose Tag (case-insensitive) matches
    /// <paramref name="tag"/>. Empty / whitespace tags fall back to
    /// "Auto" so callers can pass a possibly-null preset value
    /// directly. Returns false when no matching item is found.
    /// </summary>
    public static bool SelectComboBoxByTag(ComboBox combo, string? tag)
    {
        var desired = string.IsNullOrWhiteSpace(tag) ? "Auto" : tag.Trim();
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && item.Tag is string s
                && string.Equals(s, desired, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return true;
            }
        }
        return false;
    }
}

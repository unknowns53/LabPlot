using System.Globalization;

namespace LabPlot.Core;

/// <summary>
/// Shared validation / normalization helpers used by every
/// <c>GraphFormattingConfig</c> across the LabPlot apps.
/// </summary>
/// <remarks>
/// These helpers stay independent of any concrete config type so that
/// each app can keep its own <c>GraphFormattingConfig</c> shape (GPC,
/// Spectrum, and the upcoming DLS each carry app-specific properties).
/// The contracts here are the lowest-common-denominator value rules
/// (positive doubles, finite ranges, 7-character hex colors, optional
/// trimmed text, invariant-culture number formatting).
/// </remarks>
public static class ConfigNormalizer
{
    /// <summary>
    /// Trims surrounding whitespace; returns <c>null</c> when the input
    /// is null, empty, or whitespace-only. Intended for "optional" text
    /// fields where empty strings should be treated as "unset".
    /// </summary>
    public static string? NormalizeOptionalText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// Normalizes an optional hex color string. Returns the trimmed value
    /// only when it is a valid 7-character <c>#RRGGBB</c> color; otherwise
    /// returns <c>null</c>.
    /// </summary>
    public static string? NormalizeOptionalHex(string? text)
    {
        var normalized = NormalizeOptionalText(text);
        return normalized is not null && IsHexColor(normalized) ? normalized : null;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> matches the
    /// <c>#RRGGBB</c> pattern (7 characters, '#' prefix, 6 hex digits).
    /// </summary>
    public static bool IsHexColor(string? value)
    {
        return value is { Length: 7 }
            && value[0] == '#'
            && value[1..].All(Uri.IsHexDigit);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> is finite and
    /// strictly greater than zero.
    /// </summary>
    public static bool IsPositive(double value)
    {
        return double.IsFinite(value) && value > 0;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> is finite and
    /// greater than or equal to zero.
    /// </summary>
    public static bool IsNonNegative(double value)
    {
        return double.IsFinite(value) && value >= 0;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> is finite and
    /// lies within the inclusive range <c>[min, max]</c>.
    /// </summary>
    public static bool IsFiniteRange(double value, double min, double max)
    {
        return double.IsFinite(value) && value >= min && value <= max;
    }

    /// <summary>
    /// Formats a number with at most two fractional digits using the
    /// invariant culture. Suitable for round-tripping config values that
    /// must remain identical across locales.
    /// </summary>
    public static string FormatNumber(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;

namespace LabPlot.Core.Avalonia;

/// <summary>
/// Static utility methods shared by the Avalonia apps for parsing user
/// input, formatting numbers, normalising hex colours, and reading /
/// writing ComboBox tags. Mirrors <c>LabPlot.Core.Wpf.FormatHelpers</c>
/// so that call sites can <c>using static
/// LabPlot.Core.Avalonia.FormatHelpers;</c> and keep the same shape on
/// either backend. The WPF / Avalonia variants live in their own
/// namespaces so a control project that sees both (e.g. a future shared
/// test harness) can disambiguate via the namespace import.
///
/// Number / hex helpers are pure (UI-agnostic) and could in principle
/// move to <c>LabPlot.Core</c>, but for Phase 7 we keep them per-backend
/// to avoid touching the Core public surface while WPF is in feature
/// freeze. <c>HexToAvaloniaColor</c> and the ComboBox helpers are
/// inherently Avalonia-typed and have to stay here.
/// </summary>
public static class FormatHelpers
{
    // ===== Numeric parsing =====

    public static bool TryParseDouble(string? text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseInt(string? text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParsePositiveDouble(string? text, out double value)
    {
        return TryParseDouble(text, out value) && double.IsFinite(value) && value > 0;
    }

    public static bool TryParseNonNegativeDouble(string? text, out double value)
    {
        return TryParseDouble(text, out value) && double.IsFinite(value) && value >= 0;
    }

    // ===== Number formatting =====

    public static string FormatDouble(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// null は空文字、非 null は <see cref="FormatDouble(double)"/> と同形式で返す。
    /// DLS の Metadata / Cumulant TextBox のように「未入力ならクリア」する
    /// 三段構え Commit で使う想定。
    /// </summary>
    public static string FormatNullableDouble(double? value)
    {
        return value.HasValue ? FormatDouble(value.Value) : string.Empty;
    }

    // ===== Colour helpers =====

    public static bool IsAutoColorText(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            || text.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeHexColorCode(string text)
    {
        return TryNormalizeHexColorCode(text, out var hex) ? hex : "#000000";
    }

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
    /// Converts a "#RRGGBB" hex string into an Avalonia
    /// <see cref="Color"/>. Returns <see cref="Colors.Gray"/> on parse
    /// failure so preview borders still render something instead of
    /// throwing. WPF 版 <c>HexToMediaColor</c> と同役割で、戻り値型だけ
    /// Avalonia.Media.Color に差し替えてある。
    /// </summary>
    public static Color HexToAvaloniaColor(string hex)
    {
        try
        {
            return Color.Parse(hex);
        }
        catch
        {
            return Colors.Gray;
        }
    }

    // ===== ComboBox helpers =====

    /// <summary>
    /// Reads the <see cref="Control.Tag"/> (cast to string) of the
    /// currently selected <see cref="ComboBoxItem"/>, or null if the
    /// selection is not a tagged ComboBoxItem.
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
    ///
    /// Avalonia の ComboBox.Items は ItemCollection (IList 実装) で WPF と
    /// 同じく for ループ + 添字アクセスが効くため、Items の列挙方法を
    /// 含めて WPF 版とほぼ同形を維持できる。
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

using System.Globalization;

namespace GpcAnalyzer.Core;

/// <summary>
/// GPC 解析結果を UI チップ等に表示するときの数値フォーマットを1箇所に集約する。
/// 「結果コピー」ボタンや CSV/XLSX エクスポートは既存の生値/独自フォーマットを使い続けるため、
/// ここでの変更の影響を受けない（表示専用）。
/// </summary>
public static class GpcResultFormat
{
    /// <summary>
    /// Mn / Mw / Mp など分子量系の値。g/mol の整数として桁区切り付きで表示する (例: 4,049)。
    /// </summary>
    public static string FormatMolecularWeight(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return "-";
        }

        return FormatMolecularWeight(value.Value);
    }

    /// <inheritdoc cref="FormatMolecularWeight(double?)"/>
    public static string FormatMolecularWeight(double value)
    {
        if (!double.IsFinite(value))
        {
            return "-";
        }

        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Đ (多分散度) やピーク面積比など、分子量以外の比率値。従来どおり小数表記を維持する。
    /// </summary>
    public static string FormatRatio(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return "-";
        }

        return FormatRatio(value.Value);
    }

    /// <inheritdoc cref="FormatRatio(double?)"/>
    public static string FormatRatio(double value)
    {
        if (!double.IsFinite(value))
        {
            return "-";
        }

        var absoluteValue = Math.Abs(value);
        if (absoluteValue <= double.Epsilon)
        {
            return "0";
        }

        if (absoluteValue is >= 0.01 and < 10000)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return value.ToString("0.###E+0", CultureInfo.InvariantCulture);
    }
}

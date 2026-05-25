using System;
using Avalonia.Controls;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// per-dataset の Style 編集 TextBox (凡例名 / 線幅 / マーカーサイズ) で
/// 共通する「trim + parse + 成功時のみ apply」パターンを 1 箇所にまとめる
/// 静的ヘルパ。サプレッション フラグ判定と active-index ガードは呼び出し
/// 側に残す (各 MainWindow の private state に依存するため)。GPC / Spectrum
/// の Style 3 ハンドラがこの Helper を共有する設計。
/// </summary>
public static class DatasetStyleCommit
{
    /// <summary>
    /// 凡例名 TextBox を trim し、空白なら null として apply に渡す。
    /// </summary>
    public static void CommitLegendName(TextBox box, Action<string?> apply)
    {
        var raw = box.Text?.Trim() ?? string.Empty;
        apply(string.IsNullOrWhiteSpace(raw) ? null : raw);
    }

    /// <summary>
    /// 線幅 TextBox を positive double として parse し、成功時のみ apply。
    /// 失敗時は何もしない (TextChanged 三段構えの中間状態を許す形)。
    /// </summary>
    public static bool TryCommitPositiveDouble(TextBox box, Action<double> apply)
    {
        if (!TryParsePositiveDouble(box.Text, out var value)) return false;
        apply(value);
        return true;
    }

    /// <summary>
    /// マーカーサイズ TextBox を non-negative double として parse し、成功時のみ apply。
    /// </summary>
    public static bool TryCommitNonNegativeDouble(TextBox box, Action<double> apply)
    {
        if (!TryParseNonNegativeDouble(box.Text, out var value)) return false;
        apply(value);
        return true;
    }
}

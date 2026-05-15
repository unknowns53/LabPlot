using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// 解析結果の数値 TextBlock を「旧値 → 新値」へ ease-out cubic で 200 ms 程度補間する軽量ヘルパー。
/// 呼び出し側は新しい数値と「数値 → 文字列」formatter を渡すだけで、現在の TextBlock.Text 先頭の
/// 数字を旧値として自動 parse する。parse できない (例: 「—」「N/A」) / 同値 / 非有限値 (NaN, ∞) は
/// 即座に最終文字列を当てる。同じ TextBlock で連続呼び出しすると古いアニメは cancel する。
/// </summary>
public static class NumberCountUp
{
    /// <summary>標準アニメ時間 (ms)。呼び出し側で上書き可。</summary>
    public const int DefaultDurationMs = 200;

    // 16 ms ≈ 60 fps。重い OS でフレーム落ちしても ease-out で自然に追従する。
    private const int FrameIntervalMs = 16;

    // 動作中のアニメを TextBlock ごとに 1 本だけ保持。新しい Animate 呼び出しで前のは cancel。
    private static readonly ConcurrentDictionary<TextBlock, CancellationTokenSource> _running = new();

    /// <summary>
    /// 数値の補間アニメを開始する (実行は UI スレッド、呼び出しは fire-and-forget)。
    /// </summary>
    /// <param name="target">表示先 TextBlock (SelectableTextBlock も継承で OK)。</param>
    /// <param name="toValue">新しい数値。double.NaN / Infinity の場合は即時表示。</param>
    /// <param name="formatter">double → 表示文字列の変換 (例: <c>v => $"{v:0.0} nm"</c>)。</param>
    /// <param name="durationMs">アニメ時間 (ms)。既定 200。</param>
    public static void Animate(TextBlock target, double toValue, Func<double, string> formatter, int durationMs = DefaultDurationMs)
    {
        if (target is null || formatter is null) return;

        var currentText = target.Text ?? string.Empty;
        var hasFromValue = TryParseLeadingNumber(currentText, out var fromValue);
        var threshold = ResolutionFor(toValue);

        if (!double.IsFinite(toValue) || !hasFromValue || Math.Abs(fromValue - toValue) <= threshold || durationMs <= 0)
        {
            CancelRunning(target);
            target.Text = formatter(toValue);
            return;
        }

        CancelRunning(target);
        var cts = new CancellationTokenSource();
        _running[target] = cts;
        _ = AnimateAsync(target, fromValue, toValue, formatter, durationMs, cts);
    }

    /// <summary>明示的にアニメを止めて、与えた最終文字列に置き換える。「—」リセットなど。</summary>
    public static void Cancel(TextBlock target, string? finalText = null)
    {
        if (target is null) return;
        CancelRunning(target);
        if (finalText is not null) target.Text = finalText;
    }

    private static async Task AnimateAsync(TextBlock target, double from, double to, Func<double, string> formatter, int durationMs, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var sw = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var elapsed = sw.ElapsedMilliseconds;
                if (elapsed >= durationMs) break;
                var t = elapsed / (double)durationMs;
                // ease-out cubic: 1 - (1 - t)^3。最後に向かって滑らかに減速する。
                t = 1.0 - Math.Pow(1.0 - t, 3.0);
                var value = from + (to - from) * t;
                target.Text = formatter(value);
                await Task.Delay(FrameIntervalMs, token).ConfigureAwait(true);
            }
            if (!token.IsCancellationRequested)
            {
                target.Text = formatter(to);
            }
        }
        catch (TaskCanceledException)
        {
            // Animate 連続呼び出しで前のアニメを cancel した場合の正常終了。
        }
        finally
        {
            _running.TryRemove(target, out _);
            cts.Dispose();
        }
    }

    private static void CancelRunning(TextBlock target)
    {
        if (_running.TryRemove(target, out var oldCts))
        {
            try { oldCts.Cancel(); } catch { /* already disposed */ }
        }
    }

    /// <summary>
    /// <see cref="TextBlock.Text"/> 先頭の数値リテラル ("123.4", "1.234e-3", "-32.5") を取り出す。
    /// 単位文字 ("nm", "μs⁻¹", "°C") の前で止まる。空文字 / 数字無しは false 返却。
    /// </summary>
    private static bool TryParseLeadingNumber(string text, out double value)
    {
        value = 0.0;
        if (string.IsNullOrEmpty(text)) return false;

        var i = 0;
        // 先頭の符号
        if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
        var digitStart = i;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
        // 指数部 (e / E + 符号付き整数)
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
            while (i < text.Length && char.IsDigit(text[i])) i++;
        }
        if (i == digitStart) return false;
        return double.TryParse(text.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 「ほぼ同じ値」と判定する閾値。アニメ無しで済ませる用途。abs(toValue) のスケールに合わせて
    /// 相対誤差 1e-6 + 絶対誤差 1e-9 を混ぜる。toValue が NaN なら +∞ (常に同値) を返す。
    /// </summary>
    private static double ResolutionFor(double toValue)
    {
        if (double.IsNaN(toValue)) return double.PositiveInfinity;
        return Math.Max(Math.Abs(toValue) * 1e-6, 1e-9);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.Tools.Screenshots;

/// <summary>1 シナリオ = 出力相対パス (artifacts/screenshots/ 起点) + 非同期実行デリゲート。</summary>
internal sealed record ScreenshotScenario(string RelativePath, Func<ShotContext, Task> RunAsync);

/// <summary>
/// Batch 1 のスモーク撮影シナリオ一覧。gpc/10-data-loaded.png と smoke/popup-test.png は
/// gpc/00-startup.png で生成した同一 MainWindow インスタンスを使い回す (仕様どおり)。
/// Batch 2 以降でモジュール別ファイルへ分割する想定なので、ここでは素直な直列実行のみ。
/// </summary>
internal static class Scenarios
{
    // gpc/00-startup → gpc/10-data-loaded → smoke/popup-test の 3 シナリオで
    // 同じ MainWindow インスタンスを共有するための保持先。
    private static LabPlot.GPC.Avalonia.MainWindow? s_gpcWindow;

    public static IReadOnlyList<ScreenshotScenario> All { get; } = new[]
    {
        new ScreenshotScenario("portal/00-launcher.png", CapturePortalLauncherAsync),
        new ScreenshotScenario("gpc/00-startup.png", CaptureGpcStartupAsync),
        new ScreenshotScenario("gpc/10-data-loaded.png", CaptureGpcDataLoadedAsync),
        new ScreenshotScenario("smoke/popup-test.png", CapturePopupSmokeTestAsync),
    };

    private static async Task CapturePortalLauncherAsync(ShotContext ctx)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } portal)
        {
            throw new InvalidOperationException(
                "desktop.MainWindow (PortalWindow) が見つからない。SetupWithClassicDesktopLifetime が先に走っているか確認する。");
        }

        await ctx.CaptureAsync(portal, "portal/00-launcher.png");
    }

    private static async Task CaptureGpcStartupAsync(ShotContext ctx)
    {
        var window = new LabPlot.GPC.Avalonia.MainWindow();
        s_gpcWindow = window;
        await ctx.CaptureAsync(window, "gpc/00-startup.png");
    }

    private static async Task CaptureGpcDataLoadedAsync(ShotContext ctx)
    {
        var window = s_gpcWindow
            ?? throw new InvalidOperationException("gpc/00-startup シナリオが先に走っていない (s_gpcWindow が null)。");

        var samplesDir = Path.Combine(ctx.RepoRoot, "src", "LabPlot.GPC", "samples");
        var filePaths = new[]
        {
            Path.Combine(samplesDir, "20260116_2-000_C-PNIPAM_DMF.txt"),
            Path.Combine(samplesDir, "20260116_2-058_S-PNIPAM_DMF.txt"),
        };
        foreach (var path in filePaths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"GPC サンプルファイルが見つからない: {path}", path);
            }
        }

        // IPortalFileOpener.OpenFilesAsync は内部で WhenLoadedAsync を待つので、
        // Show 直後でも安全に呼べる。読み込み後にプロット描画が完了するまで待ってから撮影する。
        await ((IPortalFileOpener)window).OpenFilesAsync(filePaths);
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "gpc/10-data-loaded.png");
    }

    /// <summary>
    /// 判定用シナリオ (ユーザーガイド画像ではない): ComboBox の Popup (ドロップダウン) が
    /// headless capture に写るかどうかを確認する。gpc/10-data-loaded.png と同じ MainWindow を
    /// 使い回し、RecentFilesComboBox を強制的に開いて撮影する。
    ///
    /// 最初に「較正曲線と分子量」Expander (IsExpanded=False) の中の
    /// MolecularWeightYModeComboBox で試したところ、collapsed Expander の中身は
    /// Bounds=(0,0,0,0) のまま実質的にレイアウトされず (visual tree 上に祖先 Expander も
    /// 見つからない)、Popup を開いても撮影結果が無変化だった。RecentFilesComboBox は
    /// 「データファイル」セクション (既定で展開済み) に直接あり常に実サイズでレイアウトされる
    /// ので、Popup 表示可否そのものを切り分けるにはこちらが適切。既定 IsEnabled=False
    /// (履歴が無いため) だけ smoke シナリオとして強制的に上書きする。
    /// </summary>
    private static async Task CapturePopupSmokeTestAsync(ShotContext ctx)
    {
        var window = s_gpcWindow
            ?? throw new InvalidOperationException("gpc/00-startup シナリオが先に走っていない (s_gpcWindow が null)。");

        var comboBox = window.FindControl<ComboBox>("RecentFilesComboBox")
            ?? throw new InvalidOperationException("RecentFilesComboBox が見つからない (x:Name 変更?)。");

        comboBox.IsEnabled = true;
        await ShotContext.SettleAsync();

        comboBox.IsDropDownOpen = true;
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "smoke/popup-test.png");

        comboBox.IsDropDownOpen = false;
    }
}

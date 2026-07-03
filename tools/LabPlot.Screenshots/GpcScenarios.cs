using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// GPC (LabPlot.GPC.Avalonia) 用のスクリーンショットシナリオ。8 枚とも独立した
/// MainWindow インスタンスを都度生成する (前のシナリオの MRU / window state に
/// 引きずられないようにするため、各シナリオの冒頭で <see cref="IsolationHelper"/>
/// により隔離用ディレクトリを差し替える)。
///
/// 較正曲線の既定値だけは特殊事情がある: GPC.Avalonia.MainWindow の
/// <c>FormattingConfigPath</c> は static readonly フィールドで、プロセス内で最初に
/// MainWindow が構築された瞬間の LABPLOT_APPDATA_OVERRIDE を永続的に固定してしまう
/// (static コンストラクタは一度しか走らない)。そのため較正曲線の formatting_config.json は
/// プロセス内でずっと同じ 1 か所 (<see cref="GetOrCreateCalibrationRoot"/> が最初の呼び出しで
/// 作るディレクトリ) に書いておき、MRU 隔離用の per-シナリオディレクトリとは別管理にする。
/// これにより、後続シナリオで env var を差し替えても較正曲線は常に読み込まれ続ける。
/// </summary>
internal static class GpcScenarios
{
    private const string SampleC = "20260116_2-000_C-PNIPAM_DMF.txt";
    private const string SampleS = "20260116_2-058_S-PNIPAM_DMF.txt";

    private static string? s_calibrationRoot;

    public static ScreenshotScenario[] All { get; } =
    {
        new("gpc/00-startup.png", CaptureStartupAsync),
        new("gpc/05-first-load.png", CaptureFirstLoadAsync),
        new("gpc/10-data-loaded.png", CaptureDataLoadedAsync),
        new("gpc/20-mw-display.png", CaptureMwDisplayAsync),
        new("gpc/30-formatting.png", CaptureFormattingAsync),
        new("gpc/40-export.png", CaptureExportAsync),
        new("gpc/50-session.png", CaptureSessionAsync),
        new("gpc/60-preferences.png", CapturePreferencesAsync),
    };

    private static async Task CaptureStartupAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.CaptureAsync(window, "gpc/00-startup.png");
    }

    private static async Task CaptureFirstLoadAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        var samplesDir = SamplesDir(ctx);
        await ((IPortalFileOpener)window).OpenFilesAsync(new[] { Path.Combine(samplesDir, SampleC) });
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "gpc/05-first-load.png");
    }

    private static async Task CaptureDataLoadedAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        SetOverlay(window, true);
        await OpenBothSamplesAsync(ctx, window);

        await ctx.CaptureAsync(window, "gpc/10-data-loaded.png");
    }

    private static async Task CaptureMwDisplayAsync(ShotContext ctx)
    {
        var window = await CreateLoadedOverlayWithMwAsync(ctx);

        var calibrationExpander = FindExpander(window, "CalibrationExpander");
        calibrationExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, calibrationExpander);

        await ctx.CaptureAsync(window, "gpc/20-mw-display.png");
    }

    private static async Task CaptureFormattingAsync(ShotContext ctx)
    {
        var window = await CreateLoadedOverlayWithMwAsync(ctx);

        SwitchToFormatTab(window);
        FindExpander(window, "AxisRangeExpander").IsExpanded = false;
        FindExpander(window, "GraphLabelExpander").IsExpanded = false;
        FindExpander(window, "FormattingExpander").IsExpanded = true;
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "gpc/30-formatting.png");
    }

    private static async Task CaptureExportAsync(ShotContext ctx)
    {
        var window = await CreateLoadedOverlayWithMwAsync(ctx);

        var header = window.FindControl<DockPanel>("ChromatogramHeaderPanel")
            ?? throw new InvalidOperationException("ChromatogramHeaderPanel が見つからない (x:Name 変更?)。");
        var topLeft = header.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
        var rect = new Rect(topLeft, header.Bounds.Size);

        await ctx.CaptureCroppedAsync(window, rect, "gpc/40-export.png");
    }

    private static async Task CaptureSessionAsync(ShotContext ctx)
    {
        var window = await CreateLoadedOverlayWithMwAsync(ctx);

        var sessionExpander = FindExpander(window, "SessionExpander");
        sessionExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, sessionExpander);

        await ctx.CaptureAsync(window, "gpc/50-session.png");
    }

    private static async Task CapturePreferencesAsync(ShotContext ctx)
    {
        var window = await CreateLoadedOverlayWithMwAsync(ctx);

        SwitchToFormatTab(window);
        FindExpander(window, "AxisRangeExpander").IsExpanded = false;
        FindExpander(window, "GraphLabelExpander").IsExpanded = false;
        FindExpander(window, "FormattingExpander").IsExpanded = false;
        var preferencesExpander = FindExpander(window, "PreferencesExpander");
        preferencesExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, preferencesExpander);

        await ctx.CaptureAsync(window, "gpc/60-preferences.png");
    }

    // ---------- 共通ヘルパー ----------

    /// <summary>
    /// 重ね描き (2 サンプル) を読み込んだうえで「分子量表示」まで有効にした状態を作る。
    /// gpc/20-mw-display 以降の 5 シナリオは元の user-guide 画像がすべて同じ読み込み状態
    /// (較正曲線適用 + 分子量表示 ON) を使い回して撮っているため、ここで共通化する。
    /// </summary>
    private static async Task<LabPlot.GPC.Avalonia.MainWindow> CreateLoadedOverlayWithMwAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        SetOverlay(window, true);
        await OpenBothSamplesAsync(ctx, window);

        var mwCheckBox = window.FindControl<CheckBox>("MolecularWeightCheckBox")
            ?? throw new InvalidOperationException("MolecularWeightCheckBox が見つからない。");
        mwCheckBox.IsChecked = true;
        await ShotContext.SettleAsync();

        return window;
    }

    private static async Task OpenBothSamplesAsync(ShotContext ctx, LabPlot.GPC.Avalonia.MainWindow window)
    {
        var samplesDir = SamplesDir(ctx);
        var filePaths = new[] { Path.Combine(samplesDir, SampleC), Path.Combine(samplesDir, SampleS) };
        await ((IPortalFileOpener)window).OpenFilesAsync(filePaths);
        await ShotContext.SettleAsync();
    }

    private static void SetOverlay(LabPlot.GPC.Avalonia.MainWindow window, bool isChecked)
    {
        var overlay = window.FindControl<CheckBox>("OverlayCheckBox")
            ?? throw new InvalidOperationException("OverlayCheckBox が見つからない。");
        overlay.IsChecked = isChecked;
    }

    private static void SwitchToFormatTab(LabPlot.GPC.Avalonia.MainWindow window)
    {
        var formatTab = window.FindControl<RadioButton>("FormatTabRadioButton")
            ?? throw new InvalidOperationException("FormatTabRadioButton が見つからない。");
        formatTab.IsChecked = true;
    }

    private static Expander FindExpander(LabPlot.GPC.Avalonia.MainWindow window, string name) =>
        window.FindControl<Expander>(name)
            ?? throw new InvalidOperationException($"{name} (Expander) が見つからない (x:Name 変更?)。");

    private static ScrollViewer FindScrollViewer(LabPlot.GPC.Avalonia.MainWindow window) =>
        window.FindControl<ScrollViewer>("SidebarScrollViewer")
            ?? throw new InvalidOperationException("SidebarScrollViewer が見つからない。");

    private static string SamplesDir(ShotContext ctx) =>
        Path.Combine(ctx.RepoRoot, "src", "LabPlot.GPC", "samples");

    /// <summary>
    /// 較正曲線を自動読込済みの状態で GPC MainWindow を生成する。
    /// 1) 較正ルート (プロセス内で固定される formatting_config.json の置き場所) へ env var を
    ///    向けてから MainWindow を構築する (static readonly FormattingConfigPath の初回解決)。
    /// 2) 構築後、MRU / window state 用に env var をこのシナリオ専用の fresh ディレクトリへ
    ///    差し替える。GPC の較正曲線パスは 1) で既に固定済みなので、この差し替えは
    ///    RecentFilesStore など「呼び出しのたびに env var を読む」ストアだけに効く。
    /// </summary>
    private static LabPlot.GPC.Avalonia.MainWindow CreateWindow(ShotContext ctx)
    {
        var calibrationRoot = GetOrCreateCalibrationRoot(ctx);
        Environment.SetEnvironmentVariable("LABPLOT_APPDATA_OVERRIDE", calibrationRoot);

        var window = new LabPlot.GPC.Avalonia.MainWindow();

        IsolationHelper.UseFreshAppData("gpc");
        return window;
    }

    private static string GetOrCreateCalibrationRoot(ShotContext ctx)
    {
        if (s_calibrationRoot is not null)
        {
            return s_calibrationRoot;
        }

        var calibrationJsonPath = Path.Combine(SamplesDir(ctx), "standard_curve.json");
        if (!File.Exists(calibrationJsonPath))
        {
            throw new FileNotFoundException($"較正曲線サンプルが見つからない: {calibrationJsonPath}", calibrationJsonPath);
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "LabPlotScreenshots",
            $"gpc-calibration-{Guid.NewGuid():N}");
        var configDir = Path.Combine(root, "GPC_Visualization");
        Directory.CreateDirectory(configDir);

        var configPath = Path.Combine(configDir, "formatting_config.json");
        var json = JsonSerializer.Serialize(
            new { DefaultCalibrationFilePath = calibrationJsonPath },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);

        s_calibrationRoot = root;
        return root;
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;
using LabPlot.Viewer.Avalonia;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// Data Viewer (LabPlot.Viewer.Avalonia) 用のスクリーンショットシナリオ。4 枚とも独立した
/// MainWindow インスタンスを都度生成する (GPC / Spectrum / DLS と同じ隔離方針)。
///
/// <para>
/// Viewer.Avalonia.MainWindow にも GPC と同じ static readonly <c>FormattingConfigPath</c>
/// (AppDataPaths.GetApplicationDataPath() をプロセス内で最初の構築時に固定) が存在するが、
/// このバッチの 4 シナリオはどれも「既定値として保存」を使わないため、GPC のような較正ルート
/// 特別扱いは不要 (DLS と同じ単純な CreateWindow で足りる)。
/// </para>
/// </summary>
internal static class ViewerScenarios
{
    private const string DemoCsv = "viewer_demo.csv";

    public static ScreenshotScenario[] All { get; } =
    {
        new("viewer/00-startup.png", CaptureStartupAsync),
        new("viewer/10-data-loaded.png", CaptureDataLoadedAsync),
        new("viewer/20-sidebar-tabs.png", CaptureSidebarTabsAsync),
        new("viewer/50-session.png", CaptureSessionAsync),
    };

    private static async Task CaptureStartupAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.CaptureAsync(window, "viewer/00-startup.png");
    }

    private static async Task CaptureDataLoadedAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoCsvAsync(ctx, window);

        await ctx.CaptureAsync(window, "viewer/10-data-loaded.png");
    }

    private static async Task CaptureSidebarTabsAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoCsvAsync(ctx, window);

        SwitchToFormatTab(window);
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "viewer/20-sidebar-tabs.png");
    }

    private static async Task CaptureSessionAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoCsvAsync(ctx, window);

        var sessionExpander = FindExpander(window, "SessionExpander");
        sessionExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, sessionExpander);

        await ctx.CaptureAsync(window, "viewer/50-session.png");
    }

    // ---------- 共通ヘルパー ----------

    private static async Task OpenDemoCsvAsync(ShotContext ctx, MainWindow window)
    {
        var samplesDir = SamplesDir(ctx);
        await ((IPortalFileOpener)window).OpenFilesAsync(new[] { Path.Combine(samplesDir, DemoCsv) });
        await ShotContext.SettleAsync();
    }

    private static void SwitchToFormatTab(MainWindow window)
    {
        var formatTab = window.FindControl<RadioButton>("FormatTabRadioButton")
            ?? throw new InvalidOperationException("FormatTabRadioButton が見つからない。");
        formatTab.IsChecked = true;
    }

    private static Expander FindExpander(MainWindow window, string name) =>
        window.FindControl<Expander>(name)
            ?? throw new InvalidOperationException($"{name} (Expander) が見つからない (x:Name 変更?)。");

    private static ScrollViewer FindScrollViewer(MainWindow window) =>
        window.FindControl<ScrollViewer>("SidebarScrollViewer")
            ?? throw new InvalidOperationException("SidebarScrollViewer が見つからない。");

    private static string SamplesDir(ShotContext ctx) =>
        Path.Combine(ctx.RepoRoot, "src", "LabPlot.Viewer.Avalonia", "samples");

    private static MainWindow CreateWindow(ShotContext ctx)
    {
        _ = ctx;
        var window = new MainWindow();

        IsolationHelper.UseFreshAppData("viewer");
        return window;
    }
}

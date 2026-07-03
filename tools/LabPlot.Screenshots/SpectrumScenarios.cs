using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// Spectrum (LabPlot.Spectrum.Avalonia) 用のスクリーンショットシナリオ。7 枚とも独立した
/// MainWindow インスタンスを都度生成する (前のシナリオの MRU / window state に
/// 引きずられないようにするため、各シナリオの冒頭で <see cref="IsolationHelper"/>
/// により隔離用ディレクトリを差し替える)。GPC と異なり、書式の既定値注入が必要な
/// static readonly フィールド (較正曲線相当) は存在しないため <c>CreateWindow</c> は単純。
///
/// <para>
/// IR ピーク検出 (ShowIrPeakCheckBox) と Tc 表示 (ShowCloudPointCheckBox) はチェック変更 →
/// private ハンドラ → SchedulePlotCurrentDataset() → 200ms デバウンスタイマー経由でしか
/// 反映されない。headless では実時間のタイマー待ちに頼らず、internal 化した
/// <c>MainWindow.PlotCurrentDataset()</c> を直接呼んで確定反映させる。
/// </para>
///
/// <para>
/// 畳まれた Expander の中身は、親 Expander も含めて外側から順に展開してから操作する
/// (ネストした Expander の子要素を先に触ろうとすると見つからない場合があるため)。
/// </para>
/// </summary>
internal static class SpectrumScenarios
{
    private const string UvVisSample = "1-16 HO-Ph-acetylene 1.0mg.txt";
    private const string HeatingSample = "2_heating.txt";
    private const string CoolingSample = "2_cooling.txt";
    private const string IrSample = "20240420_1-97_poly(N-butyl-4-ethynylbenzamide).csv";

    public static ScreenshotScenario[] All { get; } =
    {
        new("spectrum/10-uv-vis-loaded.png", CaptureUvVisLoadedAsync),
        new("spectrum/15-temperature-hysteresis.png", CaptureTemperatureHysteresisAsync),
        new("spectrum/17-ir-peaks.png", CaptureIrPeaksAsync),
        new("spectrum/30-formatting.png", CaptureFormattingAsync),
        new("spectrum/38-calibration.png", CaptureCalibrationAsync),
        new("spectrum/50-session.png", CaptureSessionAsync),
        new("spectrum/60-preferences.png", CapturePreferencesAsync),
    };

    private static async Task CaptureUvVisLoadedAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        await OpenSingleSampleAsync(ctx, window, UvVisSample);

        await ctx.CaptureAsync(window, "spectrum/10-uv-vis-loaded.png");
    }

    private static async Task CaptureTemperatureHysteresisAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        SetOverlay(window, true);
        await OpenSamplesAsync(ctx, window, HeatingSample, CoolingSample);

        // 上の 2 セクションを畳んで、温度スキャン解析セクションのために余白を作る
        // (既存の user-guide 画像と同じレイアウト)。
        FindExpander(window, "DataFileExpander").IsExpanded = false;
        FindExpander(window, "DatasetListExpander").IsExpanded = false;

        var analysisExpander = FindExpander(window, "AnalysisExpander");
        analysisExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var cloudPointExpander = FindExpander(window, "CloudPointExpander");
        cloudPointExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var showCloudPointCheckBox = window.FindControl<CheckBox>("ShowCloudPointCheckBox")
            ?? throw new InvalidOperationException("ShowCloudPointCheckBox が見つからない。");
        showCloudPointCheckBox.IsChecked = true;

        // 200ms デバウンスタイマーを待たず、internal 化した PlotCurrentDataset() を直接呼んで
        // Tc / ヒステリシス表示を確定反映させる。
        window.PlotCurrentDataset();
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, cloudPointExpander);

        await ctx.CaptureAsync(window, "spectrum/15-temperature-hysteresis.png");
    }

    private static async Task CaptureIrPeaksAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        await OpenSingleSampleAsync(ctx, window, IrSample);

        // ShowIrPeakCheckBox は 解析 → IR スペクトル解析（ピーク検出） の中にネストしているため、
        // 外側の Expander から順に展開してから操作する。
        var analysisExpander = FindExpander(window, "AnalysisExpander");
        analysisExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var irPeakExpander = FindExpander(window, "IrPeakDetectionExpander");
        irPeakExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var showIrPeakCheckBox = window.FindControl<CheckBox>("ShowIrPeakCheckBox")
            ?? throw new InvalidOperationException("ShowIrPeakCheckBox が見つからない。");
        showIrPeakCheckBox.IsChecked = true;

        // この同梱サンプルは既定の「最大マーカー数」(5) だと、生データの端 (約500-560 cm-1) に
        // ある装置由来のアーティファクトスパイクだけで上位 4 枠を使い切ってしまい、
        // 600-4000 cm-1 の可視範囲内に残るピークが 1 個だけになってしまう (実測で確認済み)。
        // 「最大マーカー数」欄を無制限 (0) に広げると、既定の検出閾値・突出度のままでも
        // 可視範囲内の残り 2 個 (1065.9 / 1637.5 cm-1) が追加で表示される。検出パラメータの
        // 既定値そのもの (AXAML) は変更せず、既存 TextBox への入力操作として行う。
        var irPeakCountTextBox = window.FindControl<TextBox>("IrPeakCountTextBox")
            ?? throw new InvalidOperationException("IrPeakCountTextBox が見つからない。");
        irPeakCountTextBox.Text = "0";

        window.PlotCurrentDataset();
        await ShotContext.SettleAsync();

        // 元データの端に装置由来のアーティファクトスパイクがあり、自動レンジだと Y 軸が
        // 極端に伸びて使えない画になる (前回の手動撮影で踏んだ実績のある罠)。
        // 仕上げタブの軸範囲パネルへ直接値を書き込み、internal 化した PlotCurrentDataset() を
        // 呼んで手動範囲を確定反映させる (AxisRangePanel.AxisRangeCommitted は外部から
        // 発火できないイベントのため、コミット相当の再描画をここで代替する)。
        SwitchToFormatTab(window);

        var axisRangePanel = window.FindControl<LabPlot.Core.Avalonia.Controls.AxisRangePanel>("AxisRangePanel")
            ?? throw new InvalidOperationException("AxisRangePanel が見つからない。");
        axisRangePanel.SetXValues(600, 4000);
        axisRangePanel.SetYValues(0, 110);
        window.PlotCurrentDataset();
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "spectrum/17-ir-peaks.png");
    }

    private static async Task CaptureFormattingAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        await OpenSingleSampleAsync(ctx, window, UvVisSample);

        SwitchToFormatTab(window);
        FindExpander(window, "AxisRangeExpander").IsExpanded = false;
        FindExpander(window, "GraphLabelExpander").IsExpanded = false;
        FindExpander(window, "FormattingExpander").IsExpanded = true;
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "spectrum/30-formatting.png");
    }

    private static async Task CaptureCalibrationAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        await OpenSingleSampleAsync(ctx, window, UvVisSample);

        FindExpander(window, "DataFileExpander").IsExpanded = false;
        FindExpander(window, "DatasetListExpander").IsExpanded = false;

        var analysisExpander = FindExpander(window, "AnalysisExpander");
        analysisExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var calibrationExpander = FindExpander(window, "CalibrationExpander");
        calibrationExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, calibrationExpander);

        await ctx.CaptureAsync(window, "spectrum/38-calibration.png");
    }

    private static async Task CaptureSessionAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        await OpenSingleSampleAsync(ctx, window, UvVisSample);

        FindExpander(window, "DataFileExpander").IsExpanded = false;
        FindExpander(window, "DatasetListExpander").IsExpanded = false;

        var sessionExpander = FindExpander(window, "SessionExpander");
        sessionExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, sessionExpander);

        await ctx.CaptureAsync(window, "spectrum/50-session.png");
    }

    private static async Task CapturePreferencesAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);

        await OpenSingleSampleAsync(ctx, window, UvVisSample);

        SwitchToFormatTab(window);
        FindExpander(window, "AxisRangeExpander").IsExpanded = false;
        FindExpander(window, "GraphLabelExpander").IsExpanded = false;
        var preferencesExpander = FindExpander(window, "PreferencesExpander");
        preferencesExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, preferencesExpander);

        await ctx.CaptureAsync(window, "spectrum/60-preferences.png");
    }

    // ---------- 共通ヘルパー ----------

    private static async Task OpenSingleSampleAsync(ShotContext ctx, LabPlot.Spectrum.Avalonia.MainWindow window, string sampleFileName)
    {
        var samplesDir = SamplesDir(ctx);
        await ((IPortalFileOpener)window).OpenFilesAsync(new[] { Path.Combine(samplesDir, sampleFileName) });
        await ShotContext.SettleAsync();
    }

    private static async Task OpenSamplesAsync(ShotContext ctx, LabPlot.Spectrum.Avalonia.MainWindow window, params string[] sampleFileNames)
    {
        var samplesDir = SamplesDir(ctx);
        var filePaths = Array.ConvertAll(sampleFileNames, name => Path.Combine(samplesDir, name));
        await ((IPortalFileOpener)window).OpenFilesAsync(filePaths);
        await ShotContext.SettleAsync();
    }

    private static void SetOverlay(LabPlot.Spectrum.Avalonia.MainWindow window, bool isChecked)
    {
        var overlay = window.FindControl<CheckBox>("OverlayCheckBox")
            ?? throw new InvalidOperationException("OverlayCheckBox が見つからない。");
        overlay.IsChecked = isChecked;
    }

    private static void SwitchToFormatTab(LabPlot.Spectrum.Avalonia.MainWindow window)
    {
        var formatTab = window.FindControl<RadioButton>("FormatTabRadioButton")
            ?? throw new InvalidOperationException("FormatTabRadioButton が見つからない。");
        formatTab.IsChecked = true;
    }

    private static Expander FindExpander(LabPlot.Spectrum.Avalonia.MainWindow window, string name) =>
        window.FindControl<Expander>(name)
            ?? throw new InvalidOperationException($"{name} (Expander) が見つからない (x:Name 変更?)。");

    private static ScrollViewer FindScrollViewer(LabPlot.Spectrum.Avalonia.MainWindow window) =>
        window.FindControl<ScrollViewer>("SidebarScrollViewer")
            ?? throw new InvalidOperationException("SidebarScrollViewer が見つからない。");

    private static string SamplesDir(ShotContext ctx) =>
        Path.Combine(ctx.RepoRoot, "src", "LabPlot.Spectrum", "samples");

    private static LabPlot.Spectrum.Avalonia.MainWindow CreateWindow(ShotContext ctx)
    {
        _ = ctx;
        var window = new LabPlot.Spectrum.Avalonia.MainWindow();

        IsolationHelper.UseFreshAppData("spectrum");
        return window;
    }
}

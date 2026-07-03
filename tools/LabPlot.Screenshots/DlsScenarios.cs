using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LabPlot.Core.Avalonia.Helpers;
using LabPlot.DLS.Avalonia;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// DLS (LabPlot.DLS.Avalonia) 用のスクリーンショットシナリオ。10 枚とも独立した
/// MainWindow インスタンスを都度生成する (GPC / Spectrum と同じ隔離方針)。
///
/// <para>
/// DLS の解析セクション (キュムラント / 温度ランプ / 濃度シリーズ / CONTIN) と測定条件は、
/// サイドバーではなく別ウィンドウ (<see cref="AnalysisWindow"/>) にまとまっている。
/// <see cref="AnalysisWindow"/> の public コンストラクタは <c>IDlsAnalysisHost</c> を
/// 引数に取るので直接 <c>new AnalysisWindow(mainWindow)</c> することもできるが、
/// それだと MainWindow 側の private <c>_analysisWindow</c> フィールドが未設定のままになり、
/// CONTIN (サイズ分布逆変換) の計算結果を <c>_analysisWindow?.OnInversionComputed(...)</c>
/// 経由で push する経路が繋がらない (常に "—" のまま止まる)。そのため
/// <see cref="CreateLoadedAnalysisWindowAsync"/> では実際に
/// <c>OpenAnalysisWindowButton</c> の Click ルーテッドイベントを発火させて開き、
/// <c>MainWindow.OwnedWindows</c> (Show(owner) で登録される) から生成済みインスタンスを
/// 取得する。
/// </para>
///
/// <para>
/// キュムラント / 温度ランプ / 濃度シリーズは <c>AnalysisWindow</c> が
/// <c>host.AnalysisDataChanged</c> / <c>host.ActiveItemChanged</c> を受けて同期的に
/// 再計算する (Spectrum の IR ピーク検出のようなデバウンスタイマーは無い)。ただし
/// Z-average 径等の数値は <c>NumberCountUp</c> で 200ms の ease-out cubic 補間が入るため、
/// 値を書き換えたあとは <see cref="ShotContext.SettleAsync"/> で最終値まで進めてから撮影する。
/// </para>
///
/// <para>
/// demo.xlsx (17 シート) には測定条件 (温度・濃度・溶媒・屈折率・粘度) が一切埋め込まれて
/// いない (Zetasizer xlsx は本来これらを確実には出力しないため、アプリ側は解析ウィンドウでの
/// 手入力を前提にした設計になっている)。温度ランプ / 濃度シリーズを機能させるため、
/// <see cref="ApplyDemoMetadata"/> で tools/DlsSampleGenerator/DlsSampleBuilder.cs のレシピに
/// 対応する温度・濃度をシート単位で、溶媒・屈折率・粘度を全シート共通で直接書き込む
/// (実際の UI 操作 = 解析ウィンドウでシートを切り替えながら 1 件ずつ入力、と等価)。
/// </para>
/// </summary>
internal static class DlsScenarios
{
    private const string DemoWorkbook = "demo.xlsx";

    public static ScreenshotScenario[] All { get; } =
    {
        new("dls/10-data-loaded.png", CaptureDataLoadedAsync),
        new("dls/15-distribution-mode.png", CaptureDistributionModeAsync),
        new("dls/18-measurement-conditions.png", CaptureMeasurementConditionsAsync),
        new("dls/22-cumulant-analysis.png", CaptureCumulantAnalysisAsync),
        new("dls/25-temperature-ramp.png", CaptureTemperatureRampAsync),
        new("dls/27-concentration-series.png", CaptureConcentrationSeriesAsync),
        new("dls/29-size-distribution-inversion.png", CaptureSizeDistributionInversionAsync),
        new("dls/30-formatting.png", CaptureFormattingAsync),
        new("dls/50-session.png", CaptureSessionAsync),
        new("dls/60-preferences.png", CapturePreferencesAsync),
    };

    private static async Task CaptureDataLoadedAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoWorkbookAsync(ctx, window);

        await ctx.CaptureAsync(window, "dls/10-data-loaded.png");
    }

    private static async Task CaptureDistributionModeAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoWorkbookAsync(ctx, window);

        // 読み込み済みデータセットを畳んで表示セクションのドロップダウンのための
        // 余白を作る (既存 user-guide 画像と同じレイアウト)。
        FindExpander(window, "DatasetListExpander").IsExpanded = false;
        await ShotContext.SettleAsync();

        var distributionTypeComboBox = window.FindControl<ComboBox>("DistributionTypeComboBox")
            ?? throw new InvalidOperationException("DistributionTypeComboBox が見つからない (x:Name 変更?)。");
        distributionTypeComboBox.IsDropDownOpen = true;
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "dls/15-distribution-mode.png");
    }

    private static async Task CaptureMeasurementConditionsAsync(ShotContext ctx)
    {
        var (_, analysisWindow) = await CreateLoadedAnalysisWindowAsync(ctx);

        // 溶媒プリセットの自動入力を強調する既存画像に合わせ、溶媒欄へフォーカスを当てる。
        var solvent = analysisWindow.FindControl<AutoCompleteBox>("MetadataSolventAutoComplete")
            ?? throw new InvalidOperationException("MetadataSolventAutoComplete が見つからない。");
        solvent.Focus();
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(analysisWindow, "dls/18-measurement-conditions.png");
    }

    private static async Task CaptureCumulantAnalysisAsync(ShotContext ctx)
    {
        var (_, analysisWindow) = await CreateLoadedAnalysisWindowAsync(ctx);

        var cumulantExpander = FindAnalysisExpander(analysisWindow, "CumulantExpander");
        cumulantExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindAnalysisScrollViewer(analysisWindow);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, cumulantExpander);

        await ctx.CaptureAsync(analysisWindow, "dls/22-cumulant-analysis.png");
    }

    private static async Task CaptureTemperatureRampAsync(ShotContext ctx)
    {
        // PNIPAM_25C / PNIPAM_35C (単峰コイル / 二峰凝集体レシピ) や濃度シリーズの 7 シート
        // (すべて T=25°C) も温度を持たせると、温度ランプ側の Boltzmann fit に「純粋な
        // ランプではない」点が混ざり込み R² が悪化する (実測: 8 点のみ 0.9994 → 17 点全部
        // だと 0.915 まで低下) ため、この撮影専用に温度ランプの 8 シートだけへ温度を絞る。
        var (window, analysisWindow) = await CreateLoadedAnalysisWindowAsync(
            ctx, includeBaseSheetMetadata: false, includeConcentrationMetadata: false);

        SelectActiveSheet(window, "PNIPAM_ramp_25C");
        await ShotContext.SettleAsync();

        var rampExpander = FindAnalysisExpander(analysisWindow, "TemperatureRampExpander");
        rampExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindAnalysisScrollViewer(analysisWindow);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, rampExpander);

        await ctx.CaptureAsync(analysisWindow, "dls/25-temperature-ramp.png");
    }

    private static async Task CaptureConcentrationSeriesAsync(ShotContext ctx)
    {
        // 温度ランプの Expander もこの撮影では同時展開する (下記コメント参照) ので、
        // 25-temperature-ramp.png と同じ理由で PNIPAM_25C / PNIPAM_35C は温度ランプの
        // fit から除外する (濃度シリーズの 7 シートは自身の Stokes-Einstein 計算に温度が
        // 必須なので除外できない — ランプ側には 8 ランプ + 7 濃度 = 15 点が入る)。
        var (window, analysisWindow) = await CreateLoadedAnalysisWindowAsync(ctx, includeBaseSheetMetadata: false);

        SelectActiveSheet(window, "PNIPAM_conc_10mgmL");
        await ShotContext.SettleAsync();

        // 温度ランプも展開したままにしておく (既存画像は複数 Expander 同時展開の状態を
        // スクロールで見せている — 各 Expander は独立開閉可、閉じる必要はない)。
        FindAnalysisExpander(analysisWindow, "TemperatureRampExpander").IsExpanded = true;
        var concentrationExpander = FindAnalysisExpander(analysisWindow, "ConcentrationSeriesExpander");
        concentrationExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindAnalysisScrollViewer(analysisWindow);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, concentrationExpander);

        await ctx.CaptureAsync(analysisWindow, "dls/27-concentration-series.png");
    }

    private static async Task CaptureSizeDistributionInversionAsync(ShotContext ctx)
    {
        var (window, analysisWindow) = await CreateLoadedAnalysisWindowAsync(ctx);

        // CONTIN は選択シート (単一) の自己相関関数に対して計算する。demo.xlsx の
        // PNIPAM_35C は二峰凝集体レシピなので CONTIN の見せ場として適切 (DlsSampleBuilder
        // のコメント参照)。
        SelectActiveSheet(window, "PNIPAM_35C");
        await ShotContext.SettleAsync();

        var inversionExpander = FindAnalysisExpander(analysisWindow, "InversionExpander");
        inversionExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        // 重み ComboBox は InversionExpander が閉じている間コンテンツが未実体化のままで、
        // 展開直後の SelectedItem 表示テキストが描画されない headless 特有の症状が出るため、
        // SelectedIndex を明示的に再セットして表示を強制更新する。
        var weightComboBox = analysisWindow.FindControl<ComboBox>("InversionWeightComboBox")
            ?? throw new InvalidOperationException("InversionWeightComboBox が見つからない。");
        weightComboBox.SelectedIndex = -1;
        await ShotContext.SettleAsync();
        weightComboBox.SelectedIndex = 0;
        await ShotContext.SettleAsync();

        // InversionShowAsGraphButton のクリックと等価 (RequestShowAsGraph → RefreshPlot →
        // RefreshSizeDistributionInversionPlot が同期的に CONTIN を計算し、
        // _analysisWindow.OnInversionComputed で結果を push する)。
        window.RequestShowAsGraph(DistributionMode.SizeDistributionInversion);
        await ShotContext.SettleAsync();
        await ShotContext.SettleAsync();

        var scrollViewer = FindAnalysisScrollViewer(analysisWindow);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, inversionExpander);

        await ctx.CaptureAsync(analysisWindow, "dls/29-size-distribution-inversion.png");
    }

    private static async Task CaptureFormattingAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoWorkbookAsync(ctx, window);

        SwitchToFormatTab(window);
        FindExpander(window, "FormattingExpander").IsExpanded = true;
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "dls/30-formatting.png");
    }

    private static async Task CaptureSessionAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoWorkbookAsync(ctx, window);

        var sessionExpander = FindExpander(window, "SessionExpander");
        sessionExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        var scrollViewer = FindScrollViewer(window);
        await ShotContext.ScrollIntoViewAsync(scrollViewer, sessionExpander);

        await ctx.CaptureAsync(window, "dls/50-session.png");
    }

    private static async Task CapturePreferencesAsync(ShotContext ctx)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoWorkbookAsync(ctx, window);

        SwitchToFormatTab(window);
        var preferencesExpander = FindExpander(window, "PreferencesExpander");
        preferencesExpander.IsExpanded = true;
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "dls/60-preferences.png");
    }

    // ---------- 共通ヘルパー ----------

    /// <summary>
    /// demo.xlsx を読み込み、17 シート分の測定条件を投入したうえで解析ウィンドウを開く。
    /// AnalysisWindow は「解析ウィンドウを開く」ボタンの Click ルーテッドイベントを実際に
    /// 発火させて開く (直接 <c>new AnalysisWindow(window)</c> すると MainWindow 側の
    /// private フィールドに紐付かず CONTIN の push 経路が繋がらないため)。
    /// </summary>
    private static async Task<(MainWindow Window, AnalysisWindow AnalysisWindow)> CreateLoadedAnalysisWindowAsync(
        ShotContext ctx, bool includeBaseSheetMetadata = true, bool includeConcentrationMetadata = true)
    {
        var window = CreateWindow(ctx);
        await ctx.ShowAsync(window);
        await OpenDemoWorkbookAsync(ctx, window);
        ApplyDemoMetadata(window, includeBaseSheetMetadata, includeConcentrationMetadata);

        var openButton = window.FindControl<Button>("OpenAnalysisWindowButton")
            ?? throw new InvalidOperationException("OpenAnalysisWindowButton が見つからない。");
        openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ShotContext.SettleAsync();

        var analysisWindow = window.OwnedWindows.OfType<AnalysisWindow>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "解析ウィンドウが開かれなかった (OpenAnalysisWindowButton の Click イベントが" +
                "配線されているか確認する)。");
        await ctx.ShowAsync(analysisWindow);

        return (window, analysisWindow);
    }

    private static async Task OpenDemoWorkbookAsync(ShotContext ctx, MainWindow window)
    {
        var samplesDir = SamplesDir(ctx);
        await ((IPortalFileOpener)window).OpenFilesAsync(new[] { Path.Combine(samplesDir, DemoWorkbook) });
        await ShotContext.SettleAsync();
    }

    /// <summary>
    /// demo.xlsx の全 17 シートに、tools/DlsSampleGenerator/DlsSampleBuilder.cs のレシピに
    /// 対応する測定条件を書き込む。Solvent / RefractiveIndex / ViscosityMpas は実際の
    /// DlsMetadataEditor と同じく全シート共通の 1 値 (Water@25°C: n=1.3325, η=0.890 mPa·s —
    /// SolventPresetStore の組み込みテーブル) で埋める。アプリの測定条件モデルは温度ごとに
    /// 異なる粘度を持てない (溶媒を選び直すたびに全シートへブロードキャストされる) ため、
    /// レシピ側の温度依存粘度 (0.719–0.890 mPa·s) は再現しない — これは実際の UI 操作でも
    /// 越えられない制約なので、生成される Z-average 径・温度ランプの d_high 等は
    /// レシピの理論値と数 % ずれる (docs/user-guide 上の既存スクリーンショットも同じ制約下で
    /// 撮られていると考えられる)。
    /// </summary>
    private static void ApplyDemoMetadata(
        MainWindow window, bool includeBaseSheetMetadata = true, bool includeConcentrationMetadata = true)
    {
        const double refractiveIndex = 1.3325;
        const double viscosityMpas = 0.890;

        var rampTemperatures = new Dictionary<string, double>
        {
            ["PNIPAM_ramp_25C"] = 25.0,
            ["PNIPAM_ramp_27C"] = 27.0,
            ["PNIPAM_ramp_29C"] = 29.0,
            ["PNIPAM_ramp_30C"] = 30.0,
            ["PNIPAM_ramp_31C"] = 31.0,
            ["PNIPAM_ramp_32C"] = 32.0,
            ["PNIPAM_ramp_33C"] = 33.0,
            ["PNIPAM_ramp_35C"] = 35.0,
        };
        var concConcentrations = new Dictionary<string, double>
        {
            ["PNIPAM_conc_0p5mgmL"] = 0.5,
            ["PNIPAM_conc_1mgmL"] = 1.0,
            ["PNIPAM_conc_2mgmL"] = 2.0,
            ["PNIPAM_conc_4mgmL"] = 4.0,
            ["PNIPAM_conc_6mgmL"] = 6.0,
            ["PNIPAM_conc_8mgmL"] = 8.0,
            ["PNIPAM_conc_10mgmL"] = 10.0,
        };

        foreach (var item in window.DatasetItems)
        {
            item.Metadata.Solvent = "Water";
            item.Metadata.RefractiveIndex = refractiveIndex;
            item.Metadata.ViscosityMpas = viscosityMpas;

            if (includeBaseSheetMetadata && item.SheetName == "PNIPAM_25C")
            {
                item.Metadata.TemperatureCelsius = 25.0;
            }
            else if (includeBaseSheetMetadata && item.SheetName == "PNIPAM_35C")
            {
                item.Metadata.TemperatureCelsius = 35.0;
            }
            else if (rampTemperatures.TryGetValue(item.SheetName, out var rampTemp))
            {
                item.Metadata.TemperatureCelsius = rampTemp;
            }
            else if (includeConcentrationMetadata && concConcentrations.TryGetValue(item.SheetName, out var conc))
            {
                item.Metadata.TemperatureCelsius = 25.0;
                item.Metadata.ConcentrationMgPerMl = conc;
            }
        }

        // AnalysisWindow はまだ開いていない場合もあるが、後で開いたときに ctor 内の
        // RecomputeAllSections() が最新の DatasetItems を読むので問題ない。既に開いている
        // 場合は AnalysisDataChanged 経由で即座に再計算させる。
        window.RequestAnalysisDataChanged();
    }

    private static void SelectActiveSheet(MainWindow window, string sheetName)
    {
        var datasetListBox = window.FindControl<ListBox>("DatasetListBox")
            ?? throw new InvalidOperationException("DatasetListBox が見つからない。");
        var items = window.DatasetItems;
        var index = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].SheetName, sheetName, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            throw new InvalidOperationException($"シート '{sheetName}' が読み込み済みデータセットに見つからない。");
        datasetListBox.SelectedIndex = index;
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

    private static Expander FindAnalysisExpander(AnalysisWindow window, string name) =>
        window.FindControl<Expander>(name)
            ?? throw new InvalidOperationException($"{name} (Expander) が見つからない (x:Name 変更?)。");

    private static ScrollViewer FindScrollViewer(MainWindow window) =>
        window.FindControl<ScrollViewer>("SidebarScrollViewer")
            ?? throw new InvalidOperationException("SidebarScrollViewer が見つからない。");

    private static ScrollViewer FindAnalysisScrollViewer(AnalysisWindow window) =>
        window.FindControl<ScrollViewer>("AnalysisScrollViewer")
            ?? throw new InvalidOperationException("AnalysisScrollViewer が見つからない (x:Name 変更?)。");

    private static string SamplesDir(ShotContext ctx) =>
        Path.Combine(ctx.RepoRoot, "src", "LabPlot.DLS", "samples");

    private static MainWindow CreateWindow(ShotContext ctx)
    {
        _ = ctx;
        var window = new MainWindow();

        IsolationHelper.UseFreshAppData("dls");
        return window;
    }
}

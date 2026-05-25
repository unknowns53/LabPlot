using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DlsAnalyzer.Core;
using LabPlot.Core.Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// DLS 解析の Modeless サブウィンドウ。サイドバーに乗せていた 4 解析セクション
/// (キュムラント / 温度ランプ / 濃度シリーズ / CONTIN) と 測定条件 を、本体 MainWindow と
/// 同型の縦 Expander スタックにまとめる。各 Expander は独立に開閉でき (複数同時展開可・
/// 全閉じ可)、親 (MainWindow) は <see cref="IDlsAnalysisHost"/> で読み取り API + 通知
/// event を提供する。軽量な Cumulant / Ramp / Concentration は子側で host event ごとに
/// 常時再計算するが、重い CONTIN は親が描画したタイミングで <see cref="OnInversionComputed"/>
/// を介して結果を受け取る passive 戦略。
/// </summary>
public sealed partial class AnalysisWindow : Window
{
    private readonly IDlsAnalysisHost _host;

    // CONTIN 状態 3 フィールドはサイドバー時代に MainWindow が持っていたが、
    // コントロール (InversionWeightComboBox / InversionAlphaAutoCheckBox /
    // InversionAlphaTextBox) を子へ移したのに合わせ、所有権も子へ。
    // 親は BuildInversionOptions / Pull-API 経由で値を取得する。
    private DistributionMode _inversionWeight = DistributionMode.Intensity;
    private bool _inversionUseAutoAlpha = true;
    private double _inversionManualAlpha = 0.01;
    private bool _suppressInversionControlEvents;
    private bool _suppressMetadataControlEvents;

    // AvaloniaXamlLoader.Load が実行される最中に ComboBox / CheckBox の既定値設定で
    // SelectionChanged / Checked が発火し、ハンドラが走る。x:Name フィールドは Load 完了 *後*
    // に代入されるため、この瞬間に発火するハンドラから参照すると NRE。各ハンドラ冒頭で
    // `_initialized` を見て早期 return する。Expander の Expanded/Collapsed はバインド
    // していないので該当しない。
    private bool _initialized;

    // 4 解析セクションの「結果 placeholder リセット + ステータス Show/Hide」を束ねる軽量ビュー。
    // null! は InitializeComponent 直後 (x:Name フィールドが埋まったあと) の構築まで一時的。
    private AnalysisSectionView _cumulantView = null!;
    private AnalysisSectionView _rampView = null!;
    private AnalysisSectionView _concentrationView = null!;
    private AnalysisSectionView _inversionView = null!;

    // 測定条件 7 TextBox の三段構え Commit + Sync ロジックを束ねたエディタコントローラ。
    // AnalysisWindow からは TextChanged / LostFocus / KeyDown / Sync を 1 行委譲する。
    private DlsMetadataEditor _metadataEditor = null!;

    public AnalysisWindow(IDlsAnalysisHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
        _initialized = true;

        _cumulantView = new AnalysisSectionView(
            CumulantStatusText,
            CumulantZAverageText, CumulantPdiText, CumulantGammaText,
            CumulantRangeText, CumulantRSquaredText);
        _rampView = new AnalysisSectionView(
            RampStatusText,
            RampTransitionTemperatureText, RampTransitionWidthText,
            RampLowPlateauText, RampHighPlateauText, RampRSquaredText);
        _concentrationView = new AnalysisSectionView(
            ConcentrationStatusText,
            ConcentrationD0Text, ConcentrationKDText,
            ConcentrationDhText, ConcentrationRSquaredText,
            ConcentrationReferenceText);
        _inversionView = new AnalysisSectionView(
            InversionStatusText,
            InversionAlphaText, InversionRSquaredText,
            InversionBetaText, InversionFreeBinText);

        _metadataEditor = new DlsMetadataEditor(
            _host,
            focusedInputProvider: GetFocusedMetadataInput,
            isSuppressed: () => _suppressMetadataControlEvents,
            setSuppressed: v => _suppressMetadataControlEvents = v,
            MetadataTemperatureTextBox, MetadataConcentrationTextBox, MetadataSolventAutoComplete,
            MetadataRefractiveIndexTextBox, MetadataViscosityTextBox,
            MetadataWavelengthTextBox, MetadataScatteringAngleTextBox);

        // 溶媒プリセット候補を AutoCompleteBox に流し込む。組み込み 9 種 + ユーザー追加分。
        // ItemsSource は SolventPreset.ToString() = Name で filter される。
        MetadataSolventAutoComplete.ItemsSource = SolventPresetStore.LoadAll();

        // 子 Window は Application 経由のスタイル自動適用が走らないので明示適用。
        // Spectrum CalibrationCurveWindow と同方針。
        WindowAppearance.ApplyDefaults(this);

        // 温度変更で自動再補間が走ったときの out-of-range warning を Toast にブリッジ。
        _metadataEditor.AutoReinterpolationWarning += msg => Toast?.Show(msg, StatusSeverity.Warning);

        _host.AnalysisDataChanged += OnHostAnalysisDataChanged;
        _host.ActiveItemChanged += OnHostActiveItemChanged;
        Closed += OnWindowClosed;

        SyncCumulantControlsFromActiveItem();
        SyncMetadataControlsFromActiveItem();
        UpdateActiveSheetLabel();
        RecomputeAllSections();

        // v1.3 Batch F: 5 セクションの Expander 展開状態を JSON から復元する。
        // 失敗時は XAML の初期値 (Metadata / Cumulant 開、その他閉) のままで続行する。
        LoadExpanderState();
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, global::Avalonia.Controls.WindowClosingEventArgs e)
    {
        // v1.3 Batch F: Closed の時点では x:Name フィールド経由のアクセスが不安定 (子ツリーが
        // 既に detach 済み) なので、IsExpanded スナップショットは Closing で取る。
        SaveExpanderState();
    }

    // v1.3 Batch G: AnalysisWindow にも F1 (ショートカット一覧) / Esc (閉じる) を入れる。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            global::LabPlot.Core.Avalonia.KeyboardShortcutsWindow.ShowFor(this, global::LabPlot.Core.Avalonia.AppKind.Dls);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // Avalonia.Generators が partial class に InitializeComponent + x:Name フィールド代入を
    // 自動生成するので手動定義しない（Phase 7 Batch 6 で発覚した null フィールド NRE 対策）。

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _host.AnalysisDataChanged -= OnHostAnalysisDataChanged;
        _host.ActiveItemChanged -= OnHostActiveItemChanged;
        Closed -= OnWindowClosed;
        Closing -= OnWindowClosing;
    }

    // ---------- v1.3 Batch F: Expander 展開状態の永続化 ----------

    private static string ExpanderStateFilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "LabPlot", "dls-analysis-window.json");
        }
    }

    private sealed record ExpanderStateSnapshot(
        bool Metadata,
        bool Cumulant,
        bool TemperatureRamp,
        bool ConcentrationSeries,
        bool Inversion);

    private void LoadExpanderState()
    {
        try
        {
            var path = ExpanderStateFilePath;
            if (!System.IO.File.Exists(path)) return;
            var json = System.IO.File.ReadAllText(path);
            var snap = System.Text.Json.JsonSerializer.Deserialize<ExpanderStateSnapshot>(json);
            if (snap is null) return;
            MetadataExpander.IsExpanded = snap.Metadata;
            CumulantExpander.IsExpanded = snap.Cumulant;
            TemperatureRampExpander.IsExpanded = snap.TemperatureRamp;
            ConcentrationSeriesExpander.IsExpanded = snap.ConcentrationSeries;
            InversionExpander.IsExpanded = snap.Inversion;
        }
        catch
        {
            // 永続化失敗は致命的でない (XAML 既定値で続行できる)。
        }
    }

    private void SaveExpanderState()
    {
        try
        {
            var snap = new ExpanderStateSnapshot(
                Metadata: MetadataExpander.IsExpanded,
                Cumulant: CumulantExpander.IsExpanded,
                TemperatureRamp: TemperatureRampExpander.IsExpanded,
                ConcentrationSeries: ConcentrationSeriesExpander.IsExpanded,
                Inversion: InversionExpander.IsExpanded);
            var path = ExpanderStateFilePath;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(snap));
        }
        catch
        {
            // ignore — 永続化失敗は致命的でない。
        }
    }

    // ---------- Pull API for parent (CONTIN settings) ----------

    public DistributionMode InversionWeight => _inversionWeight;
    public bool InversionUseAutoAlpha => _inversionUseAutoAlpha;
    public double InversionManualAlpha => _inversionManualAlpha;

    public SizeDistributionInverterOptions BuildInversionOptions()
    {
        if (_inversionUseAutoAlpha) return new SizeDistributionInverterOptions();
        var manual = _inversionManualAlpha;
        if (!double.IsFinite(manual) || manual <= 0) manual = 0.01;
        return new SizeDistributionInverterOptions { RegularizationAlpha = manual };
    }

    // ---------- Push API for parent (CONTIN result) ----------

    public void OnInversionComputed(
        SizeDistributionInversionOutcome? outcome,
        DlsDatasetItem? activeItem,
        int failedCount,
        int missingMetaCount,
        HashSet<string> failureReasons,
        IReadOnlyList<DlsDataset> selectedDatasets)
    {
        UpdateInversionDisplay(outcome, activeItem, failedCount, missingMetaCount, failureReasons, selectedDatasets);
    }

    // ---------- Host event handlers ----------

    private void OnHostAnalysisDataChanged(object? sender, EventArgs e)
    {
        // 自分の TextBox 編集が RequestAnalysisDataChanged を呼んで本ハンドラに戻る経路で、
        // 入力途中の TextBox を reformat 書き戻しで上書きしないよう preserveFocusedTextBox=true。
        // シート切替 (OnHostActiveItemChanged) は強制 update なのでデフォルト false のまま。
        SyncCumulantControlsFromActiveItem(preserveFocusedTextBox: true);
        SyncMetadataControlsFromActiveItem(preserveFocusedTextBox: true);
        UpdateActiveSheetLabel();
        RecomputeAllSections();
    }

    private void OnHostActiveItemChanged(object? sender, EventArgs e)
    {
        SyncCumulantControlsFromActiveItem();
        SyncMetadataControlsFromActiveItem();
        UpdateActiveSheetLabel();
        RecomputeAllSections();
    }

    private void UpdateActiveSheetLabel()
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count)
        {
            AnalysisTitleBar.Subtitle = items.Count == 0
                ? "(データセット未読み込み)"
                : "(シート未選択)";
            return;
        }
        AnalysisTitleBar.Subtitle = $"アクティブシート: {items[idx].SheetName}";
    }

    private void RecomputeAllSections()
    {
        // 各 Expander が独立に開閉可 (本体 MainWindow サイドバーと同方針) なので、
        // 「アクティブな 1 セクション」ではなく軽量な 3 セクションを毎回まとめて再計算する。
        // Expander が閉じていてもバックエンドの値だけ更新しておけば、開いた瞬間に最新値が見える。
        // メタデータ (測定条件) は SyncMetadataControlsFromActiveItem 経由で UI 反映済み。
        UpdateCumulantDisplay();
        UpdateRampDisplay();
        UpdateConcentrationDisplay();

        // CONTIN は passive 戦略を維持: 親が RefreshSizeDistributionInversionPlot を
        // 走らせたときに OnInversionComputed で push される。host event だけでは
        // 重い計算は走らせない。最新値が無いときは hint を表示する。
        if (InversionAlphaText.Text == "—")
            _inversionView.ShowStatus("「グラフとして見る」を押すと計算します");
    }

    // ===================================================================
    // Tab 1: Cumulant (active sheet, lightweight, recompute on every event)
    // ===================================================================

    private void SyncCumulantControlsFromActiveItem(bool preserveFocusedTextBox = false)
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        bool hasActive = idx >= 0 && idx < items.Count;
        CumulantFitMinTextBox.IsEnabled = hasActive;
        CumulantFitMaxTextBox.IsEnabled = hasActive;

        // RequestAnalysisDataChanged ループで自分自身の打鍵中 TextBox を上書きしないためのガード。
        // シート切替 (preserveFocusedTextBox=false) では強制的に最新値で塗り直す。
        var skip = preserveFocusedTextBox ? GetFocusedTextBox() : null;

        _suppressMetadataControlEvents = true;
        try
        {
            if (!hasActive)
            {
                SetTextSkippingFocused(CumulantFitMinTextBox, string.Empty, skip);
                SetTextSkippingFocused(CumulantFitMaxTextBox, string.Empty, skip);
            }
            else
            {
                var c = items[idx].Cumulant;
                SetTextSkippingFocused(CumulantFitMinTextBox, FormatNullableDouble(c.FitRangeMinMicroseconds), skip);
                SetTextSkippingFocused(CumulantFitMaxTextBox, FormatNullableDouble(c.FitRangeMaxMicroseconds), skip);
            }
        }
        finally { _suppressMetadataControlEvents = false; }
    }

    private void UpdateCumulantDisplay()
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count)
        {
            _cumulantView.FailWith("シートを選択してください");
            return;
        }

        var item = items[idx];
        var correlation = item.Dataset.Correlation;
        if (correlation is null)
        {
            _cumulantView.FailWith("自己相関データがありません");
            return;
        }

        var outcome = CumulantAnalyzer.Analyze(
            correlation,
            item.Cumulant.FitRangeMinMicroseconds,
            item.Cumulant.FitRangeMaxMicroseconds);

        if (!outcome.Success || outcome.Result is null)
        {
            _cumulantView.FailWith(outcome.FailureReason ?? "fit に失敗しました");
            return;
        }

        var r = outcome.Result;
        // v1.3 Batch K: 数値結果は ease-out cubic で 200 ms 補間。Γ は科学表記、PdI / R² は固定桁、
        // Range は数値補間に向かない範囲表現なので即時セット。
        NumberCountUp.Animate(CumulantGammaText, r.FirstCumulantPerMicrosecond,
            v => $"{FormatScientific(v)} μs⁻¹");
        NumberCountUp.Animate(CumulantPdiText, r.PolydispersityIndex,
            v => v.ToString("0.000", CultureInfo.InvariantCulture));
        NumberCountUp.Animate(CumulantRSquaredText, r.RSquared,
            v => v.ToString("0.0000", CultureInfo.InvariantCulture));
        CumulantRangeText.Text =
            $"{FormatDouble(r.AppliedRangeMinMicroseconds)} 〜 {FormatDouble(r.AppliedRangeMaxMicroseconds)} μs ({r.PointCount} 点)";

        var size = StokesEinstein.Compute(
            r.FirstCumulantPerMicrosecond,
            item.Metadata.TemperatureCelsius,
            item.Metadata.ViscosityMpas,
            item.Metadata.RefractiveIndex,
            item.Metadata.WavelengthNm,
            item.Metadata.ScatteringAngleDegrees);

        if (size.Success && size.HydrodynamicDiameterNm.HasValue)
        {
            NumberCountUp.Animate(CumulantZAverageText, size.HydrodynamicDiameterNm.Value,
                v => $"{v.ToString("0.0", CultureInfo.InvariantCulture)} nm");
            _cumulantView.HideStatus();
        }
        else
        {
            NumberCountUp.Cancel(CumulantZAverageText, "—");
            var missing = string.Join("・", size.MissingFields);
            _cumulantView.ShowStatus(string.IsNullOrEmpty(missing)
                ? "粒径計算に必要なメタデータが不足しています"
                : $"{missing} が未入力で粒径計算できません");
        }
    }

    // Cumulant fit range TextBox: 三段構え (TextChanged サイレント / LostFocus + Enter ロールバック付き)
    // を MainWindow からそっくり踏襲。Avalonia 11 で Window.Focus() が機能しない件の再発防止のため、
    // Enter は MetadataTextBox_KeyDown 相当の sender ベース直接コミットを使う。

    private void CumulantFitMinTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        => SilentTryCommitCumulantBound(CumulantFitMinTextBox,
            v => SetCumulantFitMin(v));

    private void CumulantFitMaxTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        => SilentTryCommitCumulantBound(CumulantFitMaxTextBox,
            v => SetCumulantFitMax(v));

    private void CumulantFitRangeTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppressMetadataControlEvents) return;
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count) return;

        bool reverted = false;
        if (!TryCommitCumulantBound(CumulantFitMinTextBox, v => SetCumulantFitMin(v))) reverted = true;
        if (!TryCommitCumulantBound(CumulantFitMaxTextBox, v => SetCumulantFitMax(v))) reverted = true;

        if (reverted) SyncCumulantControlsFromActiveItem();
        UpdateCumulantDisplay();
    }

    private void AnalysisTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        if (tb == CumulantFitMinTextBox || tb == CumulantFitMaxTextBox)
            CumulantFitRangeTextBox_LostFocus(sender, e);
        else if (tb == InversionAlphaTextBox)
            InversionAlphaTextBox_LostFocus(sender, e);
        e.Handled = true;
    }

    private void SetCumulantFitMin(double? value)
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count) return;
        items[idx].Cumulant.FitRangeMinMicroseconds = value;
    }

    private void SetCumulantFitMax(double? value)
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count) return;
        items[idx].Cumulant.FitRangeMaxMicroseconds = value;
    }

    private bool TryCommitCumulantBound(TextBox textBox, Action<double?> apply)
    {
        var raw = (textBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            apply(null);
            return true;
        }
        if (!TryParsePositiveDouble(raw, out var value)) return false;

        apply(value);
        _suppressMetadataControlEvents = true;
        try { textBox.Text = FormatDouble(value); }
        finally { _suppressMetadataControlEvents = false; }
        return true;
    }

    private void SilentTryCommitCumulantBound(TextBox textBox, Action<double?> apply)
    {
        if (_suppressMetadataControlEvents) return;
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count) return;

        var raw = (textBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            apply(null);
            UpdateCumulantDisplay();
            return;
        }
        if (!TryParsePositiveDouble(raw, out var value)) return;
        apply(value);
        UpdateCumulantDisplay();
    }

    private void CumulantShowAsGraphButton_Click(object? sender, RoutedEventArgs e)
        => _host.RequestShowAsGraph(DistributionMode.Correlation);

    // ===================================================================
    // Tab 2: Temperature ramp (lightweight: child computes locally)
    // ===================================================================

    private void UpdateRampDisplay()
    {
        var (points, eligibleCount, missingTemp, missingFit) = BuildTemperatureRampPoints();
        var totalSheets = _host.DatasetItems.Count;
        TemperatureRampPointCountLabel.Text = totalSheets == 0
            ? "(0 点)"
            : $"({eligibleCount}/{totalSheets} 点)";

        var outcome = TemperatureRampAnalyzer.Analyze(points);
        if (!outcome.Success || outcome.Result is null)
        {
            var reason = outcome.FailureReason ?? "解析できません";
            var hints = new List<string>();
            if (missingTemp > 0) hints.Add($"温度未入力 {missingTemp} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            var detail = hints.Count > 0 ? $"（{string.Join(" / ", hints)}）" : string.Empty;
            _rampView.FailWith($"{reason}{detail}");
            return;
        }

        var r = outcome.Result;
        // v1.3 Batch K: 数値結果は ease-out cubic で補間表示。
        NumberCountUp.Animate(RampTransitionTemperatureText, r.TransitionTemperatureCelsius,
            v => $"{v.ToString("0.00", CultureInfo.InvariantCulture)} °C");
        NumberCountUp.Animate(RampTransitionWidthText, r.TransitionWidthCelsius,
            v => $"{v.ToString("0.00", CultureInfo.InvariantCulture)} °C");
        NumberCountUp.Animate(RampLowPlateauText, r.LowPlateauNm,
            v => $"{v.ToString("0.0", CultureInfo.InvariantCulture)} nm");
        NumberCountUp.Animate(RampHighPlateauText, r.HighPlateauNm,
            v => $"{v.ToString("0.0", CultureInfo.InvariantCulture)} nm");
        NumberCountUp.Animate(RampRSquaredText, r.RSquared,
            v => v.ToString("0.0000", CultureInfo.InvariantCulture));

        if (missingTemp > 0 || missingFit > 0)
        {
            var hints = new List<string>();
            if (missingTemp > 0) hints.Add($"温度未入力 {missingTemp} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            _rampView.ShowStatus($"残り {string.Join(" / ", hints)} は除外しました");
        }
        else
        {
            _rampView.HideStatus();
        }
    }

    private (List<TemperatureRampPoint> Points, int EligibleCount, int MissingTemp, int MissingFit)
        BuildTemperatureRampPoints()
    {
        var items = _host.DatasetItems;
        var points = new List<TemperatureRampPoint>(items.Count);
        int missingTemp = 0;
        int missingFit = 0;
        foreach (var item in items)
        {
            var t = item.Metadata.TemperatureCelsius;
            if (t is null || !double.IsFinite(t.Value)) { missingTemp++; continue; }

            var cumulant = CumulantAnalyzer.Analyze(
                item.Dataset.Correlation,
                item.Cumulant.FitRangeMinMicroseconds,
                item.Cumulant.FitRangeMaxMicroseconds);
            if (!cumulant.Success || cumulant.Result is null) { missingFit++; continue; }

            var size = StokesEinstein.Compute(
                cumulant.Result.FirstCumulantPerMicrosecond,
                item.Metadata.TemperatureCelsius,
                item.Metadata.ViscosityMpas,
                item.Metadata.RefractiveIndex,
                item.Metadata.WavelengthNm,
                item.Metadata.ScatteringAngleDegrees);
            if (!size.Success || size.HydrodynamicDiameterNm is null) { missingFit++; continue; }

            points.Add(new TemperatureRampPoint(t.Value, size.HydrodynamicDiameterNm.Value));
        }
        return (points, points.Count, missingTemp, missingFit);
    }

    private void TemperatureRampShowAsGraphButton_Click(object? sender, RoutedEventArgs e)
        => _host.RequestShowAsGraph(DistributionMode.TemperatureRamp);

    // ===================================================================
    // Tab 3: Concentration series (lightweight: child computes locally)
    // ===================================================================

    /// <summary>μm²/s display unit for the diffusion coefficient axis (D × 1e12).</summary>
    private const double DiffusionDisplayScale = 1e12;

    private void UpdateConcentrationDisplay()
    {
        var (points, refTemperatureCelsius, refViscosityMpas, multiTemperature, multiViscosity,
             eligibleCount, missingConc, missingFit) = BuildConcentrationSeriesPoints();

        var totalSheets = _host.DatasetItems.Count;
        ConcentrationSeriesPointCountLabel.Text = totalSheets == 0
            ? "(0 点)"
            : $"({eligibleCount}/{totalSheets} 点)";

        ConcentrationSeriesOutcome outcome;
        if (points.Count == 0
            || double.IsNaN(refTemperatureCelsius) || double.IsNaN(refViscosityMpas))
        {
            outcome = ConcentrationSeriesOutcome.Fail("有効な (c, D) 点がありません");
        }
        else
        {
            outcome = ConcentrationSeriesAnalyzer.Analyze(points, refTemperatureCelsius, refViscosityMpas);
        }

        if (!outcome.Success || outcome.Result is null)
        {
            var reason = outcome.FailureReason ?? "解析できません";
            var hints = new List<string>();
            if (missingConc > 0) hints.Add($"濃度未入力 {missingConc} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            var detail = hints.Count > 0 ? $"（{string.Join(" / ", hints)}）" : string.Empty;
            _concentrationView.FailWith($"{reason}{detail}");
            return;
        }

        var r = outcome.Result;
        var d0Display = r.D0M2PerSecond * DiffusionDisplayScale;
        var d0SeDisplay = r.D0StandardErrorM2PerSecond * DiffusionDisplayScale;
        // v1.3 Batch K: SE が付くフィールドは補間中も同じ SE 文字列を後置する formatter で
        // アニメ対象を中央値だけに絞る。SE 自体は変化頻度が低いので静止表示で十分。
        var d0SeSuffix = d0SeDisplay > 0
            ? $" ± {d0SeDisplay.ToString("0.00", CultureInfo.InvariantCulture)}"
            : string.Empty;
        NumberCountUp.Animate(ConcentrationD0Text, d0Display,
            v => $"{v.ToString("0.00", CultureInfo.InvariantCulture)}{d0SeSuffix} μm²/s");

        var kDSeSuffix = r.KDStandardErrorMlPerGram > 0
            ? $" ± {r.KDStandardErrorMlPerGram.ToString("0.00", CultureInfo.InvariantCulture)}"
            : string.Empty;
        NumberCountUp.Animate(ConcentrationKDText, r.KDmlPerGram,
            v => $"{v.ToString("0.00", CultureInfo.InvariantCulture)}{kDSeSuffix} mL/g");

        NumberCountUp.Animate(ConcentrationDhText, r.HydrodynamicDiameterAtZeroConcentrationNm,
            v => $"{v.ToString("0.0", CultureInfo.InvariantCulture)} nm");
        NumberCountUp.Animate(ConcentrationRSquaredText, r.RSquared,
            v => v.ToString("0.0000", CultureInfo.InvariantCulture));
        ConcentrationReferenceText.Text =
            $"T = {r.ReferenceTemperatureCelsius.ToString("0.#", CultureInfo.InvariantCulture)} °C, η = {r.ReferenceViscosityMpas.ToString("0.000", CultureInfo.InvariantCulture)} mPa·s";

        var warnings = new List<string>();
        if (missingConc > 0) warnings.Add($"濃度未入力 {missingConc} 件");
        if (missingFit > 0) warnings.Add($"キュムラント失敗 {missingFit} 件");
        if (multiTemperature) warnings.Add("シート間で温度が異なります（中央値を使用）");
        if (multiViscosity) warnings.Add("シート間で粘度が異なります（中央値を使用）");

        if (warnings.Count > 0)
            _concentrationView.ShowStatus(string.Join(" / ", warnings));
        else
            _concentrationView.HideStatus();
    }

    private (List<ConcentrationSeriesPoint> Points,
             double ReferenceTemperatureCelsius,
             double ReferenceViscosityMpas,
             bool MultipleTemperatures,
             bool MultipleViscosities,
             int EligibleCount,
             int MissingConcentration,
             int MissingFit) BuildConcentrationSeriesPoints()
    {
        var items = _host.DatasetItems;
        var points = new List<ConcentrationSeriesPoint>(items.Count);
        var temperatures = new List<double>(items.Count);
        var viscosities = new List<double>(items.Count);
        int missingConc = 0;
        int missingFit = 0;

        foreach (var item in items)
        {
            var c = item.Metadata.ConcentrationMgPerMl;
            if (c is null || !double.IsFinite(c.Value) || c.Value < 0) { missingConc++; continue; }

            var cumulant = CumulantAnalyzer.Analyze(
                item.Dataset.Correlation,
                item.Cumulant.FitRangeMinMicroseconds,
                item.Cumulant.FitRangeMaxMicroseconds);
            if (!cumulant.Success || cumulant.Result is null) { missingFit++; continue; }

            var size = StokesEinstein.Compute(
                cumulant.Result.FirstCumulantPerMicrosecond,
                item.Metadata.TemperatureCelsius,
                item.Metadata.ViscosityMpas,
                item.Metadata.RefractiveIndex,
                item.Metadata.WavelengthNm,
                item.Metadata.ScatteringAngleDegrees);
            if (!size.Success || size.DiffusionCoefficientM2PerSecond is null) { missingFit++; continue; }

            points.Add(new ConcentrationSeriesPoint(c.Value, size.DiffusionCoefficientM2PerSecond.Value));
            if (item.Metadata.TemperatureCelsius is double t) temperatures.Add(t);
            if (item.Metadata.ViscosityMpas is double eta) viscosities.Add(eta);
        }

        var refT = temperatures.Count > 0 ? Median(temperatures) : double.NaN;
        var refEta = viscosities.Count > 0 ? Median(viscosities) : double.NaN;
        var multiT = HasSignificantSpread(temperatures, relativeTolerance: 0.005);
        var multiEta = HasSignificantSpread(viscosities, relativeTolerance: 0.01);

        return (points, refT, refEta, multiT, multiEta, points.Count, missingConc, missingFit);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }

    private static bool HasSignificantSpread(List<double> values, double relativeTolerance)
    {
        if (values.Count < 2) return false;
        var min = values.Min();
        var max = values.Max();
        if (min <= 0) return max - min > relativeTolerance;
        return (max - min) / min > relativeTolerance;
    }

    private void ConcentrationShowAsGraphButton_Click(object? sender, RoutedEventArgs e)
        => _host.RequestShowAsGraph(DistributionMode.ConcentrationSeries);

    // ===================================================================
    // Tab 4: CONTIN inversion (passive: parent pushes results)
    // ===================================================================

    private void UpdateInversionDisplay(
        SizeDistributionInversionOutcome? outcome,
        DlsDatasetItem? activeItem,
        int failedCount,
        int missingMetaCount,
        HashSet<string> failureReasons,
        IReadOnlyList<DlsDataset> selectedDatasets)
    {
        if (outcome is null || activeItem is null)
        {
            _inversionView.FailWith("解析対象のシートが見つかりません");
            return;
        }

        if (!outcome.Success || outcome.Result is null)
        {
            var message = outcome.MissingFields.Count > 0
                ? "測定条件を入力してください: " + string.Join(" / ", outcome.MissingFields)
                : outcome.FailureReason ?? "解析できません";
            _inversionView.FailWith(message);
            return;
        }

        var r = outcome.Result;
        // v1.3 Batch K: CONTIN 結果も補間。α は科学表記、R²/β は固定桁。
        // FreeBin は分子だけ補間 (分母 = ビン総数は静止) して "k / N" 形式に組み立てる。
        NumberCountUp.Animate(InversionAlphaText, r.RegularizationAlpha,
            v => v.ToString("0.####E+0", CultureInfo.InvariantCulture));
        NumberCountUp.Animate(InversionRSquaredText, r.RSquared,
            v => v.ToString("0.0000", CultureInfo.InvariantCulture));
        NumberCountUp.Animate(InversionBetaText, r.Beta,
            v => v.ToString("0.000", CultureInfo.InvariantCulture));
        var totalBins = r.Bins.Count;
        NumberCountUp.Animate(InversionFreeBinText, r.FreeBinCount,
            v => $"{Math.Round(v).ToString("0", CultureInfo.InvariantCulture)} / {totalBins}");

        var hints = new List<string>();
        if (selectedDatasets.Count > 1)
            hints.Add($"先頭シート ({selectedDatasets[0].SheetName}) の値を表示");
        if (failedCount > 0) hints.Add($"逆変換失敗 {failedCount} 件");
        if (missingMetaCount > 0) hints.Add($"測定条件不足 {missingMetaCount} 件");
        if (failureReasons.Count > 0) hints.Add(string.Join(" / ", failureReasons));

        if (hints.Count > 0)
            _inversionView.ShowStatus(string.Join(" / ", hints));
        else
            _inversionView.HideStatus();
    }

    private void InversionWeightComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressInversionControlEvents) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag as string;
        _inversionWeight = tag switch
        {
            "Number" => DistributionMode.Number,
            "Volume" => DistributionMode.Volume,
            _ => DistributionMode.Intensity,
        };
        if (_host.SelectedMode == DistributionMode.SizeDistributionInversion)
            _host.RequestPlotRefresh();
    }

    private void InversionAlphaAutoCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressInversionControlEvents) return;
        _inversionUseAutoAlpha = InversionAlphaAutoCheckBox.IsChecked == true;
        InversionAlphaTextBox.IsEnabled = !_inversionUseAutoAlpha;
        if (_host.SelectedMode == DistributionMode.SizeDistributionInversion)
            _host.RequestPlotRefresh();
    }

    private void InversionAlphaTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppressInversionControlEvents) return;
        if (double.TryParse(InversionAlphaTextBox.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) && parsed > 0)
        {
            _inversionManualAlpha = parsed;
        }
        else
        {
            _suppressInversionControlEvents = true;
            try
            {
                InversionAlphaTextBox.Text = _inversionManualAlpha.ToString("0.####", CultureInfo.InvariantCulture);
            }
            finally { _suppressInversionControlEvents = false; }
        }
        if (!_inversionUseAutoAlpha && _host.SelectedMode == DistributionMode.SizeDistributionInversion)
            _host.RequestPlotRefresh();
    }

    private void InversionShowAsGraphButton_Click(object? sender, RoutedEventArgs e)
        => _host.RequestShowAsGraph(DistributionMode.SizeDistributionInversion);

    // ===================================================================
    // v1.3 Batch C: 各セクションの「結果コピー」ボタン
    // 表示中の TextBlock.Text をそのまま読み取って Tab 区切りに組み立てて
    // クリップボードへ流し込む。失敗 (clipboard 取得不可 / SetTextAsync 例外)
    // 時は Toast で Error severity を出す。
    // ===================================================================

    private async void CumulantCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        var lines = new[]
        {
            $"Z-average 径\t{CumulantZAverageText.Text}",
            $"PdI\t{CumulantPdiText.Text}",
            $"Γ\t{CumulantGammaText.Text}",
            $"適用範囲\t{CumulantRangeText.Text}",
            $"R²\t{CumulantRSquaredText.Text}",
        };
        await CopyAnalysisResultAsync("キュムラント", lines);
    }

    private async void TemperatureRampCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        var lines = new[]
        {
            $"T_c (転移点)\t{RampTransitionTemperatureText.Text}",
            $"転移幅 w\t{RampTransitionWidthText.Text}",
            $"低温プラトー d_low\t{RampLowPlateauText.Text}",
            $"高温プラトー d_high\t{RampHighPlateauText.Text}",
            $"R²\t{RampRSquaredText.Text}",
        };
        await CopyAnalysisResultAsync("温度ランプ", lines);
    }

    private async void ConcentrationCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        var lines = new[]
        {
            $"D₀ (c→0)\t{ConcentrationD0Text.Text}",
            $"k_D\t{ConcentrationKDText.Text}",
            $"d_h (c→0)\t{ConcentrationDhText.Text}",
            $"R²\t{ConcentrationRSquaredText.Text}",
            $"参照条件\t{ConcentrationReferenceText.Text}",
        };
        await CopyAnalysisResultAsync("濃度シリーズ", lines);
    }

    private async void InversionCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        var lines = new[]
        {
            $"採用 α\t{InversionAlphaText.Text}",
            $"R²\t{InversionRSquaredText.Text}",
            $"β\t{InversionBetaText.Text}",
            $"自由ビン数\t{InversionFreeBinText.Text}",
        };
        await CopyAnalysisResultAsync("CONTIN", lines);
    }

    private async System.Threading.Tasks.Task CopyAnalysisResultAsync(string label, string[] lines)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Toast?.Show("クリップボードを利用できません", StatusSeverity.Error);
                return;
            }
            var text = string.Join('\n', lines);
            await clipboard.SetTextAsync(text);
            Toast?.Show($"{label}結果をコピーしました", StatusSeverity.Success);
        }
        catch (Exception)
        {
            Toast?.Show("コピーに失敗しました", StatusSeverity.Error);
        }
    }

    // ---------- Local format helpers ----------

    private static string FormatScientific(double value)
    {
        if (!double.IsFinite(value)) return "—";
        return value.ToString("0.###e+0", CultureInfo.InvariantCulture);
    }

    // ===================================================================
    // Tab 1: Measurement metadata (active sheet + broadcast to all sheets)
    // ===================================================================
    //
    // サイドバー時代に MainWindow にあった測定条件編集 UI を 2026-05-10 に
    // AnalysisWindow Tab 1 へ移管。ロジックはそのまま踏襲し、参照を子側 API
    // (_host.DatasetItems / _host.ActiveItemIndex / _initialized) と
    // _host.RequestAnalysisDataChanged() に書き換えただけ。
    //  - 三段構え (TextChanged サイレント / LostFocus + Enter 確定 + reformat)
    //  - broadcastToAllSheets=true: 溶媒 / 屈折率 / 粘度 / 波長 / 散乱角は
    //    全 _host.DatasetItems[i].Metadata に同値を書き込む
    //  - broadcastToAllSheets=false: 温度 / 濃度はアクティブシートのみ更新

    private void SyncMetadataControlsFromActiveItem(bool preserveFocusedTextBox = false)
    {
        // ctor 早期から呼ばれる経路 (Editor 構築前) を守る。Editor 構築後は薄い委譲。
        if (!_initialized) return;
        _metadataEditor.Sync(preserveFocusedTextBox);
    }

    private TextBox? GetFocusedTextBox()
        => FocusManager?.GetFocusedElement() as TextBox;

    // Metadata 7 入力 (溶媒は AutoCompleteBox、他は TextBox) の echo 防止用に、
    // 型を絞らず Control? で返す。DlsMetadataEditor 側で AutoCompleteBox 内部 PART_TextBox の
    // ascendant 判定までやって skip 対象に含める。
    private Control? GetFocusedMetadataInput()
        => FocusManager?.GetFocusedElement() as Control;

    // Cumulant fit TextBox 2 個の Sync 専用。Metadata 7 TextBox 側は DlsMetadataEditor 内に
    // 同型の private static として持つので、Cumulant が main に統合される将来まで両所有のまま。
    private static void SetTextSkippingFocused(TextBox textBox, string newText, TextBox? skip)
    {
        if (ReferenceEquals(textBox, skip)) return;
        textBox.Text = newText;
    }

    // ===== Metadata 7 TextBox のハンドラ =====
    // 実体は DlsMetadataEditor に集約。AXAML が参照する固定名のメソッドだけ 1 行委譲で残す。

    private void MetadataInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not Control ctl) return;
        if (_metadataEditor.OnEnterPressed(ctl)) e.Handled = true;
    }

    private void MetadataTemperatureTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => _metadataEditor.OnTemperatureCommit();
    private void MetadataConcentrationTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => _metadataEditor.OnConcentrationCommit();
    private void MetadataSolventAutoComplete_LostFocus(object? sender, RoutedEventArgs e)
        => _metadataEditor.OnSolventCommit();
    private void MetadataRefractiveIndexTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => _metadataEditor.OnRefractiveIndexCommit();
    private void MetadataViscosityTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => _metadataEditor.OnViscosityCommit();
    private void MetadataWavelengthTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => _metadataEditor.OnWavelengthCommit();
    private void MetadataScatteringAngleTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => _metadataEditor.OnScatteringAngleCommit();

    private void MetadataTemperatureTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        => _metadataEditor.OnTemperatureChanged();
    private void MetadataConcentrationTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        => _metadataEditor.OnConcentrationChanged();
    private void MetadataSolventAutoComplete_TextChanged(object? sender, TextChangedEventArgs e)
        => _metadataEditor.OnSolventChanged();
    private void MetadataRefractiveIndexTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        => _metadataEditor.OnRefractiveIndexChanged();
    private void MetadataViscosityTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        => _metadataEditor.OnViscosityChanged();
    private void MetadataWavelengthTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        => _metadataEditor.OnWavelengthChanged();
    private void MetadataScatteringAngleTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        => _metadataEditor.OnScatteringAngleChanged();

    /// <summary>
    /// AutoCompleteBox から SolventPreset が選択されたら、現在温度 (MetadataTemperatureTextBox)
    /// で温度テーブルを線形補間して屈折率・粘度を即時自動入力 (全シート broadcast)。
    /// 温度未入力時は 25 deg C 既定で補間。プリセットの温度範囲外は端値クランプして
    /// warning toast を出す。選択したプリセットは <c>DlsMetadataEditor</c> に記憶させて、
    /// 以後温度を変えるたびに自動で再補間が走るようにする (鷹栖くん 2026-05-25 仕様変更)。
    /// free-form 入力 (候補にない名前) はここに飛んでこないので既存値を尊重したまま
    /// 溶媒名だけ更新される。
    /// </summary>
    private void MetadataSolventAutoComplete_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (MetadataSolventAutoComplete.SelectedItem is not SolventPreset preset) return;

        var temperatureC = TryParseDouble(MetadataTemperatureTextBox.Text, out var t)
            ? t : 25.0;
        var (n, eta) = SolventPresetStore.Interpolate(preset, temperatureC, out var outOfRange);
        if (!double.IsFinite(n) || !double.IsFinite(eta)) return;

        _metadataEditor.ApplyOpticalParametersFromPreset(n, eta);
        _metadataEditor.RememberAppliedPreset(preset);

        if (outOfRange)
        {
            Toast?.Show(
                $"「{preset.Name}」のプリセット温度範囲外なので端値を使用しました。",
                StatusSeverity.Warning);
        }
    }

    /// <summary>
    /// [＋] ボタン: 現在の溶媒名・屈折率・粘度・温度を 1 温度点としてユーザー追加プリセットに保存。
    /// 同名プリセットが既にあれば温度点を merge (同温度は上書き)。組み込み名との衝突や入力不正は
    /// Toast 警告で拒否。成功時は AutoCompleteBox の ItemsSource を再ロード。
    /// </summary>
    private void SolventPresetSaveCurrentButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        var name = (MetadataSolventAutoComplete.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Toast?.Show("溶媒名を入力してから保存してください。", StatusSeverity.Warning);
            return;
        }

        if (SolventPresetStore.BuiltInPresets.Any(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Toast?.Show($"「{name}」は組み込みプリセットです。", StatusSeverity.Warning);
            return;
        }

        if (!TryParseDouble(MetadataTemperatureTextBox.Text, out var temperatureC) || !double.IsFinite(temperatureC))
        {
            Toast?.Show("温度を入力してから保存してください (温度ごとに点を記録します)。", StatusSeverity.Warning);
            return;
        }

        if (!TryParsePositiveDouble(MetadataRefractiveIndexTextBox.Text, out var refractiveIndex))
        {
            Toast?.Show("屈折率に正の数値を入れてから保存してください。", StatusSeverity.Warning);
            return;
        }

        if (!TryParsePositiveDouble(MetadataViscosityTextBox.Text, out var viscosity))
        {
            Toast?.Show("粘度に正の数値を入れてから保存してください。", StatusSeverity.Warning);
            return;
        }

        var point = new SolventPresetPoint(temperatureC, refractiveIndex, viscosity);
        if (SolventPresetStore.AddUserPoint(name, point))
        {
            RefreshSolventPresetSource(reselectName: name);
            Toast?.Show(
                $"「{name}」に {temperatureC:0.#}°C の値を保存しました。",
                StatusSeverity.Success);
        }
    }

    /// <summary>
    /// [⚙] ボタン: 管理ダイアログをモーダル表示し、戻ってきたら AutoCompleteBox の
    /// ItemsSource を再ロード (ユーザーが削除した分を反映)。
    /// </summary>
    private async void SolventPresetManageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var dialog = new SolventPresetManagerDialog();
        await dialog.ShowDialog(this);
        RefreshSolventPresetSource();
    }

    /// <summary>
    /// AutoCompleteBox の候補を再読み込みする。<paramref name="reselectName"/> が
    /// 渡されたら同名のプリセットを SelectedItem にして見せる (保存直後の確認用)。
    /// SelectionChanged の echo を避けるため _suppressMetadataControlEvents で囲む。
    /// </summary>
    private void RefreshSolventPresetSource(string? reselectName = null)
    {
        var presets = SolventPresetStore.LoadAll();
        _suppressMetadataControlEvents = true;
        try
        {
            MetadataSolventAutoComplete.ItemsSource = presets;
            if (!string.IsNullOrWhiteSpace(reselectName))
            {
                var match = presets.FirstOrDefault(p =>
                    string.Equals(p.Name, reselectName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    MetadataSolventAutoComplete.SelectedItem = match;
                    MetadataSolventAutoComplete.Text = match.Name;
                }
            }
        }
        finally { _suppressMetadataControlEvents = false; }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DlsAnalyzer.Core;
using LabPlot.Core.Avalonia.Helpers;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// DLS 解析の Modeless サブウィンドウ。サイドバーに乗せていた 4 解析セクション
/// (キュムラント / 温度ランプ / 濃度シリーズ / CONTIN) を Tab に集約し、
/// 親 (MainWindow) は <see cref="IDlsAnalysisHost"/> で読み取り API + 通知 event を提供する。
/// 軽量な Cumulant / Ramp / Concentration は子側で再計算するが、重い CONTIN は
/// 親が描画したタイミングで <see cref="OnInversionComputed"/> を介して結果を受け取る passive 戦略。
/// </summary>
internal sealed partial class AnalysisWindow : Window
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

    public AnalysisWindow(IDlsAnalysisHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();

        // 子 Window は Application 経由のスタイル自動適用が走らないので明示適用。
        // Spectrum CalibrationCurveWindow と同方針。
        WindowAppearance.ApplyDefaults(this);

        _host.AnalysisDataChanged += OnHostAnalysisDataChanged;
        _host.ActiveItemChanged += OnHostActiveItemChanged;
        Closed += OnWindowClosed;

        SyncCumulantControlsFromActiveItem();
        UpdateActiveSheetLabel();
        RecomputeActiveTab();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _host.AnalysisDataChanged -= OnHostAnalysisDataChanged;
        _host.ActiveItemChanged -= OnHostActiveItemChanged;
        Closed -= OnWindowClosed;
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
        SyncCumulantControlsFromActiveItem();
        UpdateActiveSheetLabel();
        RecomputeActiveTab();
    }

    private void OnHostActiveItemChanged(object? sender, EventArgs e)
    {
        SyncCumulantControlsFromActiveItem();
        UpdateActiveSheetLabel();
        RecomputeActiveTab();
    }

    private void UpdateActiveSheetLabel()
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count)
        {
            ActiveSheetLabel.Text = items.Count == 0
                ? "(データセット未読み込み)"
                : "(シート未選択)";
            return;
        }
        ActiveSheetLabel.Text = $"アクティブシート: {items[idx].SheetName}";
    }

    private void RecomputeActiveTab()
    {
        if (AnalysisTabs.SelectedIndex switch
        {
            0 => "cumulant",
            1 => "ramp",
            2 => "concentration",
            3 => "inversion",
            _ => null,
        } is string tab)
        {
            switch (tab)
            {
                case "cumulant": UpdateCumulantDisplay(); break;
                case "ramp": UpdateRampDisplay(); break;
                case "concentration": UpdateConcentrationDisplay(); break;
                case "inversion":
                    // CONTIN は passive 戦略: 親が RefreshSizeDistributionInversionPlot を
                    // 走らせたときに OnInversionComputed で push される。Tab 切替だけでは
                    // 重い計算は走らせない。最新値が無いときは hint を表示する。
                    if (InversionAlphaText.Text == "—")
                        ShowInversionStatus("「グラフとして見る」を押すと計算します");
                    break;
            }
        }
    }

    private void AnalysisTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e) => RecomputeActiveTab();

    // ===================================================================
    // Tab 1: Cumulant (active sheet, lightweight, recompute on every event)
    // ===================================================================

    private void SyncCumulantControlsFromActiveItem()
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        bool hasActive = idx >= 0 && idx < items.Count;
        CumulantFitMinTextBox.IsEnabled = hasActive;
        CumulantFitMaxTextBox.IsEnabled = hasActive;

        _suppressMetadataControlEvents = true;
        try
        {
            if (!hasActive)
            {
                CumulantFitMinTextBox.Text = string.Empty;
                CumulantFitMaxTextBox.Text = string.Empty;
            }
            else
            {
                var c = items[idx].Cumulant;
                CumulantFitMinTextBox.Text = FormatNullableDouble(c.FitRangeMinMicroseconds);
                CumulantFitMaxTextBox.Text = FormatNullableDouble(c.FitRangeMaxMicroseconds);
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
            ResetCumulantDisplay();
            ShowCumulantStatus("シートを選択してください");
            return;
        }

        var item = items[idx];
        var correlation = item.Dataset.Correlation;
        if (correlation is null)
        {
            ResetCumulantDisplay();
            ShowCumulantStatus("自己相関データがありません");
            return;
        }

        var outcome = CumulantAnalyzer.Analyze(
            correlation,
            item.Cumulant.FitRangeMinMicroseconds,
            item.Cumulant.FitRangeMaxMicroseconds);

        if (!outcome.Success || outcome.Result is null)
        {
            ResetCumulantDisplay();
            ShowCumulantStatus(outcome.FailureReason ?? "fit に失敗しました");
            return;
        }

        var r = outcome.Result;
        CumulantGammaText.Text = $"{FormatScientific(r.FirstCumulantPerMicrosecond)} μs⁻¹";
        CumulantPdiText.Text = r.PolydispersityIndex.ToString("0.000", CultureInfo.InvariantCulture);
        CumulantRSquaredText.Text = r.RSquared.ToString("0.0000", CultureInfo.InvariantCulture);
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
            CumulantZAverageText.Text = $"{size.HydrodynamicDiameterNm.Value.ToString("0.0", CultureInfo.InvariantCulture)} nm";
            HideCumulantStatus();
        }
        else
        {
            CumulantZAverageText.Text = "—";
            var missing = string.Join("・", size.MissingFields);
            ShowCumulantStatus(string.IsNullOrEmpty(missing)
                ? "粒径計算に必要なメタデータが不足しています"
                : $"{missing} が未入力で粒径計算できません");
        }
    }

    private void ResetCumulantDisplay()
    {
        CumulantZAverageText.Text = "—";
        CumulantPdiText.Text = "—";
        CumulantGammaText.Text = "—";
        CumulantRangeText.Text = "—";
        CumulantRSquaredText.Text = "—";
        HideCumulantStatus();
    }

    private void ShowCumulantStatus(string message)
    {
        CumulantStatusText.Text = message;
        CumulantStatusText.IsVisible = true;
    }

    private void HideCumulantStatus()
    {
        CumulantStatusText.Text = string.Empty;
        CumulantStatusText.IsVisible = false;
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
            ResetRampDisplay();
            var reason = outcome.FailureReason ?? "解析できません";
            var hints = new List<string>();
            if (missingTemp > 0) hints.Add($"温度未入力 {missingTemp} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            var detail = hints.Count > 0 ? $"（{string.Join(" / ", hints)}）" : string.Empty;
            ShowRampStatus($"{reason}{detail}");
            return;
        }

        var r = outcome.Result;
        RampTransitionTemperatureText.Text = $"{r.TransitionTemperatureCelsius.ToString("0.00", CultureInfo.InvariantCulture)} °C";
        RampTransitionWidthText.Text = $"{r.TransitionWidthCelsius.ToString("0.00", CultureInfo.InvariantCulture)} °C";
        RampLowPlateauText.Text = $"{r.LowPlateauNm.ToString("0.0", CultureInfo.InvariantCulture)} nm";
        RampHighPlateauText.Text = $"{r.HighPlateauNm.ToString("0.0", CultureInfo.InvariantCulture)} nm";
        RampRSquaredText.Text = r.RSquared.ToString("0.0000", CultureInfo.InvariantCulture);

        if (missingTemp > 0 || missingFit > 0)
        {
            var hints = new List<string>();
            if (missingTemp > 0) hints.Add($"温度未入力 {missingTemp} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            ShowRampStatus($"残り {string.Join(" / ", hints)} は除外しました");
        }
        else
        {
            HideRampStatus();
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

    private void ResetRampDisplay()
    {
        RampTransitionTemperatureText.Text = "—";
        RampTransitionWidthText.Text = "—";
        RampLowPlateauText.Text = "—";
        RampHighPlateauText.Text = "—";
        RampRSquaredText.Text = "—";
    }

    private void ShowRampStatus(string message)
    {
        RampStatusText.Text = message;
        RampStatusText.IsVisible = true;
    }

    private void HideRampStatus()
    {
        RampStatusText.Text = string.Empty;
        RampStatusText.IsVisible = false;
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
            ResetConcentrationDisplay();
            var reason = outcome.FailureReason ?? "解析できません";
            var hints = new List<string>();
            if (missingConc > 0) hints.Add($"濃度未入力 {missingConc} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            var detail = hints.Count > 0 ? $"（{string.Join(" / ", hints)}）" : string.Empty;
            ShowConcentrationStatus($"{reason}{detail}");
            return;
        }

        var r = outcome.Result;
        var d0Display = r.D0M2PerSecond * DiffusionDisplayScale;
        var d0SeDisplay = r.D0StandardErrorM2PerSecond * DiffusionDisplayScale;
        ConcentrationD0Text.Text = d0SeDisplay > 0
            ? $"{d0Display.ToString("0.00", CultureInfo.InvariantCulture)} ± {d0SeDisplay.ToString("0.00", CultureInfo.InvariantCulture)} μm²/s"
            : $"{d0Display.ToString("0.00", CultureInfo.InvariantCulture)} μm²/s";

        ConcentrationKDText.Text = r.KDStandardErrorMlPerGram > 0
            ? $"{r.KDmlPerGram.ToString("0.00", CultureInfo.InvariantCulture)} ± {r.KDStandardErrorMlPerGram.ToString("0.00", CultureInfo.InvariantCulture)} mL/g"
            : $"{r.KDmlPerGram.ToString("0.00", CultureInfo.InvariantCulture)} mL/g";

        ConcentrationDhText.Text = $"{r.HydrodynamicDiameterAtZeroConcentrationNm.ToString("0.0", CultureInfo.InvariantCulture)} nm";
        ConcentrationRSquaredText.Text = r.RSquared.ToString("0.0000", CultureInfo.InvariantCulture);
        ConcentrationReferenceText.Text =
            $"T = {r.ReferenceTemperatureCelsius.ToString("0.#", CultureInfo.InvariantCulture)} °C, η = {r.ReferenceViscosityMpas.ToString("0.000", CultureInfo.InvariantCulture)} mPa·s";

        var warnings = new List<string>();
        if (missingConc > 0) warnings.Add($"濃度未入力 {missingConc} 件");
        if (missingFit > 0) warnings.Add($"キュムラント失敗 {missingFit} 件");
        if (multiTemperature) warnings.Add("シート間で温度が異なります（中央値を使用）");
        if (multiViscosity) warnings.Add("シート間で粘度が異なります（中央値を使用）");

        if (warnings.Count > 0)
            ShowConcentrationStatus(string.Join(" / ", warnings));
        else
            HideConcentrationStatus();
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

    private void ResetConcentrationDisplay()
    {
        ConcentrationD0Text.Text = "—";
        ConcentrationKDText.Text = "—";
        ConcentrationDhText.Text = "—";
        ConcentrationRSquaredText.Text = "—";
        ConcentrationReferenceText.Text = "—";
    }

    private void ShowConcentrationStatus(string message)
    {
        ConcentrationStatusText.Text = message;
        ConcentrationStatusText.IsVisible = true;
    }

    private void HideConcentrationStatus()
    {
        ConcentrationStatusText.Text = string.Empty;
        ConcentrationStatusText.IsVisible = false;
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
            ResetInversionDisplay();
            ShowInversionStatus("解析対象のシートが見つかりません");
            return;
        }

        if (!outcome.Success || outcome.Result is null)
        {
            ResetInversionDisplay();
            if (outcome.MissingFields.Count > 0)
                ShowInversionStatus("測定条件を入力してください: " + string.Join(" / ", outcome.MissingFields));
            else
                ShowInversionStatus(outcome.FailureReason ?? "解析できません");
            return;
        }

        var r = outcome.Result;
        InversionAlphaText.Text = r.RegularizationAlpha.ToString("0.####E+0", CultureInfo.InvariantCulture);
        InversionRSquaredText.Text = r.RSquared.ToString("0.0000", CultureInfo.InvariantCulture);
        InversionBetaText.Text = r.Beta.ToString("0.000", CultureInfo.InvariantCulture);
        InversionFreeBinText.Text = $"{r.FreeBinCount} / {r.Bins.Count}";

        var hints = new List<string>();
        if (selectedDatasets.Count > 1)
            hints.Add($"先頭シート ({selectedDatasets[0].SheetName}) の値を表示");
        if (failedCount > 0) hints.Add($"逆変換失敗 {failedCount} 件");
        if (missingMetaCount > 0) hints.Add($"測定条件不足 {missingMetaCount} 件");
        if (failureReasons.Count > 0) hints.Add(string.Join(" / ", failureReasons));

        if (hints.Count > 0)
            ShowInversionStatus(string.Join(" / ", hints));
        else
            HideInversionStatus();
    }

    private void ResetInversionDisplay()
    {
        InversionAlphaText.Text = "—";
        InversionRSquaredText.Text = "—";
        InversionBetaText.Text = "—";
        InversionFreeBinText.Text = "—";
    }

    private void ShowInversionStatus(string message)
    {
        InversionStatusText.Text = message;
        InversionStatusText.IsVisible = true;
    }

    private void HideInversionStatus()
    {
        InversionStatusText.Text = string.Empty;
        InversionStatusText.IsVisible = false;
    }

    private void InversionWeightComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressInversionControlEvents) return;
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
        if (_suppressInversionControlEvents) return;
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

    // ---------- Local format helpers ----------

    private static string FormatScientific(double value)
    {
        if (!double.IsFinite(value)) return "—";
        return value.ToString("0.###e+0", CultureInfo.InvariantCulture);
    }

    private static string FormatNullableDouble(double? value)
        => value.HasValue ? FormatDouble(value.Value) : string.Empty;
}

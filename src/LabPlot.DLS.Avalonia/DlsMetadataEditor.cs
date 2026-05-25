using System;
using Avalonia.Controls;
using DlsAnalyzer.Core;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// AnalysisWindow から測定条件 (Metadata) 7 TextBox の Commit / Sync ロジックを切り出した
/// 編集コントローラ。AnalysisWindow.axaml の TextChanged / LostFocus / KeyDown ハンドラは
/// 本クラスに 1 行委譲するだけになり、入力規則 (AnyFinite / NonNegative / Positive) と
/// 三段構え (TextChanged サイレント / Enter・LostFocus 確定) のロジックは全て本クラスに集約。
///
/// suppression フラグ <c>_suppressMetadataControlEvents</c> は AnalysisWindow 内で Cumulant
/// 側 (<c>SilentTryCommitCumulantBound</c> 等) と共有しているため、本クラスは AnalysisWindow
/// から callback (<paramref name="isSuppressed"/> / <paramref name="setSuppressed"/>) で
/// アクセスする。これにより振る舞いを変えずに抽出できる。
/// </summary>
internal sealed class DlsMetadataEditor
{
    private readonly IDlsAnalysisHost _host;
    private readonly Func<TextBox?> _focusedTextBoxProvider;
    private readonly Func<bool> _isSuppressed;
    private readonly Action<bool> _setSuppressed;

    private readonly TextBox _temperature;
    private readonly TextBox _concentration;
    private readonly TextBox _solvent;
    private readonly TextBox _refractiveIndex;
    private readonly TextBox _viscosity;
    private readonly TextBox _wavelength;
    private readonly TextBox _scatteringAngle;

    public DlsMetadataEditor(
        IDlsAnalysisHost host,
        Func<TextBox?> focusedTextBoxProvider,
        Func<bool> isSuppressed,
        Action<bool> setSuppressed,
        TextBox temperature, TextBox concentration, TextBox solvent,
        TextBox refractiveIndex, TextBox viscosity,
        TextBox wavelength, TextBox scatteringAngle)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _focusedTextBoxProvider = focusedTextBoxProvider ?? throw new ArgumentNullException(nameof(focusedTextBoxProvider));
        _isSuppressed = isSuppressed ?? throw new ArgumentNullException(nameof(isSuppressed));
        _setSuppressed = setSuppressed ?? throw new ArgumentNullException(nameof(setSuppressed));
        _temperature = temperature;
        _concentration = concentration;
        _solvent = solvent;
        _refractiveIndex = refractiveIndex;
        _viscosity = viscosity;
        _wavelength = wavelength;
        _scatteringAngle = scatteringAngle;
    }

    // ===== TextChanged: 中間入力サイレント (rollbackOnFail=false / reformatTextOnSuccess=false) =====

    public void OnTemperatureChanged()
        => CommitNumeric(_temperature, NumericConstraint.AnyFinite,
            (item, v) => item.Metadata.TemperatureCelsius = v,
            broadcastToAllSheets: false, rollbackOnFail: false, reformatTextOnSuccess: false);

    public void OnConcentrationChanged()
        => CommitNumeric(_concentration, NumericConstraint.NonNegative,
            (item, v) => item.Metadata.ConcentrationMgPerMl = v,
            broadcastToAllSheets: false, rollbackOnFail: false, reformatTextOnSuccess: false);

    public void OnSolventChanged()
        => CommitString(_solvent, (item, v) => item.Metadata.Solvent = v, broadcastToAllSheets: true);

    public void OnRefractiveIndexChanged()
        => CommitNumeric(_refractiveIndex, NumericConstraint.Positive,
            (item, v) => item.Metadata.RefractiveIndex = v,
            broadcastToAllSheets: true, rollbackOnFail: false, reformatTextOnSuccess: false);

    public void OnViscosityChanged()
        => CommitNumeric(_viscosity, NumericConstraint.Positive,
            (item, v) => item.Metadata.ViscosityMpas = v,
            broadcastToAllSheets: true, rollbackOnFail: false, reformatTextOnSuccess: false);

    public void OnWavelengthChanged()
        => CommitNumeric(_wavelength, NumericConstraint.Positive,
            (item, v) => item.Metadata.WavelengthNm = v,
            broadcastToAllSheets: true, rollbackOnFail: false, reformatTextOnSuccess: false);

    public void OnScatteringAngleChanged()
        => CommitNumeric(_scatteringAngle, NumericConstraint.Positive,
            (item, v) => item.Metadata.ScatteringAngleDegrees = v,
            broadcastToAllSheets: true, rollbackOnFail: false, reformatTextOnSuccess: false);

    // ===== LostFocus / Enter: 確定コミット (rollbackOnFail=true / reformatTextOnSuccess=true) =====

    public void OnTemperatureCommit()
        => CommitNumeric(_temperature, NumericConstraint.AnyFinite,
            (item, v) => item.Metadata.TemperatureCelsius = v,
            broadcastToAllSheets: false, rollbackOnFail: true, reformatTextOnSuccess: true);

    public void OnConcentrationCommit()
        => CommitNumeric(_concentration, NumericConstraint.NonNegative,
            (item, v) => item.Metadata.ConcentrationMgPerMl = v,
            broadcastToAllSheets: false, rollbackOnFail: true, reformatTextOnSuccess: true);

    public void OnSolventCommit()
        => CommitString(_solvent, (item, v) => item.Metadata.Solvent = v, broadcastToAllSheets: true);

    public void OnRefractiveIndexCommit()
        => CommitNumeric(_refractiveIndex, NumericConstraint.Positive,
            (item, v) => item.Metadata.RefractiveIndex = v,
            broadcastToAllSheets: true, rollbackOnFail: true, reformatTextOnSuccess: true);

    public void OnViscosityCommit()
        => CommitNumeric(_viscosity, NumericConstraint.Positive,
            (item, v) => item.Metadata.ViscosityMpas = v,
            broadcastToAllSheets: true, rollbackOnFail: true, reformatTextOnSuccess: true);

    public void OnWavelengthCommit()
        => CommitNumeric(_wavelength, NumericConstraint.Positive,
            (item, v) => item.Metadata.WavelengthNm = v,
            broadcastToAllSheets: true, rollbackOnFail: true, reformatTextOnSuccess: true);

    public void OnScatteringAngleCommit()
        => CommitNumeric(_scatteringAngle, NumericConstraint.Positive,
            (item, v) => item.Metadata.ScatteringAngleDegrees = v,
            broadcastToAllSheets: true, rollbackOnFail: true, reformatTextOnSuccess: true);

    /// <summary>Enter キーで sender に応じた確定コミットへ振り分ける。</summary>
    public bool OnEnterPressed(TextBox sender)
    {
        if (sender == _temperature) OnTemperatureCommit();
        else if (sender == _concentration) OnConcentrationCommit();
        else if (sender == _solvent) OnSolventCommit();
        else if (sender == _refractiveIndex) OnRefractiveIndexCommit();
        else if (sender == _viscosity) OnViscosityCommit();
        else if (sender == _wavelength) OnWavelengthCommit();
        else if (sender == _scatteringAngle) OnScatteringAngleCommit();
        else return false;
        return true;
    }

    // ===== 全 TextBox 同期 (アクティブシート切替・xlsx 読み込み・セッション復元時) =====

    /// <summary>
    /// アクティブシートの metadata で 7 TextBox を塗り直す。
    /// <paramref name="preserveFocusedTextBox"/>=true のときは打鍵中 TextBox を skip
    /// して、入力途中の "25." が "25" に書き戻される echo ループを防ぐ。
    /// シート切替・初期化は false (強制 update) で呼ぶ。
    /// </summary>
    public void Sync(bool preserveFocusedTextBox)
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        bool hasActive = idx >= 0 && idx < items.Count;

        _temperature.IsEnabled = hasActive;
        _concentration.IsEnabled = hasActive;
        _solvent.IsEnabled = hasActive;
        _refractiveIndex.IsEnabled = hasActive;
        _viscosity.IsEnabled = hasActive;
        _wavelength.IsEnabled = hasActive;
        _scatteringAngle.IsEnabled = hasActive;

        var skip = preserveFocusedTextBox ? _focusedTextBoxProvider() : null;

        _setSuppressed(true);
        try
        {
            if (!hasActive)
            {
                SetTextSkippingFocused(_temperature, string.Empty, skip);
                SetTextSkippingFocused(_concentration, string.Empty, skip);
                SetTextSkippingFocused(_solvent, string.Empty, skip);
                SetTextSkippingFocused(_refractiveIndex, string.Empty, skip);
                SetTextSkippingFocused(_viscosity, string.Empty, skip);
                SetTextSkippingFocused(_wavelength, string.Empty, skip);
                SetTextSkippingFocused(_scatteringAngle, string.Empty, skip);
                return;
            }

            var metadata = items[idx].Metadata;
            SetTextSkippingFocused(_temperature, AnalysisWindow.FormatNullableDouble(metadata.TemperatureCelsius), skip);
            SetTextSkippingFocused(_concentration, AnalysisWindow.FormatNullableDouble(metadata.ConcentrationMgPerMl), skip);
            SetTextSkippingFocused(_solvent, metadata.Solvent ?? string.Empty, skip);
            SetTextSkippingFocused(_refractiveIndex, AnalysisWindow.FormatNullableDouble(metadata.RefractiveIndex), skip);
            SetTextSkippingFocused(_viscosity, AnalysisWindow.FormatNullableDouble(metadata.ViscosityMpas), skip);
            SetTextSkippingFocused(_wavelength, AnalysisWindow.FormatNullableDouble(metadata.WavelengthNm), skip);
            SetTextSkippingFocused(_scatteringAngle, AnalysisWindow.FormatNullableDouble(metadata.ScatteringAngleDegrees), skip);
        }
        finally { _setSuppressed(false); }
    }

    private static void SetTextSkippingFocused(TextBox textBox, string newText, TextBox? skip)
    {
        if (ReferenceEquals(textBox, skip)) return;
        textBox.Text = newText;
    }

    // ===== 内部ロジック: 三段構え Commit =====

    private enum NumericConstraint { AnyFinite, NonNegative, Positive }

    private void CommitString(
        TextBox textBox,
        Action<DlsDatasetItem, string?> apply,
        bool broadcastToAllSheets)
    {
        if (_isSuppressed()) return;
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count) return;

        var trimmed = (textBox.Text ?? string.Empty).Trim();
        var value = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;

        if (broadcastToAllSheets)
        {
            for (int i = 0; i < items.Count; i++) apply(items[i], value);
        }
        else
        {
            apply(items[idx], value);
        }

        _host.RequestAnalysisDataChanged();
    }

    private bool CommitNumeric(
        TextBox textBox,
        NumericConstraint constraint,
        Action<DlsDatasetItem, double?> apply,
        bool broadcastToAllSheets,
        bool rollbackOnFail,
        bool reformatTextOnSuccess)
    {
        if (_isSuppressed()) return false;
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count) return false;

        var raw = (textBox.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            ApplyValue(apply, null, broadcastToAllSheets);
            if (reformatTextOnSuccess)
            {
                _setSuppressed(true);
                try { textBox.Text = string.Empty; }
                finally { _setSuppressed(false); }
            }
            _host.RequestAnalysisDataChanged();
            return true;
        }

        bool ok = constraint switch
        {
            NumericConstraint.Positive => TryParsePositiveDouble(raw, out _),
            NumericConstraint.NonNegative => TryParseNonNegativeDouble(raw, out _),
            _ => TryParseDouble(raw, out _),
        };

        if (!ok)
        {
            if (rollbackOnFail) Sync(preserveFocusedTextBox: false);
            return false;
        }

        TryParseDouble(raw, out var value);
        ApplyValue(apply, value, broadcastToAllSheets);

        if (reformatTextOnSuccess)
        {
            _setSuppressed(true);
            try { textBox.Text = FormatDouble(value); }
            finally { _setSuppressed(false); }
        }

        _host.RequestAnalysisDataChanged();
        return true;
    }

    private void ApplyValue(
        Action<DlsDatasetItem, double?> apply, double? value, bool broadcastToAllSheets)
    {
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (broadcastToAllSheets)
        {
            for (int i = 0; i < items.Count; i++) apply(items[i], value);
        }
        else
        {
            if (idx >= 0 && idx < items.Count) apply(items[idx], value);
        }
    }
}

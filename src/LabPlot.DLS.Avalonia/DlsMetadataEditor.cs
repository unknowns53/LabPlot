using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DlsAnalyzer.Core;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// AnalysisWindow から測定条件 (Metadata) 7 入力の Commit / Sync ロジックを切り出した
/// 編集コントローラ。AnalysisWindow.axaml の TextChanged / LostFocus / KeyDown ハンドラは
/// 本クラスに 1 行委譲するだけになり、入力規則 (AnyFinite / NonNegative / Positive) と
/// 三段構え (TextChanged サイレント / Enter・LostFocus 確定) のロジックは全て本クラスに集約。
///
/// 溶媒名のみ <see cref="AutoCompleteBox"/> でプリセット選択を受ける。focus-aware Sync
/// (PR #1 の echo 防止) は AutoCompleteBox 内部 <c>PART_TextBox</c> も含めて判定するため、
/// callback の型を <see cref="Control"/> に格上げしてある。
///
/// suppression フラグ <c>_suppressMetadataControlEvents</c> は AnalysisWindow 内で Cumulant
/// 側 (<c>SilentTryCommitCumulantBound</c> 等) と共有しているため、本クラスは AnalysisWindow
/// から callback (<paramref name="isSuppressed"/> / <paramref name="setSuppressed"/>) で
/// アクセスする。これにより振る舞いを変えずに抽出できる。
/// </summary>
internal sealed class DlsMetadataEditor
{
    private readonly IDlsAnalysisHost _host;
    private readonly Func<Control?> _focusedInputProvider;
    private readonly Func<bool> _isSuppressed;
    private readonly Action<bool> _setSuppressed;

    private readonly TextBox _temperature;
    private readonly TextBox _concentration;
    private readonly AutoCompleteBox _solvent;
    private readonly TextBox _refractiveIndex;
    private readonly TextBox _viscosity;
    private readonly TextBox _wavelength;
    private readonly TextBox _scatteringAngle;

    // 最後にプリセット選択で適用した SolventPreset を覚えておき、温度を後から変えたら
    // 自動で再補間して屈折率・粘度を上書きする (鷹栖くん 2026-05-25 仕様変更)。手動で
    // 屈折率 / 粘度の TextBox を編集したら自動再補間モードを抜ける (lock 解除)。
    // null なら自動再補間を行わない (free-form 入力 or 手動編集後)。
    private SolventPreset? _lastAppliedPreset;

    /// <summary>
    /// 温度変更による自動再補間が補間範囲外で端値クランプを返したときに発火する。
    /// AnalysisWindow が Toast 通知の表示にだけ使う。引数は warning メッセージ。
    /// </summary>
    public event Action<string>? AutoReinterpolationWarning;

    public DlsMetadataEditor(
        IDlsAnalysisHost host,
        Func<Control?> focusedInputProvider,
        Func<bool> isSuppressed,
        Action<bool> setSuppressed,
        TextBox temperature, TextBox concentration, AutoCompleteBox solvent,
        TextBox refractiveIndex, TextBox viscosity,
        TextBox wavelength, TextBox scatteringAngle)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _focusedInputProvider = focusedInputProvider ?? throw new ArgumentNullException(nameof(focusedInputProvider));
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
        => CommitSolventString();

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
    {
        var ok = CommitNumeric(_temperature, NumericConstraint.AnyFinite,
            (item, v) => item.Metadata.TemperatureCelsius = v,
            broadcastToAllSheets: false, rollbackOnFail: true, reformatTextOnSuccess: true);
        if (ok)
        {
            // 温度確定が成功 (空入力含む) したら、保存中の SolventPreset を新しい温度で
            // 再補間して屈折率・粘度を上書き。lock は維持 (続けて温度を弄っても追従する)。
            TryReapplyPresetForCurrentTemperature();
        }
    }

    public void OnConcentrationCommit()
        => CommitNumeric(_concentration, NumericConstraint.NonNegative,
            (item, v) => item.Metadata.ConcentrationMgPerMl = v,
            broadcastToAllSheets: false, rollbackOnFail: true, reformatTextOnSuccess: true);

    public void OnSolventCommit()
        => CommitSolventString();

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
    public bool OnEnterPressed(Control sender)
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

    /// <summary>
    /// プリセット選択で屈折率・粘度を 1 ペアにまとめて全シートに反映する。
    /// 個別の <see cref="OnRefractiveIndexCommit"/> / <see cref="OnViscosityCommit"/>
    /// を順に呼ぶと最初の commit が <c>RequestAnalysisDataChanged</c> を発火 → 親が
    /// <see cref="Sync"/> を呼び戻して、まだ commit されていない側の TextBox を「古い
    /// metadata 値」で塗り直し、続く commit が空文字や旧値を読んで失敗するので、
    /// Commit 経路を使わず metadata に直接書いて 1 回だけ host 通知する。
    /// </summary>
    public void ApplyOpticalParametersFromPreset(double refractiveIndex, double viscosityMpas)
    {
        _setSuppressed(true);
        try
        {
            var items = _host.DatasetItems;
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Metadata.RefractiveIndex = refractiveIndex;
                items[i].Metadata.ViscosityMpas = viscosityMpas;
            }

            _refractiveIndex.Text = FormatDouble(refractiveIndex);
            _viscosity.Text = FormatDouble(viscosityMpas);
        }
        finally { _setSuppressed(false); }

        _host.RequestAnalysisDataChanged();
    }

    /// <summary>
    /// 最後にプリセット選択経由で適用された <see cref="SolventPreset"/> を記憶する。
    /// 以後温度が変わるたびに <see cref="OnTemperatureCommit"/> が同プリセットを
    /// 再補間して屈折率・粘度を上書きする。手動編集で <see cref="ClearAppliedPreset"/>
    /// が呼ばれるとこの記憶は破棄される。
    /// </summary>
    public void RememberAppliedPreset(SolventPreset preset)
    {
        _lastAppliedPreset = preset;
    }

    /// <summary>
    /// 自動再補間モードを抜けて手動編集モードに戻る。屈折率 / 粘度を手で書いた直後と
    /// AnalysisWindow が「明示的に解除したい」と判断したときに使う。
    /// </summary>
    public void ClearAppliedPreset()
    {
        _lastAppliedPreset = null;
    }

    /// <summary>
    /// <see cref="OnTemperatureCommit"/> の末尾で呼ばれる helper。最後に適用された
    /// SolventPreset があるなら、現在温度で再補間して屈折率・粘度を上書きする。
    /// 補間範囲外なら <see cref="AutoReinterpolationWarning"/> 経由で AnalysisWindow に
    /// Toast 用メッセージを渡す。
    /// </summary>
    private void TryReapplyPresetForCurrentTemperature()
    {
        if (_lastAppliedPreset is null) return;
        var raw = (_temperature.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (!TryParseDouble(raw, out var t) || !double.IsFinite(t)) return;

        var (n, eta) = SolventPresetStore.Interpolate(_lastAppliedPreset, t, out var outOfRange);
        if (!double.IsFinite(n) || !double.IsFinite(eta)) return;

        ApplyOpticalParametersFromPreset(n, eta);
        if (outOfRange)
        {
            AutoReinterpolationWarning?.Invoke(
                $"「{_lastAppliedPreset.Name}」のプリセット温度範囲外なので端値を使用しました。");
        }
    }

    // ===== 全 input 同期 (アクティブシート切替・xlsx 読み込み・セッション復元時) =====

    /// <summary>
    /// アクティブシートの metadata で 7 入力を塗り直す。
    /// <paramref name="preserveFocusedTextBox"/>=true のときは打鍵中 input を skip
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

        var skip = preserveFocusedTextBox ? _focusedInputProvider() : null;

        _setSuppressed(true);
        try
        {
            if (!hasActive)
            {
                SetTextSkippingFocused(_temperature, string.Empty, skip);
                SetTextSkippingFocused(_concentration, string.Empty, skip);
                SetSolventTextSkippingFocused(string.Empty, skip);
                SetTextSkippingFocused(_refractiveIndex, string.Empty, skip);
                SetTextSkippingFocused(_viscosity, string.Empty, skip);
                SetTextSkippingFocused(_wavelength, string.Empty, skip);
                SetTextSkippingFocused(_scatteringAngle, string.Empty, skip);
                return;
            }

            var metadata = items[idx].Metadata;
            SetTextSkippingFocused(_temperature, FormatNullableDouble(metadata.TemperatureCelsius), skip);
            SetTextSkippingFocused(_concentration, FormatNullableDouble(metadata.ConcentrationMgPerMl), skip);
            SetSolventTextSkippingFocused(metadata.Solvent ?? string.Empty, skip);
            SetTextSkippingFocused(_refractiveIndex, FormatNullableDouble(metadata.RefractiveIndex), skip);
            SetTextSkippingFocused(_viscosity, FormatNullableDouble(metadata.ViscosityMpas), skip);
            SetTextSkippingFocused(_wavelength, FormatNullableDouble(metadata.WavelengthNm), skip);
            SetTextSkippingFocused(_scatteringAngle, FormatNullableDouble(metadata.ScatteringAngleDegrees), skip);
        }
        finally { _setSuppressed(false); }
    }

    private static void SetTextSkippingFocused(TextBox textBox, string newText, Control? skip)
    {
        if (ReferenceEquals(textBox, skip)) return;
        textBox.Text = newText;
    }

    /// <summary>
    /// AutoCompleteBox の Text を更新するが、内部 <c>PART_TextBox</c> が focus を持って
    /// いるとき (= ユーザーが溶媒名を打鍵中) は skip する。Avalonia の AutoCompleteBox
    /// は外側 (AutoCompleteBox 自体) ではなくテンプレ内 TextBox が focus を取るので、
    /// <see cref="Visual.GetVisualDescendants"/> を辿って判定する。
    /// </summary>
    private void SetSolventTextSkippingFocused(string newText, Control? skip)
    {
        if (skip is not null && IsControlInsideSolventBox(skip)) return;
        _solvent.Text = newText;
    }

    private bool IsControlInsideSolventBox(Control candidate)
    {
        if (ReferenceEquals(candidate, _solvent)) return true;
        foreach (var v in candidate.GetVisualAncestors())
        {
            if (ReferenceEquals(v, _solvent)) return true;
        }
        return false;
    }

    // ===== 内部ロジック: 三段構え Commit =====

    private enum NumericConstraint { AnyFinite, NonNegative, Positive }

    private void CommitSolventString()
    {
        if (_isSuppressed()) return;
        var idx = _host.ActiveItemIndex;
        var items = _host.DatasetItems;
        if (idx < 0 || idx >= items.Count) return;

        var trimmed = (_solvent.Text ?? string.Empty).Trim();
        var value = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;

        // 溶媒名は全シート共通フィールド (broadcastToAllSheets: true 相当)
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Metadata.Solvent = value;
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

        // 屈折率 / 粘度をユーザーが手で編集したら自動再補間モードを抜ける (lock 解除)。
        // ApplyOpticalParametersFromPreset から呼ばれる経路は _setSuppressed(true) で囲まれて
        // いて関数冒頭で早期 return するため、ここに来るのは純粋なユーザー入力経路だけ。
        if (ReferenceEquals(textBox, _refractiveIndex) || ReferenceEquals(textBox, _viscosity))
        {
            _lastAppliedPreset = null;
        }

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

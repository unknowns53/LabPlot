using System;
using System.Collections.Generic;
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

    // 2026-05-25: 不正入力時の Toast 通知用フック。Sync しているときや TextChanged 中の中間
    // parse 失敗 (rollbackOnFail=false 経路) では発火させず、LostFocus / Enter で確定した
    // ときの parse 失敗にだけメッセージを出す。AnalysisWindow が ToastHost を渡す想定。
    private readonly Action<string>? _invalidInputCallback;
    private readonly Dictionary<TextBox, string> _fieldLabels;

    // 溶媒名が一致した最後のプリセットを覚えておく (toast suppression と将来の
    // 高速 lookup 用の cache)。温度確定時の再補間判定そのものは _solvent.Text からの
    // re-lookup を主経路にしているので、このフィールドが何らかの理由で null に戻されても
    // TryReapply 内で復元される (1 キーストロークごとに Sync 経由で AutoCompleteBox 内部
    // 状態が更新されると、ここの cache が失われるケースが実機で確認されたので、cache
    // ではなく毎回 TryFind する方針)。
    private SolventPreset? _lastAppliedPreset;

    // 手動で屈折率 / 粘度を編集したら true に立てる。true の間は温度変更による自動再補間を
    // 抑制する (ユーザーの手入力値を尊重)。溶媒名がプリセットと一致 (adopt) した瞬間に
    // false に戻して自動再補間モードを復帰させる。
    private bool _manualOpticalOverride;

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
        TextBox wavelength, TextBox scatteringAngle,
        Action<string>? invalidInputCallback = null)
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
        _invalidInputCallback = invalidInputCallback;
        _fieldLabels = new Dictionary<TextBox, string>
        {
            [_temperature] = "温度",
            [_concentration] = "濃度",
            [_refractiveIndex] = "屈折率",
            [_viscosity] = "粘度",
            [_wavelength] = "波長",
            [_scatteringAngle] = "散乱角",
        };
    }

    // ===== TextChanged: 中間入力サイレント (rollbackOnFail=false / reformatTextOnSuccess=false) =====

    public void OnTemperatureChanged()
    {
        var ok = CommitNumeric(_temperature, NumericConstraint.AnyFinite,
            (item, v) => item.Metadata.TemperatureCelsius = v,
            broadcastToAllSheets: false, rollbackOnFail: false, reformatTextOnSuccess: false);
        if (ok)
        {
            // 打鍵中も即時に屈折率・粘度を追従させる。LostFocus / Enter まで待たないと
            // 反映されなかった旧仕様 (2026-05-25 指摘) を解消。out-of-range warning は
            // OnTemperatureCommit (LostFocus / Enter) 側でだけ発火して打鍵中の連打を防ぐ。
            TryReapplyPresetForCurrentTemperature(fireOutOfRangeWarning: false);
        }
    }

    public void OnConcentrationChanged()
        => CommitNumeric(_concentration, NumericConstraint.NonNegative,
            (item, v) => item.Metadata.ConcentrationMgPerMl = v,
            broadcastToAllSheets: false, rollbackOnFail: false, reformatTextOnSuccess: false);

    public void OnSolventChanged()
        => CommitSolventString(fireOutOfRangeWarning: true);

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
            TryReapplyPresetForCurrentTemperature(fireOutOfRangeWarning: true);
        }
    }

    public void OnConcentrationCommit()
        => CommitNumeric(_concentration, NumericConstraint.NonNegative,
            (item, v) => item.Metadata.ConcentrationMgPerMl = v,
            broadcastToAllSheets: false, rollbackOnFail: true, reformatTextOnSuccess: true);

    public void OnSolventCommit()
        => CommitSolventString(fireOutOfRangeWarning: true);

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
        // Avalonia TextBox の TextChanged は同期発火と思いきや、Text= 直後の suppress block を
        // 抜けたあと「遅延 echo」として TextChanged を再発火するケースが実機で観測された。
        // この echo を CommitNumeric が拾うと、(a) FormatDouble("0.###") で 3 桁に丸められた
        // 文字列を parse し直して metadata.RefractiveIndex を 1.3346 → 1.335 に縮小し、
        // (b) _manualOpticalOverride を true に立てて、以降の温度変更で TryReapply が
        // skip するようになる ―― 「1 文字目だけ追従、以降止まる」症状の真因。
        //
        // 対策として、metadata 側に書く値も「表示用に丸めた値を parse し直したもの」に揃え、
        // 遅延 echo が parse して書き戻しても metadata と完全に同じ double にする。これで
        // CommitNumeric 内の「parse 値 == metadata」echo 判定で skip でき、override が
        // 立たない。
        var textN = FormatDouble(refractiveIndex);
        var textEta = FormatDouble(viscosityMpas);
        var metaN = TryParseDouble(textN, out var rn) ? rn : refractiveIndex;
        var metaEta = TryParseDouble(textEta, out var re) ? re : viscosityMpas;

        _setSuppressed(true);
        try
        {
            var items = _host.DatasetItems;
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Metadata.RefractiveIndex = metaN;
                items[i].Metadata.ViscosityMpas = metaEta;
            }

            _refractiveIndex.Text = textN;
            _viscosity.Text = textEta;
        }
        finally { _setSuppressed(false); }

        _host.RequestAnalysisDataChanged();
    }

    /// <summary>
    /// 温度や溶媒名の確定後に呼ばれる helper。手動 override が無く、溶媒名がプリセット名と
    /// 一致しているなら、現在温度で再補間して屈折率・粘度を上書きする。温度未入力時は
    /// 25 deg C 既定。<paramref name="fireOutOfRangeWarning"/> = true のときだけ補間範囲外で
    /// <see cref="AutoReinterpolationWarning"/> 経由で AnalysisWindow に Toast 用
    /// メッセージを渡す (TextChanged 連打中の toast 連発を避ける目的)。
    ///
    /// preset 解決は _lastAppliedPreset の cache に頼らず毎回 _solvent.Text から TryFind
    /// する。理由: 1 キーストロークごとに走る host.RequestAnalysisDataChanged → Sync 経路で
    /// AutoCompleteBox 内部状態が触られて _lastAppliedPreset が間接的に null に戻される
    /// ケースが実機で観測された (「最初の 1 文字だけ追従、以降動かない」症状)。cache に
    /// 依存しないことでこの脆さを解消する。
    /// </summary>
    private void TryReapplyPresetForCurrentTemperature(bool fireOutOfRangeWarning)
    {
        if (_manualOpticalOverride) return;

        var solvText = (_solvent.Text ?? string.Empty).Trim();
        if (!SolventPresetStore.TryFind(solvText, out var preset)) return;
        _lastAppliedPreset = preset;

        var raw = (_temperature.Text ?? string.Empty).Trim();
        var t = 25.0;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!TryParseDouble(raw, out var parsed) || !double.IsFinite(parsed)) return;
            t = parsed;
        }

        var (n, eta) = SolventPresetStore.Interpolate(preset, t, out var outOfRange);
        if (!double.IsFinite(n) || !double.IsFinite(eta)) return;

        ApplyOpticalParametersFromPreset(n, eta);
        if (outOfRange && fireOutOfRangeWarning)
        {
            AutoReinterpolationWarning?.Invoke(
                $"「{preset.Name}」のプリセット温度範囲外なので端値を使用しました。");
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

    private void CommitSolventString(bool fireOutOfRangeWarning)
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

        // 溶媒名が SolventPresetStore のプリセット名と完全一致した瞬間、preset を記憶 + 即適用。
        // AutoCompleteBox の SelectionChanged は再選択や typed-match では発火しないケースが
        // あるので TextChanged ベース (本メソッド経由) で判定する方が確実。一致しなければ
        // 自動再補間モードを抜けて、ユーザー手入力の屈折率・粘度を尊重する。
        var presetApplied = AdoptPresetFromCurrentSolventText(fireOutOfRangeWarning);
        if (!presetApplied)
        {
            // ApplyOpticalParametersFromPreset 経路は内部で host 通知するので二重発火を避ける。
            _host.RequestAnalysisDataChanged();
        }
    }

    /// <summary>
    /// 現在の溶媒名テキストが <see cref="SolventPresetStore"/> のプリセット名と一致したら
    /// 記憶 + 即時補間適用。返り値 true なら ApplyOpticalParametersFromPreset 経由で
    /// host 通知が既に走った旨を呼び元に伝える。
    /// </summary>
    private bool AdoptPresetFromCurrentSolventText(bool fireOutOfRangeWarning)
    {
        var text = (_solvent.Text ?? string.Empty).Trim();
        if (!SolventPresetStore.TryFind(text, out var preset))
        {
            // free-form text や空文字。自動再補間する対象がないので manual override も
            // 意味を失う → リセットしておく (ユーザーが後で再度プリセット名を入れたとき
            // にクリーン状態から始められる)。
            _lastAppliedPreset = null;
            _manualOpticalOverride = false;
            return false;
        }

        // OnSolventChanged は打鍵ごとに発火するので、同一プリセットが既に記憶されている
        // ときは toast を抑制 (ユーザーが Water の "r" を消して打ち直したような編集経路で
        // 同じ preset に戻ったときに繰り返し warning が出るのを防ぐ)。
        var presetChanged = !ReferenceEquals(_lastAppliedPreset, preset);

        // プリセット名と一致 = 自動再補間モードに復帰 (もし手動 override が立っていても解除)。
        _manualOpticalOverride = false;
        _lastAppliedPreset = preset;
        TryReapplyPresetForCurrentTemperature(fireOutOfRangeWarning && presetChanged);
        return true;
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

        // 屈折率 / 粘度の場合、Apply が書いた text の遅延 TextChanged echo を弾く。
        // Apply は metadata と text を「FormatDouble("0.###") で丸めた値同士で完全一致」
        // するように書いているので、TextChanged で parse した値が現在の metadata と
        // 完全一致するなら、ユーザー入力ではなく echo (= 何も変えていない) と見なす。
        // この場合は override も立てず metadata 書き戻しもしない。本当のユーザー編集
        // (= text が異なる数値) なら parse 値 != metadata になるので、override が立って
        // 通常の編集経路に進む。
        if (ReferenceEquals(textBox, _refractiveIndex) || ReferenceEquals(textBox, _viscosity))
        {
            var currentMeta = ReferenceEquals(textBox, _refractiveIndex)
                ? items[idx].Metadata.RefractiveIndex
                : items[idx].Metadata.ViscosityMpas;
            var rawForEcho = (textBox.Text ?? string.Empty).Trim();
            if (currentMeta.HasValue
                && TryParseDouble(rawForEcho, out var parsedForEcho)
                && parsedForEcho == currentMeta.Value)
            {
                return false;
            }
            _manualOpticalOverride = true;
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
            if (rollbackOnFail)
            {
                // LostFocus / Enter で確定したのに parse に失敗した時だけ、利用者に理由を Toast で
                // 伝える。TextChanged 中の中間状態 (rollbackOnFail=false) は通知しない。
                NotifyInvalidInput(textBox, constraint, raw);
                Sync(preserveFocusedTextBox: false);
            }
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

    private void NotifyInvalidInput(TextBox textBox, NumericConstraint constraint, string raw)
    {
        if (_invalidInputCallback is null) return;
        var label = _fieldLabels.TryGetValue(textBox, out var l) ? l : "値";
        var constraintText = constraint switch
        {
            NumericConstraint.Positive => "正の数値",
            NumericConstraint.NonNegative => "0 以上の数値",
            _ => "数値",
        };
        var trimmed = raw.Length > 16 ? raw.Substring(0, 16) + "…" : raw;
        var message = string.IsNullOrEmpty(trimmed)
            ? $"「{label}」は {constraintText} を入力してください。"
            : $"「{label}」は {constraintText} を入力してください (入力: '{trimmed}')";
        _invalidInputCallback(message);
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

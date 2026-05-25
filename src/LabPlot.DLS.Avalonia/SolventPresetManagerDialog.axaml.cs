using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// 溶媒プリセット管理ダイアログ。AnalysisWindow から ShowDialog でモーダル表示し、
/// 戻り後に呼び出し側で AutoCompleteBox の ItemsSource を再ロードする。
/// 組み込み 9 種は表示するが削除ボタン無効、ユーザー追加分のみ削除可能。
/// </summary>
public sealed partial class SolventPresetManagerDialog : Window
{
    public SolventPresetManagerDialog()
    {
        InitializeComponent();
        WindowAppearance.ApplyDefaults(this);
        ReloadList();
    }

    private void ReloadList()
    {
        var rows = SolventPresetStore.LoadAll()
            .Select(p => new SolventPresetRowVm(p))
            .ToList();
        PresetListBox.ItemsSource = rows;
    }

    private void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string name) return;

        // 組み込みは CanRemove=false で IsEnabled が落ちているはずだが二重ガード。
        if (SolventPresetStore.BuiltInPresets.Any(p =>
                string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SolventPresetStore.RemoveUser(name);
        ReloadList();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// ListBox 行 1 つの表示用 view model。Detail 文字列を pre-format して
/// DataTemplate の binding を単純化する。温度テーブル化に伴い、Detail は
/// 「N 点 (T_min〜T_max °C)」形式で温度範囲を表示する。
/// </summary>
internal sealed class SolventPresetRowVm
{
    private readonly SolventPreset _preset;

    public SolventPresetRowVm(SolventPreset preset)
    {
        _preset = preset;
        Detail = FormatDetail(preset);
    }

    public string Name => _preset.Name;
    public string Detail { get; }
    public bool IsBuiltIn => _preset.IsBuiltIn;
    public bool CanRemove => !_preset.IsBuiltIn;

    private static string FormatDetail(SolventPreset preset)
    {
        var pts = preset.Points;
        if (pts.Count == 0) return "(温度点なし)";
        if (pts.Count == 1)
        {
            var p = pts[0];
            return string.Format(
                CultureInfo.InvariantCulture,
                "1 点: {0:0.#}°C  n = {1:0.###}  η = {2:0.###} mPa·s",
                p.TemperatureCelsius, p.RefractiveIndex, p.ViscosityMpas);
        }
        var tMin = pts[0].TemperatureCelsius;
        var tMax = pts[^1].TemperatureCelsius;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} 点  {1:0.#} 〜 {2:0.#} °C",
            pts.Count, tMin, tMax);
    }
}

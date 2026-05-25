using System;
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
/// 上段でプリセット名を選ぶと、下段にそのプリセットの温度点 (T, n, η) が並ぶ。
/// 組み込み 9 種は表示するが「全削除」「点削除」ボタンが無効、ユーザー追加分のみ操作可能。
/// </summary>
public sealed partial class SolventPresetManagerDialog : Window
{
    public SolventPresetManagerDialog()
    {
        InitializeComponent();
        WindowAppearance.ApplyDefaults(this);
        ReloadPresetList(reselectName: null);
    }

    private void ReloadPresetList(string? reselectName)
    {
        var rows = SolventPresetStore.LoadAll()
            .Select(p => new SolventPresetRowVm(p))
            .ToList();
        PresetListBox.ItemsSource = rows;

        if (!string.IsNullOrWhiteSpace(reselectName))
        {
            var match = rows.FirstOrDefault(r => string.Equals(
                r.Name, reselectName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                PresetListBox.SelectedItem = match;
                ReloadPointsFor(match.Name);
                return;
            }
        }

        ClearPointsList();
    }

    private void ReloadPointsFor(string presetName)
    {
        if (!SolventPresetStore.TryFind(presetName, out var preset))
        {
            ClearPointsList();
            return;
        }

        var canRemove = !preset.IsBuiltIn;
        var rows = preset.Points
            .Select(pt => new SolventPresetPointVm(preset.Name, pt, canRemove))
            .ToList();
        PresetPointsListBox.ItemsSource = rows;
        PointsHeaderText.Text = string.Format(
            CultureInfo.InvariantCulture,
            "温度点: {0} ({1} 点)",
            preset.Name,
            rows.Count);
    }

    private void ClearPointsList()
    {
        PresetPointsListBox.ItemsSource = Array.Empty<SolventPresetPointVm>();
        PointsHeaderText.Text = "温度点 (プリセットを選択してください)";
    }

    private void PresetListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PresetListBox.SelectedItem is SolventPresetRowVm row)
        {
            ReloadPointsFor(row.Name);
        }
        else
        {
            ClearPointsList();
        }
    }

    private void DeletePresetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string name) return;

        // 組み込みは CanRemove=false で IsEnabled が落ちているはずだが二重ガード。
        if (SolventPresetStore.BuiltInPresets.Any(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SolventPresetStore.RemoveUser(name);
        ReloadPresetList(reselectName: null);
    }

    private void DeletePointButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not SolventPresetPointVm vm) return;
        if (!vm.CanRemove) return;

        SolventPresetStore.RemoveUserPoint(vm.PresetName, vm.TemperatureCelsius);

        // 削除後にプリセット自体が残っているか確認。残っていなければ list を再構築 + 選択クリア、
        // 残っていれば温度点だけ再ロード。
        if (SolventPresetStore.TryFind(vm.PresetName, out _))
        {
            ReloadPresetList(reselectName: vm.PresetName);
        }
        else
        {
            ReloadPresetList(reselectName: null);
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// 上段プリセット行 1 つの表示用 view model。Detail は「点数 + 温度範囲」の要約。
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

/// <summary>
/// 下段温度点 1 つの表示用 view model。削除時に親プリセット名と温度キーを Tag 経由で取り出す。
/// </summary>
internal sealed class SolventPresetPointVm
{
    public SolventPresetPointVm(string presetName, SolventPresetPoint point, bool canRemove)
    {
        PresetName = presetName;
        TemperatureCelsius = point.TemperatureCelsius;
        RefractiveIndex = point.RefractiveIndex;
        ViscosityMpas = point.ViscosityMpas;
        CanRemove = canRemove;
        Detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0,5:0.#}°C    n = {1:0.####}    η = {2:0.####} mPa·s",
            TemperatureCelsius, RefractiveIndex, ViscosityMpas);
    }

    public string PresetName { get; }
    public double TemperatureCelsius { get; }
    public double RefractiveIndex { get; }
    public double ViscosityMpas { get; }
    public bool CanRemove { get; }
    public string Detail { get; }
}

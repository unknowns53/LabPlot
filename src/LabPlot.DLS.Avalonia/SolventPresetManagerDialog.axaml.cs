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
/// DataTemplate の binding を単純化する。
/// </summary>
internal sealed class SolventPresetRowVm
{
    private readonly SolventPreset _preset;

    public SolventPresetRowVm(SolventPreset preset)
    {
        _preset = preset;
        Detail = string.Format(
            CultureInfo.InvariantCulture,
            "n = {0:0.###}    η = {1:0.###} mPa·s",
            preset.RefractiveIndex,
            preset.ViscosityMpas);
    }

    public string Name => _preset.Name;
    public string Detail { get; }
    public bool IsBuiltIn => _preset.IsBuiltIn;
    public bool CanRemove => !_preset.IsBuiltIn;
}

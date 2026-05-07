using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace LabPlot.Spectrum.Avalonia;

/// <summary>
/// Phase 7 Batch 5a: WPF 版 <c>Spectrum_Visualization.CalibrationCurveWindow</c>
/// の Avalonia 移植スタブ。Beer-Lambert 検量線エディタの本実装は Batch 5c
/// (もしくは 5b の続き) で投入する予定。本スタブは AXAML が解決してウィンドウ
/// として開けることだけを保証し、全イベントハンドラは no-op で並べる。
/// </summary>
public partial class CalibrationCurveWindow : Window
{
    public CalibrationCurveWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // ===================== Mode / wavelength / fit =====================

    private void ModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void WavelengthTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    private void RegionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void PathLengthTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    private void FitModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    // ===================== Concentration unit / molar mass =====================

    private void UnitComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void MolarMassTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    // ===================== Sample table =====================

    private void SamplesDataGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) { }

    // ===================== Action buttons =====================

    private void ExportButton_Click(object? sender, RoutedEventArgs e) { }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}

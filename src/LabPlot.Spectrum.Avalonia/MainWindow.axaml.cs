using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.Spectrum.Avalonia;

/// <summary>
/// Phase 7 Batch 5a: WPF 版 <c>Spectrum_Visualization.MainWindow</c> の Avalonia
/// 移植スタブ。AXAML レイアウトは WPF 版と同寸法・同階層で組んであるが、
/// code-behind 側の実装は Batch 5b 以降で投入する。本スタブの責務は
/// (1) AXAML がリソース解決まで通って素のスケルトンが描画されること、
/// (2) <see cref="Window.Opened"/> ハンドラから <see cref="PlotPlaceholder"/>
/// を Empty 状態に切り替えてシマー演出を止めること、の 2 点のみ。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // スケルトンのまま放置せず、空のプレースホルダ表示に差し替える。
        // Batch 5b で実プロット (ScottPlot.Avalonia の AvaPlot) に切り替える前提。
        if (PlotPlaceholderTextBlock is not null)
        {
            PlotPlaceholderTextBlock.Text = "JASCO TXT を開いてください。";
        }
    }

    // ===================== File / dataset =====================

    private void OpenSpectrumButton_Click(object? sender, RoutedEventArgs e) { }

    private void OverlayCheckBox_Changed(object? sender, RoutedEventArgs e) { }

    private void DatasetListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void RemoveDatasetButton_Click(object? sender, RoutedEventArgs e) { }

    // ===================== Line style =====================

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e) { }

    private void LegendNameTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    private void LineWidthTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    private void MarkerSizeTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    // ===================== Axis range / labels / appearance =====================

    private void AxisRangePanel_Committed(object? sender, EventArgs e) { }

    private void GraphLabelTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    private void GraphAppearanceCheckBox_Changed(object? sender, RoutedEventArgs e) { }

    private void GraphFormatPanel_GraphFormatChanged(object? sender, EventArgs e) { }

    private void GraphFormatPanel_AspectRatioChanged(object? sender, EventArgs e) { }

    private void AxisDisplayPanel_AxisOrientationChanged(object? sender, EventArgs e) { }

    private void AxisDisplayPanel_YAxisDisplayChanged(object? sender, EventArgs e) { }

    private void MetadataOption_Changed(object? sender, RoutedEventArgs e) { }

    // ===================== Peak assignment =====================

    private void PeakAssignmentEnableAllButton_Click(object? sender, RoutedEventArgs e) { }

    private void PeakAssignmentDisableAllButton_Click(object? sender, RoutedEventArgs e) { }

    private void PeakAssignmentCheckBox_Changed(object? sender, RoutedEventArgs e) { }

    // ===================== IR peak detection =====================

    private void IrPeakOption_Changed(object? sender, RoutedEventArgs e) { }

    private void IrPeakNumericTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    private void AddManualIrPeakButton_Click(object? sender, RoutedEventArgs e) { }

    private void ClearManualIrPeakButton_Click(object? sender, RoutedEventArgs e) { }

    private void RemoveManualIrPeakButton_Click(object? sender, RoutedEventArgs e) { }

    // ===================== λmax detection =====================

    private void LambdaMaxOption_Changed(object? sender, RoutedEventArgs e) { }

    private void LambdaMaxNumericTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    private void AddManualLambdaMaxButton_Click(object? sender, RoutedEventArgs e) { }

    private void ClearManualLambdaMaxButton_Click(object? sender, RoutedEventArgs e) { }

    private void RemoveManualLambdaMaxButton_Click(object? sender, RoutedEventArgs e) { }

    // ===================== Cloud point (Tc) =====================

    private void CloudPointOption_Changed(object? sender, RoutedEventArgs e) { }

    private void CloudPointNumericTextBox_TextChanged(object? sender, TextChangedEventArgs e) { }

    // ===================== Integration =====================

    private void AddIntegrationRegionButton_Click(object? sender, RoutedEventArgs e) { }

    private void ClearIntegrationRegionsButton_Click(object? sender, RoutedEventArgs e) { }

    private void RemoveIntegrationRegionButton_Click(object? sender, RoutedEventArgs e) { }

    private void ExportIntegrationResultsButton_Click(object? sender, RoutedEventArgs e) { }

    // ===================== Calibration curve =====================

    private void OpenCalibrationEditorButton_Click(object? sender, RoutedEventArgs e) { }

    private void ExportCalibrationResultsButton_Click(object? sender, RoutedEventArgs e) { }

    // ===================== Session / preferences =====================

    private void SaveSessionButton_Click(object? sender, RoutedEventArgs e) { }

    private void LoadSessionButton_Click(object? sender, RoutedEventArgs e) { }

    private void BrowseDefaultOutputDirectoryButton_Click(object? sender, RoutedEventArgs e) { }

    private void ResetGraphSettingsButton_Click(object? sender, RoutedEventArgs e) { }

    private void SaveDefaultFormattingButton_Click(object? sender, RoutedEventArgs e) { }

    // ===================== Toolbar =====================

    private void SaveGraphButton_Click(object? sender, RoutedEventArgs e) { }

    private void ExportDataButton_Click(object? sender, RoutedEventArgs e) { }

    private void PlotContainerBorder_SizeChanged(object? sender, SizeChangedEventArgs e) { }
}

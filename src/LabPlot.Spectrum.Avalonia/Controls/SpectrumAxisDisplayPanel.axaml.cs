using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.Spectrum.Avalonia.Controls;

/// <summary>
/// Spectrum-only sidebar panel that lets the user override the X-axis
/// orientation (Auto / Inverted / Normal) and the Y-axis display mode
/// (Native / Absorbance / Transmittance). Avalonia 版 (Phase 7 Batch 5)。
/// WPF 版 <c>Spectrum_Visualization.Controls.SpectrumAxisDisplayPanel</c>
/// と同じ API surface (InvertXAxisModeTag / YAxisDisplayModeTag /
/// SetInvertXAxisModeTag / SetYAxisDisplayModeTag /
/// AxisOrientationChanged / YAxisDisplayChanged) を保つ。
/// </summary>
public partial class SpectrumAxisDisplayPanel : UserControl
{
    private bool _suppress;

    public SpectrumAxisDisplayPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>X-axis orientation override changed.</summary>
    public event EventHandler? AxisOrientationChanged;

    /// <summary>Y-axis display mode (Native / Absorbance / Transmittance) changed.</summary>
    public event EventHandler? YAxisDisplayChanged;

    /// <summary>Selected X-axis orientation tag ("Auto" / "Inverted" / "Normal").</summary>
    public string? InvertXAxisModeTag => GetComboBoxTag(InvertXAxisComboBox);

    /// <summary>Selected Y-axis display tag ("Native" / "Absorbance" / "Transmittance").</summary>
    public string? YAxisDisplayModeTag => GetComboBoxTag(YAxisDisplayComboBox);

    /// <summary>
    /// Restores the X-axis orientation ComboBox without raising
    /// <see cref="AxisOrientationChanged"/>. Used after applying a session
    /// or formatting config.
    /// </summary>
    public void SetInvertXAxisModeTag(string? tag)
    {
        _suppress = true;
        try
        {
            if (!SelectComboBoxByTag(InvertXAxisComboBox, string.IsNullOrWhiteSpace(tag) ? "Auto" : tag))
            {
                InvertXAxisComboBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>
    /// Restores the Y-axis display ComboBox without raising
    /// <see cref="YAxisDisplayChanged"/>. Used after applying a session
    /// or formatting config, and also from MainWindow when an Absorbance
    /// confirmation dialog flips the mode programmatically.
    /// </summary>
    public void SetYAxisDisplayModeTag(string? tag)
    {
        _suppress = true;
        try
        {
            if (!SelectComboBoxByTag(YAxisDisplayComboBox, string.IsNullOrWhiteSpace(tag) ? "Native" : tag))
            {
                YAxisDisplayComboBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void InvertXAxisComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        AxisOrientationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void YAxisDisplayComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        YAxisDisplayChanged?.Invoke(this, EventArgs.Empty);
    }
}

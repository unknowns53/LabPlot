using System;
using System.Windows.Controls;
using static LabPlot.Core.Wpf.FormatHelpers;

namespace Spectrum_Visualization.Controls;

/// <summary>
/// Spectrum-only sidebar panel that lets the user override the X-axis
/// orientation (Auto / Inverted / Normal) and the Y-axis display mode
/// (Native / Absorbance / Transmittance). Pulled out of the shared
/// <c>GraphFormatPanel</c> so the common control no longer carries
/// app-specific knobs that GPC and DLS would never exercise.
/// </summary>
public partial class SpectrumAxisDisplayPanel : UserControl
{
    private bool _suppress;

    public SpectrumAxisDisplayPanel()
    {
        InitializeComponent();
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

    private void InvertXAxisComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        AxisOrientationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void YAxisDisplayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        YAxisDisplayChanged?.Invoke(this, EventArgs.Empty);
    }
}

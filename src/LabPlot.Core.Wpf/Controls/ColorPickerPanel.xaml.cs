using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using static LabPlot.Core.Wpf.FormatHelpers;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// A reusable colour picker that ties a preset ComboBox, a free-form
/// hex TextBox and a preview Border into one control. Apps fill
/// <see cref="Presets"/> from XAML (the control's content slot accepts
/// <see cref="ComboBoxItem"/> children), set <see cref="AllowAuto"/>
/// for line-colour pickers that need an "Auto (palette)" entry, and
/// listen on <see cref="ColorChanged"/> to react to user edits.
///
/// The hex sentinel "Auto" (case-insensitive) plus empty / whitespace
/// input are treated as Auto when <see cref="AllowAuto"/> is true.
/// Invalid hex input falls back to <see cref="DefaultHex"/> for the
/// preview but <see cref="HexValue"/> reports null so the caller can
/// keep its own default.
/// </summary>
[ContentProperty(nameof(Presets))]
public partial class ColorPickerPanel : UserControl
{
    public static readonly DependencyProperty AllowAutoProperty =
        DependencyProperty.Register(nameof(AllowAuto), typeof(bool), typeof(ColorPickerPanel),
            new PropertyMetadata(false, OnAllowAutoChanged));

    public static readonly DependencyProperty DefaultHexProperty =
        DependencyProperty.Register(nameof(DefaultHex), typeof(string), typeof(ColorPickerPanel),
            new PropertyMetadata("#000000"));

    private bool _suppressEvent;

    public ColorPickerPanel()
    {
        InitializeComponent();
    }

    public bool AllowAuto
    {
        get => (bool)GetValue(AllowAutoProperty);
        set => SetValue(AllowAutoProperty, value);
    }

    public string DefaultHex
    {
        get => (string)GetValue(DefaultHexProperty);
        set => SetValue(DefaultHexProperty, value);
    }

    /// <summary>
    /// XAML content slot. Children added here populate the preset
    /// ComboBox; expected to be <see cref="ComboBoxItem"/>s with their
    /// <c>Tag</c> set to either a "#RRGGBB" hex code, the literal
    /// string "Auto" (only meaningful when <see cref="AllowAuto"/> is
    /// true), or "Custom" for the free-input fallback.
    /// </summary>
    public ItemCollection Presets => PresetComboBox.Items;

    /// <summary>
    /// Localised tooltip shown over the hex TextBox. Bound in XAML so
    /// it updates automatically when <see cref="AllowAuto"/> changes.
    /// </summary>
    public string HexInputToolTip => AllowAuto
        ? "Auto またはカラーコード (#RRGGBB)"
        : "カラーコード (#RRGGBB)";

    /// <summary>
    /// Raised whenever the user changes the colour (preset selection,
    /// hex edit, or Auto / Custom toggle). Suppressed while
    /// <see cref="SetHexValue"/> applies external state.
    /// </summary>
    public event EventHandler? ColorChanged;

    /// <summary>
    /// Returns the current colour as "#RRGGBB" or null when
    /// <see cref="AllowAuto"/> is true and the user picked "Auto" (or
    /// the hex TextBox contains the "Auto" sentinel / is blank).
    /// Invalid hex input also returns null so callers can fall back
    /// to their own default rather than seeing a corrupted value.
    /// </summary>
    public string? HexValue
    {
        get
        {
            if (AllowAuto && IsAutoColorText(HexTextBox.Text)) return null;
            return TryNormalizeHexColorCode(HexTextBox.Text, out var hex) ? hex : null;
        }
    }

    /// <summary>
    /// Writes <paramref name="hex"/> into the panel without raising
    /// <see cref="ColorChanged"/>. null / whitespace / "Auto" picks
    /// the Auto preset when <see cref="AllowAuto"/> is true, otherwise
    /// falls back to <see cref="DefaultHex"/>.
    /// </summary>
    public void SetHexValue(string? hex)
    {
        _suppressEvent = true;
        try
        {
            if (string.IsNullOrWhiteSpace(hex) || IsAutoColorText(hex))
            {
                if (AllowAuto)
                {
                    HexTextBox.Text = "Auto";
                    if (!SelectComboBoxByTag(PresetComboBox, "Auto"))
                    {
                        SelectComboBoxByTag(PresetComboBox, "Custom");
                    }
                }
                else
                {
                    HexTextBox.Text = DefaultHex;
                    if (!SelectComboBoxByTag(PresetComboBox, DefaultHex))
                    {
                        SelectComboBoxByTag(PresetComboBox, "Custom");
                    }
                }
            }
            else
            {
                var normalized = TryNormalizeHexColorCode(hex, out var n) ? n : DefaultHex;
                HexTextBox.Text = normalized;
                if (!SelectComboBoxByTag(PresetComboBox, normalized))
                {
                    SelectComboBoxByTag(PresetComboBox, "Custom");
                }
            }
            UpdatePreview();
        }
        finally
        {
            _suppressEvent = false;
        }
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvent) return;
        if (PresetComboBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag) return;

        _suppressEvent = true;
        try
        {
            if (tag.Equals("Auto", StringComparison.OrdinalIgnoreCase) && AllowAuto)
            {
                HexTextBox.Text = "Auto";
            }
            else if (tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                // Keep whatever the user has typed in the hex TextBox.
            }
            else
            {
                HexTextBox.Text = tag;
            }
        }
        finally
        {
            _suppressEvent = false;
        }

        UpdatePreview();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvent) return;

        _suppressEvent = true;
        try
        {
            if (AllowAuto && IsAutoColorText(HexTextBox.Text))
            {
                if (!SelectComboBoxByTag(PresetComboBox, "Auto"))
                {
                    SelectComboBoxByTag(PresetComboBox, "Custom");
                }
            }
            else if (TryNormalizeHexColorCode(HexTextBox.Text, out var hex))
            {
                if (!SelectComboBoxByTag(PresetComboBox, hex))
                {
                    SelectComboBoxByTag(PresetComboBox, "Custom");
                }
            }
            else
            {
                SelectComboBoxByTag(PresetComboBox, "Custom");
            }
        }
        finally
        {
            _suppressEvent = false;
        }

        UpdatePreview();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePreview()
    {
        string previewHex;
        if (AllowAuto && IsAutoColorText(HexTextBox.Text))
        {
            previewHex = DefaultHex;
        }
        else if (TryNormalizeHexColorCode(HexTextBox.Text, out var hex))
        {
            previewHex = hex;
        }
        else
        {
            previewHex = DefaultHex;
        }
        PreviewBorder.Background = new SolidColorBrush(HexToMediaColor(previewHex));
    }

    private static void OnAllowAutoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // The tooltip text depends on AllowAuto; refresh it directly
        // because HexInputToolTip is a plain CLR property (no INPC).
        if (d is ColorPickerPanel panel)
        {
            panel.HexTextBox.ToolTip = panel.HexInputToolTip;
        }
    }
}

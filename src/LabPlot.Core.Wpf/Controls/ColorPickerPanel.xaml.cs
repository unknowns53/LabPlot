using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using static LabPlot.Core.Wpf.FormatHelpers;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// A reusable colour picker. The main row is a single ComboBox of named
/// presets ("Auto", "Indigo", "Crimson", ..., "Custom") plus a preview
/// swatch. When the user picks "Custom", a sub-panel slides into view
/// containing a hex TextBox and an HSV palette (saturation×value square
/// + hue slider) — that way Auto / preset cases stay one row, and the
/// hex / palette controls only appear when they are actually relevant.
///
/// Apps fill <see cref="Presets"/> from XAML (the control's content slot
/// accepts <see cref="ComboBoxItem"/> children), set <see cref="AllowAuto"/>
/// for line-colour pickers that need an "Auto (palette)" entry, and listen
/// on <see cref="ColorChanged"/> to react to user edits.
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
    // Cached HSV state for the palette markers. Hue is preserved when
    // saturation drops to 0 (otherwise grey would lose its hue identity
    // and the user would lose their picked colour family on each drag).
    private double _hue;        // 0–360
    private double _saturation; // 0–1
    private double _value;      // 0–1
    private bool _isSvDragging;
    private bool _isHueDragging;

    public ColorPickerPanel()
    {
        InitializeComponent();
        // Recompute marker positions once the SV square / hue slider have
        // measured. Without this the very first paint shows markers at
        // (0, 0) until the user interacts.
        SvSquareHost.SizeChanged += (_, _) => UpdateSvMarkerPosition();
        HueSliderHost.SizeChanged += (_, _) => UpdateHueMarkerPosition();
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
    /// hex edit, palette drag, or Auto / Custom toggle). Suppressed
    /// while <see cref="SetHexValue"/> applies external state.
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
            // When the custom panel is hidden the preset ComboBox is the
            // source of truth: in particular, "Auto" must report null
            // even though HexTextBox might still hold a stale value.
            if (AllowAuto && IsAutoPresetSelected()) return null;
            if (CustomPanel.Visibility != Visibility.Visible)
            {
                // Preset case (not Auto, not Custom): pull from the tag.
                if (PresetComboBox.SelectedItem is ComboBoxItem item &&
                    item.Tag is string tag &&
                    !tag.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                    !tag.Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
                    TryNormalizeHexColorCode(tag, out var presetHex))
                {
                    return presetHex;
                }
            }
            if (AllowAuto && IsAutoColorText(HexTextBox.Text)) return null;
            return TryNormalizeHexColorCode(HexTextBox.Text, out var hex) ? hex : null;
        }
    }

    /// <summary>
    /// Writes <paramref name="hex"/> into the panel without raising
    /// <see cref="ColorChanged"/>. null / whitespace / "Auto" picks
    /// the Auto preset when <see cref="AllowAuto"/> is true, otherwise
    /// falls back to <see cref="DefaultHex"/>. A hex value that matches
    /// a preset selects that preset; anything else opens the Custom
    /// panel and seeds the palette markers.
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
            UpdateCustomPanelVisibility();
            UpdatePreview();
            UpdateHsvFromHex();
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
                // If the field is empty seed it with the default so the
                // palette has something concrete to anchor on.
                if (string.IsNullOrWhiteSpace(HexTextBox.Text) || IsAutoColorText(HexTextBox.Text))
                {
                    HexTextBox.Text = DefaultHex;
                }
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

        UpdateCustomPanelVisibility();
        UpdatePreview();
        UpdateHsvFromHex();
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

        UpdateCustomPanelVisibility();
        UpdatePreview();
        UpdateHsvFromHex();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCustomPanelVisibility()
    {
        var show = PresetComboBox.SelectedItem is ComboBoxItem item &&
                   item.Tag is string tag &&
                   tag.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        CustomPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePreview()
    {
        string previewHex;
        if (AllowAuto && IsAutoPresetSelected())
        {
            previewHex = DefaultHex;
        }
        else if (PresetComboBox.SelectedItem is ComboBoxItem item &&
                 item.Tag is string tag &&
                 !tag.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                 !tag.Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
                 TryNormalizeHexColorCode(tag, out var presetHex))
        {
            previewHex = presetHex;
        }
        else if (AllowAuto && IsAutoColorText(HexTextBox.Text))
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

    private bool IsAutoPresetSelected()
    {
        return PresetComboBox.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               tag.Equals("Auto", StringComparison.OrdinalIgnoreCase);
    }

    // ===== HSV palette =====

    private void UpdateHsvFromHex()
    {
        if (!TryNormalizeHexColorCode(HexTextBox.Text, out var hex))
        {
            // Keep the previous HSV so dragging from an invalid edit
            // doesn't snap the markers around unexpectedly.
            return;
        }
        var color = HexToMediaColor(hex);
        var (h, s, v) = RgbToHsv(color.R, color.G, color.B);
        // Preserve the hue when saturation/value collapse to grey/black,
        // so the user's previously-picked hue family survives.
        if (s > 0.001) _hue = h;
        _saturation = s;
        _value = v;
        UpdateHueStopColor();
        UpdateSvMarkerPosition();
        UpdateHueMarkerPosition();
    }

    private void ApplyHsvToHex()
    {
        var (r, g, b) = HsvToRgb(_hue, _saturation, _value);
        var hex = $"#{r:X2}{g:X2}{b:X2}";
        _suppressEvent = true;
        try
        {
            HexTextBox.Text = hex;
            // Reflect the new colour in the preset ComboBox: a palette
            // drag normally lands on a non-preset value, so we expect
            // "Custom" to stay selected. We still try the exact match
            // in case the user dragged onto a preset hue/saturation.
            if (!SelectComboBoxByTag(PresetComboBox, hex))
            {
                SelectComboBoxByTag(PresetComboBox, "Custom");
            }
        }
        finally
        {
            _suppressEvent = false;
        }
        UpdateCustomPanelVisibility();
        UpdatePreview();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHueStopColor()
    {
        var (r, g, b) = HsvToRgb(_hue, 1.0, 1.0);
        SvHueStop.Color = Color.FromRgb(r, g, b);
    }

    private void UpdateSvMarkerPosition()
    {
        var w = SvSquareHost.ActualWidth;
        var h = SvSquareHost.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var x = _saturation * w - SvMarker.Width / 2;
        var y = (1.0 - _value) * h - SvMarker.Height / 2;
        Canvas.SetLeft(SvMarker, x);
        Canvas.SetTop(SvMarker, y);
    }

    private void UpdateHueMarkerPosition()
    {
        var w = HueSliderHost.ActualWidth;
        if (w <= 0) return;
        var x = (_hue / 360.0) * w - HueMarker.Width / 2;
        Canvas.SetLeft(HueMarker, x);
        Canvas.SetTop(HueMarker, 0);
    }

    // ----- Hue slider mouse handlers -----

    private void HueSlider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isHueDragging = true;
        HueSliderHost.CaptureMouse();
        UpdateHueFromMouse(e.GetPosition(HueSliderHost).X);
    }

    private void HueSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isHueDragging) return;
        UpdateHueFromMouse(e.GetPosition(HueSliderHost).X);
    }

    private void HueSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isHueDragging) return;
        _isHueDragging = false;
        HueSliderHost.ReleaseMouseCapture();
    }

    private void UpdateHueFromMouse(double mouseX)
    {
        var w = HueSliderHost.ActualWidth;
        if (w <= 0) return;
        var clamped = Math.Clamp(mouseX, 0, w);
        _hue = (clamped / w) * 360.0;
        if (_hue >= 360) _hue = 0;
        UpdateHueStopColor();
        UpdateHueMarkerPosition();
        ApplyHsvToHex();
    }

    // ----- Saturation/Value square mouse handlers -----

    private void SvSquare_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSvDragging = true;
        SvSquareHost.CaptureMouse();
        UpdateSvFromMouse(e.GetPosition(SvSquareHost));
    }

    private void SvSquare_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSvDragging) return;
        UpdateSvFromMouse(e.GetPosition(SvSquareHost));
    }

    private void SvSquare_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSvDragging) return;
        _isSvDragging = false;
        SvSquareHost.ReleaseMouseCapture();
    }

    private void UpdateSvFromMouse(Point position)
    {
        var w = SvSquareHost.ActualWidth;
        var h = SvSquareHost.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var x = Math.Clamp(position.X, 0, w);
        var y = Math.Clamp(position.Y, 0, h);
        _saturation = x / w;
        _value = 1.0 - (y / h);
        UpdateSvMarkerPosition();
        ApplyHsvToHex();
    }

    // ===== HSV ↔ RGB conversion =====

    /// <summary>
    /// Converts (R, G, B) bytes into HSV. Hue is in [0, 360);
    /// saturation and value are in [0, 1]. Achromatic pixels report
    /// hue = 0 (callers preserving hue should ignore this case).
    /// </summary>
    private static (double h, double s, double v) RgbToHsv(byte rByte, byte gByte, byte bByte)
    {
        double r = rByte / 255.0;
        double g = gByte / 255.0;
        double b = bByte / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                h = 60.0 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                h = 60.0 * (((b - r) / delta) + 2);
            }
            else
            {
                h = 60.0 * (((r - g) / delta) + 4);
            }
        }
        if (h < 0) h += 360;
        double s = max <= 0 ? 0 : delta / max;
        double v = max;
        return (h, s, v);
    }

    /// <summary>
    /// Converts HSV (h ∈ [0, 360), s, v ∈ [0, 1]) into 8-bit RGB.
    /// </summary>
    private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double hh = h / 60.0;
        double x = c * (1 - Math.Abs((hh % 2) - 1));
        double r1, g1, b1;
        if (hh < 1) { r1 = c; g1 = x; b1 = 0; }
        else if (hh < 2) { r1 = x; g1 = c; b1 = 0; }
        else if (hh < 3) { r1 = 0; g1 = c; b1 = x; }
        else if (hh < 4) { r1 = 0; g1 = x; b1 = c; }
        else if (hh < 5) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }
        double m = v - c;
        byte r = (byte)Math.Round((r1 + m) * 255);
        byte g = (byte)Math.Round((g1 + m) * 255);
        byte b = (byte)Math.Round((b1 + m) * 255);
        return (r, g, b);
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

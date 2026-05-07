using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Metadata;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// Avalonia port of <c>LabPlot.Core.Wpf.Controls.ColorPickerPanel</c>.
/// Single ComboBox of named presets ("Auto", "Indigo", "Crimson", ...,
/// "Custom") plus a preview swatch on the main row, with a sub-panel
/// (hex TextBox + HSV palette) appearing only when the user selects
/// "Custom". The HSV ↔ RGB conversion logic is identical to the WPF
/// version; only the input plumbing (Mouse → Pointer, Visibility →
/// IsVisible, DependencyProperty → StyledProperty, ContentProperty →
/// [Content]) differs.
///
/// Apps fill <see cref="Presets"/> from XAML by writing
/// <c>ComboBoxItem</c> children directly inside the panel tag (the
/// <see cref="ContentAttribute"/> redirects the panel's default content
/// slot to the Preset ComboBox's Items collection), set
/// <see cref="AllowAuto"/> for line-colour pickers that need an "Auto
/// (palette)" entry, and listen on <see cref="ColorChanged"/> to react
/// to user edits. <see cref="HexValue"/> reports null when "Auto" is
/// active so callers can keep their own default.
/// </summary>
public partial class ColorPickerPanel : UserControl
{
    public static readonly StyledProperty<bool> AllowAutoProperty =
        AvaloniaProperty.Register<ColorPickerPanel, bool>(nameof(AllowAuto), false);

    public static readonly StyledProperty<string> DefaultHexProperty =
        AvaloniaProperty.Register<ColorPickerPanel, string>(nameof(DefaultHex), "#000000");

    private bool _suppressEvent;
    // Cached HSV state for the palette markers. Hue is preserved when
    // saturation drops to 0 (otherwise grey would lose its hue identity
    // and the user would lose their picked colour family on each drag).
    private double _hue;        // 0–360
    private double _saturation; // 0–1
    private double _value;      // 0–1
    private bool _isSvDragging;
    private bool _isHueDragging;

    private ComboBox? _presetComboBox;
    private Border? _previewBorder;
    private Border? _customPanel;
    private TextBox? _hexTextBox;
    private Rectangle? _svHueRect;
    private Grid? _svSquareHost;
    private Ellipse? _svMarker;
    private Grid? _hueSliderHost;
    private Border? _hueMarker;

    public ColorPickerPanel()
    {
        InitializeComponent();

        _presetComboBox = this.FindControl<ComboBox>("PresetComboBox");
        _previewBorder = this.FindControl<Border>("PreviewBorder");
        _customPanel = this.FindControl<Border>("CustomPanel");
        _hexTextBox = this.FindControl<TextBox>("HexTextBox");
        _svHueRect = this.FindControl<Rectangle>("SvHueRect");
        _svSquareHost = this.FindControl<Grid>("SvSquareHost");
        _svMarker = this.FindControl<Ellipse>("SvMarker");
        _hueSliderHost = this.FindControl<Grid>("HueSliderHost");
        _hueMarker = this.FindControl<Border>("HueMarker");

        // Recompute marker positions once the SV square / hue slider have
        // measured. Without this the very first paint shows markers at
        // (0, 0) until the user interacts. Avalonia の SizeChanged は
        // WPF と同名イベントで発火タイミングも同じ。
        if (_svSquareHost is not null)
            _svSquareHost.SizeChanged += (_, _) => UpdateSvMarkerPosition();
        if (_hueSliderHost is not null)
            _hueSliderHost.SizeChanged += (_, _) => UpdateHueMarkerPosition();
    }

    // Avalonia.Generators が partial class に InitializeComponent + x:Name フィールド代入を
    // 自動生成するので手動定義しない。

    public bool AllowAuto
    {
        get => GetValue(AllowAutoProperty);
        set => SetValue(AllowAutoProperty, value);
    }

    public string DefaultHex
    {
        get => GetValue(DefaultHexProperty);
        set => SetValue(DefaultHexProperty, value);
    }

    /// <summary>
    /// Preset ComboBox の Items コレクションへのアクセサ。外側 XAML から
    /// は <c>&lt;controls:ColorPickerPanel.Presets&gt;&lt;ComboBoxItem .../&gt;
    /// &lt;/controls:ColorPickerPanel.Presets&gt;</c> の明示書きで子要素を
    /// 流し込む。各 ComboBoxItem の <c>Tag</c> は "#RRGGBB" hex / "Auto"
    /// (<see cref="AllowAuto"/> = true のとき) / "Custom"（自由入力フォール
    /// バック）のいずれか。WPF 版のような <c>[Content]</c> 属性の暗黙
    /// 転送は使わない方針: Avalonia の <c>[Content]</c> は UserControl 自身
    /// の axaml ロード中にも getter を呼びに来るため、ここでは安全な
    /// explicit 構文で受ける（Phase 7 Batch 6 で発覚した起動時クラッシュ
    /// "PresetComboBox is not initialised yet" の回避）。
    /// </summary>
    public ItemCollection Presets => _presetComboBox?.Items
        ?? throw new InvalidOperationException("ColorPickerPanel: PresetComboBox is not initialised yet.");

    /// <summary>
    /// Localised tooltip shown over the hex TextBox. Bound in XAML so
    /// it updates automatically when <see cref="AllowAuto"/> changes
    /// (binding is re-evaluated via the OnPropertyChanged hook below).
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
            if (_presetComboBox is null || _hexTextBox is null || _customPanel is null)
                return null;

            // When the custom panel is hidden the preset ComboBox is the
            // source of truth: in particular, "Auto" must report null
            // even though HexTextBox might still hold a stale value.
            if (AllowAuto && IsAutoPresetSelected()) return null;
            if (!_customPanel.IsVisible)
            {
                // Preset case (not Auto, not Custom): pull from the tag.
                if (_presetComboBox.SelectedItem is ComboBoxItem item &&
                    item.Tag is string tag &&
                    !tag.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                    !tag.Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
                    TryNormalizeHexColorCode(tag, out var presetHex))
                {
                    return presetHex;
                }
            }
            if (AllowAuto && IsAutoColorText(_hexTextBox.Text)) return null;
            return TryNormalizeHexColorCode(_hexTextBox.Text, out var hex) ? hex : null;
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
        if (_presetComboBox is null || _hexTextBox is null) return;

        _suppressEvent = true;
        try
        {
            if (string.IsNullOrWhiteSpace(hex) || IsAutoColorText(hex))
            {
                if (AllowAuto)
                {
                    _hexTextBox.Text = "Auto";
                    if (!SelectComboBoxByTag(_presetComboBox, "Auto"))
                    {
                        SelectComboBoxByTag(_presetComboBox, "Custom");
                    }
                }
                else
                {
                    _hexTextBox.Text = DefaultHex;
                    if (!SelectComboBoxByTag(_presetComboBox, DefaultHex))
                    {
                        SelectComboBoxByTag(_presetComboBox, "Custom");
                    }
                }
            }
            else
            {
                var normalized = TryNormalizeHexColorCode(hex, out var n) ? n : DefaultHex;
                _hexTextBox.Text = normalized;
                if (!SelectComboBoxByTag(_presetComboBox, normalized))
                {
                    SelectComboBoxByTag(_presetComboBox, "Custom");
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

    /// <summary>
    /// AllowAuto は ToolTip 文字列の出力を変えるが、CLR プロパティ
    /// HexInputToolTip には INotifyPropertyChanged が無いので Binding が
    /// 自動更新できない。WPF 版は OnAllowAutoChanged で TextBox.ToolTip
    /// に直接代入していたので、Avalonia でも同じ手で OnPropertyChanged を
    /// hook して ToolTip.SetTip(...) で書き直す。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AllowAutoProperty && _hexTextBox is not null)
        {
            ToolTip.SetTip(_hexTextBox, HexInputToolTip);
        }
    }

    private void PresetComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvent) return;
        if (_presetComboBox is null || _hexTextBox is null) return;
        if (_presetComboBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag) return;

        _suppressEvent = true;
        try
        {
            if (tag.Equals("Auto", StringComparison.OrdinalIgnoreCase) && AllowAuto)
            {
                _hexTextBox.Text = "Auto";
            }
            else if (tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                // Keep whatever the user has typed in the hex TextBox.
                // If the field is empty seed it with the default so the
                // palette has something concrete to anchor on.
                if (string.IsNullOrWhiteSpace(_hexTextBox.Text) || IsAutoColorText(_hexTextBox.Text))
                {
                    _hexTextBox.Text = DefaultHex;
                }
            }
            else
            {
                _hexTextBox.Text = tag;
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

    private void HexTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvent) return;
        if (_presetComboBox is null || _hexTextBox is null) return;

        _suppressEvent = true;
        try
        {
            if (AllowAuto && IsAutoColorText(_hexTextBox.Text))
            {
                if (!SelectComboBoxByTag(_presetComboBox, "Auto"))
                {
                    SelectComboBoxByTag(_presetComboBox, "Custom");
                }
            }
            else if (TryNormalizeHexColorCode(_hexTextBox.Text, out var hex))
            {
                if (!SelectComboBoxByTag(_presetComboBox, hex))
                {
                    SelectComboBoxByTag(_presetComboBox, "Custom");
                }
            }
            else
            {
                SelectComboBoxByTag(_presetComboBox, "Custom");
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
        if (_presetComboBox is null || _customPanel is null) return;
        var show = _presetComboBox.SelectedItem is ComboBoxItem item &&
                   item.Tag is string tag &&
                   tag.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        _customPanel.IsVisible = show;
    }

    private void UpdatePreview()
    {
        if (_previewBorder is null || _hexTextBox is null) return;

        string previewHex;
        if (AllowAuto && IsAutoPresetSelected())
        {
            previewHex = DefaultHex;
        }
        else if (_presetComboBox?.SelectedItem is ComboBoxItem item &&
                 item.Tag is string tag &&
                 !tag.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                 !tag.Equals("Custom", StringComparison.OrdinalIgnoreCase) &&
                 TryNormalizeHexColorCode(tag, out var presetHex))
        {
            previewHex = presetHex;
        }
        else if (AllowAuto && IsAutoColorText(_hexTextBox.Text))
        {
            previewHex = DefaultHex;
        }
        else if (TryNormalizeHexColorCode(_hexTextBox.Text, out var hex))
        {
            previewHex = hex;
        }
        else
        {
            previewHex = DefaultHex;
        }
        _previewBorder.Background = new SolidColorBrush(HexToAvaloniaColor(previewHex));
    }

    private bool IsAutoPresetSelected()
    {
        return _presetComboBox?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               tag.Equals("Auto", StringComparison.OrdinalIgnoreCase);
    }

    // ===== HSV palette =====

    private void UpdateHsvFromHex()
    {
        if (_hexTextBox is null) return;
        if (!TryNormalizeHexColorCode(_hexTextBox.Text, out var hex))
        {
            // Keep the previous HSV so dragging from an invalid edit
            // doesn't snap the markers around unexpectedly.
            return;
        }
        var color = HexToAvaloniaColor(hex);
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
        if (_hexTextBox is null || _presetComboBox is null) return;

        var (r, g, b) = HsvToRgb(_hue, _saturation, _value);
        var hex = $"#{r:X2}{g:X2}{b:X2}";
        _suppressEvent = true;
        try
        {
            _hexTextBox.Text = hex;
            // Reflect the new colour in the preset ComboBox: a palette
            // drag normally lands on a non-preset value, so we expect
            // "Custom" to stay selected. We still try the exact match
            // in case the user dragged onto a preset hue/saturation.
            if (!SelectComboBoxByTag(_presetComboBox, hex))
            {
                SelectComboBoxByTag(_presetComboBox, "Custom");
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
        if (_svHueRect is null) return;
        var (r, g, b) = HsvToRgb(_hue, 1.0, 1.0);
        var hueColor = Color.FromRgb(r, g, b);

        // Avalonia の XAML compiler が GradientStop に名前を付けさせない
        // ので、Rectangle.Fill を毎回 LinearGradientBrush ごと再生成する。
        // White → 現在の Hue 色のグラデを relative 0%→100% で構築。
        var brush = new LinearGradientBrush
        {
            StartPoint = RelativePoint.TopLeft,
            EndPoint = new RelativePoint(1.0, 0.0, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(Colors.White, 0.0));
        brush.GradientStops.Add(new GradientStop(hueColor, 1.0));
        _svHueRect.Fill = brush;
    }

    private void UpdateSvMarkerPosition()
    {
        if (_svSquareHost is null || _svMarker is null) return;
        var w = _svSquareHost.Bounds.Width;
        var h = _svSquareHost.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var x = _saturation * w - _svMarker.Width / 2;
        var y = (1.0 - _value) * h - _svMarker.Height / 2;
        Canvas.SetLeft(_svMarker, x);
        Canvas.SetTop(_svMarker, y);
    }

    private void UpdateHueMarkerPosition()
    {
        if (_hueSliderHost is null || _hueMarker is null) return;
        var w = _hueSliderHost.Bounds.Width;
        if (w <= 0) return;
        var x = (_hue / 360.0) * w - _hueMarker.Width / 2;
        Canvas.SetLeft(_hueMarker, x);
        Canvas.SetTop(_hueMarker, 0);
    }

    // ----- Hue slider pointer handlers -----
    // Avalonia の Pointer 系は Mouse とは独立した抽象で、Touch / Pen も
    // 同じイベントに乗ってくる。CaptureMouse / ReleaseMouseCapture は
    // e.Pointer.Capture(target) / e.Pointer.Capture(null) に置き換える。

    private void HueSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_hueSliderHost is null) return;
        if (!e.GetCurrentPoint(_hueSliderHost).Properties.IsLeftButtonPressed) return;

        _isHueDragging = true;
        e.Pointer.Capture(_hueSliderHost);
        UpdateHueFromPointer(e.GetPosition(_hueSliderHost).X);
    }

    private void HueSlider_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isHueDragging || _hueSliderHost is null) return;
        UpdateHueFromPointer(e.GetPosition(_hueSliderHost).X);
    }

    private void HueSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isHueDragging) return;
        _isHueDragging = false;
        e.Pointer.Capture(null);
    }

    private void UpdateHueFromPointer(double pointerX)
    {
        if (_hueSliderHost is null) return;
        var w = _hueSliderHost.Bounds.Width;
        if (w <= 0) return;
        var clamped = Math.Clamp(pointerX, 0, w);
        _hue = (clamped / w) * 360.0;
        if (_hue >= 360) _hue = 0;
        UpdateHueStopColor();
        UpdateHueMarkerPosition();
        ApplyHsvToHex();
    }

    // ----- Saturation/Value square pointer handlers -----

    private void SvSquare_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_svSquareHost is null) return;
        if (!e.GetCurrentPoint(_svSquareHost).Properties.IsLeftButtonPressed) return;

        _isSvDragging = true;
        e.Pointer.Capture(_svSquareHost);
        UpdateSvFromPointer(e.GetPosition(_svSquareHost));
    }

    private void SvSquare_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSvDragging || _svSquareHost is null) return;
        UpdateSvFromPointer(e.GetPosition(_svSquareHost));
    }

    private void SvSquare_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSvDragging) return;
        _isSvDragging = false;
        e.Pointer.Capture(null);
    }

    private void UpdateSvFromPointer(Point position)
    {
        if (_svSquareHost is null) return;
        var w = _svSquareHost.Bounds.Width;
        var h = _svSquareHost.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var x = Math.Clamp(position.X, 0, w);
        var y = Math.Clamp(position.Y, 0, h);
        _saturation = x / w;
        _value = 1.0 - (y / h);
        UpdateSvMarkerPosition();
        ApplyHsvToHex();
    }

    // ===== HSV ↔ RGB conversion =====
    // 数学的に純粋なロジックなので WPF 版から完全コピー。

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
}

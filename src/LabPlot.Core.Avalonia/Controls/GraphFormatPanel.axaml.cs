using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LabPlot.Core;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// Avalonia port of <c>LabPlot.Core.Wpf.Controls.GraphFormatPanel</c>.
/// Hosts the font / ticks / frame &amp; grid / background / legend Expanders
/// and exposes a Capture/Apply pair driven by
/// <see cref="GraphFormattingConfigBase"/>, so the per-app MainWindow only
/// has to forward shared properties. WPF 版と同じ public API
/// (Capture / Apply / TogglePlotGrid / SyncLegendPlacement /
/// GraphFormatChanged / AspectRatioChanged / AspectRatioTag /
/// AspectRatioValue / ShowLineStyle / ShowLabelEditing) を維持する。
/// </summary>
/// <remarks>
/// Avalonia 固有の置換点:
/// <list type="bullet">
/// <item>WPF の IsEditable ComboBox は Avalonia には存在しないため、
/// フォント選択は <see cref="AutoCompleteBox"/> に置き換え。候補リストは
/// code-behind で <c>ItemsSource</c> に流し込み、Text プロパティ + TextChanged
/// で自由入力を吸い上げる。</item>
/// <item>WPF の <c>Checked</c> / <c>Unchecked</c> 二本立ては Avalonia では
/// <c>IsCheckedChanged</c> 一本に統一する。</item>
/// <item>DependencyProperty → StyledProperty に置換、ShowLineStyle /
/// ShowLabelEditing は API 互換のため将来拡張用 placeholder として維持。</item>
/// </list>
/// </remarks>
public partial class GraphFormatPanel : UserControl
{
    private static readonly string[] GraphFontOptions =
    {
        "Auto",
        "Yu Gothic",
        "Meiryo",
        "Arial",
        "Times New Roman",
        "MS Gothic",
    };

    private bool _suppress;

    private AutoCompleteBox? _graphFontComboBox;
    private TextBox? _graphFontSizeTextBox;
    private CheckBox? _yAxisTickLabelsCheckBox;
    private CheckBox? _majorTicksCheckBox;
    private CheckBox? _minorTicksCheckBox;
    private TextBox? _tickDensityTextBox;
    private TextBox? _tickWidthTextBox;
    private CheckBox? _plotGridCheckBox;
    private CheckBox? _plotFrameCheckBox;
    private TextBox? _plotFrameWidthTextBox;
    private ColorPickerPanel? _plotFrameColorPicker;
    private ColorPickerPanel? _backgroundColorPicker;
    private ComboBox? _aspectRatioComboBox;
    private ComboBox? _legendVisibilityComboBox;
    private ComboBox? _legendPositionComboBox;
    private TextBox? _legendOffsetXTextBox;
    private TextBox? _legendOffsetYTextBox;
    private TextBox? _legendFontSizeTextBox;

    public GraphFormatPanel()
    {
        InitializeComponent();

        _graphFontComboBox = this.FindControl<AutoCompleteBox>("GraphFontComboBox");
        _graphFontSizeTextBox = this.FindControl<TextBox>("GraphFontSizeTextBox");
        _yAxisTickLabelsCheckBox = this.FindControl<CheckBox>("YAxisTickLabelsCheckBox");
        _majorTicksCheckBox = this.FindControl<CheckBox>("MajorTicksCheckBox");
        _minorTicksCheckBox = this.FindControl<CheckBox>("MinorTicksCheckBox");
        _tickDensityTextBox = this.FindControl<TextBox>("TickDensityTextBox");
        _tickWidthTextBox = this.FindControl<TextBox>("TickWidthTextBox");
        _plotGridCheckBox = this.FindControl<CheckBox>("PlotGridCheckBox");
        _plotFrameCheckBox = this.FindControl<CheckBox>("PlotFrameCheckBox");
        _plotFrameWidthTextBox = this.FindControl<TextBox>("PlotFrameWidthTextBox");
        _plotFrameColorPicker = this.FindControl<ColorPickerPanel>("PlotFrameColorPicker");
        _backgroundColorPicker = this.FindControl<ColorPickerPanel>("BackgroundColorPicker");
        _aspectRatioComboBox = this.FindControl<ComboBox>("AspectRatioComboBox");
        _legendVisibilityComboBox = this.FindControl<ComboBox>("LegendVisibilityComboBox");
        _legendPositionComboBox = this.FindControl<ComboBox>("LegendPositionComboBox");
        _legendOffsetXTextBox = this.FindControl<TextBox>("LegendOffsetXTextBox");
        _legendOffsetYTextBox = this.FindControl<TextBox>("LegendOffsetYTextBox");
        _legendFontSizeTextBox = this.FindControl<TextBox>("LegendFontSizeTextBox");

        // AutoCompleteBox に候補一覧を流し込み、初期値を "Auto" に。
        // WPF 版の <ComboBoxItem Tag="..." /> 列挙の代替として code-behind で
        // 一括設定する形に統一。Watermark は xaml 側で "Auto" を指定済みなので、
        // Text 空のときも UI 上は Auto と表示される。
        if (_graphFontComboBox is not null)
        {
            _graphFontComboBox.ItemsSource = GraphFontOptions;
            _graphFontComboBox.Text = "Auto";
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Bubble for any change that should re-capture the formatting config.</summary>
    public event EventHandler? GraphFormatChanged;

    /// <summary>
    /// Fired when <c>AspectRatioComboBox</c> changes. Separate from
    /// <see cref="GraphFormatChanged"/> because some apps need to resize the
    /// plot host border (a layout side-effect that should not run on every
    /// font / tick change).
    /// </summary>
    public event EventHandler? AspectRatioChanged;

    public static readonly StyledProperty<bool> ShowLineStyleProperty =
        AvaloniaProperty.Register<GraphFormatPanel, bool>(nameof(ShowLineStyle), false);

    /// <summary>
    /// Reserved for Phase 5 Batch 5/6: pin per-dataset line style controls
    /// inside this panel. Currently a no-op placeholder. WPF 版と API 互換。
    /// </summary>
    public bool ShowLineStyle
    {
        get => GetValue(ShowLineStyleProperty);
        set => SetValue(ShowLineStyleProperty, value);
    }

    public static readonly StyledProperty<bool> ShowLabelEditingProperty =
        AvaloniaProperty.Register<GraphFormatPanel, bool>(nameof(ShowLabelEditing), false);

    /// <summary>
    /// Reserved for Phase 5 Batch 5/6: pin Title / X / Y axis label editors
    /// inside this panel. Currently a no-op placeholder. WPF 版と API 互換。
    /// </summary>
    public bool ShowLabelEditing
    {
        get => GetValue(ShowLabelEditingProperty);
        set => SetValue(ShowLabelEditingProperty, value);
    }

    /// <summary>Selected aspect-ratio ComboBox tag (e.g. "Auto" / "16:9").</summary>
    public string? AspectRatioTag => _aspectRatioComboBox is null
        ? null
        : GetComboBoxTag(_aspectRatioComboBox);

    /// <summary>
    /// Selected aspect ratio as width / height (e.g. <c>16/9</c>). Returns
    /// <c>null</c> when the user picked Auto or the tag is malformed; that
    /// is the signal callers use to fall back to the platform default.
    /// Accepts ":" / "/" / "x" / "X" as the dividing character.
    /// </summary>
    public double? AspectRatioValue
    {
        get
        {
            var tag = AspectRatioTag;
            if (string.IsNullOrWhiteSpace(tag)
                || tag.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var parts = tag.Split(':', '/', 'x', 'X');
            if (parts.Length != 2)
            {
                return null;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                || width <= 0
                || height <= 0)
            {
                return null;
            }

            return width / height;
        }
    }

    /// <summary>
    /// Toggles the "グリッドを表示" CheckBox. Wired up by per-app Ctrl+G
    /// shortcut handlers so the grid toggle keeps working after the
    /// CheckBox migrated into this panel.
    /// </summary>
    public void TogglePlotGrid()
    {
        if (_plotGridCheckBox is null) return;
        if (!_plotGridCheckBox.IsEnabled) return;
        _plotGridCheckBox.IsChecked = _plotGridCheckBox.IsChecked != true;
    }

    /// <summary>
    /// Pushes a new legend placement (<paramref name="position"/> +
    /// pixel offsets) into the position ComboBox and the X / Y TextBoxes
    /// without firing <see cref="GraphFormatChanged"/>.
    /// </summary>
    public void SyncLegendPlacement(string position, double offsetX, double offsetY)
    {
        if (_legendPositionComboBox is null
            || _legendOffsetXTextBox is null
            || _legendOffsetYTextBox is null) return;

        _suppress = true;
        try
        {
            SelectComboBoxByTag(_legendPositionComboBox, position);
            _legendOffsetXTextBox.Text = ConfigNormalizer.FormatNumber(offsetX);
            _legendOffsetYTextBox.Text = ConfigNormalizer.FormatNumber(offsetY);
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>
    /// Read panel UI state into the shared portion of <paramref name="config"/>.
    /// Subclass-specific properties (calibration paths, λmax markers, ...)
    /// are the caller's responsibility.
    /// </summary>
    public void Capture(GraphFormattingConfigBase config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_graphFontSizeTextBox is null
            || _plotGridCheckBox is null
            || _yAxisTickLabelsCheckBox is null
            || _majorTicksCheckBox is null
            || _minorTicksCheckBox is null
            || _tickDensityTextBox is null
            || _tickWidthTextBox is null
            || _plotFrameCheckBox is null
            || _plotFrameWidthTextBox is null
            || _plotFrameColorPicker is null
            || _backgroundColorPicker is null
            || _legendVisibilityComboBox is null
            || _legendPositionComboBox is null
            || _legendOffsetXTextBox is null
            || _legendOffsetYTextBox is null
            || _legendFontSizeTextBox is null) return;

        config.FontName = GetSelectedGraphFontName();
        config.FontSize = TryParsePositiveDouble(_graphFontSizeTextBox.Text, out var fontSize)
            ? fontSize
            : GraphFormattingConfigBase.DefaultFontSize;
        config.ShowGrid = _plotGridCheckBox.IsChecked == true;
        config.ShowYAxisTickLabels = _yAxisTickLabelsCheckBox.IsChecked == true;
        config.ShowMajorTicks = _majorTicksCheckBox.IsChecked == true;
        config.ShowMinorTicks = _minorTicksCheckBox.IsChecked == true;
        config.TickDensity = TryParsePositiveDouble(_tickDensityTextBox.Text, out var tickDensity)
            ? tickDensity
            : GraphFormattingConfigBase.DefaultTickDensity;
        config.TickWidth = TryParsePositiveDouble(_tickWidthTextBox.Text, out var tickWidth)
            ? tickWidth
            : GraphFormattingConfigBase.DefaultTickWidth;
        config.ShowPlotFrame = _plotFrameCheckBox.IsChecked == true;
        config.PlotFrameWidth = TryParsePositiveDouble(_plotFrameWidthTextBox.Text, out var frameWidth)
            ? frameWidth
            : GraphFormattingConfigBase.DefaultPlotFrameWidth;
        config.PlotFrameColorHex = _plotFrameColorPicker.HexValue
            ?? GraphFormattingConfigBase.DefaultPlotFrameColorHex;
        config.BackgroundColorHex = _backgroundColorPicker.HexValue
            ?? GraphFormattingConfigBase.DefaultBackgroundColorHex;
        config.AspectRatio = NormalizeAspectRatio(AspectRatioTag);
        config.LegendVisibility = GetComboBoxTag(_legendVisibilityComboBox);
        config.LegendPosition = GetComboBoxTag(_legendPositionComboBox)
            ?? GraphFormattingConfigBase.DefaultLegendPositionValue;
        config.LegendOffsetX = TryParseDouble(_legendOffsetXTextBox.Text, out var legendOffsetX)
            ? legendOffsetX
            : 0.0;
        config.LegendOffsetY = TryParseDouble(_legendOffsetYTextBox.Text, out var legendOffsetY)
            ? legendOffsetY
            : 0.0;
        config.LegendFontSize = TryParsePositiveDouble(_legendFontSizeTextBox.Text, out var legendFontSize)
            ? legendFontSize
            : null;
    }

    /// <summary>
    /// Push the shared portion of <paramref name="config"/> into the panel UI.
    /// Caller is expected to <c>Normalize()</c> the config first; this method
    /// suppresses change events while writing.
    /// </summary>
    public void Apply(GraphFormattingConfigBase config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_graphFontSizeTextBox is null
            || _plotGridCheckBox is null
            || _yAxisTickLabelsCheckBox is null
            || _majorTicksCheckBox is null
            || _minorTicksCheckBox is null
            || _tickDensityTextBox is null
            || _tickWidthTextBox is null
            || _plotFrameCheckBox is null
            || _plotFrameWidthTextBox is null
            || _plotFrameColorPicker is null
            || _backgroundColorPicker is null
            || _aspectRatioComboBox is null
            || _legendVisibilityComboBox is null
            || _legendPositionComboBox is null
            || _legendOffsetXTextBox is null
            || _legendOffsetYTextBox is null
            || _legendFontSizeTextBox is null) return;

        _suppress = true;
        try
        {
            SelectGraphFontComboBoxValue(config.FontName);
            _graphFontSizeTextBox.Text = config.FormatFontSize();
            _plotGridCheckBox.IsChecked = config.ShowGrid;
            _yAxisTickLabelsCheckBox.IsChecked = config.ShowYAxisTickLabels;
            _majorTicksCheckBox.IsChecked = config.ShowMajorTicks;
            _minorTicksCheckBox.IsChecked = config.ShowMinorTicks;
            _tickDensityTextBox.Text = config.FormatTickDensity();
            _tickWidthTextBox.Text = config.FormatTickWidth();
            _plotFrameCheckBox.IsChecked = config.ShowPlotFrame;
            _plotFrameWidthTextBox.Text = config.FormatFrameWidth();
            _plotFrameColorPicker.SetHexValue(config.PlotFrameColorHex);
            _backgroundColorPicker.SetHexValue(config.BackgroundColorHex);

            if (!SelectComboBoxByTag(_aspectRatioComboBox, config.AspectRatio ?? "Auto"))
            {
                _aspectRatioComboBox.SelectedIndex = 0;
            }

            SelectComboBoxByTag(_legendVisibilityComboBox, config.LegendVisibility ?? "Auto");
            SelectComboBoxByTag(_legendPositionComboBox, config.LegendPosition);
            _legendOffsetXTextBox.Text = config.FormatLegendOffsetX();
            _legendOffsetYTextBox.Text = config.FormatLegendOffsetY();
            _legendFontSizeTextBox.Text = config.FormatLegendFontSize();
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>
    /// AutoCompleteBox 用 helper。WPF 版は ComboBoxItem.Tag (= preset)
    /// + IsEditable=True で打ち込んだフリーテキストの 2 系統を統合して
    /// 返していたが、Avalonia 版は AutoCompleteBox.Text 1 本に集約され、
    /// SelectedItem がリスト内の文字列を返す形になる。Auto / 空欄は null
    /// を返して GraphFormattingConfigBase.FontName=null と一致させる。
    /// </summary>
    private string? GetSelectedGraphFontName()
    {
        var text = (_graphFontComboBox?.Text ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(text)
               || text.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : text;
    }

    private void SelectGraphFontComboBoxValue(string? fontName)
    {
        if (_graphFontComboBox is null) return;
        if (string.IsNullOrWhiteSpace(fontName)
            || fontName.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            _graphFontComboBox.Text = "Auto";
            return;
        }
        _graphFontComboBox.Text = fontName;
    }

    private static string? NormalizeAspectRatio(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)
            || tag.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return tag;
    }

    private void RaiseGraphFormatChanged()
    {
        if (_suppress) return;
        GraphFormatChanged?.Invoke(this, EventArgs.Empty);
    }

    // Avalonia の CheckBox は WPF の Checked / Unchecked 二本立てを
    // IsCheckedChanged 一本に統合する (RoutedEventArgs を受ける)。
    private void CheckBox_Changed(object? sender, RoutedEventArgs e) => RaiseGraphFormatChanged();
    private void NumericTextBox_TextChanged(object? sender, TextChangedEventArgs e) => RaiseGraphFormatChanged();
    private void ComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => RaiseGraphFormatChanged();
    private void ColorPicker_ColorChanged(object? sender, EventArgs e) => RaiseGraphFormatChanged();

    /// <summary>
    /// AutoCompleteBox は SelectedItem の変化と Text の自由入力で別々に
    /// イベントが飛ぶが、フォント名としての観測点はテキスト 1 本でよい。
    /// TextChanged で RaiseGraphFormatChanged を呼べば、リスト選択 (Text に
    /// 反映される) もフリー入力も同じ経路で吸い上げられる。Avalonia の
    /// AutoCompleteBox は TextChanged を `EventHandler{TextChangedEventArgs}`
    /// で発火するので、TextBox.TextChanged と同じ引数で受ける。
    /// </summary>
    private void GraphFontComboBox_TextChanged(object? sender, TextChangedEventArgs e) => RaiseGraphFormatChanged();

    private void LegendOffsetTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        // 空欄や parse 失敗は 0 として扱われるが、テキストが空のまま見えると
        // 0 と空欄の往復で見え方が揺れるので focus が外れたタイミングで
        // 正規化された数値表現に揃える。
        if (sender is not TextBox textBox) return;
        if (_suppress) return;

        var current = TryParseDouble(textBox.Text, out var parsed) ? parsed : 0.0;
        var normalized = ConfigNormalizer.FormatNumber(current);
        if (textBox.Text != normalized)
        {
            _suppress = true;
            try
            {
                textBox.Text = normalized;
            }
            finally
            {
                _suppress = false;
            }
        }
    }

    private void AspectRatioComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        AspectRatioChanged?.Invoke(this, EventArgs.Empty);
        GraphFormatChanged?.Invoke(this, EventArgs.Empty);
    }
}

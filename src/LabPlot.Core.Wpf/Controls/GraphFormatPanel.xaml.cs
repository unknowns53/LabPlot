using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LabPlot.Core;
using static LabPlot.Core.Wpf.FormatHelpers;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// Common "graph format" sub-panel reused by every LabPlot app. Hosts the
/// font / ticks / frame &amp; grid / background / legend Expanders and exposes
/// a Capture/Apply pair driven by <see cref="GraphFormattingConfigBase"/>, so
/// the per-app MainWindow only has to forward shared properties.
/// </summary>
/// <remarks>
/// The outer "グラフ書式" Expander stays in MainWindow.xaml so apps that need
/// extra panels in the same scope (Spectrum's "軸の表示" /
/// "メタデータ" sub-Expanders) can keep them sibling to this control.
/// App-specific knobs are intentionally NOT hosted here; for example,
/// Spectrum's X-axis orientation / Y-axis display ComboBoxes live in its own
/// <c>SpectrumAxisDisplayPanel</c> rather than being toggled in via a
/// <c>ShowAxisOrientation</c> flag on this control.
/// </remarks>
public partial class GraphFormatPanel : UserControl
{
    private bool _suppress;

    public GraphFormatPanel()
    {
        InitializeComponent();
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

    public static readonly DependencyProperty ShowLineStyleProperty =
        DependencyProperty.Register(
            nameof(ShowLineStyle),
            typeof(bool),
            typeof(GraphFormatPanel),
            new PropertyMetadata(false));

    /// <summary>
    /// Reserved for Phase 5 Batch 5/6: pin per-dataset line style controls
    /// inside this panel. Currently a no-op placeholder.
    /// </summary>
    public bool ShowLineStyle
    {
        get => (bool)GetValue(ShowLineStyleProperty);
        set => SetValue(ShowLineStyleProperty, value);
    }

    public static readonly DependencyProperty ShowLabelEditingProperty =
        DependencyProperty.Register(
            nameof(ShowLabelEditing),
            typeof(bool),
            typeof(GraphFormatPanel),
            new PropertyMetadata(false));

    /// <summary>
    /// Reserved for Phase 5 Batch 5/6: pin Title / X / Y axis label editors
    /// inside this panel. Currently a no-op placeholder.
    /// </summary>
    public bool ShowLabelEditing
    {
        get => (bool)GetValue(ShowLabelEditingProperty);
        set => SetValue(ShowLabelEditingProperty, value);
    }

    /// <summary>Selected aspect-ratio ComboBox tag (e.g. "Auto" / "16:9").</summary>
    public string? AspectRatioTag => GetComboBoxTag(AspectRatioComboBox);

    /// <summary>
    /// Selected aspect ratio as width / height (e.g. <c>16/9</c>). Returns
    /// <c>null</c> when the user picked Auto or the tag is malformed; that
    /// is the signal callers use to fall back to the platform default
    /// (e.g. <see cref="Helpers.GraphSaveHelpers.DefaultExportWidth"/>).
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
        if (!PlotGridCheckBox.IsEnabled) return;
        PlotGridCheckBox.IsChecked = PlotGridCheckBox.IsChecked != true;
    }

    /// <summary>
    /// Pushes a new legend placement (<paramref name="position"/> +
    /// pixel offsets) into the position ComboBox and the X / Y TextBoxes
    /// without firing <see cref="GraphFormatChanged"/>. Used by
    /// <c>LegendDragController</c> after a drag commits so the panel
    /// readout matches the new legend position without provoking a
    /// recursive re-capture. <paramref name="position"/> is one of the
    /// nine anchor strings (<c>"UpperLeft"</c>, <c>"UpperCenter"</c>,
    /// ..., <c>"LowerRight"</c>); unknown values leave the ComboBox
    /// untouched.
    /// </summary>
    public void SyncLegendPlacement(string position, double offsetX, double offsetY)
    {
        _suppress = true;
        try
        {
            SelectComboBoxByTag(LegendPositionComboBox, position);
            LegendOffsetXTextBox.Text = ConfigNormalizer.FormatNumber(offsetX);
            LegendOffsetYTextBox.Text = ConfigNormalizer.FormatNumber(offsetY);
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>
    /// Read panel UI state into the shared portion of <paramref name="config"/>.
    /// Subclass-specific properties (calibration paths, λmax markers, ...) are
    /// the caller's responsibility.
    /// </summary>
    public void Capture(GraphFormattingConfigBase config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.FontName = GetSelectedGraphFontName();
        config.FontSize = TryParsePositiveDouble(GraphFontSizeTextBox.Text, out var fontSize)
            ? fontSize
            : GraphFormattingConfigBase.DefaultFontSize;
        config.ShowGrid = PlotGridCheckBox.IsChecked == true;
        config.ShowYAxisTickLabels = YAxisTickLabelsCheckBox.IsChecked == true;
        config.ShowMajorTicks = MajorTicksCheckBox.IsChecked == true;
        config.ShowMinorTicks = MinorTicksCheckBox.IsChecked == true;
        config.TickDensity = TryParsePositiveDouble(TickDensityTextBox.Text, out var tickDensity)
            ? tickDensity
            : GraphFormattingConfigBase.DefaultTickDensity;
        config.TickWidth = TryParsePositiveDouble(TickWidthTextBox.Text, out var tickWidth)
            ? tickWidth
            : GraphFormattingConfigBase.DefaultTickWidth;
        config.ShowPlotFrame = PlotFrameCheckBox.IsChecked == true;
        config.PlotFrameWidth = TryParsePositiveDouble(PlotFrameWidthTextBox.Text, out var frameWidth)
            ? frameWidth
            : GraphFormattingConfigBase.DefaultPlotFrameWidth;
        config.PlotFrameColorHex = PlotFrameColorPicker.HexValue
            ?? GraphFormattingConfigBase.DefaultPlotFrameColorHex;
        config.BackgroundColorHex = BackgroundColorPicker.HexValue
            ?? GraphFormattingConfigBase.DefaultBackgroundColorHex;
        config.AspectRatio = NormalizeAspectRatio(AspectRatioTag);
        config.LegendVisibility = GetComboBoxTag(LegendVisibilityComboBox);
        config.LegendPosition = GetComboBoxTag(LegendPositionComboBox)
            ?? GraphFormattingConfigBase.DefaultLegendPositionValue;
        config.LegendOffsetX = TryParseDouble(LegendOffsetXTextBox.Text, out var legendOffsetX)
            ? legendOffsetX
            : 0.0;
        config.LegendOffsetY = TryParseDouble(LegendOffsetYTextBox.Text, out var legendOffsetY)
            ? legendOffsetY
            : 0.0;
        config.LegendFontSize = TryParsePositiveDouble(LegendFontSizeTextBox.Text, out var legendFontSize)
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

        _suppress = true;
        try
        {
            SelectGraphFontComboBoxValue(config.FontName);
            GraphFontSizeTextBox.Text = config.FormatFontSize();
            PlotGridCheckBox.IsChecked = config.ShowGrid;
            YAxisTickLabelsCheckBox.IsChecked = config.ShowYAxisTickLabels;
            MajorTicksCheckBox.IsChecked = config.ShowMajorTicks;
            MinorTicksCheckBox.IsChecked = config.ShowMinorTicks;
            TickDensityTextBox.Text = config.FormatTickDensity();
            TickWidthTextBox.Text = config.FormatTickWidth();
            PlotFrameCheckBox.IsChecked = config.ShowPlotFrame;
            PlotFrameWidthTextBox.Text = config.FormatFrameWidth();
            PlotFrameColorPicker.SetHexValue(config.PlotFrameColorHex);
            BackgroundColorPicker.SetHexValue(config.BackgroundColorHex);

            if (!SelectComboBoxByTag(AspectRatioComboBox, config.AspectRatio ?? "Auto"))
            {
                AspectRatioComboBox.SelectedIndex = 0;
            }

            SelectComboBoxByTag(LegendVisibilityComboBox, config.LegendVisibility ?? "Auto");
            SelectComboBoxByTag(LegendPositionComboBox, config.LegendPosition);
            LegendOffsetXTextBox.Text = config.FormatLegendOffsetX();
            LegendOffsetYTextBox.Text = config.FormatLegendOffsetY();
            LegendFontSizeTextBox.Text = config.FormatLegendFontSize();
        }
        finally
        {
            _suppress = false;
        }
    }

    private string? GetSelectedGraphFontName()
    {
        if (GraphFontComboBox.SelectedItem is ComboBoxItem item
            && item.Tag is string selectedTag
            && !selectedTag.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return selectedTag;
        }

        var text = GraphFontComboBox.Text.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : text;
    }

    private void SelectGraphFontComboBoxValue(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)
            || fontName.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            GraphFontComboBox.SelectedIndex = 0;
            return;
        }

        if (SelectComboBoxByTag(GraphFontComboBox, fontName))
        {
            return;
        }

        GraphFontComboBox.SelectedIndex = -1;
        GraphFontComboBox.Text = fontName;
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

    private void CheckBox_Changed(object sender, RoutedEventArgs e) => RaiseGraphFormatChanged();
    private void NumericTextBox_TextChanged(object sender, TextChangedEventArgs e) => RaiseGraphFormatChanged();
    private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RaiseGraphFormatChanged();
    private void ColorPicker_ColorChanged(object? sender, EventArgs e) => RaiseGraphFormatChanged();

    private void LegendOffsetTextBox_LostFocus(object sender, RoutedEventArgs e)
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

    private void GraphFontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RaiseGraphFormatChanged();
    }

    private void GraphFontComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        // IsEditable=True の ComboBox は SelectionChanged だけだとリストにない
        // フォント名を打ち込んだとき反映されない。テンプレート内の編集用 TextBox を
        // 取り出して TextChanged を購読し、自由入力でも変更通知が走るようにする。
        if (GraphFontComboBox.Template?.FindName("PART_EditableTextBox", GraphFontComboBox) is TextBox editableTextBox)
        {
            editableTextBox.TextChanged -= GraphFontEditableTextChanged;
            editableTextBox.TextChanged += GraphFontEditableTextChanged;
        }
    }

    private void GraphFontEditableTextChanged(object? sender, TextChangedEventArgs e)
    {
        RaiseGraphFormatChanged();
    }

    private void AspectRatioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        AspectRatioChanged?.Invoke(this, EventArgs.Empty);
        GraphFormatChanged?.Invoke(this, EventArgs.Empty);
    }
}

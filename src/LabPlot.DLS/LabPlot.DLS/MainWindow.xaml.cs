using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DlsAnalyzer.Core;
using LabPlot.Core;
using Microsoft.Win32;
using ScottPlot.WPF;
using static LabPlot.Core.PlotAppearance;
using static LabPlot.Core.Wpf.FormatHelpers;

namespace LabPlot.DLS;

public partial class MainWindow : Window
{
    // Stable per-dataset overlay palette. Index is the dataset's position in
    // _datasets, so a sheet keeps its colour even as the user toggles the
    // selection on/off in the sidebar.
    private static readonly string[] AutoLineColors =
    [
        "#2563EB",
        "#DC2626",
        "#16A34A",
        "#EA580C",
        "#7C3AED",
        "#0891B2",
        "#4B5563",
    ];

    private readonly ZetasizerXlsxReader _reader = new();
    private readonly List<DlsDataset> _datasets = new();
    private readonly List<DlsDataset> _selectedDatasets = new();
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private WpfPlot? _plot;
    private DistributionMode _selectedMode = DistributionMode.Number;
    private int _selectedRunIndex;
    private bool _suppressRunComboEvents;
    private bool _suppressFormattingEvents;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _plot = new WpfPlot();
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_plot);

            // Push the factory-default formatting config into the controls
            // and align the active distribution / run with it. Once session
            // persistence ships in Batch 6 a saved config will replace the
            // factory default before this runs.
            ApplyFormattingConfigToControls(_formattingConfig);
            _selectedMode = DistributionModeFromTag(_formattingConfig.DefaultDistributionMode);
            SelectComboBoxByTag(DistributionTypeComboBox, _formattingConfig.DefaultDistributionMode);
            _selectedRunIndex = Math.Max(0, _formattingConfig.DefaultRunIndex);

            InitializeEmptyPlot();
        }
        catch (Exception ex)
        {
            ShowError($"グラフ表示の初期化に失敗しました: {ex.Message}");
        }
    }

    private void InitializeEmptyPlot()
    {
        if (_plot is null) return;
        _plot.Plot.Clear();
        _plot.Plot.Title("Particle Size Distribution");
        _plot.Plot.XLabel("Size (d.nm)");
        _plot.Plot.YLabel(ModeLabel(_selectedMode));
        ApplyLogXTicks();
        _plot.Plot.Axes.SetLimits(Math.Log10(0.3), Math.Log10(10000), 0, 30);
        ApplyPlotAppearance();
        ApplyLegend(0);
        _plot.Refresh();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Zetasizer xlsx を開く",
            Filter = "Excel ファイル (*.xlsx)|*.xlsx|すべてのファイル (*.*)|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var loaded = _reader.Read(dialog.FileName);
            _datasets.Clear();
            foreach (var ds in loaded) _datasets.Add(ds);

            DatasetListBox.ItemsSource = null;
            DatasetListBox.ItemsSource = _datasets;
            DatasetCountText.Text = _datasets.Count == 0
                ? "粒径分布シートが見つかりませんでした"
                : $"{_datasets.Count} シート読み込み済み（{Path.GetFileName(dialog.FileName)}）";

            HideError();

            if (_datasets.Count > 0)
                DatasetListBox.SelectedIndex = 0;
            else
                ClearActiveDatasets();
        }
        catch (Exception ex)
        {
            ShowError($"読み込みに失敗しました: {ex.Message}");
        }
    }

    private void DatasetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        // Snapshot the selection in dataset order (not click order) so the
        // overlay palette stays predictable as the user toggles items.
        _selectedDatasets.Clear();
        foreach (var ds in _datasets)
        {
            if (DatasetListBox.SelectedItems.Contains(ds))
                _selectedDatasets.Add(ds);
        }
        UpdateRunCombo();
        UpdateDistributionTypeAvailability();
        RefreshPlot();
    }

    private void DistributionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged fires once during InitializeComponent because
        // ComboBoxItem.IsSelected="True" is applied while RunComboBox has
        // not been parsed yet. Skip until the XAML tree is fully built.
        if (!IsInitialized) return;
        if (DistributionTypeComboBox.SelectedItem is not ComboBoxItem item) return;
        _selectedMode = DistributionModeFromTag(item.Tag as string);
        UpdateRunCombo();
        RefreshPlot();
    }

    private void RunComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressRunComboEvents) return;
        _selectedRunIndex = Math.Max(0, RunComboBox.SelectedIndex);
        RefreshPlot();
    }

    private void FormatTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void FormatCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void AxisRangePanel_Committed(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void GraphFontComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        // IsEditable=True の ComboBox は SelectionChanged だけだとリストにないフォント名を
        // 打ち込んだとき反映されない。テンプレート内の編集用 TextBox を取り出して
        // TextChanged を購読し、自由入力でも再描画が走るようにする（GPC と同じ手法）。
        if (GraphFontComboBox.Template?.FindName("PART_EditableTextBox", GraphFontComboBox) is TextBox editableTextBox)
        {
            editableTextBox.TextChanged -= GraphFontComboBox_EditableTextChanged;
            editableTextBox.TextChanged += GraphFontComboBox_EditableTextChanged;
        }
    }

    private void GraphFontComboBox_EditableTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void PlotFrameColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        SyncPlotFrameColorInputFromComboBox();
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void PlotFrameColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        if (PlotFrameColorPreviewBorder is null) return;

        var hex = GetPlotFrameColorHex();
        _suppressFormattingEvents = true;
        try
        {
            SelectColorComboBoxValue(PlotFrameColorComboBox, hex, false);
        }
        finally
        {
            _suppressFormattingEvents = false;
        }

        UpdatePlotFrameColorPreview(hex);
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void BackgroundColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        SyncBackgroundColorInputFromComboBox();
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void BackgroundColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        if (BackgroundColorPreviewBorder is null) return;

        var hex = GetBackgroundColorHex();
        _suppressFormattingEvents = true;
        try
        {
            SelectColorComboBoxValue(BackgroundColorComboBox, hex, false);
        }
        finally
        {
            _suppressFormattingEvents = false;
        }

        UpdateBackgroundColorPreview(hex);
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void LineColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        if (LineColorComboBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag) return;

        // Tag-driven ComboBox commits its choice straight into the hex TextBox
        // (Auto -> "Auto" sentinel, named hex -> normalized hex, Custom keeps
        // the existing free-form text so the user can finish typing).
        _suppressFormattingEvents = true;
        try
        {
            if (tag.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                SetLineColorInput(null);
            }
            else if (!tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                SetLineColorInput(tag);
            }
        }
        finally
        {
            _suppressFormattingEvents = false;
        }

        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void LineColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        if (LineColorPreviewBorder is null) return;

        string? inputHex = null;
        if (!IsAutoColorText(LineColorHexTextBox.Text)
            && TryNormalizeHexColorCode(LineColorHexTextBox.Text, out var hex))
        {
            inputHex = hex;
        }

        _suppressFormattingEvents = true;
        try
        {
            SelectColorComboBoxValue(LineColorComboBox, inputHex, true);
        }
        finally
        {
            _suppressFormattingEvents = false;
        }

        UpdateLineColorPreview(inputHex);
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void ClearActiveDatasets()
    {
        _selectedDatasets.Clear();
        InitializeEmptyPlot();
    }

    private void UpdateDistributionTypeAvailability()
    {
        // Enable a distribution kind if at least one selected dataset has it;
        // datasets lacking the kind are silently skipped during overlay draw.
        for (int i = 0; i < DistributionTypeComboBox.Items.Count; i++)
        {
            if (DistributionTypeComboBox.Items[i] is not ComboBoxItem item) continue;
            var mode = DistributionModeFromTag(item.Tag as string);
            item.IsEnabled = _selectedDatasets.Count == 0
                || _selectedDatasets.Any(ds => GetDistribution(ds, mode) is not null);
        }
    }

    private void UpdateRunCombo()
    {
        // Run picker only makes sense for a single-dataset selection. With
        // multiple datasets each one keeps its own ActiveRunIndex (default 0)
        // since runs are not aligned across measurements.
        _suppressRunComboEvents = true;
        try
        {
            RunComboBox.Items.Clear();
            if (_selectedDatasets.Count != 1)
            {
                RunComboBox.IsEnabled = false;
                _selectedRunIndex = 0;
                return;
            }

            var distribution = GetDistribution(_selectedDatasets[0], _selectedMode);
            if (distribution is null || distribution.RunCount == 0)
            {
                RunComboBox.IsEnabled = false;
                _selectedRunIndex = 0;
                return;
            }

            for (int i = 0; i < distribution.RunCount; i++)
                RunComboBox.Items.Add(new ComboBoxItem { Content = $"Run {i + 1}" });

            RunComboBox.IsEnabled = distribution.RunCount > 1;
            _selectedRunIndex = Math.Clamp(_selectedRunIndex, 0, distribution.RunCount - 1);
            RunComboBox.SelectedIndex = _selectedRunIndex;
        }
        finally
        {
            _suppressRunComboEvents = false;
        }
    }

    private void RefreshPlot()
    {
        if (_plot is null) return;

        if (_selectedDatasets.Count == 0)
        {
            InitializeEmptyPlot();
            return;
        }

        _plot.Plot.Clear();

        var seriesCount = 0;
        foreach (var dataset in _selectedDatasets)
        {
            var distribution = GetDistribution(dataset, _selectedMode);
            if (distribution is null || distribution.RunCount == 0) continue;

            var runIndex = _selectedDatasets.Count == 1
                ? Math.Clamp(_selectedRunIndex, 0, distribution.RunCount - 1)
                : Math.Clamp(distribution.ActiveRunIndex, 0, distribution.RunCount - 1);
            var run = distribution.Runs[runIndex];
            var sizes = distribution.SizeBinsNm;
            var n = Math.Min(run.Count, sizes.Count);
            if (n == 0) continue;

            var xs = new double[n];
            var ys = new double[n];
            for (int p = 0; p < n; p++)
            {
                xs[p] = Math.Log10(Math.Max(sizes[p], 1e-6));
                ys[p] = run[p];
            }

            var scatter = _plot.Plot.Add.Scatter(xs, ys);
            scatter.LineWidth = (float)_formattingConfig.LineWidth;
            scatter.MarkerSize = (float)_formattingConfig.MarkerSize;
            ApplyDatasetColor(scatter, dataset);
            scatter.LegendText = dataset.SheetName;
            seriesCount++;
        }

        if (seriesCount == 0)
        {
            // All selected datasets lack the chosen distribution. Render an
            // empty labelled plot so the user notices the mode mismatch.
            _plot.Plot.Title($"{ModeLabel(_selectedMode)} データなし");
            _plot.Plot.XLabel("Size (d.nm)");
            _plot.Plot.YLabel(ModeLabel(_selectedMode));
            ApplyLogXTicks();
            _plot.Plot.Axes.SetLimits(Math.Log10(0.3), Math.Log10(10000), 0, 30);
            ApplyPlotAppearance();
            ApplyLegend(0);
            _plot.Refresh();
            return;
        }

        _plot.Plot.Title(BuildTitle());
        _plot.Plot.XLabel("Size (d.nm)");
        _plot.Plot.YLabel(ModeLabel(_selectedMode));
        ApplyLogXTicks();
        _plot.Plot.Axes.AutoScale();
        ApplyPlotAppearance();
        ApplyLegend(seriesCount);
        _plot.Refresh();
    }

    private string BuildTitle()
    {
        if (_selectedDatasets.Count == 1)
        {
            var dataset = _selectedDatasets[0];
            var distribution = GetDistribution(dataset, _selectedMode);
            var runLabel = distribution is { RunCount: > 1 }
                ? $", Run {Math.Clamp(_selectedRunIndex, 0, distribution.RunCount - 1) + 1}"
                : string.Empty;
            return $"{dataset.SheetName} ({ModeLabel(_selectedMode)}{runLabel})";
        }

        return $"Particle Size Distribution ({ModeLabel(_selectedMode)}, {_selectedDatasets.Count} datasets)";
    }

    private void ApplyDatasetColor(ScottPlot.Plottables.Scatter scatter, DlsDataset dataset)
    {
        // Default line colour overrides the per-dataset palette when the user
        // has picked an explicit hex (or a non-Auto preset). Auto keeps the
        // stable per-dataset palette indexed off _datasets.
        if (!string.IsNullOrWhiteSpace(_formattingConfig.DefaultLineColorHex))
        {
            scatter.Color = ScottPlot.Color.FromHex(new[] { _formattingConfig.DefaultLineColorHex }).First();
            return;
        }

        var index = _datasets.IndexOf(dataset);
        if (index < 0) index = 0;
        var hex = AutoLineColors[index % AutoLineColors.Length];
        scatter.Color = ScottPlot.Color.FromHex(new[] { hex }).First();
    }

    private void ApplyLogXTicks()
    {
        if (_plot is null) return;

        // Render the X axis as log10(d.nm) with major ticks at 0.1, 1, 10,
        // 100, 1000, 10000 nm and minor ticks at the 2x..9x positions per
        // decade (matches the GPC molecular-weight axis approach).
        var generator = new ScottPlot.TickGenerators.NumericManual();
        for (int exponent = -1; exponent <= 4; exponent++)
        {
            var label = exponent switch
            {
                < 0 => Math.Pow(10, exponent).ToString("0.#", CultureInfo.InvariantCulture),
                _ => Math.Pow(10, exponent).ToString("0", CultureInfo.InvariantCulture),
            };
            generator.AddMajor(exponent, label);
            if (exponent < 4)
            {
                for (int multiplier = 2; multiplier <= 9; multiplier++)
                    generator.AddMinor(exponent + Math.Log10(multiplier));
            }
        }
        _plot.Plot.Axes.Bottom.TickGenerator = generator;
    }

    private void ApplyPlotAppearance(float scale = 1f)
    {
        if (_plot is null) return;
        var plot = _plot.Plot;

        ApplyAll(plot, _formattingConfig, scale);

        // Axis range overrides. Manual mode replaces whatever AutoScale or
        // SetLimits set up before. X is in log10(d.nm) space so we translate
        // the configured nm endpoints through Log10 once.
        if (_formattingConfig.XAxisMode == "Manual")
        {
            var xMinLog = Math.Log10(Math.Max(_formattingConfig.XAxisMinNm, 1e-6));
            var xMaxLog = Math.Log10(Math.Max(_formattingConfig.XAxisMaxNm, 1e-6));
            plot.Axes.SetLimitsX(xMinLog, xMaxLog);
        }
        if (_formattingConfig.YAxisMode == "Manual")
        {
            plot.Axes.SetLimitsY(_formattingConfig.YAxisMinPercent, _formattingConfig.YAxisMaxPercent);
        }
    }

    private void ApplyLegend(int seriesCount)
    {
        if (_plot is null) return;
        var plot = _plot.Plot;

        if (seriesCount == 0)
        {
            plot.Legend.IsVisible = false;
            return;
        }

        // "Auto" preserves the Batch 3a behaviour: legend appears only when
        // 2+ datasets are overlaid, since a single-series plot has nothing
        // to disambiguate.
        bool show = _formattingConfig.LegendVisibility switch
        {
            "Always" => true,
            "Never" => false,
            _ => seriesCount >= 2,
        };
        plot.Legend.IsVisible = show;
        if (show)
        {
            plot.Legend.Alignment = MapAlignment(_formattingConfig.LegendPosition);
        }
    }

    private GraphFormattingConfig CaptureFormattingConfigFromControls()
    {
        var config = new GraphFormattingConfig
        {
            FontName = GetSelectedGraphFontName(),
            FontSize = GetPlotFontSize(),
            ShowGrid = PlotGridCheckBox.IsChecked == true,
            ShowYAxisTickLabels = YAxisTickLabelsCheckBox.IsChecked == true,
            ShowMajorTicks = MajorTicksCheckBox.IsChecked == true,
            ShowMinorTicks = MinorTicksCheckBox.IsChecked == true,
            ShowPlotFrame = PlotFrameCheckBox.IsChecked == true,
            PlotFrameWidth = GetPlotFrameWidth(),
            PlotFrameColorHex = GetPlotFrameColorHex(),
            BackgroundColorHex = GetBackgroundColorHex(),
            ShowTitle = TitleVisibleCheckBox.IsChecked == true,
            TitleBold = TitleBoldCheckBox.IsChecked == true,
            AxisLabelBold = AxisLabelBoldCheckBox.IsChecked == true,
            AspectRatio = GetSelectedAspectRatioConfigValue(),
            DefaultLineColorHex = GetSelectedLineColorConfigValue(),
            LineWidth = TryParsePositiveDouble(LineWidthTextBox.Text, out var lineWidth)
                ? lineWidth
                : GraphFormattingConfig.DefaultLineWidth,
            MarkerSize = TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var markerSize)
                ? markerSize
                : GraphFormattingConfig.DefaultMarkerSize,
            DefaultOutputDirectory = _formattingConfig.DefaultOutputDirectory,
            // Axis range: empty textboxes mean "Auto" (let ScottPlot auto-scale).
            // Both endpoints must be filled for the axis to flip into "Manual".
            XAxisMode = (AxisRangePanel.XMinValue.HasValue && AxisRangePanel.XMaxValue.HasValue)
                ? "Manual" : "Auto",
            XAxisMinNm = AxisRangePanel.XMinValue ?? GraphFormattingConfig.DefaultXAxisMinNm,
            XAxisMaxNm = AxisRangePanel.XMaxValue ?? GraphFormattingConfig.DefaultXAxisMaxNm,
            YAxisMode = (AxisRangePanel.YMinValue.HasValue && AxisRangePanel.YMaxValue.HasValue)
                ? "Manual" : "Auto",
            YAxisMinPercent = AxisRangePanel.YMinValue ?? GraphFormattingConfig.DefaultYAxisMinPercent,
            YAxisMaxPercent = AxisRangePanel.YMaxValue ?? GraphFormattingConfig.DefaultYAxisMaxPercent,
            LegendVisibility = GetComboBoxTag(LegendVisibilityComboBox),
            LegendPosition = GetComboBoxTag(LegendPositionComboBox)
                ?? GraphFormattingConfig.DefaultLegendPositionValue,
            DefaultDistributionMode = GetComboBoxTag(DefaultDistributionComboBox)
                ?? GraphFormattingConfig.DefaultDistributionModeValue,
            DefaultRunIndex = TryParseInt(DefaultRunIndexTextBox.Text, out var idx) ? idx : 0,
        };
        config.Normalize();
        return config;
    }

    private void ApplyFormattingConfigToControls(GraphFormattingConfig config)
    {
        config.Normalize();

        _suppressFormattingEvents = true;
        try
        {
            SelectComboBoxByTag(GraphFontComboBox, config.FontName ?? "Auto");
            GraphFontSizeTextBox.Text = config.FormatFontSize();
            PlotGridCheckBox.IsChecked = config.ShowGrid;
            YAxisTickLabelsCheckBox.IsChecked = config.ShowYAxisTickLabels;
            MajorTicksCheckBox.IsChecked = config.ShowMajorTicks;
            MinorTicksCheckBox.IsChecked = config.ShowMinorTicks;
            PlotFrameCheckBox.IsChecked = config.ShowPlotFrame;
            PlotFrameWidthTextBox.Text = config.FormatFrameWidth();
            SetPlotFrameColorInput(config.PlotFrameColorHex);
            SetBackgroundColorInput(config.BackgroundColorHex);
            TitleVisibleCheckBox.IsChecked = config.ShowTitle;
            TitleBoldCheckBox.IsChecked = config.TitleBold;
            AxisLabelBoldCheckBox.IsChecked = config.AxisLabelBold;
            SelectComboBoxByTag(AspectRatioComboBox, config.AspectRatio ?? "Auto");
            SetLineColorInput(config.DefaultLineColorHex);
            LineWidthTextBox.Text = config.FormatLineWidth();
            MarkerSizeTextBox.Text = config.FormatMarkerSize();

            // "Manual" mode: write the saved values back into the panel.
            // "Auto" mode: leave the textboxes empty so the plot auto-scales.
            AxisRangePanel.SetXValues(
                config.XAxisMode == "Manual" ? config.XAxisMinNm : null,
                config.XAxisMode == "Manual" ? config.XAxisMaxNm : null);
            AxisRangePanel.SetYValues(
                config.YAxisMode == "Manual" ? config.YAxisMinPercent : null,
                config.YAxisMode == "Manual" ? config.YAxisMaxPercent : null);
            SelectComboBoxByTag(LegendVisibilityComboBox, config.LegendVisibility ?? "Auto");
            SelectComboBoxByTag(LegendPositionComboBox, config.LegendPosition);
            SelectComboBoxByTag(DefaultDistributionComboBox, config.DefaultDistributionMode);
            DefaultRunIndexTextBox.Text = config.DefaultRunIndex.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressFormattingEvents = false;
        }
    }

    // ---------- Capture helpers ----------

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

    private double GetPlotFontSize()
        => TryParsePositiveDouble(GraphFontSizeTextBox.Text, out var fontSize)
            ? fontSize
            : GraphFormattingConfig.DefaultFontSize;

    private double GetPlotFrameWidth()
        => TryParsePositiveDouble(PlotFrameWidthTextBox.Text, out var width)
            ? width
            : GraphFormattingConfig.DefaultPlotFrameWidth;

    private string GetPlotFrameColorHex()
        => TryNormalizeHexColorCode(PlotFrameColorHexTextBox.Text, out var hex)
            ? hex
            : GraphFormattingConfig.DefaultPlotFrameColorHex;

    private string GetBackgroundColorHex()
        => TryNormalizeHexColorCode(BackgroundColorHexTextBox.Text, out var hex)
            ? hex
            : GraphFormattingConfig.DefaultBackgroundColorHex;

    private string? GetSelectedLineColorConfigValue()
    {
        if (IsAutoColorText(LineColorHexTextBox.Text)) return null;
        return TryNormalizeHexColorCode(LineColorHexTextBox.Text, out var hex) ? hex : null;
    }

    private string? GetSelectedAspectRatioConfigValue()
    {
        var ratioText = GetComboBoxTag(AspectRatioComboBox) ?? AspectRatioComboBox.Text.Trim();
        return string.IsNullOrWhiteSpace(ratioText)
            || ratioText.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : ratioText;
    }

    // ---------- Apply helpers (config -> controls) ----------

    private void SetPlotFrameColorInput(string? hex)
    {
        var normalized = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GraphFormattingConfig.DefaultPlotFrameColorHex;

        if (!SelectComboBoxByTag(PlotFrameColorComboBox, normalized))
        {
            SelectComboBoxByTag(PlotFrameColorComboBox, "Custom");
        }

        PlotFrameColorHexTextBox.Text = normalized;
        UpdatePlotFrameColorPreview(normalized);
    }

    private void SetBackgroundColorInput(string? hex)
    {
        var normalized = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GraphFormattingConfig.DefaultBackgroundColorHex;

        if (!SelectComboBoxByTag(BackgroundColorComboBox, normalized))
        {
            SelectComboBoxByTag(BackgroundColorComboBox, "Custom");
        }

        BackgroundColorHexTextBox.Text = normalized;
        UpdateBackgroundColorPreview(normalized);
    }

    private void SetLineColorInput(string? hex)
    {
        LineColorHexTextBox.Text = string.IsNullOrWhiteSpace(hex) ? "Auto" : NormalizeHexColorCode(hex);
        UpdateLineColorPreview(hex);
    }

    private void SyncPlotFrameColorInputFromComboBox()
    {
        var tag = GetComboBoxTag(PlotFrameColorComboBox);
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            UpdatePlotFrameColorPreview(GetPlotFrameColorHex());
            return;
        }
        SetPlotFrameColorInput(tag);
    }

    private void SyncBackgroundColorInputFromComboBox()
    {
        var tag = GetComboBoxTag(BackgroundColorComboBox);
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBackgroundColorPreview(GetBackgroundColorHex());
            return;
        }
        SetBackgroundColorInput(tag);
    }

    private void UpdatePlotFrameColorPreview(string? hex)
    {
        if (PlotFrameColorPreviewBorder is null) return;
        var previewHex = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GraphFormattingConfig.DefaultPlotFrameColorHex;
        PlotFrameColorPreviewBorder.Background = new SolidColorBrush(HexToMediaColor(previewHex));
    }

    private void UpdateBackgroundColorPreview(string? hex)
    {
        if (BackgroundColorPreviewBorder is null) return;
        var previewHex = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GraphFormattingConfig.DefaultBackgroundColorHex;
        BackgroundColorPreviewBorder.Background = new SolidColorBrush(HexToMediaColor(previewHex));
    }

    private void UpdateLineColorPreview(string? hex)
    {
        if (LineColorPreviewBorder is null) return;
        var previewHex = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : AutoLineColors[0];
        LineColorPreviewBorder.Background = new SolidColorBrush(HexToMediaColor(previewHex));
    }

    private static void SelectColorComboBoxValue(ComboBox comboBox, string? hex, bool allowAuto)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            if (allowAuto && SelectComboBoxByTag(comboBox, "Auto"))
            {
                return;
            }
            SelectComboBoxByTag(comboBox, "Custom");
            return;
        }

        if (!SelectComboBoxByTag(comboBox, hex))
        {
            SelectComboBoxByTag(comboBox, "Custom");
        }
    }

    // ---------- Generic helpers ----------

    private static ScottPlot.Alignment MapAlignment(string position) => position switch
    {
        "UpperRight" => ScottPlot.Alignment.UpperRight,
        "UpperLeft" => ScottPlot.Alignment.UpperLeft,
        "LowerRight" => ScottPlot.Alignment.LowerRight,
        "LowerLeft" => ScottPlot.Alignment.LowerLeft,
        "MiddleRight" => ScottPlot.Alignment.MiddleRight,
        _ => ScottPlot.Alignment.UpperRight,
    };

    private static DistributionMode DistributionModeFromTag(string? tag) => tag switch
    {
        "Intensity" => DistributionMode.Intensity,
        "Volume" => DistributionMode.Volume,
        _ => DistributionMode.Number,
    };

    private static ParticleSizeDistribution? GetDistribution(DlsDataset? dataset, DistributionMode mode) => mode switch
    {
        DistributionMode.Number => dataset?.NumberDistribution,
        DistributionMode.Intensity => dataset?.IntensityDistribution,
        DistributionMode.Volume => dataset?.VolumeDistribution,
        _ => null,
    };

    private static string ModeLabel(DistributionMode mode) => mode switch
    {
        DistributionMode.Intensity => "Intensity (%)",
        DistributionMode.Volume => "Volume (%)",
        _ => "Number (%)",
    };

    private void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private enum DistributionMode
    {
        Number,
        Intensity,
        Volume,
    }
}

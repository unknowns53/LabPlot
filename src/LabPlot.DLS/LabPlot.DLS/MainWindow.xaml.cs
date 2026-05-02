using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
    // Parallel to _datasets: VM that bridges the ListBox (color preview +
    // sheet name) and per-sheet style overrides (color / legend name /
    // line width / marker size). Constructed fresh on every file load so
    // a new file resets all per-sheet state, while sheet selection within
    // a loaded file preserves it (per Batch U3 design).
    private readonly List<DlsDatasetItem> _datasetItems = new();
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private WpfPlot? _plot;
    private DistributionMode _selectedMode = DistributionMode.Number;
    private int _selectedRunIndex;
    // Index into _datasetItems / _datasets that the per-dataset style
    // panel currently edits (last-clicked sheet in a multi-select). -1
    // when nothing is selected, in which case the panel is disabled.
    private int _activeItemIndex = -1;
    private bool _suppressRunComboEvents;
    private bool _suppressFormattingEvents;
    private bool _suppressStyleControlEvents;

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
            // Per-dataset style controls start disabled until the user
            // selects a sheet (no file loaded yet → _activeItemIndex = -1).
            SyncStyleControlsFromActiveItem();
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
        _plot.Plot.Title(GetGraphTitle("Particle Size Distribution"));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, "Size (d.nm)"));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, ModeLabel(_selectedMode)));
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

            // Rebuild VM list so a fresh file resets every per-sheet style
            // override. Sheet-to-sheet selection within the same file
            // preserves state because we never recreate the VMs there.
            _datasetItems.Clear();
            foreach (var ds in _datasets) _datasetItems.Add(new DlsDatasetItem(ds));

            DatasetListBox.ItemsSource = null;
            DatasetListBox.ItemsSource = _datasetItems;
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
        foreach (var item in _datasetItems)
        {
            if (DatasetListBox.SelectedItems.Contains(item))
                _selectedDatasets.Add(item.Dataset);
        }

        // Active item drives the per-dataset style panel: prefer the most
        // recently added selection (last click in a multi-select), fall
        // back to the last item still selected when the user only removed
        // items, and -1 when nothing is selected.
        DlsDatasetItem? activeItem = null;
        foreach (var added in e.AddedItems)
        {
            if (added is DlsDatasetItem item) activeItem = item;
        }
        if (activeItem is null && DatasetListBox.SelectedItems.Count > 0)
        {
            activeItem = DatasetListBox.SelectedItems[DatasetListBox.SelectedItems.Count - 1] as DlsDatasetItem;
        }
        _activeItemIndex = activeItem is null ? -1 : _datasetItems.IndexOf(activeItem);
        SyncStyleControlsFromActiveItem();

        UpdateRunCombo();
        UpdateDistributionTypeAvailability();
        RefreshPlot();
    }

    // Pushes the active sheet's per-dataset style values into the panel
    // controls. Suppresses change events so writing the values back does
    // not retrigger RefreshPlot — that path is owned by the per-control
    // *_Changed handlers instead.
    private void SyncStyleControlsFromActiveItem()
    {
        bool hasActive = _activeItemIndex >= 0 && _activeItemIndex < _datasetItems.Count;
        LineColorPicker.IsEnabled = hasActive;
        LegendNameTextBox.IsEnabled = hasActive;
        LineWidthTextBox.IsEnabled = hasActive;
        MarkerSizeTextBox.IsEnabled = hasActive;

        if (!hasActive)
        {
            ActiveDatasetLabel.Text = "(選択中シート)";
            return;
        }

        var item = _datasetItems[_activeItemIndex];
        ActiveDatasetLabel.Text = $"({item.SheetName})";

        _suppressStyleControlEvents = true;
        try
        {
            // Match the GPC pattern: DefaultHex tracks the active palette
            // index so the "Auto" preset preview reflects the actual color
            // ScottPlot will render for this sheet.
            LineColorPicker.DefaultHex = AutoLineColors[_activeItemIndex % AutoLineColors.Length];
            LineColorPicker.SetHexValue(item.Style.ColorHex);
            LegendNameTextBox.Text = item.Style.LegendName ?? string.Empty;
            LineWidthTextBox.Text = FormatDouble(item.Style.LineWidth);
            MarkerSizeTextBox.Text = FormatDouble(item.Style.MarkerSize);
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }
    }

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        _datasetItems[_activeItemIndex].Style.ColorHex = LineColorPicker.HexValue;
        RefreshPlot();
    }

    private void LegendNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var legendName = LegendNameTextBox.Text.Trim();
        _datasetItems[_activeItemIndex].Style.LegendName =
            string.IsNullOrWhiteSpace(legendName) ? null : legendName;
        RefreshPlot();
    }

    private void LineWidthTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        if (TryParsePositiveDouble(LineWidthTextBox.Text, out var width))
        {
            _datasetItems[_activeItemIndex].Style.LineWidth = width;
            RefreshPlot();
        }
    }

    private void MarkerSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        if (TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var size))
        {
            _datasetItems[_activeItemIndex].Style.MarkerSize = size;
            RefreshPlot();
        }
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

    // Title / X / Y label TextBoxes do not feed into _formattingConfig: the
    // values are read directly by GetGraphTitle / GetGraphLabel at plot
    // time (matching the GPC / Spectrum convention).
    private void GraphLabelTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
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

    private void ColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void ClearActiveDatasets()
    {
        _selectedDatasets.Clear();
        _activeItemIndex = -1;
        SyncStyleControlsFromActiveItem();
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

            // Per-sheet style takes precedence over the global default.
            // Independence from _formattingConfig.LineWidth / MarkerSize
            // matches the GPC convention (the "line style" panel edits
            // per-sheet values; the global default only seeds new sheets).
            var datasetIdx = _datasets.IndexOf(dataset);
            var style = (datasetIdx >= 0 && datasetIdx < _datasetItems.Count)
                ? _datasetItems[datasetIdx].Style
                : null;

            var scatter = _plot.Plot.Add.Scatter(xs, ys);
            scatter.LineWidth = (float)(style?.LineWidth ?? _formattingConfig.LineWidth);
            scatter.MarkerSize = (float)(style?.MarkerSize ?? _formattingConfig.MarkerSize);
            ApplyDatasetColor(scatter, dataset);
            var customLegendName = style?.LegendName;
            scatter.LegendText = string.IsNullOrWhiteSpace(customLegendName)
                ? dataset.SheetName
                : customLegendName!;
            seriesCount++;
        }

        // Sync the ListBox color swatches with whatever the plot just
        // rendered. Done unconditionally so unselected rows still reflect
        // their assigned palette colour.
        for (int i = 0; i < _datasetItems.Count; i++)
        {
            _datasetItems[i].ColorBrush = ResolveDatasetBrush(i);
        }

        if (seriesCount == 0)
        {
            // All selected datasets lack the chosen distribution. Render an
            // empty labelled plot so the user notices the mode mismatch.
            _plot.Plot.Title(GetGraphTitle($"{ModeLabel(_selectedMode)} データなし"));
            _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, "Size (d.nm)"));
            _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, ModeLabel(_selectedMode)));
            ApplyLogXTicks();
            _plot.Plot.Axes.SetLimits(Math.Log10(0.3), Math.Log10(10000), 0, 30);
            ApplyPlotAppearance();
            ApplyLegend(0);
            _plot.Refresh();
            return;
        }

        _plot.Plot.Title(GetGraphTitle(BuildTitle()));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, "Size (d.nm)"));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, ModeLabel(_selectedMode)));
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
        // Three-tier fallback (per-sheet override → global default → palette).
        // The per-sheet override comes from the per-dataset style panel and
        // takes precedence so the user can paint individual sheets without
        // changing the global default. Auto keeps the stable palette indexed
        // off _datasets so each sheet's swatch is consistent across renders.
        var index = _datasets.IndexOf(dataset);
        if (index < 0) index = 0;

        if (index < _datasetItems.Count
            && !string.IsNullOrWhiteSpace(_datasetItems[index].Style.ColorHex))
        {
            scatter.Color = ScottPlot.Color.FromHex(new[] { _datasetItems[index].Style.ColorHex! }).First();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_formattingConfig.DefaultLineColorHex))
        {
            scatter.Color = ScottPlot.Color.FromHex(new[] { _formattingConfig.DefaultLineColorHex }).First();
            return;
        }

        var hex = AutoLineColors[index % AutoLineColors.Length];
        scatter.Color = ScottPlot.Color.FromHex(new[] { hex }).First();
    }

    // Mirrors ApplyDatasetColor's resolution but returns the hex / Media
    // brush directly so DatasetListBox row swatches stay in sync with the
    // plot. Called from RefreshPlot for every loaded sheet (selected or
    // not) so unselected rows still show their assigned palette colour.
    private SolidColorBrush ResolveDatasetBrush(int datasetIndex)
    {
        string hex;
        if (datasetIndex >= 0 && datasetIndex < _datasetItems.Count
            && !string.IsNullOrWhiteSpace(_datasetItems[datasetIndex].Style.ColorHex))
        {
            hex = _datasetItems[datasetIndex].Style.ColorHex!;
        }
        else if (!string.IsNullOrWhiteSpace(_formattingConfig.DefaultLineColorHex))
        {
            hex = _formattingConfig.DefaultLineColorHex!;
        }
        else
        {
            var i = Math.Max(0, datasetIndex);
            hex = AutoLineColors[i % AutoLineColors.Length];
        }
        return new SolidColorBrush(HexToMediaColor(hex));
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

        // Auto-show: 2+ overlaid datasets OR any selected sheet has a
        // custom legend name (so a single rename still surfaces). Matches
        // the GPC / Spectrum ShouldShowLegend rule. seriesCount == 0
        // forces autoShow false so an empty plot never shows an empty
        // legend frame in Auto mode.
        bool hasCustomLegendName = false;
        if (seriesCount > 0)
        {
            foreach (var ds in _selectedDatasets)
            {
                var idx = _datasets.IndexOf(ds);
                if (idx >= 0 && idx < _datasetItems.Count
                    && !string.IsNullOrWhiteSpace(_datasetItems[idx].Style.LegendName))
                {
                    hasCustomLegendName = true;
                    break;
                }
            }
        }

        var autoShow = seriesCount >= 2 || (seriesCount > 0 && hasCustomLegendName);
        PlotAppearance.ApplyLegend(_plot.Plot, _formattingConfig, autoShow);
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
            PlotFrameColorHex = PlotFrameColorPicker.HexValue ?? GraphFormattingConfig.DefaultPlotFrameColorHex,
            BackgroundColorHex = BackgroundColorPicker.HexValue ?? GraphFormattingConfig.DefaultBackgroundColorHex,
            ShowTitle = TitleVisibleCheckBox.IsChecked == true,
            TitleBold = TitleBoldCheckBox.IsChecked == true,
            AxisLabelBold = AxisLabelBoldCheckBox.IsChecked == true,
            AspectRatio = GetSelectedAspectRatioConfigValue(),
            // Per-sheet line style controls live in their own panel and
            // mutate _datasetItems[i].Style directly, so capture preserves
            // whatever default seeded the file load (factory defaults at
            // first; future Phase 4 Batch 6 session loads will replace
            // these via _formattingConfig assignment).
            DefaultLineColorHex = _formattingConfig.DefaultLineColorHex,
            LineWidth = _formattingConfig.LineWidth,
            MarkerSize = _formattingConfig.MarkerSize,
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
            LegendFontSize = GetLegendFontSize(),
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
            PlotFrameColorPicker.SetHexValue(config.PlotFrameColorHex);
            BackgroundColorPicker.SetHexValue(config.BackgroundColorHex);
            TitleVisibleCheckBox.IsChecked = config.ShowTitle;
            TitleBoldCheckBox.IsChecked = config.TitleBold;
            AxisLabelBoldCheckBox.IsChecked = config.AxisLabelBold;
            SelectComboBoxByTag(AspectRatioComboBox, config.AspectRatio ?? "Auto");
            // Per-sheet line style controls are driven by
            // SyncStyleControlsFromActiveItem (selection change), not by
            // the global formatting config — that decoupling is what lets
            // each sheet keep its own state.

            // "Manual" mode: write the saved values back into the panel.
            // "Auto" mode: leave the textboxes empty so the plot auto-scales.
            AxisRangePanel.SetXValues(
                config.XAxisMode == "Manual" ? config.XAxisMinNm : null,
                config.XAxisMode == "Manual" ? config.XAxisMaxNm : null);
            AxisRangePanel.SetYValues(
                config.YAxisMode == "Manual" ? config.YAxisMinPercent : null,
                config.YAxisMode == "Manual" ? config.YAxisMaxPercent : null);
            SelectComboBoxByTag(LegendVisibilityComboBox, config.LegendVisibility ?? "Auto");
            LegendFontSizeTextBox.Text = config.FormatLegendFontSize();
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

    private double? GetLegendFontSize()
        => TryParsePositiveDouble(LegendFontSizeTextBox.Text, out var fontSize)
            ? fontSize
            : null;

    private double GetPlotFrameWidth()
        => TryParsePositiveDouble(PlotFrameWidthTextBox.Text, out var width)
            ? width
            : GraphFormattingConfig.DefaultPlotFrameWidth;

    private string? GetSelectedAspectRatioConfigValue()
    {
        var ratioText = GetComboBoxTag(AspectRatioComboBox) ?? AspectRatioComboBox.Text.Trim();
        return string.IsNullOrWhiteSpace(ratioText)
            || ratioText.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : ratioText;
    }

    // ---------- Generic helpers ----------

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

    // Per-sheet style overrides. ColorHex / LegendName are nullable so the
    // empty case falls back to the auto palette / SheetName. LineWidth /
    // MarkerSize default to the shared GraphFormattingConfigBase values so
    // a freshly loaded file paints with the global defaults until the
    // user touches the per-sheet panel.
    private sealed class DlsDatasetStyle
    {
        public string? ColorHex { get; set; }
        public string? LegendName { get; set; }
        public double LineWidth { get; set; } = GraphFormattingConfigBase.DefaultLineWidth;
        public double MarkerSize { get; set; } = GraphFormattingConfigBase.DefaultMarkerSize;
    }

    // ListBox row VM. Holds the underlying DlsDataset, the per-sheet
    // style, and a notifying ColorBrush so the color swatch updates as
    // soon as RefreshPlot() recomputes the palette / overrides.
    private sealed class DlsDatasetItem : INotifyPropertyChanged
    {
        public DlsDataset Dataset { get; }
        public DlsDatasetStyle Style { get; } = new();
        public string SheetName => Dataset.SheetName;

        private SolidColorBrush _colorBrush = new(Colors.Gray);
        public SolidColorBrush ColorBrush
        {
            get => _colorBrush;
            set
            {
                if (_colorBrush.Color == value.Color) return;
                _colorBrush = value;
                OnPropertyChanged();
            }
        }

        public DlsDatasetItem(DlsDataset dataset)
        {
            Dataset = dataset;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // GPC / Spectrum convention: empty TextBox falls back to the dataset-
    // derived default. Matches GetGraphTitle / GetGraphLabel in the other
    // two apps so the GPC-basis label panel works identically here.
    private string GetGraphTitle(string defaultTitle)
    {
        var title = TitleTextBox.Text.Trim();
        return string.IsNullOrWhiteSpace(title) ? defaultTitle : title;
    }

    private static string GetGraphLabel(TextBox textBox, string defaultLabel)
    {
        var label = textBox.Text.Trim();
        return string.IsNullOrWhiteSpace(label) ? defaultLabel : label;
    }

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

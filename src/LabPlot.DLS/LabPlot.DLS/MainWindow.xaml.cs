using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DlsAnalyzer.Core;
using LabPlot.Core;
using Microsoft.Win32;
using ScottPlot.WPF;
using static LabPlot.Core.PlotAppearance;

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
        // Colour is keyed off the dataset's position in _datasets so it stays
        // stable as the user toggles selection on/off in the sidebar.
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
        // Inherit any common-property values from the existing config so the
        // 3b-2 shared-format UI can plug in without 3b-1 erasing them. Until
        // 3b-2 ships, these fields stay at their factory defaults.
        var config = new GraphFormattingConfig
        {
            FontName = _formattingConfig.FontName,
            FontSize = _formattingConfig.FontSize,
            ShowGrid = _formattingConfig.ShowGrid,
            ShowYAxisTickLabels = _formattingConfig.ShowYAxisTickLabels,
            ShowMajorTicks = _formattingConfig.ShowMajorTicks,
            ShowMinorTicks = _formattingConfig.ShowMinorTicks,
            ShowPlotFrame = _formattingConfig.ShowPlotFrame,
            PlotFrameWidth = _formattingConfig.PlotFrameWidth,
            PlotFrameColorHex = _formattingConfig.PlotFrameColorHex,
            BackgroundColorHex = _formattingConfig.BackgroundColorHex,
            ShowTitle = _formattingConfig.ShowTitle,
            TitleBold = _formattingConfig.TitleBold,
            AxisLabelBold = _formattingConfig.AxisLabelBold,
            AspectRatio = _formattingConfig.AspectRatio,
            DefaultLineColorHex = _formattingConfig.DefaultLineColorHex,
            LineWidth = _formattingConfig.LineWidth,
            MarkerSize = _formattingConfig.MarkerSize,
            DefaultOutputDirectory = _formattingConfig.DefaultOutputDirectory,
            XAxisMode = GetComboBoxTag(XAxisModeComboBox),
            XAxisMinNm = TryParseDouble(XAxisMinTextBox.Text, out var xmin)
                ? xmin
                : GraphFormattingConfig.DefaultXAxisMinNm,
            XAxisMaxNm = TryParseDouble(XAxisMaxTextBox.Text, out var xmax)
                ? xmax
                : GraphFormattingConfig.DefaultXAxisMaxNm,
            YAxisMode = GetComboBoxTag(YAxisModeComboBox),
            YAxisMinPercent = TryParseDouble(YAxisMinTextBox.Text, out var ymin)
                ? ymin
                : GraphFormattingConfig.DefaultYAxisMinPercent,
            YAxisMaxPercent = TryParseDouble(YAxisMaxTextBox.Text, out var ymax)
                ? ymax
                : GraphFormattingConfig.DefaultYAxisMaxPercent,
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
            SelectComboBoxByTag(XAxisModeComboBox, config.XAxisMode ?? "Auto");
            XAxisMinTextBox.Text = FormatDouble(config.XAxisMinNm);
            XAxisMaxTextBox.Text = FormatDouble(config.XAxisMaxNm);
            SelectComboBoxByTag(YAxisModeComboBox, config.YAxisMode ?? "Auto");
            YAxisMinTextBox.Text = FormatDouble(config.YAxisMinPercent);
            YAxisMaxTextBox.Text = FormatDouble(config.YAxisMaxPercent);
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

    private static ScottPlot.Alignment MapAlignment(string position) => position switch
    {
        "UpperRight" => ScottPlot.Alignment.UpperRight,
        "UpperLeft" => ScottPlot.Alignment.UpperLeft,
        "LowerRight" => ScottPlot.Alignment.LowerRight,
        "LowerLeft" => ScottPlot.Alignment.LowerLeft,
        "MiddleRight" => ScottPlot.Alignment.MiddleRight,
        _ => ScottPlot.Alignment.UpperRight,
    };

    private static string? GetComboBoxTag(ComboBox combo)
    {
        if (combo.SelectedItem is not ComboBoxItem item) return null;
        return item.Tag as string;
    }

    private static void SelectComboBoxByTag(ComboBox combo, string tag)
    {
        foreach (var raw in combo.Items)
        {
            if (raw is ComboBoxItem item
                && item.Tag is string s
                && string.Equals(s, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static bool TryParseDouble(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseInt(string? text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string FormatDouble(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

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

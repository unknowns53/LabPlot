using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using GpcAnalyzer.Core;
using Microsoft.Win32;
using ScottPlot.WPF;

namespace GPC_Visualization;

public partial class MainWindow : Window
{
    private readonly IGpcDataReader _reader = new CsvGpcDataReader();
    private readonly StandardCurveFileReader _standardCurveReader = new();
    private readonly MolecularWeightConverter _molecularWeightConverter = new();
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
    private const int DefaultExportWidth = 1200;
    private const int DefaultExportHeight = 720;
    private const string DefaultPlotFrameColorHex = "#475569";

    private readonly List<GpcDataset> _loadedDatasets = new();
    private readonly List<DatasetStyle> _datasetStyles = new();
    private readonly ObservableCollection<DatasetEntryVm> _datasetEntries = new();
    private int _activeIndex = -1;
    private GpcDataset? _currentDataset;
    private CalibrationCurveSet? _calibrationCurveSet;
    private CalibrationCurve? _selectedCalibrationCurve;
    private WpfPlot? _chromatogramPlot;
    private bool _updatingCalibrationSelection;
    private bool _suppressStyleControlEvents;
    private bool _suppressDatasetListEvents;

    public MainWindow()
    {
        InitializeComponent();
        DatasetListBox.ItemsSource = _datasetEntries;
        Loaded += MainWindow_Loaded;
    }

    private sealed class DatasetStyle
    {
        public string? ColorHex { get; set; }
        public string? LegendName { get; set; }
        public double LineWidth { get; set; } = 1.5;
        public double MarkerSize { get; set; }
    }

    public sealed class DatasetEntryVm
    {
        public string DisplayName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public SolidColorBrush ColorBrush { get; init; } = new(Colors.Gray);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(InitializePlotControl, DispatcherPriority.ApplicationIdle);
    }

    private void OpenCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "GPCデータを開く",
            Filter = "GPCデータ (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|CSV (*.csv)|*.csv|テキスト (*.txt;*.tsv)|*.txt;*.tsv|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var dataset = _reader.Read(dialog.FileName);
            AddLoadedDataset(dataset);

            if (_calibrationCurveSet is not null)
            {
                PopulateSolventComboBox();
            }
            else
            {
                UpdateMolecularWeightAvailability();
            }

            PlotCurrentDataset();
            SetStatus($"{dataset.Points.Count:N0} 点のデータを読み込みました。", false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            _currentDataset = null;
            _loadedDatasets.Clear();
            _datasetStyles.Clear();
            _activeIndex = -1;
            RefreshDatasetEntries();
            SaveGraphButton.IsEnabled = false;
            UpdateStatisticsText((MolecularWeightStatistics?)null);
            SetStatus($"読み込みに失敗しました: {ex.Message}", true);
        }
    }

    private void AddLoadedDataset(GpcDataset dataset)
    {
        var overlay = OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0;
        if (!overlay)
        {
            _loadedDatasets.Clear();
            _datasetStyles.Clear();
        }

        _loadedDatasets.Add(dataset);
        _datasetStyles.Add(new DatasetStyle());
        _activeIndex = _loadedDatasets.Count - 1;
        _currentDataset = dataset;

        FilePathTextBlock.Text = _loadedDatasets.Count > 1
            ? $"{_loadedDatasets.Count} files (latest: {dataset.SourceFilePath})"
            : dataset.SourceFilePath ?? string.Empty;

        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();
    }

    private void OpenCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "較正曲線JSONを開く",
            Filter = "JSON (*.json)|*.json|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _calibrationCurveSet = _standardCurveReader.Read(dialog.FileName);
            CalibrationPathTextBlock.Text = $"較正曲線: {dialog.FileName}";
            PopulateSolventComboBox();
            UpdateMolecularWeightAvailability();
            PlotCurrentDataset();
            SetStatus("較正曲線を読み込みました。", false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException)
        {
            _calibrationCurveSet = null;
            _selectedCalibrationCurve = null;
            CalibrationPathTextBlock.Text = "較正曲線: 未選択";
            SolventComboBox.ItemsSource = null;
            SolventComboBox.IsEnabled = false;
            DetectorComboBox.ItemsSource = null;
            DetectorComboBox.IsEnabled = false;
            UpdateMolecularWeightAvailability();
            PlotCurrentDataset();
            SetStatus($"較正曲線の読み込みに失敗しました: {ex.Message}", true);
        }
    }

    private void SolventComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingCalibrationSelection)
        {
            return;
        }

        PopulateDetectorComboBox();
    }

    private void DetectorComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingCalibrationSelection)
        {
            return;
        }

        SelectCalibrationCurve();
    }

    private void MolecularWeightCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_currentDataset is null)
        {
            return;
        }

        MolecularWeightYModeComboBox.IsEnabled = MolecularWeightCheckBox.IsChecked == true
            && _selectedCalibrationCurve is not null;
        PlotCurrentDataset();

        if (MolecularWeightCheckBox.IsChecked == true && _selectedCalibrationCurve is not null)
        {
            var yModeLabel = GetSelectedMolecularWeightYMode() == MolecularWeightYMode.DifferentialWeightFraction
                ? "dw/dlogM"
                : "Signal";
            SetStatus($"分子量表示に切り替えました: {_selectedCalibrationCurve.Solvent}/{_selectedCalibrationCurve.Detector}, Y={yModeLabel}", false);
        }
        else
        {
            SetStatus("保持時間表示に切り替えました。", false);
        }
    }

    private void MolecularWeightYModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentDataset is not null && MolecularWeightCheckBox.IsChecked == true)
        {
            PlotCurrentDataset();
        }
    }

    private void OverlayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_currentDataset is not null)
        {
            PlotCurrentDataset();
        }
    }

    private void ApplyGraphSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        PlotCurrentDataset();
    }

    private void ResetGraphSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        TitleTextBox.Clear();
        XLabelTextBox.Clear();
        YLabelTextBox.Clear();
        XMinTextBox.Clear();
        XMaxTextBox.Clear();
        YMinTextBox.Clear();
        YMaxTextBox.Clear();
        GraphFontComboBox.SelectedIndex = 0;
        GraphFontSizeTextBox.Text = "12";
        PlotGridCheckBox.IsChecked = true;
        YAxisTickLabelsCheckBox.IsChecked = true;
        PlotFrameCheckBox.IsChecked = true;
        PlotFrameWidthTextBox.Text = "1";
        PlotFrameColorComboBox.SelectedIndex = 0;
        AspectRatioComboBox.SelectedIndex = 0;

        foreach (var style in _datasetStyles)
        {
            style.ColorHex = null;
            style.LegendName = null;
            style.LineWidth = 1.5;
            style.MarkerSize = 0;
        }

        SyncStyleControlsFromActiveDataset();
        RefreshDatasetEntries();
        UpdatePlotHostAspectRatio();
        PlotCurrentDataset();
    }

    private void SaveGraphButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDataset is null || _chromatogramPlot is null)
        {
            SetStatus("保存するグラフがありません。", true);
            return;
        }

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset.SourceFilePath) ?? "gpc_chromatogram";
        var dialog = new SaveFileDialog
        {
            Title = "グラフをPNGで保存",
            Filter = "PNG画像 (*.png)|*.png",
            FileName = $"{defaultName}.png",
            DefaultExt = ".png",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var (width, height) = GetExportImageSize();
            _chromatogramPlot.Plot.SavePng(dialog.FileName, width, height);
            SetStatus($"グラフを保存しました: {dialog.FileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"保存に失敗しました: {ex.Message}", true);
        }
    }

    private void InitializePlotControl()
    {
        try
        {
            _chromatogramPlot = new WpfPlot();
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_chromatogramPlot);
            UpdatePlotHostAspectRatio();
            InitializeEmptyPlot();

            if (_currentDataset is not null)
            {
                PlotCurrentDataset();
                SaveGraphButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            PlotPlaceholderTextBlock.Text = "グラフ表示の初期化に失敗しました。";
            SetStatus($"グラフ表示の初期化に失敗しました: {ex.Message}", true);
        }
    }

    private void InitializeEmptyPlot()
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        _chromatogramPlot.Plot.Title("GPC chromatogram");
        _chromatogramPlot.Plot.XLabel("Time");
        _chromatogramPlot.Plot.YLabel("Signal");
        _chromatogramPlot.Plot.Axes.NumericTicksBottom();
        ApplyPlotAppearance();
        UpdateStatisticsText(null);
        _chromatogramPlot.Refresh();
    }

    private void PlotCurrentDataset()
    {
        if (_currentDataset is null)
        {
            SaveGraphButton.IsEnabled = false;
            UpdateStatisticsText((MolecularWeightStatistics?)null);
            return;
        }

        var activeDataset = GetSelectedDetectorDataset(_currentDataset);
        var plotEntries = GetDatasetsToPlotWithIndices();
        if (MolecularWeightCheckBox.IsChecked == true)
        {
            if (_selectedCalibrationCurve is null)
            {
                SetStatus("分子量表示には較正曲線、溶媒、検出器の選択が必要です。", true);
                SaveGraphButton.IsEnabled = _chromatogramPlot is not null;
                return;
            }

            try
            {
                var yMode = GetSelectedMolecularWeightYMode();
                var convertedEntries = plotEntries
                    .Select(entry => (
                        Dataset: _molecularWeightConverter.Convert(entry.Dataset, _selectedCalibrationCurve, yMode),
                        Index: entry.Index))
                    .ToArray();
                var activeConverted = _molecularWeightConverter.Convert(activeDataset, _selectedCalibrationCurve, yMode);
                PlotMolecularWeightDatasets(convertedEntries, activeConverted);
            }
            catch (InvalidDataException ex)
            {
                SetStatus($"分子量表示に失敗しました: {ex.Message}", true);
                return;
            }
        }
        else
        {
            PlotRetentionTimeDatasets(plotEntries, activeDataset);
        }

        SaveGraphButton.IsEnabled = _chromatogramPlot is not null;
    }

    private (GpcDataset Dataset, int Index)[] GetDatasetsToPlotWithIndices()
    {
        if (OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0)
        {
            var result = new (GpcDataset, int)[_loadedDatasets.Count];
            for (var i = 0; i < _loadedDatasets.Count; i++)
            {
                result[i] = (GetSelectedDetectorDataset(_loadedDatasets[i]), i);
            }
            return result;
        }

        if (_activeIndex < 0 || _activeIndex >= _loadedDatasets.Count)
        {
            return Array.Empty<(GpcDataset, int)>();
        }

        return new[] { (GetSelectedDetectorDataset(_loadedDatasets[_activeIndex]), _activeIndex) };
    }

    private GpcDataset GetSelectedDetectorDataset(GpcDataset dataset)
    {
        if (DetectorComboBox.SelectedItem is string detector
            && dataset.TryGetDetectorDataset(detector, out _))
        {
            return dataset.WithDetector(detector);
        }

        return dataset;
    }

    private void PlotRetentionTimeDatasets(IReadOnlyList<(GpcDataset Dataset, int Index)> entries, GpcDataset activeDataset)
    {
        if (_chromatogramPlot is null)
        {
            SetStatus("グラフ表示を初期化中です。少し待ってからもう一度お試しください。", true);
            return;
        }

        _chromatogramPlot.Plot.Clear();
        _chromatogramPlot.Plot.Axes.NumericTicksBottom();

        var allXs = new List<double>();
        var allYs = new List<double>();
        for (var i = 0; i < entries.Count; i++)
        {
            var (dataset, datasetIndex) = entries[i];
            var xs = dataset.Points.Select(point => point.X).ToArray();
            var ys = dataset.Points.Select(point => point.Y).ToArray();
            allXs.AddRange(xs);
            allYs.AddRange(ys);

            var signal = _chromatogramPlot.Plot.Add.Scatter(xs, ys);
            signal.LegendText = GetSeriesLegendText(dataset, "Signal", datasetIndex);
            ApplySeriesStyle(signal, datasetIndex);
        }

        if (ShouldShowLegend(entries.Select(entry => entry.Index)))
        {
            _chromatogramPlot.Plot.ShowLegend();
        }
        else
        {
            _chromatogramPlot.Plot.HideLegend();
        }

        _chromatogramPlot.Plot.Title(GetGraphTitle(Path.GetFileName(activeDataset.SourceFilePath) ?? "GPC chromatogram"));
        _chromatogramPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, activeDataset.XLabel));
        _chromatogramPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, activeDataset.YLabel));
        _chromatogramPlot.Plot.Axes.AutoScale();
        if (!ApplyAxisLimits(allXs.ToArray(), allYs.ToArray(), false))
        {
            _chromatogramPlot.Refresh();
            return;
        }

        UpdateStatisticsForDatasets(entries
            .Select(e => (
                Label: GetStatsLabel(e.Dataset.SourceFilePath, e.Index),
                Stats: e.Dataset.MolecularWeightStatistics))
            .ToList());
        ApplyPlotAppearance();
        _chromatogramPlot.Refresh();
    }

    private void PlotMolecularWeightDatasets(
        IReadOnlyList<(MolecularWeightDataset Dataset, int Index)> entries,
        MolecularWeightDataset activeDataset)
    {
        if (_chromatogramPlot is null)
        {
            SetStatus("グラフ表示を初期化中です。少し待ってからもう一度お試しください。", true);
            return;
        }

        _chromatogramPlot.Plot.Clear();
        SetMolecularWeightLogTicks();

        var allXs = new List<double>();
        var allYs = new List<double>();
        for (var i = 0; i < entries.Count; i++)
        {
            var (dataset, datasetIndex) = entries[i];
            var xs = dataset.Points.Select(point => Math.Log10(point.MolecularWeight)).ToArray();
            var ys = dataset.Points.Select(point => point.Signal).ToArray();
            allXs.AddRange(xs);
            allYs.AddRange(ys);

            var signal = _chromatogramPlot.Plot.Add.Scatter(xs, ys);
            signal.LegendText = GetSeriesLegendText(dataset, $"{dataset.Solvent}/{dataset.Detector}", datasetIndex);
            ApplySeriesStyle(signal, datasetIndex);
        }

        if (ShouldShowLegend(entries.Select(entry => entry.Index)))
        {
            _chromatogramPlot.Plot.ShowLegend();
        }
        else
        {
            _chromatogramPlot.Plot.HideLegend();
        }

        _chromatogramPlot.Plot.Title(GetGraphTitle(Path.GetFileName(activeDataset.SourceFilePath) ?? "GPC chromatogram"));
        _chromatogramPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, $"{activeDataset.XLabel} (log scale)"));
        _chromatogramPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, activeDataset.YLabel));
        _chromatogramPlot.Plot.Axes.AutoScale();
        if (!ApplyAxisLimits(allXs.ToArray(), allYs.ToArray(), true))
        {
            _chromatogramPlot.Refresh();
            return;
        }

        UpdateStatisticsForDatasets(entries
            .Select(e => (
                Label: GetStatsLabel(e.Dataset.SourceFilePath, e.Index),
                Stats: e.Dataset.Statistics))
            .ToList());
        ApplyPlotAppearance();
        _chromatogramPlot.Refresh();
    }

    private void ApplyPlotAppearance()
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        var plot = _chromatogramPlot.Plot;
        ApplyPlotFont(plot);
        ApplyPlotFontSize(plot);
        ApplyPlotGrid(plot);
        ApplyYAxisTickLabels(plot);
        ApplyPlotFrame(plot);
    }

    private void ApplyPlotFont(ScottPlot.Plot plot)
    {
        var fontName = GetSelectedGraphFontName();
        if (fontName is null)
        {
            plot.Font.Automatic();
            return;
        }

        try
        {
            plot.Font.Set(fontName);
        }
        catch
        {
            plot.Font.Automatic();
        }
    }

    private void ApplyPlotFontSize(ScottPlot.Plot plot)
    {
        var fontSize = GetPlotFontSize();
        plot.Axes.Title.Label.FontSize = fontSize + 2;
        plot.Axes.Bottom.Label.FontSize = fontSize;
        plot.Axes.Left.Label.FontSize = fontSize;
        plot.Axes.Bottom.TickLabelStyle.FontSize = Math.Max(6, fontSize - 1);
        plot.Axes.Left.TickLabelStyle.FontSize = Math.Max(6, fontSize - 1);
        plot.Legend.FontSize = Math.Max(6, fontSize - 1);
    }

    private void ApplyPlotGrid(ScottPlot.Plot plot)
    {
        if (PlotGridCheckBox.IsChecked == true)
        {
            plot.ShowGrid();
        }
        else
        {
            plot.HideGrid();
        }
    }

    private void ApplyYAxisTickLabels(ScottPlot.Plot plot)
    {
        plot.Axes.Left.TickLabelStyle.IsVisible = YAxisTickLabelsCheckBox.IsChecked == true;
    }

    private void ApplyPlotFrame(ScottPlot.Plot plot)
    {
        var frameVisible = PlotFrameCheckBox.IsChecked == true;
        plot.Axes.Frame(frameVisible);

        if (!frameVisible)
        {
            return;
        }

        plot.Axes.FrameWidth(GetPlotFrameWidth());
        plot.Axes.FrameColor(GetSelectedScottPlotColor(PlotFrameColorComboBox, DefaultPlotFrameColorHex));
    }

    private void ApplySeriesStyle(ScottPlot.Plottables.Scatter signal, int datasetIndex)
    {
        if (datasetIndex >= 0 && datasetIndex < _datasetStyles.Count)
        {
            var style = _datasetStyles[datasetIndex];
            signal.LineWidth = (float)style.LineWidth;
            signal.MarkerSize = (float)style.MarkerSize;
            var hex = style.ColorHex ?? AutoLineColors[datasetIndex % AutoLineColors.Length];
            signal.Color = ScottPlot.Color.FromHex(new[] { hex }).First();
            return;
        }

        signal.LineWidth = 1.5f;
        signal.MarkerSize = 0f;
        var fallback = AutoLineColors[Math.Max(0, datasetIndex) % AutoLineColors.Length];
        signal.Color = ScottPlot.Color.FromHex(new[] { fallback }).First();
    }

    private static string GetStatsLabel(string? sourceFilePath, int index)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFilePath);
        return string.IsNullOrWhiteSpace(name) ? $"dataset {index + 1}" : name;
    }

    private static bool TryParsePositiveDouble(string text, out double value)
    {
        return TryParseDouble(text, out value) && value > 0;
    }

    private static bool TryParseNonNegativeDouble(string text, out double value)
    {
        return TryParseDouble(text, out value) && value >= 0;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    private bool ShouldShowLegend(IEnumerable<int> datasetIndices)
    {
        var indices = datasetIndices.ToArray();
        return indices.Length > 1 || indices.Any(HasCustomLegendName);
    }

    private bool HasCustomLegendName(int datasetIndex)
    {
        return datasetIndex >= 0
            && datasetIndex < _datasetStyles.Count
            && !string.IsNullOrWhiteSpace(_datasetStyles[datasetIndex].LegendName);
    }

    private string? GetCustomLegendName(int datasetIndex)
    {
        if (!HasCustomLegendName(datasetIndex))
        {
            return null;
        }

        return _datasetStyles[datasetIndex].LegendName!.Trim();
    }

    private string GetSeriesLegendText(GpcDataset dataset, string fallback, int datasetIndex)
    {
        var customName = GetCustomLegendName(datasetIndex);
        if (customName is not null)
        {
            return customName;
        }

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        var detector = string.IsNullOrWhiteSpace(dataset.Detector) ? string.Empty : $" / Detector {dataset.Detector}";
        return string.IsNullOrWhiteSpace(fileName) ? $"{fallback}{detector}" : $"{fileName}{detector}";
    }

    private string GetSeriesLegendText(MolecularWeightDataset dataset, string fallback, int datasetIndex)
    {
        var customName = GetCustomLegendName(datasetIndex);
        if (customName is not null)
        {
            return customName;
        }

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? fallback : $"{fileName} / {fallback}";
    }

    private bool ApplyAxisLimits(double[] xs, double[] ys, bool xIsMolecularWeight)
    {
        if (_chromatogramPlot is null)
        {
            return false;
        }

        if (!TryReadOptionalDouble(XMinTextBox, "X Min", out var xMin)
            || !TryReadOptionalDouble(XMaxTextBox, "X Max", out var xMax)
            || !TryReadOptionalDouble(YMinTextBox, "Y Min", out var yMin)
            || !TryReadOptionalDouble(YMaxTextBox, "Y Max", out var yMax))
        {
            return false;
        }

        if (xIsMolecularWeight
            && (!TryConvertMolecularWeightLimit(ref xMin, "X Min")
                || !TryConvertMolecularWeightLimit(ref xMax, "X Max")))
        {
            return false;
        }

        var finiteXs = xs.Where(double.IsFinite).ToArray();
        var finiteYs = ys.Where(double.IsFinite).ToArray();

        if (xMin.HasValue || xMax.HasValue)
        {
            if (!TryGetRequestedRange(finiteXs, xMin, xMax, "X", out var left, out var right))
            {
                return false;
            }

            _chromatogramPlot.Plot.Axes.SetLimitsX(left, right);
        }

        if (yMin.HasValue || yMax.HasValue)
        {
            if (!TryGetRequestedRange(finiteYs, yMin, yMax, "Y", out var bottom, out var top))
            {
                return false;
            }

            _chromatogramPlot.Plot.Axes.SetLimitsY(bottom, top);
        }

        return true;
    }

    private bool TryConvertMolecularWeightLimit(ref double? value, string label)
    {
        if (!value.HasValue)
        {
            return true;
        }

        if (value.Value <= 0)
        {
            SetStatus($"{label} must be positive in molecular-weight view.", true);
            return false;
        }

        value = Math.Log10(value.Value);
        return true;
    }

    private bool TryGetRequestedRange(
        IReadOnlyList<double> dataValues,
        double? requestedMin,
        double? requestedMax,
        string axisName,
        out double min,
        out double max)
    {
        min = requestedMin ?? (dataValues.Count > 0 ? dataValues.Min() : double.NaN);
        max = requestedMax ?? (dataValues.Count > 0 ? dataValues.Max() : double.NaN);

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            SetStatus($"{axisName} axis range could not be determined.", true);
            return false;
        }

        if (min >= max)
        {
            SetStatus($"{axisName} Min must be smaller than {axisName} Max.", true);
            return false;
        }

        return true;
    }

    private bool TryReadOptionalDouble(TextBox textBox, string label, out double? value)
    {
        value = null;
        var text = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var parsed)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
        {
            value = parsed;
            return true;
        }

        SetStatus($"{label} must be a number.", true);
        return false;
    }

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

    private void UpdateStatisticsText(MolecularWeightStatistics? statistics)
    {
        if (statistics is null || !statistics.HasAnyValue)
        {
            StatisticsTextBlock.Text = "Mn: -   Mw: -   PDI: -";
            return;
        }

        if (statistics.Peaks.Count > 0)
        {
            var peakSummaries = statistics.Peaks
                .Take(3)
                .Select(FormatPeakStatistic);
            StatisticsTextBlock.Text = "Peaks by %: " + string.Join("   ", peakSummaries);
            return;
        }

        var source = statistics.Source == MolecularWeightStatisticsSource.DataFile ? "file" : "calc";
        StatisticsTextBlock.Text = $"Mn: {FormatStatistic(statistics.Mn)}   Mw: {FormatStatistic(statistics.Mw)}   PDI: {FormatStatistic(statistics.Pdi)} ({source})";
    }

    private void UpdateStatisticsForDatasets(IReadOnlyList<(string Label, MolecularWeightStatistics? Stats)> entries)
    {
        if (entries.Count == 0)
        {
            StatisticsTextBlock.Text = "Mn: -   Mw: -   PDI: -";
            return;
        }

        if (entries.Count == 1)
        {
            UpdateStatisticsText(entries[0].Stats);
            return;
        }

        var lines = new List<string>(entries.Count);
        foreach (var (label, stats) in entries)
        {
            lines.Add($"{label}: {FormatStatisticsInline(stats)}");
        }
        StatisticsTextBlock.Text = string.Join("\n", lines);
    }

    private static string FormatStatisticsInline(MolecularWeightStatistics? statistics)
    {
        if (statistics is null || !statistics.HasAnyValue)
        {
            return "Mn -  Mw -  PDI -";
        }

        if (statistics.Peaks.Count > 0)
        {
            var top = statistics.Peaks[0];
            return $"#{top.PeakId} Mn {FormatStatistic(top.Mn)}  Mw {FormatStatistic(top.Mw)}  PDI {FormatStatistic(top.Pdi)}";
        }

        var source = statistics.Source == MolecularWeightStatisticsSource.DataFile ? "file" : "calc";
        return $"Mn {FormatStatistic(statistics.Mn)}  Mw {FormatStatistic(statistics.Mw)}  PDI {FormatStatistic(statistics.Pdi)} ({source})";
    }

    private static string FormatPeakStatistic(MolecularWeightPeak peak)
    {
        return $"#{peak.PeakId} {FormatStatistic(peak.Percent)}% Mn {FormatStatistic(peak.Mn)} Mw {FormatStatistic(peak.Mw)} PDI {FormatStatistic(peak.Pdi)}";
    }

    private static string FormatStatistic(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return "-";
        }

        var absoluteValue = Math.Abs(value.Value);
        if (absoluteValue <= double.Epsilon)
        {
            return "0";
        }

        if (absoluteValue is >= 0.01 and < 10000)
        {
            return value.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return value.Value.ToString("0.###E+0", CultureInfo.InvariantCulture);
    }

    private void SetMolecularWeightLogTicks()
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        var minExponent = (int)Math.Log10(MolecularWeightConverter.DefaultMinMolecularWeight);
        var maxExponent = (int)Math.Log10(MolecularWeightConverter.DefaultMaxMolecularWeight);
        var tickPositions = Enumerable
            .Range(minExponent, maxExponent - minExponent + 1)
            .Select(exponent => (double)exponent)
            .ToArray();
        var tickLabels = tickPositions
            .Select(exponent => $"10{ToSuperscript((int)exponent)}")
            .ToArray();

        _chromatogramPlot.Plot.Axes.Bottom.SetTicks(tickPositions, tickLabels);
    }

    private static string ToSuperscript(int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return string.Concat(text.Select(character => character switch
        {
            '-' => '⁻',
            '0' => '⁰',
            '1' => '¹',
            '2' => '²',
            '3' => '³',
            '4' => '⁴',
            '5' => '⁵',
            '6' => '⁶',
            '7' => '⁷',
            '8' => '⁸',
            '9' => '⁹',
            _ => character,
        }));
    }

    private void PopulateSolventComboBox()
    {
        if (_calibrationCurveSet is null)
        {
            return;
        }

        _updatingCalibrationSelection = true;
        try
        {
            SolventComboBox.ItemsSource = _calibrationCurveSet.Solvents;
            SolventComboBox.IsEnabled = _calibrationCurveSet.Solvents.Count > 0;
            SolventComboBox.SelectedItem = GuessPreferredSolvent(_calibrationCurveSet.Solvents)
                ?? _calibrationCurveSet.Solvents.FirstOrDefault();
        }
        finally
        {
            _updatingCalibrationSelection = false;
        }

        PopulateDetectorComboBox();
    }

    private void PopulateDetectorComboBox()
    {
        if (_calibrationCurveSet is null || SolventComboBox.SelectedItem is not string solvent)
        {
            _selectedCalibrationCurve = null;
            UpdateMolecularWeightAvailability();
            return;
        }

        var detectors = _calibrationCurveSet.GetDetectors(solvent);

        _updatingCalibrationSelection = true;
        try
        {
            DetectorComboBox.ItemsSource = detectors;
            DetectorComboBox.IsEnabled = detectors.Count > 0;
            DetectorComboBox.SelectedItem = detectors.FirstOrDefault(detector => detector.Equals("A", StringComparison.OrdinalIgnoreCase))
                ?? detectors.FirstOrDefault();
        }
        finally
        {
            _updatingCalibrationSelection = false;
        }

        SelectCalibrationCurve();
    }

    private void SelectCalibrationCurve()
    {
        _selectedCalibrationCurve = null;

        if (_calibrationCurveSet is not null
            && SolventComboBox.SelectedItem is string solvent
            && DetectorComboBox.SelectedItem is string detector)
        {
            _selectedCalibrationCurve = _calibrationCurveSet.GetCurve(solvent, detector);
            if (_currentDataset is not null && _currentDataset.TryGetDetectorDataset(detector, out _))
            {
                _currentDataset = _currentDataset.WithDetector(detector);
            }
        }

        UpdateMolecularWeightAvailability();

        if (_currentDataset is not null)
        {
            PlotCurrentDataset();
        }
    }

    private void UpdateMolecularWeightAvailability()
    {
        var canConvert = _currentDataset is not null && _selectedCalibrationCurve is not null;
        MolecularWeightCheckBox.IsEnabled = canConvert;
        MolecularWeightYModeComboBox.IsEnabled = canConvert && MolecularWeightCheckBox.IsChecked == true;

        if (!canConvert)
        {
            MolecularWeightCheckBox.IsChecked = false;
            MolecularWeightYModeComboBox.SelectedIndex = 0;
        }
    }

    private MolecularWeightYMode GetSelectedMolecularWeightYMode()
    {
        if (MolecularWeightYModeComboBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && tag.Equals("DwdLogM", StringComparison.OrdinalIgnoreCase))
        {
            return MolecularWeightYMode.DifferentialWeightFraction;
        }

        return MolecularWeightYMode.Signal;
    }

    private string? GuessPreferredSolvent(IReadOnlyList<string> solvents)
    {
        var sourceFilePath = _currentDataset?.SourceFilePath ?? string.Empty;
        return solvents.FirstOrDefault(solvent => sourceFilePath.Contains(solvent, StringComparison.OrdinalIgnoreCase))
            ?? solvents.FirstOrDefault(solvent => solvent.Equals("DMF", StringComparison.OrdinalIgnoreCase));
    }

    private void SetStatus(string message, bool isError)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
            : new SolidColorBrush(Color.FromRgb(71, 85, 105));
    }

    private void RefreshDatasetEntries()
    {
        _suppressDatasetListEvents = true;
        try
        {
            _datasetEntries.Clear();
            for (var i = 0; i < _loadedDatasets.Count; i++)
            {
                var ds = _loadedDatasets[i];
                var style = _datasetStyles[i];
                var hex = style.ColorHex ?? AutoLineColors[i % AutoLineColors.Length];
                _datasetEntries.Add(new DatasetEntryVm
                {
                    DisplayName = Path.GetFileName(ds.SourceFilePath) ?? $"dataset {i + 1}",
                    FullPath = ds.SourceFilePath ?? string.Empty,
                    ColorBrush = new SolidColorBrush(HexToMediaColor(hex)),
                });
            }

            DatasetListBox.SelectedIndex = _datasetEntries.Count > 0
                ? Math.Clamp(_activeIndex, 0, _datasetEntries.Count - 1)
                : -1;
        }
        finally
        {
            _suppressDatasetListEvents = false;
        }

        DatasetListPlaceholder.Visibility = _datasetEntries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SyncStyleControlsFromActiveDataset()
    {
        _suppressStyleControlEvents = true;
        try
        {
            if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
            {
                LineColorComboBox.SelectedIndex = 0;
                LegendNameTextBox.Clear();
                LineWidthTextBox.Text = "1.5";
                MarkerSizeTextBox.Text = "0";
                ActiveDatasetLabel.Text = "(データ未選択)";
                return;
            }

            var style = _datasetStyles[_activeIndex];

            var colorIndex = 0;
            if (style.ColorHex is not null)
            {
                for (var i = 1; i < LineColorComboBox.Items.Count; i++)
                {
                    if (LineColorComboBox.Items[i] is ComboBoxItem item
                        && item.Tag is string tag
                        && tag.Equals(style.ColorHex, StringComparison.OrdinalIgnoreCase))
                    {
                        colorIndex = i;
                        break;
                    }
                }
            }
            LineColorComboBox.SelectedIndex = colorIndex;

            LegendNameTextBox.Text = style.LegendName ?? string.Empty;
            LineWidthTextBox.Text = style.LineWidth.ToString("0.##", CultureInfo.InvariantCulture);
            MarkerSizeTextBox.Text = style.MarkerSize.ToString("0.##", CultureInfo.InvariantCulture);

            var activeName = Path.GetFileNameWithoutExtension(_loadedDatasets[_activeIndex].SourceFilePath);
            ActiveDatasetLabel.Text = string.IsNullOrWhiteSpace(activeName)
                ? "(選択中データセット)"
                : $"({activeName})";
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }
    }

    private void DatasetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDatasetListEvents)
        {
            return;
        }

        var newIndex = DatasetListBox.SelectedIndex;
        if (newIndex < 0 || newIndex >= _loadedDatasets.Count)
        {
            return;
        }

        _activeIndex = newIndex;
        _currentDataset = _loadedDatasets[newIndex];
        FilePathTextBlock.Text = _currentDataset?.SourceFilePath ?? string.Empty;

        SyncStyleControlsFromActiveDataset();
        PlotCurrentDataset();
    }

    private void RemoveDatasetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not DatasetEntryVm vm)
        {
            return;
        }

        var index = _datasetEntries.IndexOf(vm);
        if (index < 0 || index >= _loadedDatasets.Count)
        {
            return;
        }

        _loadedDatasets.RemoveAt(index);
        _datasetStyles.RemoveAt(index);

        if (_loadedDatasets.Count == 0)
        {
            _activeIndex = -1;
            _currentDataset = null;
            RefreshDatasetEntries();
            SyncStyleControlsFromActiveDataset();
            FilePathTextBlock.Text = string.Empty;
            SaveGraphButton.IsEnabled = false;
            UpdateStatisticsText((MolecularWeightStatistics?)null);
            if (_chromatogramPlot is not null)
            {
                InitializeEmptyPlot();
            }
            SetStatus("すべてのデータセットを削除しました。", false);
            return;
        }

        if (_activeIndex > index)
        {
            _activeIndex--;
        }
        else if (_activeIndex == index)
        {
            _activeIndex = Math.Min(index, _loadedDatasets.Count - 1);
        }
        _currentDataset = _loadedDatasets[_activeIndex];

        FilePathTextBlock.Text = _loadedDatasets.Count > 1
            ? $"{_loadedDatasets.Count} files (latest: {_currentDataset.SourceFilePath})"
            : _currentDataset.SourceFilePath ?? string.Empty;

        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();
        PlotCurrentDataset();
    }

    private void LineColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            return;
        }

        var style = _datasetStyles[_activeIndex];
        if (LineColorComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            style.ColorHex = tag.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? null : tag;
        }

        RefreshDatasetEntries();
        PlotCurrentDataset();
    }

    private void LegendNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            return;
        }

        var legendName = LegendNameTextBox.Text.Trim();
        _datasetStyles[_activeIndex].LegendName = string.IsNullOrWhiteSpace(legendName)
            ? null
            : legendName;
        PlotCurrentDataset();
    }

    private void LineWidthTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            return;
        }

        if (TryParsePositiveDouble(LineWidthTextBox.Text, out var width))
        {
            _datasetStyles[_activeIndex].LineWidth = width;
        }
    }

    private void MarkerSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            return;
        }

        if (TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var size))
        {
            _datasetStyles[_activeIndex].MarkerSize = size;
        }
    }

    private void GraphAppearanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyGraphAppearanceAndRefresh();
    }

    private void GraphAppearanceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyGraphAppearanceAndRefresh();
    }

    private void AspectRatioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePlotHostAspectRatio();
        _chromatogramPlot?.Refresh();
    }

    private void PlotContainerBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlotHostAspectRatio();
    }

    private void ApplyGraphAppearanceAndRefresh()
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        ApplyPlotAppearance();
        _chromatogramPlot.Refresh();
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

    private float GetPlotFontSize()
    {
        return TryParsePositiveDouble(GraphFontSizeTextBox.Text, out var fontSize)
            ? (float)fontSize
            : 12f;
    }

    private float GetPlotFrameWidth()
    {
        return TryParsePositiveDouble(PlotFrameWidthTextBox.Text, out var width)
            ? (float)width
            : 1f;
    }

    private static ScottPlot.Color GetSelectedScottPlotColor(ComboBox comboBox, string fallbackHex)
    {
        var hex = comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
            : fallbackHex;

        try
        {
            return ScottPlot.Color.FromHex(new[] { hex }).First();
        }
        catch
        {
            return ScottPlot.Color.FromHex(new[] { fallbackHex }).First();
        }
    }

    private double? GetSelectedAspectRatio()
    {
        var ratioText = AspectRatioComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
            : AspectRatioComboBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(ratioText)
            || ratioText.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = ratioText.Split(':', '/', 'x', 'X');
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

    private void UpdatePlotHostAspectRatio()
    {
        if (PlotHost is null || PlotContainerBorder is null)
        {
            return;
        }

        var ratio = GetSelectedAspectRatio();
        if (!ratio.HasValue)
        {
            PlotHost.Width = double.NaN;
            PlotHost.Height = double.NaN;
            PlotHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            PlotHost.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        var availableWidth = PlotContainerBorder.ActualWidth
            - PlotContainerBorder.BorderThickness.Left
            - PlotContainerBorder.BorderThickness.Right;
        var availableHeight = PlotContainerBorder.ActualHeight
            - PlotContainerBorder.BorderThickness.Top
            - PlotContainerBorder.BorderThickness.Bottom;

        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        var targetWidth = availableWidth;
        var targetHeight = targetWidth / ratio.Value;
        if (targetHeight > availableHeight)
        {
            targetHeight = availableHeight;
            targetWidth = targetHeight * ratio.Value;
        }

        PlotHost.HorizontalAlignment = HorizontalAlignment.Center;
        PlotHost.VerticalAlignment = VerticalAlignment.Center;
        PlotHost.Width = Math.Max(0, targetWidth);
        PlotHost.Height = Math.Max(0, targetHeight);
    }

    private (int Width, int Height) GetExportImageSize()
    {
        var ratio = GetSelectedAspectRatio();
        if (!ratio.HasValue)
        {
            return (DefaultExportWidth, DefaultExportHeight);
        }

        var width = ratio.Value == 1
            ? 1000
            : DefaultExportWidth;
        var height = Math.Max(1, (int)Math.Round(width / ratio.Value));
        return (width, height);
    }

    private static Color HexToMediaColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Gray;
        }
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Buffers.Binary;
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
    private const int ExportDpi = 300;
    private const float DisplayDpi = 96f;
    private const int DefaultExportWidth = 3600;
    private const int DefaultExportHeight = 2160;
    private const int SquareExportWidth = 3000;
    private const int OverlayDownsampleMinSeriesCount = 3;
    private const int OverlayDownsampleMinTotalPoints = 120_000;
    private const int OverlayDisplayPointBudget = 120_000;
    private const int MinOverlayDisplayPointsPerSeries = 1_200;
    private const int MaxOverlayDisplayPointsPerSeries = 8_000;
    private static readonly TimeSpan PlotRefreshDebounceInterval = TimeSpan.FromMilliseconds(200);
    private static readonly JsonSerializerOptions FormattingConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly string FormattingConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GPC_Visualization",
        "formatting_config.json");

    private readonly List<GpcDataset> _loadedDatasets = new();
    private readonly List<DatasetStyle> _datasetStyles = new();
    private readonly ObservableCollection<DatasetEntryVm> _datasetEntries = new();
    private readonly Dictionary<MolecularWeightCacheKey, MolecularWeightDataset> _molecularWeightCache = new();
    private readonly Dictionary<PlotSeriesCacheKey, PlotSeriesData> _plotSeriesCache = new();
    private readonly DispatcherTimer _plotRefreshDebounceTimer = new() { Interval = PlotRefreshDebounceInterval };
    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private int _activeIndex = -1;
    private GpcDataset? _currentDataset;
    private CalibrationCurveSet? _calibrationCurveSet;
    private CalibrationCurve? _selectedCalibrationCurve;
    private WpfPlot? _chromatogramPlot;
    private bool _updatingCalibrationSelection;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressDatasetListEvents;
    private bool _forceFullResolutionPlot;
    private bool _currentPlotUsesDownsampledData;

    public MainWindow()
    {
        InitializeComponent();
        LoadFormattingDefaults();
        ApplyFormattingConfigToControls(_formattingDefaults);
        DatasetListBox.ItemsSource = _datasetEntries;
        _plotRefreshDebounceTimer.Tick += PlotRefreshDebounceTimer_Tick;
        Loaded += MainWindow_Loaded;
    }

    private sealed class DatasetStyle
    {
        public string? ColorHex { get; set; }
        public string? LegendName { get; set; }
        public double LineWidth { get; set; } = GraphFormattingConfig.DefaultLineWidth;
        public double MarkerSize { get; set; } = GraphFormattingConfig.DefaultMarkerSize;
    }

    private readonly record struct MolecularWeightCacheKey(
        IReadOnlyList<GpcDataPoint> Points,
        string? SourceFilePath,
        string YLabel,
        MolecularWeightStatistics? Statistics,
        CalibrationCurve Curve,
        MolecularWeightYMode YMode,
        double MinMolecularWeight,
        double MaxMolecularWeight);

    private readonly record struct PlotSeriesCacheKey(double[] XValues, double[] YValues, int MaxPointCount);

    private sealed class PlotSeriesData
    {
        public required double[] XValues { get; init; }

        public required double[] YValues { get; init; }

        public required AxisDataRange XRange { get; init; }

        public required AxisDataRange YRange { get; init; }

        public required bool IsDownsampled { get; init; }
    }

    private struct AxisDataRange
    {
        public bool HasValue { get; private set; }

        public double Min { get; private set; }

        public double Max { get; private set; }

        public void Include(double value)
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            if (!HasValue)
            {
                Min = value;
                Max = value;
                HasValue = true;
                return;
            }

            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
        }

        public void Include(IReadOnlyList<double> values)
        {
            for (var i = 0; i < values.Count; i++)
            {
                Include(values[i]);
            }
        }

        public void Include(AxisDataRange range)
        {
            if (!range.HasValue)
            {
                return;
            }

            if (!HasValue)
            {
                Min = range.Min;
                Max = range.Max;
                HasValue = true;
                return;
            }

            Min = Math.Min(Min, range.Min);
            Max = Math.Max(Max, range.Max);
        }
    }

    private enum GraphSaveFormat
    {
        Png,
        Svg,
    }

    private DatasetStyle CreateDefaultDatasetStyle()
    {
        var style = new DatasetStyle();
        ApplyDefaultDatasetStyle(style);
        return style;
    }

    private void ApplyDefaultDatasetStyle(DatasetStyle style)
    {
        style.ColorHex = _formattingDefaults.DefaultLineColorHex;
        style.LegendName = null;
        style.LineWidth = _formattingDefaults.LineWidth;
        style.MarkerSize = _formattingDefaults.MarkerSize;
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

    private void LoadFormattingDefaults()
    {
        _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();

        try
        {
            if (!File.Exists(FormattingConfigPath))
            {
                return;
            }

            var json = File.ReadAllText(FormattingConfigPath);
            var config = JsonSerializer.Deserialize<GraphFormattingConfig>(json, FormattingConfigJsonOptions);
            if (config is null)
            {
                return;
            }

            config.Normalize();
            _formattingDefaults = config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"書式設定configを読み込めませんでした: {ex.Message}", true);
        }
    }

    private void SaveFormattingDefaults()
    {
        _formattingDefaults.Normalize();

        var directory = Path.GetDirectoryName(FormattingConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_formattingDefaults, FormattingConfigJsonOptions);
        File.WriteAllText(FormattingConfigPath, json);
    }

    private GraphFormattingConfig CaptureFormattingConfigFromControls()
    {
        var config = new GraphFormattingConfig
        {
            FontName = GetSelectedGraphFontName(),
            FontSize = GetPlotFontSize(),
            ShowGrid = PlotGridCheckBox.IsChecked == true,
            ShowYAxisTickLabels = YAxisTickLabelsCheckBox.IsChecked == true,
            ShowPlotFrame = PlotFrameCheckBox.IsChecked == true,
            PlotFrameWidth = GetPlotFrameWidth(),
            PlotFrameColorHex = GetPlotFrameColorHex(),
            AspectRatio = GetSelectedAspectRatioConfigValue(),
            DefaultLineColorHex = GetSelectedLineColorConfigValue(),
            LineWidth = TryParsePositiveDouble(LineWidthTextBox.Text, out var lineWidth)
                ? lineWidth
                : GraphFormattingConfig.DefaultLineWidth,
            MarkerSize = TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var markerSize)
                ? markerSize
                : GraphFormattingConfig.DefaultMarkerSize,
        };

        config.Normalize();
        return config;
    }

    private void ApplyFormattingConfigToControls(GraphFormattingConfig config)
    {
        config.Normalize();

        _suppressGraphAppearanceEvents = true;
        try
        {
            SelectGraphFontComboBoxValue(config.FontName);
            GraphFontSizeTextBox.Text = config.FormatFontSize();
            PlotGridCheckBox.IsChecked = config.ShowGrid;
            YAxisTickLabelsCheckBox.IsChecked = config.ShowYAxisTickLabels;
            PlotFrameCheckBox.IsChecked = config.ShowPlotFrame;
            PlotFrameWidthTextBox.Text = config.FormatFrameWidth();
            SetPlotFrameColorInput(config.PlotFrameColorHex);

            if (!SelectComboBoxItemByTag(AspectRatioComboBox, config.AspectRatio ?? "Auto"))
            {
                AspectRatioComboBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressGraphAppearanceEvents = false;
        }

        _suppressStyleControlEvents = true;
        try
        {
            if (!SelectComboBoxItemByTag(LineColorComboBox, config.DefaultLineColorHex ?? "Auto"))
            {
                SelectComboBoxItemByTag(LineColorComboBox, config.DefaultLineColorHex is null ? "Auto" : "Custom");
            }

            SetLineColorInput(config.DefaultLineColorHex);
            LegendNameTextBox.Clear();
            LineWidthTextBox.Text = config.FormatLineWidth();
            MarkerSizeTextBox.Text = config.FormatMarkerSize();
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }
    }

    private async void OpenCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var allowMultiple = OverlayCheckBox.IsChecked == true;
        var dialog = new OpenFileDialog
        {
            Title = allowMultiple
                ? "GPCデータを開く（複数選択可）"
                : "GPCデータを開く",
            Filter = "GPCデータ (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|CSV (*.csv)|*.csv|テキスト (*.txt;*.tsv)|*.txt;*.tsv|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = allowMultiple,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var fileNames = dialog.FileNames.Length > 0
                ? dialog.FileNames
                : [dialog.FileName];
            OpenCsvButton.IsEnabled = false;
            SetStatus("GPCデータを読み込み中です...", false);

            var datasets = await Task.Run(() => fileNames
                .Select(fileName => _reader.Read(fileName))
                .ToArray());
            foreach (var dataset in datasets)
            {
                AddLoadedDataset(dataset);
            }

            if (_calibrationCurveSet is not null)
            {
                PopulateSolventComboBox();
            }
            else
            {
                UpdateMolecularWeightAvailability();
            }

            PlotCurrentDataset();
            var pointCount = datasets.Sum(dataset => dataset.Points.Count);
            var status = datasets.Length == 1
                ? $"{pointCount:N0} 点のデータを読み込みました。"
                : $"{datasets.Length:N0} ファイル / {pointCount:N0} 点のデータを読み込みました。";
            SetStatus(status, false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            _currentDataset = null;
            _loadedDatasets.Clear();
            _datasetStyles.Clear();
            ClearComputedDataCaches();
            _activeIndex = -1;
            RefreshDatasetEntries();
            SaveGraphButton.IsEnabled = false;
            UpdateStatisticsText((MolecularWeightStatistics?)null);
            SetStatus($"読み込みに失敗しました: {ex.Message}", true);
        }
        finally
        {
            OpenCsvButton.IsEnabled = true;
        }
    }

    private void AddLoadedDataset(GpcDataset dataset)
    {
        var overlay = OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0;
        if (!overlay)
        {
            _loadedDatasets.Clear();
            _datasetStyles.Clear();
            ClearComputedDataCaches();
        }

        _loadedDatasets.Add(dataset);
        _datasetStyles.Add(CreateDefaultDatasetStyle());
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
            ClearComputedDataCaches();
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
            ClearComputedDataCaches();
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

    private void ResetGraphSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        TitleTextBox.Clear();
        XLabelTextBox.Clear();
        YLabelTextBox.Clear();
        XMinTextBox.Clear();
        XMaxTextBox.Clear();
        YMinTextBox.Clear();
        YMaxTextBox.Clear();
        ApplyFormattingConfigToControls(_formattingDefaults);

        foreach (var style in _datasetStyles)
        {
            ApplyDefaultDatasetStyle(style);
        }

        SyncStyleControlsFromActiveDataset();
        RefreshDatasetEntries();
        UpdatePlotHostAspectRatio();
        PlotCurrentDataset();
    }

    private void SaveDefaultFormattingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _formattingDefaults = CaptureFormattingConfigFromControls();
            SaveFormattingDefaults();
            SetStatus($"書式の既定値を保存しました: {FormattingConfigPath}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"書式の既定値を保存できませんでした: {ex.Message}", true);
        }
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
            Title = "グラフを保存",
            Filter = "PNG画像 (*.png)|*.png|SVGベクター画像 (*.svg)|*.svg",
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
            var saveFormat = GetGraphSaveFormat(dialog.FileName, dialog.FilterIndex);
            var fileName = EnsureGraphSaveFileExtension(dialog.FileName, saveFormat);
            var exportStyleScale = GetExportStyleScale();
            var restoreDownsampledPlot = _currentPlotUsesDownsampledData;

            if (restoreDownsampledPlot)
            {
                _forceFullResolutionPlot = true;
                try
                {
                    PlotCurrentDataset();
                }
                finally
                {
                    _forceFullResolutionPlot = false;
                }
            }

            ApplyExportStyleScale(exportStyleScale);
            try
            {
                if (saveFormat == GraphSaveFormat.Svg)
                {
                    SaveGraphSvg(fileName, width, height);
                    SetStatus($"グラフをSVGで保存しました: {fileName} ({width:N0} x {height:N0})", false);
                    return;
                }

                _chromatogramPlot.Plot.SavePng(fileName, width, height);
                ApplyPngDpiMetadata(fileName, ExportDpi);
                SetStatus($"グラフをPNGで保存しました: {fileName} ({width:N0} x {height:N0} px, {ExportDpi} dpi)", false);
            }
            finally
            {
                ApplyExportStyleScale(1f);
                if (restoreDownsampledPlot)
                {
                    PlotCurrentDataset();
                }
                else
                {
                    _chromatogramPlot.Refresh();
                }
            }
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

    private void SchedulePlotCurrentDataset()
    {
        _plotRefreshDebounceTimer.Stop();
        _plotRefreshDebounceTimer.Start();
    }

    private void PlotRefreshDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _plotRefreshDebounceTimer.Stop();
        PlotCurrentDataset();
    }

    private void PlotCurrentDataset()
    {
        _plotRefreshDebounceTimer.Stop();

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
                        Dataset: GetMolecularWeightDataset(entry.Dataset, _selectedCalibrationCurve, yMode),
                        Index: entry.Index))
                    .ToArray();
                var activeConverted = convertedEntries
                    .Where(entry => entry.Index == _activeIndex)
                    .Select(entry => entry.Dataset)
                    .FirstOrDefault()
                    ?? GetMolecularWeightDataset(activeDataset, _selectedCalibrationCurve, yMode);
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

    private MolecularWeightDataset GetMolecularWeightDataset(
        GpcDataset dataset,
        CalibrationCurve curve,
        MolecularWeightYMode yMode)
    {
        var key = new MolecularWeightCacheKey(
            dataset.Points,
            dataset.SourceFilePath,
            dataset.YLabel,
            dataset.MolecularWeightStatistics,
            curve,
            yMode,
            MolecularWeightConverter.DefaultMinMolecularWeight,
            MolecularWeightConverter.DefaultMaxMolecularWeight);

        if (_molecularWeightCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var converted = _molecularWeightConverter.Convert(dataset, curve, yMode);
        _molecularWeightCache[key] = converted;
        return converted;
    }

    private void ClearComputedDataCaches()
    {
        _molecularWeightCache.Clear();
        _plotSeriesCache.Clear();
        _currentPlotUsesDownsampledData = false;
    }

    private int GetDisplayPointLimit(int seriesCount, long totalPointCount)
    {
        if (_forceFullResolutionPlot
            || seriesCount < OverlayDownsampleMinSeriesCount
            || totalPointCount <= OverlayDownsampleMinTotalPoints)
        {
            return int.MaxValue;
        }

        var perSeriesBudget = OverlayDisplayPointBudget / Math.Max(1, seriesCount);
        return Math.Clamp(
            perSeriesBudget,
            MinOverlayDisplayPointsPerSeries,
            MaxOverlayDisplayPointsPerSeries);
    }

    private PlotSeriesData GetPlotSeriesData(double[] xs, double[] ys, int maxPointCount)
    {
        var pointCount = Math.Min(xs.Length, ys.Length);
        var normalizedMaxPointCount = maxPointCount == int.MaxValue
            ? int.MaxValue
            : Math.Max(2, maxPointCount);
        var key = new PlotSeriesCacheKey(xs, ys, normalizedMaxPointCount);
        if (_plotSeriesCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var xRange = CreateDataRange(xs, pointCount);
        var yRange = CreateDataRange(ys, pointCount);
        var isDownsampled = pointCount > normalizedMaxPointCount;
        var series = isDownsampled
            ? CreateDownsampledPlotSeries(xs, ys, pointCount, normalizedMaxPointCount, xRange, yRange)
            : new PlotSeriesData
            {
                XValues = xs,
                YValues = ys,
                XRange = xRange,
                YRange = yRange,
                IsDownsampled = false,
            };

        _plotSeriesCache[key] = series;
        return series;
    }

    private static AxisDataRange CreateDataRange(IReadOnlyList<double> values, int count)
    {
        var range = new AxisDataRange();
        var valueCount = Math.Min(values.Count, count);
        for (var i = 0; i < valueCount; i++)
        {
            range.Include(values[i]);
        }

        return range;
    }

    private static PlotSeriesData CreateDownsampledPlotSeries(
        double[] xs,
        double[] ys,
        int sourcePointCount,
        int maxPointCount,
        AxisDataRange xRange,
        AxisDataRange yRange)
    {
        var targetBucketCount = Math.Max(1, (maxPointCount - 2) / 2);
        var bucketSize = Math.Max(1, (int)Math.Ceiling((sourcePointCount - 2) / (double)targetBucketCount));
        var downsampledXs = new List<double>(maxPointCount + 2) { xs[0] };
        var downsampledYs = new List<double>(maxPointCount + 2) { ys[0] };

        for (var start = 1; start < sourcePointCount - 1; start += bucketSize)
        {
            var end = Math.Min(sourcePointCount - 1, start + bucketSize);
            var minIndex = start;
            var maxIndex = start;
            var minY = double.PositiveInfinity;
            var maxY = double.NegativeInfinity;

            for (var i = start; i < end; i++)
            {
                var y = ys[i];
                if (!double.IsFinite(y))
                {
                    continue;
                }

                if (y < minY)
                {
                    minY = y;
                    minIndex = i;
                }

                if (y > maxY)
                {
                    maxY = y;
                    maxIndex = i;
                }
            }

            if (!double.IsFinite(minY) || !double.IsFinite(maxY))
            {
                AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, start);
                continue;
            }

            if (minIndex <= maxIndex)
            {
                AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, minIndex);
                if (maxIndex != minIndex)
                {
                    AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, maxIndex);
                }
            }
            else
            {
                AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, maxIndex);
                AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, minIndex);
            }
        }

        AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, sourcePointCount - 1);

        return new PlotSeriesData
        {
            XValues = downsampledXs.ToArray(),
            YValues = downsampledYs.ToArray(),
            XRange = xRange,
            YRange = yRange,
            IsDownsampled = true,
        };
    }

    private static void AddDownsampledPoint(
        List<double> downsampledXs,
        List<double> downsampledYs,
        IReadOnlyList<double> xs,
        IReadOnlyList<double> ys,
        int index)
    {
        if (downsampledXs.Count > 0
            && downsampledXs[^1].Equals(xs[index])
            && downsampledYs[^1].Equals(ys[index]))
        {
            return;
        }

        downsampledXs.Add(xs[index]);
        downsampledYs.Add(ys[index]);
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

    private static long GetRetentionTimePointCount(IReadOnlyList<(GpcDataset Dataset, int Index)> entries)
    {
        var pointCount = 0L;
        for (var i = 0; i < entries.Count; i++)
        {
            pointCount += entries[i].Dataset.XValues.LongLength;
        }

        return pointCount;
    }

    private static long GetMolecularWeightPointCount(IReadOnlyList<(MolecularWeightDataset Dataset, int Index)> entries)
    {
        var pointCount = 0L;
        for (var i = 0; i < entries.Count; i++)
        {
            pointCount += entries[i].Dataset.LogMolecularWeightValues.LongLength;
        }

        return pointCount;
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

        var displayPointLimit = GetDisplayPointLimit(entries.Count, GetRetentionTimePointCount(entries));
        _currentPlotUsesDownsampledData = false;
        var xRange = new AxisDataRange();
        var yRange = new AxisDataRange();
        for (var i = 0; i < entries.Count; i++)
        {
            var (dataset, datasetIndex) = entries[i];
            var series = GetPlotSeriesData(dataset.XValues, dataset.YValues, displayPointLimit);
            xRange.Include(series.XRange);
            yRange.Include(series.YRange);
            _currentPlotUsesDownsampledData |= series.IsDownsampled;

            var signal = _chromatogramPlot.Plot.Add.Scatter(series.XValues, series.YValues);
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
        if (!ApplyAxisLimits(xRange, yRange, false))
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

        var displayPointLimit = GetDisplayPointLimit(entries.Count, GetMolecularWeightPointCount(entries));
        _currentPlotUsesDownsampledData = false;
        var xRange = new AxisDataRange();
        var yRange = new AxisDataRange();
        for (var i = 0; i < entries.Count; i++)
        {
            var (dataset, datasetIndex) = entries[i];
            var series = GetPlotSeriesData(dataset.LogMolecularWeightValues, dataset.SignalValues, displayPointLimit);
            xRange.Include(series.XRange);
            yRange.Include(series.YRange);
            _currentPlotUsesDownsampledData |= series.IsDownsampled;

            var signal = _chromatogramPlot.Plot.Add.Scatter(series.XValues, series.YValues);
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
        if (!ApplyAxisLimits(xRange, yRange, true))
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

    private void ApplyPlotAppearance(float scale = 1f)
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        var plot = _chromatogramPlot.Plot;
        ApplyPlotFont(plot);
        ApplyPlotFontSize(plot, scale);
        ApplyPlotGrid(plot);
        ApplyYAxisTickLabels(plot);
        ApplyPlotFrame(plot, scale);
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

    private void ApplyPlotFontSize(ScottPlot.Plot plot, float scale = 1f)
    {
        var fontSize = GetPlotFontSize() * scale;
        plot.Axes.Title.Label.FontSize = fontSize + (2 * scale);
        plot.Axes.Bottom.Label.FontSize = fontSize;
        plot.Axes.Left.Label.FontSize = fontSize;
        plot.Axes.Bottom.TickLabelStyle.FontSize = Math.Max(6 * scale, fontSize - scale);
        plot.Axes.Left.TickLabelStyle.FontSize = Math.Max(6 * scale, fontSize - scale);
        plot.Legend.FontSize = Math.Max(6 * scale, fontSize - scale);
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

    private void ApplyPlotFrame(ScottPlot.Plot plot, float scale = 1f)
    {
        var frameVisible = PlotFrameCheckBox.IsChecked == true;
        plot.Axes.Frame(frameVisible);

        if (!frameVisible)
        {
            return;
        }

        plot.Axes.FrameWidth(GetPlotFrameWidth() * scale);
        plot.Axes.FrameColor(GetScottPlotColor(GetPlotFrameColorHex(), GraphFormattingConfig.DefaultPlotFrameColorHex));
    }

    private void ApplySeriesStyle(ScottPlot.Plottables.Scatter signal, int datasetIndex, float scale = 1f)
    {
        if (datasetIndex >= 0 && datasetIndex < _datasetStyles.Count)
        {
            var style = _datasetStyles[datasetIndex];
            signal.LineWidth = (float)style.LineWidth * scale;
            signal.MarkerSize = (float)style.MarkerSize * scale;
            var hex = style.ColorHex ?? AutoLineColors[datasetIndex % AutoLineColors.Length];
            signal.Color = ScottPlot.Color.FromHex(new[] { hex }).First();
            return;
        }

        signal.LineWidth = (float)GraphFormattingConfig.DefaultLineWidth * scale;
        signal.MarkerSize = (float)GraphFormattingConfig.DefaultMarkerSize * scale;
        var fallback = AutoLineColors[Math.Max(0, datasetIndex) % AutoLineColors.Length];
        signal.Color = ScottPlot.Color.FromHex(new[] { fallback }).First();
    }

    private void ApplyExportStyleScale(float scale)
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        ApplyPlotAppearance(scale);
        ApplyExistingSeriesStyles(scale);
    }

    private void ApplyExistingSeriesStyles(float scale)
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        var entries = GetDatasetsToPlotWithIndices();
        var scatters = _chromatogramPlot.Plot
            .GetPlottables()
            .OfType<ScottPlot.Plottables.Scatter>()
            .ToArray();

        for (var i = 0; i < scatters.Length; i++)
        {
            var datasetIndex = i < entries.Length ? entries[i].Index : i;
            ApplySeriesStyle(scatters[i], datasetIndex, scale);
        }
    }

    private static float GetExportStyleScale()
    {
        return ExportDpi / DisplayDpi;
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

    private bool ApplyAxisLimits(AxisDataRange xRange, AxisDataRange yRange, bool xIsMolecularWeight)
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

        if (xMin.HasValue || xMax.HasValue)
        {
            if (!TryGetRequestedRange(xRange, xMin, xMax, "X", out var left, out var right))
            {
                return false;
            }

            _chromatogramPlot.Plot.Axes.SetLimitsX(left, right);
        }

        if (yMin.HasValue || yMax.HasValue)
        {
            if (!TryGetRequestedRange(yRange, yMin, yMax, "Y", out var bottom, out var top))
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
        AxisDataRange dataRange,
        double? requestedMin,
        double? requestedMax,
        string axisName,
        out double min,
        out double max)
    {
        min = requestedMin ?? (dataRange.HasValue ? dataRange.Min : double.NaN);
        max = requestedMax ?? (dataRange.HasValue ? dataRange.Max : double.NaN);

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
                if (!SelectComboBoxItemByTag(LineColorComboBox, _formattingDefaults.DefaultLineColorHex ?? "Auto"))
                {
                    SelectComboBoxItemByTag(LineColorComboBox, _formattingDefaults.DefaultLineColorHex is null ? "Auto" : "Custom");
                }

                SetLineColorInput(_formattingDefaults.DefaultLineColorHex);
                LegendNameTextBox.Clear();
                LineWidthTextBox.Text = _formattingDefaults.FormatLineWidth();
                MarkerSizeTextBox.Text = _formattingDefaults.FormatMarkerSize();
                ActiveDatasetLabel.Text = "(データ未選択)";
                return;
            }

            var style = _datasetStyles[_activeIndex];

            if (!SelectComboBoxItemByTag(LineColorComboBox, style.ColorHex ?? "Auto"))
            {
                SelectComboBoxItemByTag(LineColorComboBox, style.ColorHex is null ? "Auto" : "Custom");
            }

            SetLineColorInput(style.ColorHex);
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
        ClearComputedDataCaches();

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
            if (tag.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                style.ColorHex = null;
            }
            else if (!tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                style.ColorHex = NormalizeHexColorCode(tag);
            }
        }

        _suppressStyleControlEvents = true;
        try
        {
            SetLineColorInput(style.ColorHex);
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }

        RefreshDatasetEntries();
        PlotCurrentDataset();
    }

    private void LineColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents || LineColorHexTextBox is null || LineColorPreviewBorder is null)
        {
            return;
        }
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            string? inputHex = null;
            if (!IsAutoColorText(LineColorHexTextBox.Text)
                && TryNormalizeHexColorCode(LineColorHexTextBox.Text, out var hex))
            {
                inputHex = hex;
            }

            _suppressStyleControlEvents = true;
            try
            {
                SelectColorComboBoxValue(LineColorComboBox, inputHex, true);
            }
            finally
            {
                _suppressStyleControlEvents = false;
            }

            UpdateLineColorPreview(inputHex);
            return;
        }

        var style = _datasetStyles[_activeIndex];
        if (IsAutoColorText(LineColorHexTextBox.Text))
        {
            style.ColorHex = null;
        }
        else if (TryNormalizeHexColorCode(LineColorHexTextBox.Text, out var hex))
        {
            style.ColorHex = hex;
        }
        else
        {
            UpdateLineColorPreview(style.ColorHex);
            return;
        }

        _suppressStyleControlEvents = true;
        try
        {
            SelectColorComboBoxValue(LineColorComboBox, style.ColorHex, true);
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }

        UpdateLineColorPreview(style.ColorHex);
        RefreshDatasetEntries();
        SchedulePlotCurrentDataset();
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
        SchedulePlotCurrentDataset();
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
            SchedulePlotCurrentDataset();
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
            SchedulePlotCurrentDataset();
        }
    }

    private void GraphAppearanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        if (ReferenceEquals(sender, PlotFrameColorComboBox)
            && PlotFrameColorHexTextBox is not null
            && PlotFrameColorPreviewBorder is not null)
        {
            _suppressGraphAppearanceEvents = true;
            try
            {
                SyncPlotFrameColorInputFromComboBox();
            }
            finally
            {
                _suppressGraphAppearanceEvents = false;
            }
        }

        ApplyGraphAppearanceAndRefresh();
    }

    private void PlotFrameColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents || PlotFrameColorHexTextBox is null || PlotFrameColorPreviewBorder is null)
        {
            return;
        }

        var hex = GetPlotFrameColorHex();
        _suppressGraphAppearanceEvents = true;
        try
        {
            SelectColorComboBoxValue(PlotFrameColorComboBox, hex, false);
        }
        finally
        {
            _suppressGraphAppearanceEvents = false;
        }

        UpdatePlotFrameColorPreview(hex);
        ApplyGraphAppearanceAndRefresh();
    }

    private void GraphAppearanceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        ApplyGraphAppearanceAndRefresh();
    }

    private void GraphLabelTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        SchedulePlotCurrentDataset();
    }

    private void GraphAppearanceNumericTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        SchedulePlotCurrentDataset();
    }

    private void AxisRangeTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter && e.Key != System.Windows.Input.Key.Return)
        {
            return;
        }

        e.Handled = true;
        CommitAxisRangeFromInputs();
    }

    private void AxisRangeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitAxisRangeFromInputs();
    }

    private void CommitAxisRangeFromInputs()
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        if (!IsAxisRangeInputValidOrEmpty(XMinTextBox)
            || !IsAxisRangeInputValidOrEmpty(XMaxTextBox)
            || !IsAxisRangeInputValidOrEmpty(YMinTextBox)
            || !IsAxisRangeInputValidOrEmpty(YMaxTextBox))
        {
            return;
        }

        PlotCurrentDataset();
    }

    private static bool IsAxisRangeInputValidOrEmpty(TextBox textBox)
    {
        var text = textBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out _)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _);
    }

    private void AspectRatioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

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

    private void SelectGraphFontComboBoxValue(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)
            || fontName.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            GraphFontComboBox.SelectedIndex = 0;
            return;
        }

        if (SelectComboBoxItemByTag(GraphFontComboBox, fontName))
        {
            return;
        }

        GraphFontComboBox.SelectedIndex = -1;
        GraphFontComboBox.Text = fontName;
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

    private static bool SelectComboBoxItemByTag(ComboBox comboBox, string? tag)
    {
        var desiredTag = string.IsNullOrWhiteSpace(tag) ? "Auto" : tag.Trim();

        for (var i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item
                && item.Tag is string itemTag
                && itemTag.Equals(desiredTag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    private static void SelectColorComboBoxValue(ComboBox comboBox, string? hex, bool allowAuto)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            if (allowAuto && SelectComboBoxItemByTag(comboBox, "Auto"))
            {
                return;
            }

            SelectComboBoxItemByTag(comboBox, "Custom");
            return;
        }

        if (!SelectComboBoxItemByTag(comboBox, hex))
        {
            SelectComboBoxItemByTag(comboBox, "Custom");
        }
    }

    private static string? GetSelectedComboBoxTag(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
            : null;
    }

    private string? GetSelectedLineColorConfigValue()
    {
        if (IsAutoColorText(LineColorHexTextBox.Text))
        {
            return null;
        }

        return TryNormalizeHexColorCode(LineColorHexTextBox.Text, out var hex)
            ? hex
            : null;
    }

    private string? GetSelectedAspectRatioConfigValue()
    {
        var ratioText = GetSelectedComboBoxTag(AspectRatioComboBox) ?? AspectRatioComboBox.Text.Trim();
        return string.IsNullOrWhiteSpace(ratioText)
            || ratioText.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : ratioText;
    }

    private float GetPlotFontSize()
    {
        return TryParsePositiveDouble(GraphFontSizeTextBox.Text, out var fontSize)
            ? (float)fontSize
            : (float)GraphFormattingConfig.DefaultFontSize;
    }

    private float GetPlotFrameWidth()
    {
        return TryParsePositiveDouble(PlotFrameWidthTextBox.Text, out var width)
            ? (float)width
            : (float)GraphFormattingConfig.DefaultPlotFrameWidth;
    }

    private string GetPlotFrameColorHex()
    {
        return TryNormalizeHexColorCode(PlotFrameColorHexTextBox.Text, out var hex)
            ? hex
            : GraphFormattingConfig.DefaultPlotFrameColorHex;
    }

    private static ScottPlot.Color GetScottPlotColor(string hex, string fallbackHex)
    {
        try
        {
            return ScottPlot.Color.FromHex(new[] { hex }).First();
        }
        catch
        {
            return ScottPlot.Color.FromHex(new[] { fallbackHex }).First();
        }
    }

    private void SyncPlotFrameColorInputFromComboBox()
    {
        var tag = GetSelectedComboBoxTag(PlotFrameColorComboBox);
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            UpdatePlotFrameColorPreview(GetPlotFrameColorHex());
            return;
        }

        SetPlotFrameColorInput(tag);
    }

    private void SetPlotFrameColorInput(string? hex)
    {
        var normalized = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GraphFormattingConfig.DefaultPlotFrameColorHex;

        if (!SelectComboBoxItemByTag(PlotFrameColorComboBox, normalized))
        {
            SelectComboBoxItemByTag(PlotFrameColorComboBox, "Custom");
        }

        PlotFrameColorHexTextBox.Text = normalized;
        UpdatePlotFrameColorPreview(normalized);
    }

    private void SetLineColorInput(string? hex)
    {
        LineColorHexTextBox.Text = string.IsNullOrWhiteSpace(hex) ? "Auto" : NormalizeHexColorCode(hex);
        UpdateLineColorPreview(hex);
    }

    private void UpdatePlotFrameColorPreview(string? hex)
    {
        if (PlotFrameColorPreviewBorder is null)
        {
            return;
        }

        var previewHex = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GraphFormattingConfig.DefaultPlotFrameColorHex;
        PlotFrameColorPreviewBorder.Background = new SolidColorBrush(HexToMediaColor(previewHex));
    }

    private void UpdateLineColorPreview(string? hex)
    {
        if (LineColorPreviewBorder is null)
        {
            return;
        }

        var previewHex = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GetAutoLineColorPreviewHex();
        LineColorPreviewBorder.Background = new SolidColorBrush(HexToMediaColor(previewHex));
    }

    private string GetAutoLineColorPreviewHex()
    {
        if (_activeIndex >= 0)
        {
            return AutoLineColors[_activeIndex % AutoLineColors.Length];
        }

        return _formattingDefaults.DefaultLineColorHex ?? AutoLineColors[0];
    }

    private static bool IsAutoColorText(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            || text.Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHexColorCode(string text)
    {
        return TryNormalizeHexColorCode(text, out var hex) ? hex : "#000000";
    }

    private static bool TryNormalizeHexColorCode(string? text, out string hex)
    {
        hex = string.Empty;

        var value = text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length != 6 || !value.All(Uri.IsHexDigit))
        {
            return false;
        }

        hex = $"#{value.ToUpperInvariant()}";
        return true;
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
            ? SquareExportWidth
            : DefaultExportWidth;
        var height = Math.Max(1, (int)Math.Round(width / ratio.Value));
        return (width, height);
    }

    private void SaveGraphSvg(string filePath, int width, int height)
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        var svg = _chromatogramPlot.Plot.GetSvgHtml(width, height);
        File.WriteAllText(filePath, svg);
    }

    private static GraphSaveFormat GetGraphSaveFormat(string filePath, int filterIndex)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return GraphSaveFormat.Svg;
        }

        return filterIndex == 2
            ? GraphSaveFormat.Svg
            : GraphSaveFormat.Png;
    }

    private static string EnsureGraphSaveFileExtension(string filePath, GraphSaveFormat saveFormat)
    {
        var extension = saveFormat == GraphSaveFormat.Svg ? ".svg" : ".png";
        return Path.ChangeExtension(filePath, extension);
    }

    private static void ApplyPngDpiMetadata(string filePath, int dpi)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (!HasPngSignature(bytes))
        {
            return;
        }

        var pixelsPerMeter = checked((uint)Math.Round(dpi / 0.0254));
        var physicalPixelDimensionsChunk = CreatePngPhysicalPixelDimensionsChunk(pixelsPerMeter);
        var offset = 8;
        var insertOffset = -1;

        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > int.MaxValue || offset + 12 + (int)length > bytes.Length)
            {
                return;
            }

            var chunkLength = 12 + (int)length;
            var chunkTypeOffset = offset + 4;
            if (PngChunkTypeEquals(bytes, chunkTypeOffset, "pHYs"))
            {
                File.WriteAllBytes(filePath, ReplaceBytes(bytes, offset, chunkLength, physicalPixelDimensionsChunk));
                return;
            }

            if (PngChunkTypeEquals(bytes, chunkTypeOffset, "IHDR"))
            {
                insertOffset = offset + chunkLength;
            }

            offset += chunkLength;
        }

        if (insertOffset > 0)
        {
            File.WriteAllBytes(filePath, InsertBytes(bytes, insertOffset, physicalPixelDimensionsChunk));
        }
    }

    private static byte[] CreatePngPhysicalPixelDimensionsChunk(uint pixelsPerMeter)
    {
        const int chunkDataLength = 9;
        var chunk = new byte[4 + 4 + chunkDataLength + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), chunkDataLength);
        chunk[4] = (byte)'p';
        chunk[5] = (byte)'H';
        chunk[6] = (byte)'Y';
        chunk[7] = (byte)'s';
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8, 4), pixelsPerMeter);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12, 4), pixelsPerMeter);
        chunk[16] = 1;

        var crc = CalculatePngCrc(chunk.AsSpan(4, 4 + chunkDataLength));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17, 4), crc);
        return chunk;
    }

    private static uint CalculatePngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1
                    ? (crc >> 1) ^ 0xEDB88320u
                    : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static bool HasPngSignature(byte[] bytes)
    {
        return bytes.Length >= 8
            && bytes[0] == 137
            && bytes[1] == 80
            && bytes[2] == 78
            && bytes[3] == 71
            && bytes[4] == 13
            && bytes[5] == 10
            && bytes[6] == 26
            && bytes[7] == 10;
    }

    private static bool PngChunkTypeEquals(byte[] bytes, int offset, string type)
    {
        return offset + 4 <= bytes.Length
            && bytes[offset] == (byte)type[0]
            && bytes[offset + 1] == (byte)type[1]
            && bytes[offset + 2] == (byte)type[2]
            && bytes[offset + 3] == (byte)type[3];
    }

    private static byte[] InsertBytes(byte[] source, int offset, byte[] insertion)
    {
        var result = new byte[source.Length + insertion.Length];
        Buffer.BlockCopy(source, 0, result, 0, offset);
        Buffer.BlockCopy(insertion, 0, result, offset, insertion.Length);
        Buffer.BlockCopy(source, offset, result, offset + insertion.Length, source.Length - offset);
        return result;
    }

    private static byte[] ReplaceBytes(byte[] source, int offset, int count, byte[] replacement)
    {
        var result = new byte[source.Length - count + replacement.Length];
        Buffer.BlockCopy(source, 0, result, 0, offset);
        Buffer.BlockCopy(replacement, 0, result, offset, replacement.Length);
        var sourceTailOffset = offset + count;
        var resultTailOffset = offset + replacement.Length;
        Buffer.BlockCopy(source, sourceTailOffset, result, resultTailOffset, source.Length - sourceTailOffset);
        return result;
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

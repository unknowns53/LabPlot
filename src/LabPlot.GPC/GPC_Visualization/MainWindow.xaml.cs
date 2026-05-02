using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Buffers.Binary;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using GpcAnalyzer.Core;
using LabPlot.Core;
using Microsoft.Win32;
using ScottPlot.WPF;
using static LabPlot.Core.PlotAppearance;
using static LabPlot.Core.Wpf.FormatHelpers;

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
    private readonly List<string?> _datasetSelectedPeakIds = new();
    private readonly ObservableCollection<DatasetEntryVm> _datasetEntries = new();
    private readonly Dictionary<MolecularWeightCacheKey, MolecularWeightDataset> _molecularWeightCache = new();
    private readonly Dictionary<PlotSeriesCacheKey, PlotSeriesData> _plotSeriesCache = new();
    private readonly DispatcherTimer _plotRefreshDebounceTimer = new() { Interval = PlotRefreshDebounceInterval };
    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private int _activeIndex = -1;
    private GpcDataset? _currentDataset;
    private CalibrationCurveSet? _calibrationCurveSet;
    private CalibrationCurve? _selectedCalibrationCurve;
    private string? _calibrationFilePath;
    private WpfPlot? _chromatogramPlot;
    private bool _updatingCalibrationSelection;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressDatasetListEvents;

    // Cache of the per-app "auto-show" decision from the most recent plot
    // pass. Format-panel handlers (legend visibility / position combo box)
    // read this to refresh the legend without re-running the heavy plot
    // path; per-dataset state changes update it via the Plot* methods.
    private bool _currentLegendAutoShow;
    private bool _forceFullResolutionPlot;
    private bool _currentPlotUsesDownsampledData;
    private bool _suppressRepresentativePeakSelection;
    private MolecularWeightStatistics? _currentStatistics;

    private const string DatasetReorderDataFormat = "Gpc.DatasetEntryIndex";
    private Point? _datasetDragStartPoint;
    private InsertionAdorner? _datasetInsertionAdorner;

    public MainWindow()
    {
        InitializeComponent();
        LoadFormattingDefaults();
        ApplyFormattingConfigToControls(_formattingDefaults);
        DatasetListBox.ItemsSource = _datasetEntries;
        _plotRefreshDebounceTimer.Tick += PlotRefreshDebounceTimer_Tick;
        RegisterShortcuts();
        Loaded += MainWindow_Loaded;
    }

    private void RegisterShortcuts()
    {
        AddShortcut(System.Windows.Input.Key.O, System.Windows.Input.ModifierKeys.Control,
            () => OpenCsvButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Control,
            () => SaveGraphButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.E, System.Windows.Input.ModifierKeys.Control,
            () => ExportDataButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.R, System.Windows.Input.ModifierKeys.Control,
            () => AxisRangePanel.ResetToAuto());
        AddShortcut(System.Windows.Input.Key.O, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
            () => LoadSessionButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
            () => SaveSessionButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.D1, System.Windows.Input.ModifierKeys.Control,
            () => CycleComboBoxSelection(SolventComboBox));
        AddShortcut(System.Windows.Input.Key.D2, System.Windows.Input.ModifierKeys.Control,
            () => CycleComboBoxSelection(DetectorComboBox));
        AddShortcut(System.Windows.Input.Key.D3, System.Windows.Input.ModifierKeys.Control,
            () => ToggleCheckBox(MolecularWeightCheckBox));
        AddShortcut(System.Windows.Input.Key.D4, System.Windows.Input.ModifierKeys.Control,
            () => CycleComboBoxSelection(MolecularWeightYModeComboBox));
        AddShortcut(System.Windows.Input.Key.L, System.Windows.Input.ModifierKeys.Control,
            () => ToggleCheckBox(OverlayCheckBox));
        AddShortcut(System.Windows.Input.Key.G, System.Windows.Input.ModifierKeys.Control,
            () => ToggleCheckBox(PlotGridCheckBox));
        AddShortcut(System.Windows.Input.Key.F2, System.Windows.Input.ModifierKeys.None,
            FocusLegendNameTextBox);
    }

    private static void CycleComboBoxSelection(ComboBox comboBox)
    {
        if (comboBox is null || !comboBox.IsEnabled || comboBox.Items.Count <= 1)
        {
            return;
        }

        var current = comboBox.SelectedIndex;
        var next = (current + 1) % comboBox.Items.Count;
        if (next != current)
        {
            comboBox.SelectedIndex = next;
        }
    }

    private static void ToggleCheckBox(CheckBox checkBox)
    {
        if (checkBox is null || !checkBox.IsEnabled)
        {
            return;
        }

        checkBox.IsChecked = checkBox.IsChecked != true;
    }

    private void FocusLegendNameTextBox()
    {
        if (LegendNameTextBox is null || !LegendNameTextBox.IsEnabled)
        {
            return;
        }

        LegendNameTextBox.Focus();
        LegendNameTextBox.SelectAll();
    }

    private void AddShortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers, Action handler)
    {
        var command = new System.Windows.Input.RoutedUICommand();
        InputBindings.Add(new System.Windows.Input.KeyBinding(command, key, modifiers));
        CommandBindings.Add(new System.Windows.Input.CommandBinding(command, (_, e) =>
        {
            handler();
            e.Handled = true;
        }));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            InitializePlotControl();
            TryLoadDefaultCalibration();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void TryLoadDefaultCalibration()
    {
        var path = _formattingDefaults.DefaultCalibrationFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            SetStatus($"既定の較正曲線が見つかりませんでした: {path}", true);
            return;
        }

        try
        {
            _calibrationCurveSet = _standardCurveReader.Read(path);
            _calibrationFilePath = path;
            ClearComputedDataCaches();
            CalibrationPathTextBlock.Text = $"較正曲線: {path}";
            PopulateSolventComboBox();
            UpdateMolecularWeightAvailability();
            SetStatus($"既定の較正曲線を読み込みました: {path}", false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException)
        {
            _calibrationCurveSet = null;
            _selectedCalibrationCurve = null;
            _calibrationFilePath = null;
            CalibrationPathTextBlock.Text = "較正曲線: 未選択";
            SetStatus($"既定の較正曲線を読み込めませんでした: {ex.Message}", true);
        }
    }

    private string? GetDefaultOutputDirectoryIfExists()
    {
        var dir = _formattingDefaults.DefaultOutputDirectory;
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
    }

    private void ApplyDefaultOutputDirectoryToDialog(FileDialog dialog)
    {
        if (GetDefaultOutputDirectoryIfExists() is { } initialDirectory)
        {
            dialog.InitialDirectory = initialDirectory;
        }
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
            DefaultLineColorHex = LineColorPicker.HexValue,
            LineWidth = TryParsePositiveDouble(LineWidthTextBox.Text, out var lineWidth)
                ? lineWidth
                : GraphFormattingConfig.DefaultLineWidth,
            MarkerSize = TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var markerSize)
                ? markerSize
                : GraphFormattingConfig.DefaultMarkerSize,
            DefaultCalibrationFilePath = DefaultCalibrationPathTextBox.Text,
            DefaultOutputDirectory = DefaultOutputDirectoryTextBox.Text,
            LegendVisibility = GetComboBoxTag(LegendVisibilityComboBox),
            LegendPosition = GetComboBoxTag(LegendPositionComboBox)
                ?? GraphFormattingConfigBase.DefaultLegendPositionValue,
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
            MajorTicksCheckBox.IsChecked = config.ShowMajorTicks;
            MinorTicksCheckBox.IsChecked = config.ShowMinorTicks;
            PlotFrameCheckBox.IsChecked = config.ShowPlotFrame;
            PlotFrameWidthTextBox.Text = config.FormatFrameWidth();
            PlotFrameColorPicker.SetHexValue(config.PlotFrameColorHex);
            BackgroundColorPicker.SetHexValue(config.BackgroundColorHex);
            TitleVisibleCheckBox.IsChecked = config.ShowTitle;
            TitleBoldCheckBox.IsChecked = config.TitleBold;
            AxisLabelBoldCheckBox.IsChecked = config.AxisLabelBold;

            if (!SelectComboBoxItemByTag(AspectRatioComboBox, config.AspectRatio ?? "Auto"))
            {
                AspectRatioComboBox.SelectedIndex = 0;
            }

            SelectComboBoxByTag(LegendVisibilityComboBox, config.LegendVisibility ?? "Auto");
            SelectComboBoxByTag(LegendPositionComboBox, config.LegendPosition);
        }
        finally
        {
            _suppressGraphAppearanceEvents = false;
        }

        _suppressStyleControlEvents = true;
        try
        {
            LineColorPicker.SetHexValue(config.DefaultLineColorHex);
            LegendNameTextBox.Clear();
            LineWidthTextBox.Text = config.FormatLineWidth();
            MarkerSizeTextBox.Text = config.FormatMarkerSize();
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }

        DefaultCalibrationPathTextBox.Text = config.DefaultCalibrationFilePath ?? string.Empty;
        DefaultOutputDirectoryTextBox.Text = config.DefaultOutputDirectory ?? string.Empty;
    }

    private void BrowseDefaultCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "既定の較正曲線 JSON を選択",
            Filter = "JSON (*.json)|*.json|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        var current = DefaultCalibrationPathTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(current))
        {
            var directory = Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }

            if (File.Exists(current))
            {
                dialog.FileName = Path.GetFileName(current);
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            DefaultCalibrationPathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseDefaultOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "既定の出力フォルダを選択",
        };

        var current = DefaultOutputDirectoryTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            DefaultOutputDirectoryTextBox.Text = dialog.FolderName;
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
            _datasetSelectedPeakIds.Clear();
            ClearComputedDataCaches();
            _activeIndex = -1;
            RefreshDatasetEntries();
            SetGraphActionsEnabled(false);
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
            _datasetSelectedPeakIds.Clear();
            ClearComputedDataCaches();
        }

        _loadedDatasets.Add(dataset);
        _datasetStyles.Add(CreateDefaultDatasetStyle());
        _datasetSelectedPeakIds.Add(null);
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
            _calibrationFilePath = dialog.FileName;
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
            _calibrationFilePath = null;
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
        AxisRangePanel.SetXValues(null, null);
        AxisRangePanel.SetYValues(null, null);
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

    private void SetGraphActionsEnabled(bool enabled)
    {
        SaveGraphButton.IsEnabled = enabled;
        ExportDataButton.IsEnabled = enabled;
        SaveSessionButton.IsEnabled = enabled;
    }

    private void ExportDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedDatasets.Count == 0)
        {
            SetStatus("出力可能なデータがありません。", true);
            return;
        }

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset?.SourceFilePath) ?? "gpc_analysis";
        var dialog = new SaveFileDialog
        {
            Title = "解析結果を保存",
            Filter = "Excelブック (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            FileName = $"{defaultName}.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var data = BuildAnalysisExport();
            if (data.Entries.Count == 0)
            {
                SetStatus("出力可能なデータがありません。", true);
                return;
            }

            var format = GetAnalysisExportFormat(dialog.FileName, dialog.FilterIndex);
            var fileName = EnsureAnalysisExportExtension(dialog.FileName, format);
            IAnalysisExporter exporter = format == AnalysisExportFormat.Csv
                ? new CsvAnalysisExporter()
                : new XlsxAnalysisExporter();
            exporter.Export(data, fileName);
            SetStatus($"解析結果を保存しました: {fileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"保存に失敗しました: {ex.Message}", true);
        }
    }

    private AnalysisExport BuildAnalysisExport()
    {
        var entries = new List<GpcAnalysisExportEntry>();
        var plotEntries = GetDatasetsToPlotWithIndices();
        var molecularWeightEnabled =
            MolecularWeightCheckBox.IsChecked == true && _selectedCalibrationCurve is not null;
        var yMode = molecularWeightEnabled
            ? GetSelectedMolecularWeightYMode()
            : MolecularWeightYMode.Signal;

        foreach (var (dataset, index) in plotEntries)
        {
            MolecularWeightDataset? mwDataset = null;
            var stats = dataset.MolecularWeightStatistics;

            if (molecularWeightEnabled && _selectedCalibrationCurve is not null)
            {
                try
                {
                    mwDataset = GetMolecularWeightDataset(dataset, _selectedCalibrationCurve, yMode);
                    stats ??= mwDataset.Statistics;
                }
                catch (InvalidDataException)
                {
                }
            }

            stats = ApplyStoredSelectedPeak(stats, index);

            entries.Add(new GpcAnalysisExportEntry
            {
                DisplayName = Path.GetFileName(dataset.SourceFilePath) ?? $"dataset_{index + 1}",
                SourceFilePath = dataset.SourceFilePath,
                Detector = dataset.Detector,
                XLabel = dataset.XLabel,
                YLabel = dataset.YLabel,
                ChromatogramPoints = dataset.Points,
                Statistics = stats,
                MolecularWeightDataset = mwDataset,
            });
        }

        return new AnalysisExport
        {
            Entries = entries,
            GeneratorName = "GPC Visualization",
        };
    }

    private enum AnalysisExportFormat
    {
        Xlsx,
        Csv,
    }

    private static AnalysisExportFormat GetAnalysisExportFormat(string filePath, int filterIndex)
    {
        var ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisExportFormat.Csv;
        }

        if (string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisExportFormat.Xlsx;
        }

        return filterIndex == 2 ? AnalysisExportFormat.Csv : AnalysisExportFormat.Xlsx;
    }

    private static string EnsureAnalysisExportExtension(string filePath, AnalysisExportFormat format)
    {
        var expected = format == AnalysisExportFormat.Csv ? ".csv" : ".xlsx";
        if (string.Equals(Path.GetExtension(filePath), expected, StringComparison.OrdinalIgnoreCase))
        {
            return filePath;
        }

        return Path.ChangeExtension(filePath, expected);
    }

    private void SaveSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedDatasets.Count == 0)
        {
            SetStatus("保存する状態がありません。", true);
            return;
        }

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset?.SourceFilePath) ?? "gpc_session";
        var dialog = new SaveFileDialog
        {
            Title = "解析条件を保存",
            Filter = "GPC 解析条件 (*.gpcjson)|*.gpcjson|JSON (*.json)|*.json",
            FileName = $"{defaultName}.gpcjson",
            DefaultExt = ".gpcjson",
            AddExtension = true,
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var session = BuildAnalysisSession();
            new AnalysisSessionStore<GpcAnalysisSession>().Save(session, dialog.FileName);
            SetStatus($"解析条件を保存しました: {dialog.FileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"保存に失敗しました: {ex.Message}", true);
        }
    }

    private void LoadSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "解析条件を読み込み",
            Filter = "GPC 解析条件 (*.gpcjson;*.json)|*.gpcjson;*.json|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        GpcAnalysisSession session;
        try
        {
            session = new AnalysisSessionStore<GpcAnalysisSession>().Load(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            SetStatus($"読み込みに失敗しました: {ex.Message}", true);
            return;
        }

        var warnings = new List<string>();
        ApplyAnalysisSession(session, warnings);

        if (warnings.Count == 0)
        {
            SetStatus($"解析条件を読み込みました: {dialog.FileName}", false);
        }
        else
        {
            SetStatus($"解析条件を読み込みました（一部復元できない項目あり: {string.Join(" / ", warnings)})", true);
        }
    }

    private GpcAnalysisSession BuildAnalysisSession()
    {
        var datasets = new List<GpcAnalysisSessionDataset>();
        for (var i = 0; i < _loadedDatasets.Count; i++)
        {
            var dataset = _loadedDatasets[i];
            var style = i < _datasetStyles.Count ? _datasetStyles[i] : CreateDefaultDatasetStyle();
            var selectedPeakId = i < _datasetSelectedPeakIds.Count ? _datasetSelectedPeakIds[i] : null;

            datasets.Add(new GpcAnalysisSessionDataset
            {
                SourceFilePath = dataset.SourceFilePath ?? string.Empty,
                Detector = dataset.Detector,
                SelectedPeakId = selectedPeakId,
                Style = new AnalysisSessionStyle
                {
                    ColorHex = style.ColorHex,
                    LegendName = style.LegendName,
                    LineWidth = style.LineWidth,
                    MarkerSize = style.MarkerSize,
                },
            });
        }

        AnalysisSessionCalibration? calibration = null;
        if (!string.IsNullOrWhiteSpace(_calibrationFilePath))
        {
            calibration = new AnalysisSessionCalibration
            {
                FilePath = _calibrationFilePath!,
                Solvent = SolventComboBox.SelectedItem as string,
                Detector = DetectorComboBox.SelectedItem as string,
            };
        }

        var molecularWeight = new AnalysisSessionMolecularWeight
        {
            Enabled = MolecularWeightCheckBox.IsChecked == true,
            YMode = GetSelectedMolecularWeightYMode().ToString(),
            MinMolecularWeight = MolecularWeightConverter.DefaultMinMolecularWeight,
            MaxMolecularWeight = MolecularWeightConverter.DefaultMaxMolecularWeight,
        };

        var axes = new GpcAnalysisSessionAxes
        {
            Mode = MolecularWeightCheckBox.IsChecked == true
                ? nameof(AnalysisSessionAxisMode.MolecularWeight)
                : nameof(AnalysisSessionAxisMode.RetentionTime),
            XMin = AxisRangePanel.XMinValue,
            XMax = AxisRangePanel.XMaxValue,
            YMin = AxisRangePanel.YMinValue,
            YMax = AxisRangePanel.YMaxValue,
        };

        var labels = new AnalysisSessionLabels
        {
            Title = NullIfWhiteSpace(TitleTextBox.Text),
            XLabel = NullIfWhiteSpace(XLabelTextBox.Text),
            YLabel = NullIfWhiteSpace(YLabelTextBox.Text),
        };

        // 環境設定はセッションには含めず、ユーザーごとの formatting_config.json にだけ保存する。
        var sessionFormatting = CaptureFormattingConfigFromControls();
        sessionFormatting.DefaultCalibrationFilePath = null;
        sessionFormatting.DefaultOutputDirectory = null;

        return new GpcAnalysisSession
        {
            Overlay = OverlayCheckBox.IsChecked == true,
            ActiveDatasetIndex = _activeIndex,
            Datasets = datasets,
            Calibration = calibration,
            MolecularWeight = molecularWeight,
            Axes = axes,
            Labels = labels,
            Formatting = sessionFormatting,
        };
    }

    private void ApplyAnalysisSession(GpcAnalysisSession session, List<string> warnings)
    {
        _loadedDatasets.Clear();
        _datasetStyles.Clear();
        _datasetSelectedPeakIds.Clear();
        _calibrationCurveSet = null;
        _selectedCalibrationCurve = null;
        _calibrationFilePath = null;
        ClearComputedDataCaches();
        _activeIndex = -1;
        _currentDataset = null;
        _currentStatistics = null;

        if (session.Formatting is not null)
        {
            session.Formatting.Normalize();
            // 環境設定はセッションファイルではなくユーザーごとの formatting_config に属するので保持する。
            session.Formatting.DefaultCalibrationFilePath = _formattingDefaults.DefaultCalibrationFilePath;
            session.Formatting.DefaultOutputDirectory = _formattingDefaults.DefaultOutputDirectory;
            _formattingDefaults = session.Formatting;
            ApplyFormattingConfigToControls(session.Formatting);
        }

        if (session.Calibration is { FilePath: var calibrationPath } && !string.IsNullOrWhiteSpace(calibrationPath))
        {
            if (File.Exists(calibrationPath))
            {
                try
                {
                    _calibrationCurveSet = _standardCurveReader.Read(calibrationPath);
                    _calibrationFilePath = calibrationPath;
                    CalibrationPathTextBlock.Text = $"較正曲線: {calibrationPath}";
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException)
                {
                    warnings.Add($"較正曲線読み込み失敗 ({ex.Message})");
                    CalibrationPathTextBlock.Text = "較正曲線: 未選択";
                }
            }
            else
            {
                warnings.Add($"較正曲線が見つかりません ({calibrationPath})");
                CalibrationPathTextBlock.Text = "較正曲線: 未選択";
            }
        }
        else
        {
            CalibrationPathTextBlock.Text = "較正曲線: 未選択";
        }

        var sessionToLoadedIndex = new Dictionary<int, int>();
        for (var i = 0; i < session.Datasets.Count; i++)
        {
            var sessionDataset = session.Datasets[i];
            if (string.IsNullOrWhiteSpace(sessionDataset.SourceFilePath)
                || !File.Exists(sessionDataset.SourceFilePath))
            {
                warnings.Add($"ファイル欠落 ({sessionDataset.SourceFilePath ?? "不明"})");
                continue;
            }

            try
            {
                var loaded = _reader.Read(sessionDataset.SourceFilePath);
                if (!string.IsNullOrWhiteSpace(sessionDataset.Detector)
                    && loaded.AvailableDetectors.Contains(sessionDataset.Detector!, StringComparer.OrdinalIgnoreCase))
                {
                    loaded = loaded.WithDetector(sessionDataset.Detector!);
                }

                _loadedDatasets.Add(loaded);
                _datasetStyles.Add(new DatasetStyle
                {
                    ColorHex = sessionDataset.Style.ColorHex,
                    LegendName = sessionDataset.Style.LegendName,
                    LineWidth = sessionDataset.Style.LineWidth,
                    MarkerSize = sessionDataset.Style.MarkerSize,
                });
                _datasetSelectedPeakIds.Add(sessionDataset.SelectedPeakId);
                sessionToLoadedIndex[i] = _loadedDatasets.Count - 1;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                warnings.Add($"読み込み失敗 ({Path.GetFileName(sessionDataset.SourceFilePath)}: {ex.Message})");
            }
        }

        OverlayCheckBox.IsChecked = session.Overlay;

        if (_loadedDatasets.Count == 0)
        {
            FilePathTextBlock.Text = string.Empty;
            SetGraphActionsEnabled(false);
            UpdateStatisticsText((MolecularWeightStatistics?)null);
            RefreshDatasetEntries();
            if (_chromatogramPlot is not null)
            {
                InitializeEmptyPlot();
            }
            return;
        }

        if (sessionToLoadedIndex.TryGetValue(session.ActiveDatasetIndex, out var mappedActive))
        {
            _activeIndex = mappedActive;
        }
        else
        {
            _activeIndex = _loadedDatasets.Count - 1;
        }

        _currentDataset = _loadedDatasets[_activeIndex];
        FilePathTextBlock.Text = _loadedDatasets.Count > 1
            ? $"{_loadedDatasets.Count} files (latest: {_currentDataset.SourceFilePath})"
            : _currentDataset.SourceFilePath ?? string.Empty;

        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();

        if (_calibrationCurveSet is not null)
        {
            PopulateSolventComboBox();

            if (session.Calibration is not null
                && !string.IsNullOrWhiteSpace(session.Calibration.Solvent))
            {
                var matchSolvent = _calibrationCurveSet.Solvents
                    .FirstOrDefault(s => string.Equals(
                        s,
                        session.Calibration.Solvent,
                        StringComparison.OrdinalIgnoreCase));
                if (matchSolvent is not null)
                {
                    SolventComboBox.SelectedItem = matchSolvent;
                }
            }

            if (session.Calibration is not null
                && !string.IsNullOrWhiteSpace(session.Calibration.Detector))
            {
                var matchDetector = DetectorComboBox.Items.Cast<object>()
                    .OfType<string>()
                    .FirstOrDefault(d => string.Equals(
                        d,
                        session.Calibration.Detector,
                        StringComparison.OrdinalIgnoreCase));
                if (matchDetector is not null)
                {
                    DetectorComboBox.SelectedItem = matchDetector;
                }
            }
        }
        else
        {
            UpdateMolecularWeightAvailability();
        }

        ApplyMolecularWeightYModeSelection(session.MolecularWeight.YMode);

        if (session.MolecularWeight.Enabled && _selectedCalibrationCurve is not null)
        {
            MolecularWeightCheckBox.IsChecked = true;
        }
        else
        {
            MolecularWeightCheckBox.IsChecked = false;
            if (session.MolecularWeight.Enabled)
            {
                warnings.Add("分子量表示の前提（較正曲線/溶媒/検出器）が揃わなかったため無効化しました");
            }
        }

        TitleTextBox.Text = session.Labels.Title ?? string.Empty;
        XLabelTextBox.Text = session.Labels.XLabel ?? string.Empty;
        YLabelTextBox.Text = session.Labels.YLabel ?? string.Empty;

        AxisRangePanel.SetXValues(session.Axes.XMin, session.Axes.XMax);
        AxisRangePanel.SetYValues(session.Axes.YMin, session.Axes.YMax);

        SetGraphActionsEnabled(true);
        // PlotCurrentDataset() で _datasetSelectedPeakIds が反映されるので、
        // ここで個別に SelectedPeak を再適用する必要はない。
        PlotCurrentDataset();
    }

    private void ApplyMolecularWeightYModeSelection(string yMode)
    {
        var targetTag = string.Equals(
            yMode,
            nameof(MolecularWeightYMode.DifferentialWeightFraction),
            StringComparison.OrdinalIgnoreCase) ? "DwdLogM" : "Signal";

        foreach (var item in MolecularWeightYModeComboBox.Items)
        {
            if (item is ComboBoxItem cbItem
                && cbItem.Tag is string tag
                && string.Equals(tag, targetTag, StringComparison.OrdinalIgnoreCase))
            {
                MolecularWeightYModeComboBox.SelectedItem = cbItem;
                return;
            }
        }
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
        ApplyDefaultOutputDirectoryToDialog(dialog);

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
            _chromatogramPlot.PreviewMouseUp += ChromatogramPlot_MouseInteractionFinished;
            _chromatogramPlot.MouseWheel += ChromatogramPlot_MouseInteractionFinished;
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_chromatogramPlot);
            UpdatePlotHostAspectRatio();
            InitializeEmptyPlot();

            if (_currentDataset is not null)
            {
                PlotCurrentDataset();
                SetGraphActionsEnabled(true);
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
            SetGraphActionsEnabled(false);
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
                SetGraphActionsEnabled(_chromatogramPlot is not null);
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

        SetGraphActionsEnabled(_chromatogramPlot is not null);
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

        _currentLegendAutoShow = ShouldShowLegend(entries.Select(entry => entry.Index));
        ApplyLegend(_chromatogramPlot.Plot, CaptureFormattingConfigFromControls(),
            autoShow: _currentLegendAutoShow);

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
                Stats: ApplyStoredSelectedPeak(e.Dataset.MolecularWeightStatistics, e.Index)))
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

        _currentLegendAutoShow = ShouldShowLegend(entries.Select(entry => entry.Index));
        ApplyLegend(_chromatogramPlot.Plot, CaptureFormattingConfigFromControls(),
            autoShow: _currentLegendAutoShow);

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
                Stats: ApplyStoredSelectedPeak(e.Dataset.Statistics, e.Index)))
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
        var config = CaptureFormattingConfigFromControls();
        ApplyAll(plot, config, scale);
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

        var xMin = AxisRangePanel.XMinValue;
        var xMax = AxisRangePanel.XMaxValue;
        var yMin = AxisRangePanel.YMinValue;
        var yMax = AxisRangePanel.YMaxValue;

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
        _currentStatistics = statistics;

        if (statistics is null || !statistics.HasAnyValue)
        {
            StatisticsTextBlock.Text = "Mn: -   Mw: -   Ð: -";
            UpdateRepresentativePeakSelector(null);
            return;
        }

        if (statistics.Peaks.Count > 0)
        {
            StatisticsTextBlock.Text = FormatRepresentativeStatistics(statistics);
            UpdateRepresentativePeakSelector(statistics);
            return;
        }

        var source = statistics.Source == MolecularWeightStatisticsSource.DataFile ? "file" : "calc";
        StatisticsTextBlock.Text = $"Mn: {FormatStatistic(statistics.Mn)}   Mw: {FormatStatistic(statistics.Mw)}   Ð: {FormatStatistic(statistics.Pdi)} ({source})";
        UpdateRepresentativePeakSelector(null);
    }

    private void UpdateStatisticsForDatasets(IReadOnlyList<(string Label, MolecularWeightStatistics? Stats)> entries)
    {
        if (entries.Count == 0)
        {
            _currentStatistics = null;
            StatisticsTextBlock.Text = "Mn: -   Mw: -   Ð: -";
            UpdateRepresentativePeakSelector(null);
            return;
        }

        if (entries.Count == 1)
        {
            UpdateStatisticsText(entries[0].Stats);
            return;
        }

        _currentStatistics = null;
        UpdateRepresentativePeakSelector(null);

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
            return "Mn -  Mw -  Ð -";
        }

        if (statistics.Peaks.Count > 0)
        {
            var representativePeakId = statistics.SelectedPeakId
                ?? MolecularWeightStatistics.SelectAutoRepresentativePeak(statistics.Peaks)?.PeakId;
            var label = representativePeakId is null ? "" : $"#{representativePeakId} ";
            return $"{label}Mn {FormatStatistic(statistics.Mn)}  Mw {FormatStatistic(statistics.Mw)}  Ð {FormatStatistic(statistics.Pdi)}";
        }

        var source = statistics.Source == MolecularWeightStatisticsSource.DataFile ? "file" : "calc";
        return $"Mn {FormatStatistic(statistics.Mn)}  Mw {FormatStatistic(statistics.Mw)}  Ð {FormatStatistic(statistics.Pdi)} ({source})";
    }

    private static string FormatRepresentativeStatistics(MolecularWeightStatistics statistics)
    {
        string label;
        if (statistics.IsAutoSelected)
        {
            var auto = MolecularWeightStatistics.SelectAutoRepresentativePeak(statistics.Peaks);
            label = auto is not null ? $"自動 (Peak #{auto.PeakId})" : "自動";
        }
        else
        {
            label = $"Peak #{statistics.SelectedPeakId}";
        }

        return $"{label}   Mn: {FormatStatistic(statistics.Mn)}   Mw: {FormatStatistic(statistics.Mw)}   Ð: {FormatStatistic(statistics.Pdi)}";
    }

    private void UpdateRepresentativePeakSelector(MolecularWeightStatistics? statistics)
    {
        _suppressRepresentativePeakSelection = true;
        try
        {
            RepresentativePeakComboBox.Items.Clear();

            if (statistics is null || statistics.Peaks.Count == 0)
            {
                RepresentativePeakPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var auto = MolecularWeightStatistics.SelectAutoRepresentativePeak(statistics.Peaks);
            var autoItem = new ComboBoxItem
            {
                Content = auto is not null ? $"自動 (Peak #{auto.PeakId})" : "自動",
                Tag = null,
            };
            RepresentativePeakComboBox.Items.Add(autoItem);

            var orderedForList = statistics.Peaks
                .OrderBy(peak => TryParsePeakNumber(peak.PeakId))
                .ThenBy(peak => peak.PeakId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var peak in orderedForList)
            {
                RepresentativePeakComboBox.Items.Add(new ComboBoxItem
                {
                    Content = FormatPeakComboBoxItem(peak),
                    Tag = peak.PeakId,
                });
            }

            var selectedIndex = 0;
            if (!statistics.IsAutoSelected)
            {
                for (var i = 1; i < RepresentativePeakComboBox.Items.Count; i++)
                {
                    if (RepresentativePeakComboBox.Items[i] is ComboBoxItem item
                        && string.Equals(item.Tag as string, statistics.SelectedPeakId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            RepresentativePeakComboBox.SelectedIndex = selectedIndex;
            RepresentativePeakPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _suppressRepresentativePeakSelection = false;
        }
    }

    private void RepresentativePeakComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRepresentativePeakSelection)
        {
            return;
        }

        if (_currentStatistics is null || _currentStatistics.Peaks.Count == 0)
        {
            return;
        }

        if (RepresentativePeakComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var peakId = item.Tag as string;
        if (_activeIndex >= 0 && _activeIndex < _datasetSelectedPeakIds.Count)
        {
            _datasetSelectedPeakIds[_activeIndex] = peakId;
        }
        var updated = _currentStatistics.WithSelectedPeak(peakId);
        _currentStatistics = updated;
        StatisticsTextBlock.Text = FormatRepresentativeStatistics(updated);
    }

    private MolecularWeightStatistics? ApplyStoredSelectedPeak(MolecularWeightStatistics? stats, int datasetIndex)
    {
        if (stats is null || stats.Peaks.Count == 0)
        {
            return stats;
        }

        if (datasetIndex < 0 || datasetIndex >= _datasetSelectedPeakIds.Count)
        {
            return stats;
        }

        var storedPeakId = _datasetSelectedPeakIds[datasetIndex];
        if (storedPeakId is null)
        {
            return stats;
        }

        return stats.WithSelectedPeak(storedPeakId);
    }

    private static string FormatPeakComboBoxItem(MolecularWeightPeak peak)
    {
        var pieces = new List<string> { $"Peak #{peak.PeakId}" };
        if (peak.Mw.HasValue && double.IsFinite(peak.Mw.Value))
        {
            pieces.Add($"Mw {FormatStatistic(peak.Mw)}");
        }
        if (peak.Percent.HasValue && double.IsFinite(peak.Percent.Value))
        {
            pieces.Add($"{FormatStatistic(peak.Percent)}%");
        }
        return string.Join("   ", pieces);
    }

    private static int TryParsePeakNumber(string peakId)
    {
        return int.TryParse(peakId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.MaxValue;
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

        // 対数軸は X = log10(MW) として表示しているので、主目盛は整数指数 (10^k)、
        // 補助目盛は各ディケード内の log10(2·10^k)..log10(9·10^k) の8点を入れる。
        // SetTicks は major のみで minor を持てないので、NumericManual を直接組み立てる。
        var generator = new ScottPlot.TickGenerators.NumericManual();
        for (var exponent = minExponent; exponent <= maxExponent; exponent++)
        {
            generator.AddMajor(exponent, $"10{ToSuperscript(exponent)}");

            if (exponent < maxExponent)
            {
                for (var multiplier = 2; multiplier <= 9; multiplier++)
                {
                    generator.AddMinor(exponent + Math.Log10(multiplier));
                }
            }
        }

        _chromatogramPlot.Plot.Axes.Bottom.TickGenerator = generator;
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
                LineColorPicker.DefaultHex = _formattingDefaults.DefaultLineColorHex ?? AutoLineColors[0];
                LineColorPicker.SetHexValue(_formattingDefaults.DefaultLineColorHex);
                LegendNameTextBox.Clear();
                LineWidthTextBox.Text = _formattingDefaults.FormatLineWidth();
                MarkerSizeTextBox.Text = _formattingDefaults.FormatMarkerSize();
                ActiveDatasetLabel.Text = "(データ未選択)";
                return;
            }

            var style = _datasetStyles[_activeIndex];

            // The picker's preview falls back to DefaultHex when ColorHex is null
            // (Auto). Make that fallback match the auto-palette colour used at
            // draw time for this dataset index, so the preview swatch matches
            // the line on the plot.
            LineColorPicker.DefaultHex = AutoLineColors[_activeIndex % AutoLineColors.Length];
            LineColorPicker.SetHexValue(style.ColorHex);
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

    private void DatasetListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is not null)
        {
            // Click landed on the row's delete button - leave it to the button.
            _datasetDragStartPoint = null;
            return;
        }

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null)
        {
            _datasetDragStartPoint = null;
            return;
        }

        _datasetDragStartPoint = e.GetPosition(DatasetListBox);
    }

    private void DatasetListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _datasetDragStartPoint is null)
        {
            return;
        }

        var current = e.GetPosition(DatasetListBox);
        var delta = current - _datasetDragStartPoint.Value;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        var sourceIndex = DatasetListBox.ItemContainerGenerator.IndexFromContainer(item);
        if (sourceIndex < 0 || sourceIndex >= _datasetEntries.Count)
        {
            return;
        }

        try
        {
            var data = new DataObject(DatasetReorderDataFormat, sourceIndex);
            DragDrop.DoDragDrop(item, data, DragDropEffects.Move);
        }
        finally
        {
            _datasetDragStartPoint = null;
            RemoveInsertionAdorner();
        }
    }

    private void DatasetListBox_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DatasetReorderDataFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var (targetItem, insertAbove) = ResolveDropTarget(e);
        if (targetItem is null)
        {
            RemoveInsertionAdorner();
            return;
        }

        UpdateInsertionAdorner(targetItem, insertAbove);
    }

    private void DatasetListBox_DragLeave(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(DatasetListBox);
        if (pos.X < 0 || pos.Y < 0
            || pos.X > DatasetListBox.ActualWidth
            || pos.Y > DatasetListBox.ActualHeight)
        {
            RemoveInsertionAdorner();
        }
    }

    private void DatasetListBox_Drop(object sender, DragEventArgs e)
    {
        RemoveInsertionAdorner();

        if (e.Data.GetData(DatasetReorderDataFormat) is not int oldIndex)
        {
            return;
        }

        if (oldIndex < 0 || oldIndex >= _datasetEntries.Count)
        {
            return;
        }

        var (targetItem, insertAbove) = ResolveDropTarget(e);
        int newIndex;
        if (targetItem is null)
        {
            newIndex = _datasetEntries.Count - 1;
        }
        else
        {
            var targetIndex = DatasetListBox.ItemContainerGenerator.IndexFromContainer(targetItem);
            if (targetIndex < 0)
            {
                return;
            }

            newIndex = insertAbove ? targetIndex : targetIndex + 1;
            if (newIndex > oldIndex)
            {
                newIndex--;
            }
        }

        if (newIndex < 0)
        {
            newIndex = 0;
        }
        else if (newIndex >= _datasetEntries.Count)
        {
            newIndex = _datasetEntries.Count - 1;
        }

        if (newIndex == oldIndex)
        {
            return;
        }

        MoveDataset(oldIndex, newIndex);
    }

    private (ListBoxItem? Item, bool InsertAbove) ResolveDropTarget(DragEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return (null, false);
        }

        var pos = e.GetPosition(item);
        var insertAbove = pos.Y < item.ActualHeight / 2;
        return (item, insertAbove);
    }

    private void UpdateInsertionAdorner(ListBoxItem item, bool insertAbove)
    {
        if (_datasetInsertionAdorner is not null
            && ReferenceEquals(_datasetInsertionAdorner.AdornedElement, item)
            && _datasetInsertionAdorner.IsAbove == insertAbove)
        {
            return;
        }

        RemoveInsertionAdorner();

        var layer = AdornerLayer.GetAdornerLayer(item);
        if (layer is null)
        {
            return;
        }

        _datasetInsertionAdorner = new InsertionAdorner(item, insertAbove);
        layer.Add(_datasetInsertionAdorner);
    }

    private void RemoveInsertionAdorner()
    {
        if (_datasetInsertionAdorner is null)
        {
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(_datasetInsertionAdorner.AdornedElement);
        layer?.Remove(_datasetInsertionAdorner);
        _datasetInsertionAdorner = null;
    }

    private void MoveDataset(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex
            || oldIndex < 0 || oldIndex >= _loadedDatasets.Count
            || newIndex < 0 || newIndex >= _loadedDatasets.Count)
        {
            return;
        }

        // Bake the currently-resolved auto colors into each style so the visual
        // mapping between data and color survives the reorder. ApplySeriesStyle
        // resolves null ColorHex via AutoLineColors[index % N], which would
        // otherwise shift colors when the indices change.
        for (var i = 0; i < _datasetStyles.Count; i++)
        {
            if (string.IsNullOrEmpty(_datasetStyles[i].ColorHex))
            {
                _datasetStyles[i].ColorHex = AutoLineColors[i % AutoLineColors.Length];
            }
        }

        var dataset = _loadedDatasets[oldIndex];
        _loadedDatasets.RemoveAt(oldIndex);
        _loadedDatasets.Insert(newIndex, dataset);

        var style = _datasetStyles[oldIndex];
        _datasetStyles.RemoveAt(oldIndex);
        _datasetStyles.Insert(newIndex, style);

        if (_datasetSelectedPeakIds.Count > Math.Max(oldIndex, newIndex))
        {
            var peakId = _datasetSelectedPeakIds[oldIndex];
            _datasetSelectedPeakIds.RemoveAt(oldIndex);
            _datasetSelectedPeakIds.Insert(newIndex, peakId);
        }

        _suppressDatasetListEvents = true;
        try
        {
            _datasetEntries.Move(oldIndex, newIndex);
        }
        finally
        {
            _suppressDatasetListEvents = false;
        }

        if (_activeIndex == oldIndex)
        {
            _activeIndex = newIndex;
        }
        else if (oldIndex < _activeIndex && _activeIndex <= newIndex)
        {
            _activeIndex--;
        }
        else if (newIndex <= _activeIndex && _activeIndex < oldIndex)
        {
            _activeIndex++;
        }

        _suppressDatasetListEvents = true;
        try
        {
            DatasetListBox.SelectedIndex = _activeIndex;
        }
        finally
        {
            _suppressDatasetListEvents = false;
        }

        if (_activeIndex >= 0 && _activeIndex < _loadedDatasets.Count)
        {
            _currentDataset = _loadedDatasets[_activeIndex];
            FilePathTextBlock.Text = _currentDataset?.SourceFilePath ?? string.Empty;
        }

        SyncStyleControlsFromActiveDataset();
        PlotCurrentDataset();
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
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
        _datasetSelectedPeakIds.RemoveAt(index);
        ClearComputedDataCaches();

        if (_loadedDatasets.Count == 0)
        {
            _activeIndex = -1;
            _currentDataset = null;
            RefreshDatasetEntries();
            SyncStyleControlsFromActiveDataset();
            FilePathTextBlock.Text = string.Empty;
            SetGraphActionsEnabled(false);
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

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count) return;

        // Per-dataset line colour: null = "use auto palette",
        // "#RRGGBB" = explicit override. ColorPickerPanel handles the
        // preset / hex / preview triplet, so we only mirror its output
        // into the active dataset's style record.
        var style = _datasetStyles[_activeIndex];
        style.ColorHex = LineColorPicker.HexValue;

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

        ApplyGraphAppearanceAndRefresh();
    }

    private void GraphFormatColor_ColorChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        ApplyGraphAppearanceAndRefresh();
    }

    private void GraphFontComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        // IsEditable=True の ComboBox は SelectionChanged だけだとリストにないフォント名を打ち込んだとき
        // 反映されない。テンプレート内の編集用 TextBox を取り出して TextChanged を購読し、
        // 自由入力でも再描画が走るようにする。
        if (GraphFontComboBox.Template?.FindName("PART_EditableTextBox", GraphFontComboBox) is TextBox editableTextBox)
        {
            editableTextBox.TextChanged -= GraphFontComboBox_EditableTextChanged;
            editableTextBox.TextChanged += GraphFontComboBox_EditableTextChanged;
        }
    }

    private void GraphFontComboBox_EditableTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        // SelectedItem が同じままで Text だけが変わるケースをここで拾う。
        SchedulePlotCurrentDataset();
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

    private void AxisRangePanel_Committed(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        PlotCurrentDataset();
    }

    private void ChromatogramPlot_MouseInteractionFinished(object sender, System.Windows.Input.MouseEventArgs e)
    {
        SyncAxisInputsFromPlot();
    }

    private void SyncAxisInputsFromPlot()
    {
        if (_chromatogramPlot is null || _currentDataset is null)
        {
            return;
        }

        var limits = _chromatogramPlot.Plot.Axes.GetLimits();
        if (!IsFiniteRange(limits.Left, limits.Right) || !IsFiniteRange(limits.Bottom, limits.Top))
        {
            return;
        }

        var xIsMolecularWeight = MolecularWeightCheckBox.IsChecked == true && _selectedCalibrationCurve is not null;
        var xMin = xIsMolecularWeight ? Math.Pow(10, limits.Left) : limits.Left;
        var xMax = xIsMolecularWeight ? Math.Pow(10, limits.Right) : limits.Right;

        AxisRangePanel.SetXValues(xMin, xMax);
        AxisRangePanel.SetYValues(limits.Bottom, limits.Top);
    }

    private static bool IsFiniteRange(double min, double max)
    {
        return double.IsFinite(min) && double.IsFinite(max) && min < max;
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
        // Re-evaluate legend visibility / position so changes to the legend
        // combo boxes (or any other format control) reflect immediately
        // instead of waiting for the next dataset replot. _currentLegendAutoShow
        // is set by the most recent Plot* pass and stays in sync with the
        // currently rendered series set.
        ApplyLegend(_chromatogramPlot.Plot, CaptureFormattingConfigFromControls(),
            autoShow: _currentLegendAutoShow);
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

    private static string? GetSelectedComboBoxTag(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
            ? tag
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

}

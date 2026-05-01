using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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
using SpectrumAnalyzer.Core;
using Microsoft.Win32;
using ScottPlot.WPF;

namespace Spectrum_Visualization;

public partial class MainWindow : Window
{
    private readonly ISpectrumDataReader _reader = new JascoSpectrumReader();

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

    // ScottPlot 5.x's TickMarkStyle defaults; multiply by display scale for export.
    private const float MajorTickLengthBase = 4f;
    private const float MajorTickWidthBase = 1f;
    private const float MinorTickLengthBase = 2f;
    private const float MinorTickWidthBase = 1f;

    private static readonly TimeSpan PlotRefreshDebounceInterval = TimeSpan.FromMilliseconds(200);

    private static readonly JsonSerializerOptions FormattingConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly string FormattingConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Spectrum_Visualization",
        "formatting_config.json");

    private readonly List<SpectrumDataset> _loadedDatasets = new();
    private readonly List<DatasetStyle> _datasetStyles = new();
    private readonly ObservableCollection<DatasetEntryVm> _datasetEntries = new();
    private readonly ObservableCollection<PeakAssignmentVm> _peakAssignmentVms = new();
    private readonly ObservableCollection<IntegrationRegionVm> _integrationRegionVms = new();
    private readonly ObservableCollection<IntegrationResultRowVm> _integrationResultRowVms = new();
    private readonly DispatcherTimer _plotRefreshDebounceTimer = new() { Interval = PlotRefreshDebounceInterval };
    private readonly AnalysisSessionStore _sessionStore = new();

    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private int _activeIndex = -1;
    private SpectrumDataset? _currentDataset;
    private WpfPlot? _spectrumPlot;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressDatasetListEvents;

    private const string DatasetReorderDataFormat = "Spectrum.DatasetEntryIndex";
    private Point? _datasetDragStartPoint;
    private InsertionAdorner? _datasetInsertionAdorner;

    public MainWindow()
    {
        // Suppress event handlers that fire during XAML parse (ComboBox.SelectionChanged
        // can trigger before all named controls have been created, leading to
        // NullReferenceException when the handler dereferences a sibling control).
        _suppressGraphAppearanceEvents = true;
        _suppressStyleControlEvents = true;
        _suppressDatasetListEvents = true;

        InitializeComponent();

        _suppressGraphAppearanceEvents = false;
        _suppressStyleControlEvents = false;
        _suppressDatasetListEvents = false;

        InitializePeakAssignmentVms();
        LoadFormattingDefaults();
        ApplyFormattingConfigToControls(_formattingDefaults);
        DatasetListBox.ItemsSource = _datasetEntries;
        PeakAssignmentItemsControl.ItemsSource = _peakAssignmentVms;
        IntegrationRegionItemsControl.ItemsSource = _integrationRegionVms;
        IntegrationResultItemsControl.ItemsSource = _integrationResultRowVms;
        _plotRefreshDebounceTimer.Tick += PlotRefreshDebounceTimer_Tick;
        RegisterShortcuts();
        Loaded += MainWindow_Loaded;
    }

    private void RegisterShortcuts()
    {
        AddShortcut(System.Windows.Input.Key.O, System.Windows.Input.ModifierKeys.Control,
            () => OpenSpectrumButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Control,
            () => SaveGraphButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.E, System.Windows.Input.ModifierKeys.Control,
            () => ExportDataButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.R, System.Windows.Input.ModifierKeys.Control,
            () => AutoAxisRangeButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.O, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
            () => LoadSessionButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
            () => SaveSessionButton_Click(this, new RoutedEventArgs()));
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
        Dispatcher.BeginInvoke(InitializePlotControl, DispatcherPriority.ApplicationIdle);
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

    private enum AnalysisExportFormat
    {
        Csv,
        Xlsx,
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

    public sealed class PeakAssignmentVm : INotifyPropertyChanged
    {
        public required PeakAssignment Source { get; init; }
        public required string Label { get; init; }
        public required SolidColorBrush ColorBrush { get; init; }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void InitializePeakAssignmentVms()
    {
        _peakAssignmentVms.Clear();
        foreach (var assignment in IrPeakAssignmentTable.Default)
        {
            _peakAssignmentVms.Add(new PeakAssignmentVm
            {
                Source = assignment,
                Label = assignment.Label,
                ColorBrush = new SolidColorBrush(HexToMediaColor(assignment.ColorHex)),
            });
        }
    }

    public sealed class IntegrationRegionVm : INotifyPropertyChanged
    {
        private string _label = string.Empty;
        private string _xMinText = string.Empty;
        private string _xMaxText = string.Empty;
        private BaselineMethod _baseline = BaselineMethod.Linear;

        public string Label
        {
            get => _label;
            set
            {
                if (_label == value) return;
                _label = value;
                OnPropertyChanged();
            }
        }

        public string XMinText
        {
            get => _xMinText;
            set
            {
                if (_xMinText == value) return;
                _xMinText = value;
                OnPropertyChanged();
            }
        }

        public string XMaxText
        {
            get => _xMaxText;
            set
            {
                if (_xMaxText == value) return;
                _xMaxText = value;
                OnPropertyChanged();
            }
        }

        public BaselineMethod Baseline
        {
            get => _baseline;
            set
            {
                if (_baseline == value) return;
                _baseline = value;
                OnPropertyChanged();
            }
        }

        public IntegrationRegion? ToModel()
        {
            if (string.IsNullOrWhiteSpace(_label))
            {
                return null;
            }

            if (!TryParseDouble(_xMinText, out var xMin) || !TryParseDouble(_xMaxText, out var xMax))
            {
                return null;
            }

            var region = new IntegrationRegion
            {
                Label = _label.Trim(),
                XMin = xMin,
                XMax = xMax,
                BaselineMethod = _baseline,
            };
            return region.IsValid ? region : null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class IntegrationResultRowVm
    {
        public string DatasetName { get; init; } = string.Empty;
        public string RegionLabel { get; init; } = string.Empty;
        public string AreaText { get; init; } = string.Empty;
        public string Tooltip { get; init; } = string.Empty;

        public static IntegrationResultRowVm From(string datasetName, SpectrumDataset dataset, IntegrationResult result)
        {
            var areaText = result.HasResult
                ? result.Area.ToString("G6", CultureInfo.InvariantCulture)
                : "—";

            string tooltip;
            if (result.HasResult)
            {
                var unit = string.IsNullOrWhiteSpace(dataset.RawYUnits) ? "?" : dataset.RawYUnits;
                tooltip =
                    $"Area = {result.Area.ToString("G6", CultureInfo.InvariantCulture)}\n"
                    + $"Raw = {result.RawArea.ToString("G6", CultureInfo.InvariantCulture)}\n"
                    + $"Baseline = {result.BaselineArea.ToString("G6", CultureInfo.InvariantCulture)}\n"
                    + $"N = {result.PointCount}\n"
                    + $"YUNITS = {unit}";
            }
            else
            {
                tooltip = "領域が dataset の X 範囲外、または有効な点が不足しています";
            }

            return new IntegrationResultRowVm
            {
                DatasetName = datasetName,
                RegionLabel = result.Region.Label,
                AreaText = areaText,
                Tooltip = tooltip,
            };
        }
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
            PlotFrameColorHex = GetPlotFrameColorHex(),
            BackgroundColorHex = GetBackgroundColorHex(),
            ShowTitle = TitleVisibleCheckBox.IsChecked == true,
            TitleBold = TitleBoldCheckBox.IsChecked == true,
            AxisLabelBold = AxisLabelBoldCheckBox.IsChecked == true,
            AspectRatio = GetSelectedAspectRatioConfigValue(),
            InvertXAxisMode = GetSelectedInvertXAxisModeConfigValue(),
            YAxisDisplayMode = GetSelectedYAxisDisplayModeConfigValue(),
            EnabledIrPeakAssignmentLabels = _peakAssignmentVms
                .Where(vm => vm.IsEnabled)
                .Select(vm => vm.Label)
                .ToList(),
            IntegrationRegions = _integrationRegionVms
                .Select(vm => vm.ToModel())
                .Where(region => region is not null)
                .Cast<IntegrationRegion>()
                .ToList(),
            DefaultLineColorHex = GetSelectedLineColorConfigValue(),
            LineWidth = TryParsePositiveDouble(LineWidthTextBox.Text, out var lineWidth)
                ? lineWidth
                : GraphFormattingConfig.DefaultLineWidth,
            MarkerSize = TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var markerSize)
                ? markerSize
                : GraphFormattingConfig.DefaultMarkerSize,
            DefaultOutputDirectory = DefaultOutputDirectoryTextBox.Text,
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
            SetPlotFrameColorInput(config.PlotFrameColorHex);
            SetBackgroundColorInput(config.BackgroundColorHex);
            TitleVisibleCheckBox.IsChecked = config.ShowTitle;
            TitleBoldCheckBox.IsChecked = config.TitleBold;
            AxisLabelBoldCheckBox.IsChecked = config.AxisLabelBold;

            if (!SelectComboBoxItemByTag(AspectRatioComboBox, config.AspectRatio ?? "Auto"))
            {
                AspectRatioComboBox.SelectedIndex = 0;
            }

            if (!SelectComboBoxItemByTag(InvertXAxisComboBox, config.InvertXAxisMode ?? "Auto"))
            {
                InvertXAxisComboBox.SelectedIndex = 0;
            }

            if (!SelectComboBoxItemByTag(YAxisDisplayComboBox, config.YAxisDisplayMode ?? "Native"))
            {
                YAxisDisplayComboBox.SelectedIndex = 0;
            }

            ApplyEnabledPeakAssignments(config.EnabledIrPeakAssignmentLabels);
            ApplyIntegrationRegions(config.IntegrationRegions);
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

        DefaultOutputDirectoryTextBox.Text = config.DefaultOutputDirectory ?? string.Empty;
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

    private async void OpenSpectrumButton_Click(object sender, RoutedEventArgs e)
    {
        var allowMultiple = OverlayCheckBox.IsChecked == true;
        var dialog = new OpenFileDialog
        {
            Title = allowMultiple
                ? "JASCO スペクトルを開く（複数選択可）"
                : "JASCO スペクトルを開く",
            Filter = "JASCO スペクトル (*.txt;*.csv)|*.txt;*.csv|JASCO TXT (*.txt)|*.txt|JASCO CSV (*.csv)|*.csv|All files (*.*)|*.*",
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
            OpenSpectrumButton.IsEnabled = false;
            SetStatus("スペクトルデータを読み込み中です...", false);

            var datasets = await Task.Run(() => fileNames
                .Select(fileName => _reader.Read(fileName))
                .ToArray());
            foreach (var dataset in datasets)
            {
                AddLoadedDataset(dataset);
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
            _activeIndex = -1;
            RefreshDatasetEntries();
            SetGraphActionsEnabled(false);
            SetStatus($"読み込みに失敗しました: {ex.Message}", true);
        }
        finally
        {
            OpenSpectrumButton.IsEnabled = true;
        }
    }

    private void AddLoadedDataset(SpectrumDataset dataset)
    {
        var overlay = OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0;
        if (!overlay)
        {
            _loadedDatasets.Clear();
            _datasetStyles.Clear();
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

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset?.SourceFilePath) ?? "spectrum_analysis";
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
        var entries = new List<AnalysisExportEntry>();
        var plotEntries = GetDatasetsToPlotWithIndices();
        var yDisplayMode = GetSelectedYAxisDisplayMode();

        foreach (var (dataset, index) in plotEntries)
        {
            var displayName = GetCustomLegendName(index)
                ?? Path.GetFileNameWithoutExtension(dataset.SourceFilePath)
                ?? $"dataset {index + 1}";

            entries.Add(new AnalysisExportEntry
            {
                DisplayName = displayName,
                SourceFilePath = dataset.SourceFilePath,
                XLabel = GetGraphLabel(XLabelTextBox, dataset.XLabel),
                YLabel = GetGraphLabel(YLabelTextBox, SpectrumYAxisConverter.GetDisplayYLabel(dataset, yDisplayMode)),
                Points = SpectrumYAxisConverter.GetDisplayPoints(dataset, yDisplayMode),
            });
        }

        return new AnalysisExport
        {
            Entries = entries,
        };
    }

    private static AnalysisExportFormat GetAnalysisExportFormat(string filePath, int filterIndex)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisExportFormat.Csv;
        }

        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisExportFormat.Xlsx;
        }

        return filterIndex == 2
            ? AnalysisExportFormat.Csv
            : AnalysisExportFormat.Xlsx;
    }

    private static string EnsureAnalysisExportExtension(string filePath, AnalysisExportFormat format)
    {
        var extension = format == AnalysisExportFormat.Csv ? ".csv" : ".xlsx";
        return Path.ChangeExtension(filePath, extension);
    }

    private void SaveSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedDatasets.Count == 0)
        {
            SetStatus("保存できる解析がありません。", true);
            return;
        }

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset?.SourceFilePath) ?? "spectrum_session";
        var dialog = new SaveFileDialog
        {
            Title = "解析条件を保存",
            Filter = "Spectrum セッション (*.specjson)|*.specjson|JSON (*.json)|*.json",
            FileName = $"{defaultName}.specjson",
            DefaultExt = ".specjson",
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
            _sessionStore.Save(session, dialog.FileName);
            SetStatus($"解析条件を保存しました: {dialog.FileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"保存に失敗しました: {ex.Message}", true);
        }
    }

    private void LoadSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "解析条件を読込",
            Filter = "Spectrum セッション (*.specjson;*.json)|*.specjson;*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var session = _sessionStore.Load(dialog.FileName);
            var warnings = new List<string>();
            ApplyAnalysisSession(session, warnings);

            if (warnings.Count == 0)
            {
                SetStatus($"解析条件を読み込みました: {dialog.FileName}", false);
            }
            else
            {
                SetStatus($"解析条件を読み込みましたが、一部に注意があります: {string.Join(" / ", warnings)}", true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FileNotFoundException)
        {
            SetStatus($"読込に失敗しました: {ex.Message}", true);
        }
    }

    private AnalysisSession BuildAnalysisSession()
    {
        var session = new AnalysisSession
        {
            Overlay = OverlayCheckBox.IsChecked == true,
            ActiveDatasetIndex = _activeIndex,
            Formatting = CaptureFormattingConfigFromControls(),
            Labels = new AnalysisSessionLabels
            {
                Title = TitleTextBox.Text,
                XLabel = XLabelTextBox.Text,
                YLabel = YLabelTextBox.Text,
            },
            Axes = new AnalysisSessionAxes
            {
                XMin = TryParseOptionalDouble(XMinTextBox.Text),
                XMax = TryParseOptionalDouble(XMaxTextBox.Text),
                YMin = TryParseOptionalDouble(YMinTextBox.Text),
                YMax = TryParseOptionalDouble(YMaxTextBox.Text),
            },
        };

        for (var i = 0; i < _loadedDatasets.Count; i++)
        {
            var dataset = _loadedDatasets[i];
            var style = i < _datasetStyles.Count ? _datasetStyles[i] : CreateDefaultDatasetStyle();
            session.Datasets.Add(new AnalysisSessionDataset
            {
                SourceFilePath = dataset.SourceFilePath ?? string.Empty,
                Style = new AnalysisSessionStyle
                {
                    ColorHex = style.ColorHex,
                    LegendName = style.LegendName,
                    LineWidth = style.LineWidth,
                    MarkerSize = style.MarkerSize,
                },
            });
        }

        return session;
    }

    private void ApplyAnalysisSession(AnalysisSession session, List<string> warnings)
    {
        var loaded = new List<SpectrumDataset>();
        var styles = new List<DatasetStyle>();

        foreach (var entry in session.Datasets)
        {
            if (string.IsNullOrWhiteSpace(entry.SourceFilePath))
            {
                continue;
            }

            try
            {
                var dataset = _reader.Read(entry.SourceFilePath);
                loaded.Add(dataset);
                styles.Add(new DatasetStyle
                {
                    ColorHex = entry.Style.ColorHex,
                    LegendName = entry.Style.LegendName,
                    LineWidth = entry.Style.LineWidth,
                    MarkerSize = entry.Style.MarkerSize,
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FileNotFoundException)
            {
                warnings.Add($"{Path.GetFileName(entry.SourceFilePath)} を再読み込みできませんでした: {ex.Message}");
            }
        }

        _loadedDatasets.Clear();
        _datasetStyles.Clear();
        _loadedDatasets.AddRange(loaded);
        _datasetStyles.AddRange(styles);

        if (_loadedDatasets.Count == 0)
        {
            _activeIndex = -1;
            _currentDataset = null;
            RefreshDatasetEntries();
            SetGraphActionsEnabled(false);
            return;
        }

        _activeIndex = Math.Clamp(session.ActiveDatasetIndex, 0, _loadedDatasets.Count - 1);
        _currentDataset = _loadedDatasets[_activeIndex];

        OverlayCheckBox.IsChecked = session.Overlay;

        if (session.Formatting is not null)
        {
            ApplyFormattingConfigToControls(session.Formatting);
        }

        var labels = session.Labels;
        TitleTextBox.Text = labels.Title ?? string.Empty;
        XLabelTextBox.Text = labels.XLabel ?? string.Empty;
        YLabelTextBox.Text = labels.YLabel ?? string.Empty;

        var axes = session.Axes;
        XMinTextBox.Text = FormatOptional(axes.XMin);
        XMaxTextBox.Text = FormatOptional(axes.XMax);
        YMinTextBox.Text = FormatOptional(axes.YMin);
        YMaxTextBox.Text = FormatOptional(axes.YMax);

        FilePathTextBlock.Text = _loadedDatasets.Count > 1
            ? $"{_loadedDatasets.Count} files (latest: {_currentDataset.SourceFilePath})"
            : _currentDataset.SourceFilePath ?? string.Empty;

        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();
        UpdatePlotHostAspectRatio();
        PlotCurrentDataset();
    }

    private static double? TryParseOptionalDouble(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var parsed)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string FormatOptional(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("G", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private void SaveGraphButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDataset is null || _spectrumPlot is null)
        {
            SetStatus("保存するグラフがありません。", true);
            return;
        }

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset.SourceFilePath) ?? "spectrum";
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

            ApplyExportStyleScale(exportStyleScale);
            try
            {
                if (saveFormat == GraphSaveFormat.Svg)
                {
                    SaveGraphSvg(fileName, width, height);
                    SetStatus($"グラフをSVGで保存しました: {fileName} ({width:N0} x {height:N0})", false);
                    return;
                }

                _spectrumPlot.Plot.SavePng(fileName, width, height);
                ApplyPngDpiMetadata(fileName, ExportDpi);
                SetStatus($"グラフをPNGで保存しました: {fileName} ({width:N0} x {height:N0} px, {ExportDpi} dpi)", false);
            }
            finally
            {
                ApplyExportStyleScale(1f);
                _spectrumPlot.Refresh();
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
            _spectrumPlot = new WpfPlot();
            _spectrumPlot.PreviewMouseUp += SpectrumPlot_MouseInteractionFinished;
            _spectrumPlot.MouseWheel += SpectrumPlot_MouseInteractionFinished;
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_spectrumPlot);
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
        if (_spectrumPlot is null)
        {
            return;
        }

        _spectrumPlot.Plot.Title("Spectrum");
        _spectrumPlot.Plot.XLabel("X");
        _spectrumPlot.Plot.YLabel("Y");
        _spectrumPlot.Plot.Axes.NumericTicksBottom();
        ApplyPlotAppearance();
        _spectrumPlot.Refresh();
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

        if (_currentDataset is null || _spectrumPlot is null)
        {
            UpdatePeakAssignmentUi(null);
            UpdateIntegrationResults();
            SetGraphActionsEnabled(false);
            return;
        }

        UpdatePeakAssignmentUi(_currentDataset);

        var plotEntries = GetDatasetsToPlotWithIndices();
        var activeDataset = _currentDataset;
        var yDisplayMode = GetSelectedYAxisDisplayMode();

        _spectrumPlot.Plot.Clear();
        _spectrumPlot.Plot.Axes.NumericTicksBottom();

        var xRange = new AxisDataRange();
        var yRange = new AxisDataRange();
        var inconvertibleCount = 0;
        for (var i = 0; i < plotEntries.Length; i++)
        {
            var (dataset, datasetIndex) = plotEntries[i];
            var yValues = SpectrumYAxisConverter.GetDisplayYValues(dataset, yDisplayMode);
            if (yDisplayMode != YAxisDisplayMode.Native
                && !SpectrumYAxisConverter.CanDisplay(dataset, yDisplayMode))
            {
                inconvertibleCount++;
            }

            xRange.Include(dataset.XValues);
            yRange.Include(yValues);

            var signal = _spectrumPlot.Plot.Add.Scatter(dataset.XValues, yValues);
            signal.LegendText = GetSeriesLegendText(dataset, $"dataset {datasetIndex + 1}", datasetIndex);
            ApplySeriesStyle(signal, datasetIndex);
        }

        if (ShouldShowLegend(plotEntries.Select(entry => entry.Index)))
        {
            _spectrumPlot.Plot.ShowLegend();
        }
        else
        {
            _spectrumPlot.Plot.HideLegend();
        }

        _spectrumPlot.Plot.Title(GetGraphTitle(Path.GetFileNameWithoutExtension(activeDataset.SourceFilePath) ?? "Spectrum"));
        _spectrumPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, activeDataset.XLabel));
        _spectrumPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, SpectrumYAxisConverter.GetDisplayYLabel(activeDataset, yDisplayMode)));
        _spectrumPlot.Plot.Axes.AutoScale();

        // IR convention: high wavenumbers on the left (4000 → 400 cm⁻¹).
        // The user can override this through the format panel (Auto / Inverted / Normal).
        var invertX = GetSelectedInvertXAxisModeConfigValue() switch
        {
            "Inverted" => true,
            "Normal" => false,
            _ => activeDataset.IsInfraredSpectrum,
        };
        if (invertX && xRange.HasValue)
        {
            _spectrumPlot.Plot.Axes.SetLimitsX(xRange.Max, xRange.Min);
        }
        else if (!invertX && xRange.HasValue && activeDataset.IsInfraredSpectrum)
        {
            // Force normal direction for IR data when the user explicitly opts out.
            _spectrumPlot.Plot.Axes.SetLimitsX(xRange.Min, xRange.Max);
        }

        if (!ApplyAxisLimits(xRange, yRange, invertX))
        {
            _spectrumPlot.Refresh();
            return;
        }

        DrawPeakAssignments(activeDataset, yRange);
        DrawIntegrationRegions(yRange);

        ApplyPlotAppearance();
        _spectrumPlot.Refresh();
        SetGraphActionsEnabled(true);

        UpdateIntegrationResults();

        if (inconvertibleCount > 0 && yDisplayMode != YAxisDisplayMode.Native)
        {
            SetStatus(
                $"{inconvertibleCount} 件のデータセットは Y 軸単位の変換ができないため、ネイティブ単位のまま表示しています。",
                false);
        }
    }

    private (SpectrumDataset Dataset, int Index)[] GetDatasetsToPlotWithIndices()
    {
        if (OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0)
        {
            var result = new (SpectrumDataset, int)[_loadedDatasets.Count];
            for (var i = 0; i < _loadedDatasets.Count; i++)
            {
                result[i] = (_loadedDatasets[i], i);
            }
            return result;
        }

        if (_activeIndex < 0 || _activeIndex >= _loadedDatasets.Count)
        {
            return Array.Empty<(SpectrumDataset, int)>();
        }

        return new[] { (_loadedDatasets[_activeIndex], _activeIndex) };
    }

    private void ApplyPlotAppearance(float scale = 1f)
    {
        if (_spectrumPlot is null)
        {
            return;
        }

        var plot = _spectrumPlot.Plot;
        ApplyPlotFont(plot);
        ApplyPlotFontSize(plot, scale);
        ApplyPlotGrid(plot);
        ApplyYAxisTickLabels(plot);
        ApplyPlotFrame(plot, scale);
        ApplyPlotTickMarks(plot, scale);
        ApplyPlotTitleStyle(plot);
        ApplyPlotAxisLabelStyle(plot);
        ApplyPlotBackground(plot);
    }

    private void ApplyPlotTickMarks(ScottPlot.Plot plot, float scale = 1f)
    {
        var showMajor = MajorTicksCheckBox.IsChecked == true;
        var showMinor = MinorTicksCheckBox.IsChecked == true;
        var yAxisVisible = YAxisTickLabelsCheckBox.IsChecked == true;

        ConfigureTickMarkStyle(plot.Axes.Bottom.MajorTickStyle, MajorTickLengthBase, MajorTickWidthBase, scale, showMajor);
        ConfigureTickMarkStyle(plot.Axes.Bottom.MinorTickStyle, MinorTickLengthBase, MinorTickWidthBase, scale, showMinor);
        ConfigureTickMarkStyle(plot.Axes.Left.MajorTickStyle, MajorTickLengthBase, MajorTickWidthBase, scale, showMajor && yAxisVisible);
        ConfigureTickMarkStyle(plot.Axes.Left.MinorTickStyle, MinorTickLengthBase, MinorTickWidthBase, scale, showMinor && yAxisVisible);
    }

    private static void ConfigureTickMarkStyle(ScottPlot.TickMarkStyle style, float lengthBase, float widthBase, float scale, bool visible)
    {
        style.Length = visible ? lengthBase * scale : 0f;
        style.Width = widthBase * scale;
        style.Hairline = false;
    }

    private void ApplyPlotTitleStyle(ScottPlot.Plot plot)
    {
        plot.Axes.Title.Label.IsVisible = TitleVisibleCheckBox.IsChecked == true;
        plot.Axes.Title.Label.Bold = TitleBoldCheckBox.IsChecked == true;
    }

    private void ApplyPlotAxisLabelStyle(ScottPlot.Plot plot)
    {
        var bold = AxisLabelBoldCheckBox.IsChecked == true;
        plot.Axes.Bottom.Label.Bold = bold;
        plot.Axes.Left.Label.Bold = bold;
    }

    private void ApplyPlotBackground(ScottPlot.Plot plot)
    {
        var color = GetScottPlotColor(GetBackgroundColorHex(), GraphFormattingConfig.DefaultBackgroundColorHex);
        plot.FigureBackground.Color = color;
        plot.DataBackground.Color = color;
    }

    private void ApplyPlotFont(ScottPlot.Plot plot)
    {
        var fontName = GetSelectedGraphFontName();
        if (fontName is null)
        {
            plot.Font.Automatic();
            ResetLabelFontTypeface(plot);
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

        ResetLabelFontTypeface(plot);
    }

    private static void ResetLabelFontTypeface(ScottPlot.Plot plot)
    {
        plot.Axes.Title.Label.Font = null;
        plot.Axes.Bottom.Label.Font = null;
        plot.Axes.Left.Label.Font = null;
        plot.Axes.Bottom.TickLabelStyle.Font = null;
        plot.Axes.Left.TickLabelStyle.Font = null;
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
        var yLabelsVisible = YAxisTickLabelsCheckBox.IsChecked == true;

        plot.Axes.Bottom.FrameLineStyle.IsVisible = true;
        plot.Axes.Left.FrameLineStyle.IsVisible = frameVisible || yLabelsVisible;
        plot.Axes.Top.FrameLineStyle.IsVisible = frameVisible;
        plot.Axes.Right.FrameLineStyle.IsVisible = frameVisible;

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
        if (_spectrumPlot is null)
        {
            return;
        }

        ApplyPlotAppearance(scale);
        ApplyExistingSeriesStyles(scale);
    }

    private void ApplyExistingSeriesStyles(float scale)
    {
        if (_spectrumPlot is null)
        {
            return;
        }

        var entries = GetDatasetsToPlotWithIndices();
        var scatters = _spectrumPlot.Plot
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

    private string GetSeriesLegendText(SpectrumDataset dataset, string fallback, int datasetIndex)
    {
        var customName = GetCustomLegendName(datasetIndex);
        if (customName is not null)
        {
            return customName;
        }

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }

    private bool ApplyAxisLimits(AxisDataRange xRange, AxisDataRange yRange, bool invertX = false)
    {
        if (_spectrumPlot is null)
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

        if (xMin.HasValue || xMax.HasValue)
        {
            if (!TryGetRequestedRange(xRange, xMin, xMax, "X", out var left, out var right, allowInverted: invertX))
            {
                return false;
            }

            if (invertX)
            {
                _spectrumPlot.Plot.Axes.SetLimitsX(right, left);
            }
            else
            {
                _spectrumPlot.Plot.Axes.SetLimitsX(left, right);
            }
        }

        if (yMin.HasValue || yMax.HasValue)
        {
            if (!TryGetRequestedRange(yRange, yMin, yMax, "Y", out var bottom, out var top))
            {
                return false;
            }

            _spectrumPlot.Plot.Axes.SetLimitsY(bottom, top);
        }

        return true;
    }

    private bool TryGetRequestedRange(
        AxisDataRange dataRange,
        double? requestedMin,
        double? requestedMax,
        string axisName,
        out double min,
        out double max,
        bool allowInverted = false)
    {
        min = requestedMin ?? (dataRange.HasValue ? dataRange.Min : double.NaN);
        max = requestedMax ?? (dataRange.HasValue ? dataRange.Max : double.NaN);

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            SetStatus($"{axisName} axis range could not be determined.", true);
            return false;
        }

        if (min == max)
        {
            SetStatus($"{axisName} Min と Max は異なる値である必要があります。", true);
            return false;
        }

        if (min > max)
        {
            if (!allowInverted)
            {
                SetStatus($"{axisName} Min must be smaller than {axisName} Max.", true);
                return false;
            }

            // IR: high wavenumbers belong on the left. Accept either input
            // order and normalize to (min, max) so downstream callers can
            // decide whether to invert via SetLimitsX argument order.
            (min, max) = (max, min);
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

    private void SetStatus(string message, bool isError)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))
            : new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
    }

    private void RefreshDatasetEntries()
    {
        _suppressDatasetListEvents = true;
        try
        {
            _datasetEntries.Clear();
            for (var i = 0; i < _loadedDatasets.Count; i++)
            {
                var dataset = _loadedDatasets[i];
                var style = i < _datasetStyles.Count ? _datasetStyles[i] : null;
                var hex = style?.ColorHex ?? AutoLineColors[i % AutoLineColors.Length];
                var displayName = !string.IsNullOrWhiteSpace(style?.LegendName)
                    ? style!.LegendName!.Trim()
                    : Path.GetFileNameWithoutExtension(dataset.SourceFilePath) ?? $"dataset {i + 1}";

                _datasetEntries.Add(new DatasetEntryVm
                {
                    DisplayName = displayName,
                    FullPath = dataset.SourceFilePath ?? string.Empty,
                    ColorBrush = new SolidColorBrush(HexToMediaColor(hex)),
                });
            }

            DatasetListPlaceholder.Visibility = _datasetEntries.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            DatasetListBox.SelectedIndex = _activeIndex >= 0 && _activeIndex < _datasetEntries.Count
                ? _activeIndex
                : -1;
        }
        finally
        {
            _suppressDatasetListEvents = false;
        }
    }

    private void SyncStyleControlsFromActiveDataset()
    {
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            ActiveDatasetLabel.Text = "(選択中データセット)";
            return;
        }

        var dataset = _loadedDatasets[_activeIndex];
        var style = _datasetStyles[_activeIndex];
        ActiveDatasetLabel.Text = $"({Path.GetFileNameWithoutExtension(dataset.SourceFilePath)})";

        _suppressStyleControlEvents = true;
        try
        {
            if (style.ColorHex is null)
            {
                SelectComboBoxItemByTag(LineColorComboBox, "Auto");
            }
            else if (!SelectComboBoxItemByTag(LineColorComboBox, style.ColorHex))
            {
                SelectComboBoxItemByTag(LineColorComboBox, "Custom");
            }

            SetLineColorInput(style.ColorHex);
            LegendNameTextBox.Text = style.LegendName ?? string.Empty;
            LineWidthTextBox.Text = style.LineWidth.ToString("0.##", CultureInfo.InvariantCulture);
            MarkerSizeTextBox.Text = style.MarkerSize.ToString("0.##", CultureInfo.InvariantCulture);
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

        var index = DatasetListBox.SelectedIndex;
        if (index < 0 || index >= _loadedDatasets.Count)
        {
            return;
        }

        _activeIndex = index;
        _currentDataset = _loadedDatasets[index];
        SyncStyleControlsFromActiveDataset();
        PlotCurrentDataset();
    }

    private void DatasetListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is not null)
        {
            // Click landed on the row's delete button — leave it to the button.
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
        if (sender is not Button { Tag: DatasetEntryVm vm })
        {
            return;
        }

        var index = _datasetEntries.IndexOf(vm);
        if (index < 0 || index >= _loadedDatasets.Count)
        {
            return;
        }

        _loadedDatasets.RemoveAt(index);
        if (index < _datasetStyles.Count)
        {
            _datasetStyles.RemoveAt(index);
        }

        if (_loadedDatasets.Count == 0)
        {
            _activeIndex = -1;
            _currentDataset = null;
            FilePathTextBlock.Text = string.Empty;
            RefreshDatasetEntries();
            SetGraphActionsEnabled(false);
            if (_spectrumPlot is not null)
            {
                _spectrumPlot.Plot.Clear();
                InitializeEmptyPlot();
            }
            return;
        }

        _activeIndex = Math.Clamp(_activeIndex >= index ? _activeIndex - 1 : _activeIndex, 0, _loadedDatasets.Count - 1);
        _currentDataset = _loadedDatasets[_activeIndex];
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

        var tag = GetSelectedComboBoxTag(LineColorComboBox);
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            ApplyDatasetStyle(style => style.ColorHex = null);
            SetLineColorInput(null);
            RefreshDatasetEntries();
            PlotCurrentDataset();
            return;
        }

        if (tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            UpdateLineColorPreview(LineColorHexTextBox.Text);
            return;
        }

        ApplyDatasetStyle(style => style.ColorHex = tag);
        SetLineColorInput(tag);
        RefreshDatasetEntries();
        PlotCurrentDataset();
    }

    private void LineColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }

        var text = LineColorHexTextBox.Text;
        if (IsAutoColorText(text))
        {
            ApplyDatasetStyle(style => style.ColorHex = null);
            UpdateLineColorPreview(null);
            RefreshDatasetEntries();
            PlotCurrentDataset();
            return;
        }

        if (TryNormalizeHexColorCode(text, out var hex))
        {
            ApplyDatasetStyle(style => style.ColorHex = hex);
            UpdateLineColorPreview(hex);
            RefreshDatasetEntries();
            PlotCurrentDataset();
        }
    }

    private void LegendNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }

        var name = LegendNameTextBox.Text.Trim();
        ApplyDatasetStyle(style => style.LegendName = string.IsNullOrWhiteSpace(name) ? null : name);
        RefreshDatasetEntries();
        SchedulePlotCurrentDataset();
    }

    private void LineWidthTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }

        if (TryParsePositiveDouble(LineWidthTextBox.Text, out var width))
        {
            ApplyDatasetStyle(style => style.LineWidth = width);
            SchedulePlotCurrentDataset();
        }
    }

    private void MarkerSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }

        if (TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var size))
        {
            ApplyDatasetStyle(style => style.MarkerSize = size);
            SchedulePlotCurrentDataset();
        }
    }

    private void ApplyDatasetStyle(Action<DatasetStyle> mutate)
    {
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            return;
        }

        mutate(_datasetStyles[_activeIndex]);
    }

    private void GraphAppearanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        if (sender is ComboBox comboBox)
        {
            if (ReferenceEquals(comboBox, PlotFrameColorComboBox))
            {
                SyncPlotFrameColorInputFromComboBox();
            }
            else if (ReferenceEquals(comboBox, BackgroundColorComboBox))
            {
                SyncBackgroundColorInputFromComboBox();
            }
        }

        ApplyGraphAppearanceAndRefresh();
    }

    private void BackgroundColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        if (TryNormalizeHexColorCode(BackgroundColorHexTextBox.Text, out var hex))
        {
            UpdateBackgroundColorPreview(hex);
            ApplyGraphAppearanceAndRefresh();
        }
    }

    private void GraphFontComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (GraphFontComboBox.Template?.FindName("PART_EditableTextBox", GraphFontComboBox) is TextBox editable)
        {
            editable.TextChanged -= GraphFontComboBox_EditableTextChanged;
            editable.TextChanged += GraphFontComboBox_EditableTextChanged;
        }
    }

    private void GraphFontComboBox_EditableTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        ApplyGraphAppearanceAndRefresh();
    }

    private void PlotFrameColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        if (TryNormalizeHexColorCode(PlotFrameColorHexTextBox.Text, out var hex))
        {
            UpdatePlotFrameColorPreview(hex);
            ApplyGraphAppearanceAndRefresh();
        }
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

        ApplyGraphAppearanceAndRefresh();
    }

    private void AxisRangeTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitAxisRangeFromInputs();
            e.Handled = true;
        }
    }

    private void AxisRangeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitAxisRangeFromInputs();
    }

    private void AutoAxisRangeButton_Click(object sender, RoutedEventArgs e)
    {
        XMinTextBox.Clear();
        XMaxTextBox.Clear();
        YMinTextBox.Clear();
        YMaxTextBox.Clear();

        if (_currentDataset is null || _spectrumPlot is null)
        {
            return;
        }

        _spectrumPlot.Plot.Axes.AutoScale();
        PlotCurrentDataset();
    }

    private void SpectrumPlot_MouseInteractionFinished(object sender, System.Windows.Input.MouseEventArgs e)
    {
        SyncAxisInputsFromPlot();
    }

    private void SyncAxisInputsFromPlot()
    {
        if (_spectrumPlot is null)
        {
            return;
        }

        var limits = _spectrumPlot.Plot.Axes.GetLimits();
        _suppressGraphAppearanceEvents = true;
        try
        {
            XMinTextBox.Text = FormatAxisValue(limits.Left);
            XMaxTextBox.Text = FormatAxisValue(limits.Right);
            YMinTextBox.Text = FormatAxisValue(limits.Bottom);
            YMaxTextBox.Text = FormatAxisValue(limits.Top);
        }
        finally
        {
            _suppressGraphAppearanceEvents = false;
        }
    }

    private static string FormatAxisValue(double value)
    {
        return double.IsFinite(value)
            ? value.ToString("G6", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private void CommitAxisRangeFromInputs()
    {
        if (_spectrumPlot is null || _currentDataset is null)
        {
            return;
        }

        PlotCurrentDataset();
    }

    private void AspectRatioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        UpdatePlotHostAspectRatio();
        SchedulePlotCurrentDataset();
    }

    private void InvertXAxisComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        SchedulePlotCurrentDataset();
    }

    private void YAxisDisplayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        SchedulePlotCurrentDataset();
    }

    private void PeakAssignmentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        SchedulePlotCurrentDataset();
    }

    private void PeakAssignmentEnableAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetAllPeakAssignmentsEnabled(true);
    }

    private void PeakAssignmentDisableAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetAllPeakAssignmentsEnabled(false);
    }

    private void SetAllPeakAssignmentsEnabled(bool enabled)
    {
        // Let each VM update flow through the TwoWay binding to its CheckBox,
        // which fires PeakAssignmentCheckBox_Changed -> SchedulePlotCurrentDataset.
        // The debounce timer collapses the burst of N change events into a
        // single PlotCurrentDataset run.
        foreach (var vm in _peakAssignmentVms)
        {
            vm.IsEnabled = enabled;
        }
    }

    private void AddIntegrationRegionButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = new IntegrationRegionVm
        {
            Label = $"region {_integrationRegionVms.Count + 1}",
            Baseline = BaselineMethod.Linear,
        };
        vm.PropertyChanged += IntegrationRegionVm_PropertyChanged;
        _integrationRegionVms.Add(vm);
        UpdateIntegrationResults();
        SchedulePlotCurrentDataset();
    }

    private void RemoveIntegrationRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is IntegrationRegionVm vm)
        {
            vm.PropertyChanged -= IntegrationRegionVm_PropertyChanged;
            _integrationRegionVms.Remove(vm);
            UpdateIntegrationResults();
            SchedulePlotCurrentDataset();
        }
    }

    private void ClearIntegrationRegionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_integrationRegionVms.Count == 0)
        {
            return;
        }

        foreach (var vm in _integrationRegionVms)
        {
            vm.PropertyChanged -= IntegrationRegionVm_PropertyChanged;
        }

        _integrationRegionVms.Clear();
        UpdateIntegrationResults();
        SchedulePlotCurrentDataset();
    }

    private void ExportIntegrationResultsButton_Click(object sender, RoutedEventArgs e)
    {
        var validRegions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .Where(region => region is not null)
            .Cast<IntegrationRegion>()
            .ToArray();

        if (validRegions.Length == 0)
        {
            SetStatus("出力できる積分結果がありません（領域を追加してください）", true);
            return;
        }

        var datasets = GetDatasetsToPlotWithIndices();
        if (datasets.Length == 0)
        {
            SetStatus("データセットが読み込まれていません", true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "積分結果を保存",
            Filter = "Excelブック (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            FileName = "integration_results",
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var rows = new List<IntegrationExportRow>();
        foreach (var (dataset, index) in datasets)
        {
            var datasetName = GetCustomLegendName(index)
                ?? Path.GetFileNameWithoutExtension(dataset.SourceFilePath)
                ?? $"dataset {index + 1}";

            foreach (var region in validRegions)
            {
                rows.Add(new IntegrationExportRow
                {
                    DatasetName = datasetName,
                    Region = region,
                    Result = SpectrumIntegrator.Integrate(dataset, region),
                    YUnits = dataset.RawYUnits ?? string.Empty,
                });
            }
        }

        var export = new IntegrationExport { Rows = rows };

        try
        {
            var extension = Path.GetExtension(dialog.FileName);
            if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                export.WriteCsv(dialog.FileName);
            }
            else
            {
                export.WriteXlsx(dialog.FileName);
            }

            SetStatus($"積分結果を保存しました: {dialog.FileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"保存に失敗しました: {ex.Message}", true);
        }
    }

    private void IntegrationRegionVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        UpdateIntegrationResults();
        SchedulePlotCurrentDataset();
    }

    private void UpdateIntegrationResults()
    {
        _integrationResultRowVms.Clear();

        var validRegions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .Where(region => region is not null)
            .Cast<IntegrationRegion>()
            .ToArray();

        if (validRegions.Length > 0)
        {
            var datasets = GetDatasetsToPlotWithIndices();
            foreach (var (dataset, index) in datasets)
            {
                var datasetName = GetCustomLegendName(index)
                    ?? Path.GetFileNameWithoutExtension(dataset.SourceFilePath)
                    ?? $"dataset {index + 1}";

                foreach (var region in validRegions)
                {
                    var result = SpectrumIntegrator.Integrate(dataset, region);
                    _integrationResultRowVms.Add(IntegrationResultRowVm.From(datasetName, dataset, result));
                }
            }
        }

        IntegrationResultEmptyHintTextBlock.Visibility =
            _integrationResultRowVms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlotContainerBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlotHostAspectRatio();
    }

    private void ApplyGraphAppearanceAndRefresh()
    {
        if (_spectrumPlot is null)
        {
            return;
        }

        SchedulePlotCurrentDataset();
    }

    private void SelectGraphFontComboBoxValue(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            SelectComboBoxItemByTag(GraphFontComboBox, "Auto");
            GraphFontComboBox.Text = "Auto";
            return;
        }

        if (!SelectComboBoxItemByTag(GraphFontComboBox, fontName))
        {
            GraphFontComboBox.SelectedIndex = -1;
            GraphFontComboBox.Text = fontName;
        }
    }

    private string? GetSelectedGraphFontName()
    {
        var tag = GetSelectedComboBoxTag(GraphFontComboBox);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            return tag.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? null : tag;
        }

        var text = GraphFontComboBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return text;
    }

    private string? GetSelectedLineColorConfigValue()
    {
        var tag = GetSelectedComboBoxTag(LineColorComboBox);
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            return TryNormalizeHexColorCode(LineColorHexTextBox.Text, out var hex) ? hex : null;
        }

        return TryNormalizeHexColorCode(tag, out var resolved) ? resolved : null;
    }

    private string? GetSelectedAspectRatioConfigValue()
    {
        var tag = GetSelectedComboBoxTag(AspectRatioComboBox);
        return string.IsNullOrWhiteSpace(tag) || tag.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : tag;
    }

    private string? GetSelectedInvertXAxisModeConfigValue()
    {
        var tag = GetSelectedComboBoxTag(InvertXAxisComboBox);
        return string.IsNullOrWhiteSpace(tag) || tag.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : tag;
    }

    private void ApplyEnabledPeakAssignments(IList<string>? labels)
    {
        var set = new HashSet<string>(labels ?? Array.Empty<string>(), StringComparer.Ordinal);
        foreach (var vm in _peakAssignmentVms)
        {
            vm.IsEnabled = set.Contains(vm.Label);
        }
    }

    private void ApplyIntegrationRegions(IList<IntegrationRegion>? regions)
    {
        foreach (var existing in _integrationRegionVms)
        {
            existing.PropertyChanged -= IntegrationRegionVm_PropertyChanged;
        }

        _integrationRegionVms.Clear();

        if (regions is null)
        {
            UpdateIntegrationResults();
            return;
        }

        foreach (var region in regions)
        {
            if (region is null || !region.IsValid)
            {
                continue;
            }

            var vm = new IntegrationRegionVm
            {
                Label = region.Label,
                XMinText = region.XMin.ToString("G", CultureInfo.InvariantCulture),
                XMaxText = region.XMax.ToString("G", CultureInfo.InvariantCulture),
                Baseline = region.BaselineMethod,
            };
            vm.PropertyChanged += IntegrationRegionVm_PropertyChanged;
            _integrationRegionVms.Add(vm);
        }

        UpdateIntegrationResults();
    }

    private void UpdatePeakAssignmentUi(SpectrumDataset? dataset)
    {
        var enabled = dataset?.IsInfraredSpectrum == true;
        PeakAssignmentItemsControl.IsEnabled = enabled;
        PeakAssignmentEnableAllButton.IsEnabled = enabled;
        PeakAssignmentDisableAllButton.IsEnabled = enabled;
        PeakAssignmentHintTextBlock.Visibility = enabled
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void DrawIntegrationRegions(AxisDataRange yRange)
    {
        if (_spectrumPlot is null || _integrationRegionVms.Count == 0 || !yRange.HasValue)
        {
            return;
        }

        var axisLimits = _spectrumPlot.Plot.Axes.GetLimits();
        var bandBottom = axisLimits.Bottom;
        var bandTop = axisLimits.Top;
        var ySpan = bandTop - bandBottom;
        var yPad = ySpan > 0 ? ySpan * 100.0 : 1.0;

        // Slate-400, deliberately neutral so it does not collide with dataset
        // colors or IR peak assignment colors.
        var color = ScottPlot.Color.FromHex("94A3B8");

        foreach (var vm in _integrationRegionVms)
        {
            var region = vm.ToModel();
            if (region is null)
            {
                continue;
            }

            var rect = _spectrumPlot.Plot.Add.Rectangle(
                region.XMin, region.XMax,
                bandBottom - yPad, bandTop + yPad);
            rect.FillStyle.Color = color.WithAlpha((byte)50);
            rect.LineStyle.Color = color;
            rect.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
            rect.LineStyle.Width = 1;
            rect.LegendText = string.Empty;
        }
    }

    private void DrawPeakAssignments(SpectrumDataset dataset, AxisDataRange yRange)
    {
        if (_spectrumPlot is null || !dataset.IsInfraredSpectrum || !yRange.HasValue)
        {
            return;
        }

        // Read the actual axis limits after AutoScale + invert so the band
        // matches what is visible on screen at draw time. AxisSpan-based APIs
        // were unreliable across redraws (visible only intermittently);
        // explicit rectangles render deterministically.
        var axisLimits = _spectrumPlot.Plot.Axes.GetLimits();
        var bandBottom = axisLimits.Bottom;
        var bandTop = axisLimits.Top;
        var ySpan = bandTop - bandBottom;
        // Pad the rectangle Y range generously so the band survives moderate
        // mouse pan / zoom without a redraw. Plot.Clear at the start of each
        // PlotCurrentDataset run wipes these rectangles, so the inflated range
        // never bleeds into a future AutoScale.
        var yPad = ySpan > 0 ? ySpan * 100.0 : 1.0;
        var labelY = ySpan > 0 ? bandTop - ySpan * 0.02 : bandTop;

        foreach (var vm in _peakAssignmentVms)
        {
            if (!vm.IsEnabled)
            {
                continue;
            }

            var assignment = vm.Source;
            var hex = assignment.ColorHex.TrimStart('#');
            var color = ScottPlot.Color.FromHex(hex);

            if (assignment.IsRange)
            {
                var rect = _spectrumPlot.Plot.Add.Rectangle(
                    assignment.MinWavenumber, assignment.MaxWavenumber,
                    bandBottom - yPad, bandTop + yPad);
                rect.FillStyle.Color = color.WithAlpha((byte)40);
                rect.LineStyle.IsVisible = false;
                rect.LegendText = string.Empty;
            }

            var line = _spectrumPlot.Plot.Add.VerticalLine(assignment.CenterWavenumber);
            line.LineStyle.Color = color;
            line.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
            line.LineStyle.Width = 1;
            line.LegendText = string.Empty;

            var text = _spectrumPlot.Plot.Add.Text(assignment.Label, assignment.CenterWavenumber, labelY);
            text.LabelFontColor = color;
            text.LabelFontSize = 9;
            text.LabelAlignment = ScottPlot.Alignment.UpperCenter;
        }
    }

    private string? GetSelectedYAxisDisplayModeConfigValue()
    {
        var tag = GetSelectedComboBoxTag(YAxisDisplayComboBox);
        return string.IsNullOrWhiteSpace(tag) || tag.Equals("Native", StringComparison.OrdinalIgnoreCase)
            ? null
            : tag;
    }

    private YAxisDisplayMode GetSelectedYAxisDisplayMode()
    {
        return GetSelectedComboBoxTag(YAxisDisplayComboBox) switch
        {
            "Absorbance" => YAxisDisplayMode.Absorbance,
            "Transmittance" => YAxisDisplayMode.Transmittance,
            _ => YAxisDisplayMode.Native,
        };
    }

    private float GetPlotFontSize()
    {
        return TryParsePositiveDouble(GraphFontSizeTextBox.Text, out var value)
            ? (float)value
            : (float)GraphFormattingConfig.DefaultFontSize;
    }

    private float GetPlotFrameWidth()
    {
        return TryParsePositiveDouble(PlotFrameWidthTextBox.Text, out var value)
            ? (float)value
            : (float)GraphFormattingConfig.DefaultPlotFrameWidth;
    }

    private string GetPlotFrameColorHex()
    {
        return TryNormalizeHexColorCode(PlotFrameColorHexTextBox.Text, out var hex)
            ? hex
            : GraphFormattingConfig.DefaultPlotFrameColorHex;
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

    private string GetBackgroundColorHex()
    {
        return TryNormalizeHexColorCode(BackgroundColorHexTextBox.Text, out var hex)
            ? hex
            : GraphFormattingConfig.DefaultBackgroundColorHex;
    }

    private void SyncBackgroundColorInputFromComboBox()
    {
        var tag = GetSelectedComboBoxTag(BackgroundColorComboBox);
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            UpdateBackgroundColorPreview(GetBackgroundColorHex());
            return;
        }

        SetBackgroundColorInput(tag);
    }

    private void SetBackgroundColorInput(string? hex)
    {
        var normalized = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GraphFormattingConfig.DefaultBackgroundColorHex;

        if (!SelectComboBoxItemByTag(BackgroundColorComboBox, normalized))
        {
            SelectComboBoxItemByTag(BackgroundColorComboBox, "Custom");
        }

        BackgroundColorHexTextBox.Text = normalized;
        UpdateBackgroundColorPreview(normalized);
    }

    private void UpdateBackgroundColorPreview(string? hex)
    {
        if (BackgroundColorPreviewBorder is null)
        {
            return;
        }

        var previewHex = TryNormalizeHexColorCode(hex, out var colorHex)
            ? colorHex
            : GraphFormattingConfig.DefaultBackgroundColorHex;
        BackgroundColorPreviewBorder.Background = new SolidColorBrush(HexToMediaColor(previewHex));
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

    private static bool SelectComboBoxItemByTag(ComboBox comboBox, string? tag)
    {
        if (comboBox is null || string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag is string tagValue
                && tagValue.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = cbi;
                return true;
            }
        }

        return false;
    }

    private static string? GetSelectedComboBoxTag(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : null;
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
        if (_spectrumPlot is null)
        {
            return;
        }

        var svg = _spectrumPlot.Plot.GetSvgHtml(width, height);
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

    private static ScottPlot.Color GetScottPlotColor(string hex, string fallback)
    {
        try
        {
            return ScottPlot.Color.FromHex(new[] { hex }).First();
        }
        catch
        {
            return ScottPlot.Color.FromHex(new[] { fallback }).First();
        }
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

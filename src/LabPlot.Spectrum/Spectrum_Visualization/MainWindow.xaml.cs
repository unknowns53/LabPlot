using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using SpectrumAnalyzer.Core;
using LabPlot.Core;
using LabPlot.Core.Wpf.Helpers;
using Microsoft.Win32;
using ScottPlot.WPF;
using static LabPlot.Core.PlotAppearance;
using static LabPlot.Core.Wpf.FormatHelpers;

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
    private readonly AnalysisSessionStore<SpectrumAnalysisSession> _sessionStore = new();

    // Saved defaults read from / written to %AppData%\Spectrum_Visualization\formatting_config.json.
    // The Reset ボタン restores controls to this snapshot, so it must NOT be
    // overwritten by transient operations like loading a session — sessions
    // mutate _formattingConfig instead. Calibration confirmation and
    // "Save as defaults" are the only flows that write here.
    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();

    // Live working state. Tracks whatever the user is currently looking at
    // (post-session-load, post-calibration-edit, etc.). Calibration access
    // and dataset-style seeding read from here so they reflect the visible
    // state instead of the persisted defaults.
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private int _activeIndex = -1;
    private SpectrumDataset? _currentDataset;
    private WpfPlot? _spectrumPlot;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressDatasetListEvents;

    // Cache of the per-app "auto-show" decision from the most recent plot
    // pass. Format-panel handlers (legend visibility / position combo box)
    // read this to refresh the legend without re-running the heavy plot
    // path; per-dataset state changes update it via the Plot* methods.
    private bool _currentLegendAutoShow;

    private const string DatasetReorderDataFormat = "Spectrum.DatasetEntryIndex";
    private Point? _datasetDragStartPoint;
    private InsertionAdorner? _datasetInsertionAdorner;

    // Mouse-drag region selection for the integration feature.
    private Canvas? _integrationDragOverlay;
    private System.Windows.Shapes.Rectangle? _integrationDragPreview;
    private bool _isIntegrationDragMode;
    private bool _integrationDragStarted;
    private Point _integrationDragStartPoint;
    private IntegrationRegionVm? _integrationDragTargetVm;

    // Edge-resize for already-defined integration regions. The user can
    // grab the left or right edge of a band rectangle and drag it; the
    // bound XMinText / XMaxText updates live so the plot re-renders and
    // the result panel recalculates as the mouse moves.
    private const double IntegrationEdgeHitTolerancePixels = 5.0;
    private bool _isIntegrationResizing;
    private IntegrationRegionVm? _integrationResizeTargetVm;
    private bool _integrationResizeIsLeftEdge;
    private string? _integrationResizeOriginalText;

    // Click-to-add gesture for manually-corrected λmax markers. Persisted in
    // GraphFormattingConfig.ManualLambdaMaxEntries; rendered as a filled
    // triangle (vs. open triangle for auto-detected peaks) so the user can
    // tell them apart.
    private readonly ObservableCollection<ManualLambdaMaxEntryVm> _manualLambdaMaxEntryVms = new();
    private bool _isManualLambdaMaxAddMode;

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
        _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
        ApplyFormattingConfigToControls(_formattingConfig);
        DatasetListBox.ItemsSource = _datasetEntries;
        PeakAssignmentItemsControl.ItemsSource = _peakAssignmentVms;
        IntegrationRegionItemsControl.ItemsSource = _integrationRegionVms;
        IntegrationResultItemsControl.ItemsSource = _integrationResultRowVms;
        ManualLambdaMaxItemsControl.ItemsSource = _manualLambdaMaxEntryVms;
        UpdateManualLambdaMaxEmptyVisibility();
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
            () => AxisRangePanel.ResetToAuto());
        AddShortcut(System.Windows.Input.Key.O, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
            () => LoadSessionButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
            () => SaveSessionButton_Click(this, new RoutedEventArgs()));
        AddShortcut(System.Windows.Input.Key.L, System.Windows.Input.ModifierKeys.Control,
            () => ToggleCheckBox(OverlayCheckBox));
        AddShortcut(System.Windows.Input.Key.G, System.Windows.Input.ModifierKeys.Control,
            () => GraphFormatPanel.TogglePlotGrid());
        AddShortcut(System.Windows.Input.Key.F2, System.Windows.Input.ModifierKeys.None,
            FocusLegendNameTextBox);
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

    private static void ToggleCheckBox(CheckBox checkBox)
    {
        if (checkBox is null || !checkBox.IsEnabled)
        {
            return;
        }

        checkBox.IsChecked = checkBox.IsChecked != true;
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
        => FormattingDefaultsStore.GetExistingDefaultOutputDirectory(_formattingDefaults);

    private void ApplyDefaultOutputDirectoryToDialog(FileDialog dialog)
        => FormattingDefaultsStore.ApplyDefaultOutputDirectoryToDialog(dialog, _formattingDefaults);

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
        // Seed from the live config so a freshly-loaded session's default
        // colour / width applies to subsequently-added datasets, not the
        // user's persisted defaults from formatting_config.json.
        style.ColorHex = _formattingConfig.DefaultLineColorHex;
        style.LegendName = null;
        style.LineWidth = _formattingConfig.LineWidth;
        style.MarkerSize = _formattingConfig.MarkerSize;
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
        private string _rubberBandSegmentsText = "16";
        private string _polynomialOrderText = "2";

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
                OnPropertyChanged(nameof(IsRubberBand));
                OnPropertyChanged(nameof(IsPolynomial));
            }
        }

        public string RubberBandSegmentsText
        {
            get => _rubberBandSegmentsText;
            set
            {
                if (_rubberBandSegmentsText == value) return;
                _rubberBandSegmentsText = value;
                OnPropertyChanged();
            }
        }

        public string PolynomialOrderText
        {
            get => _polynomialOrderText;
            set
            {
                if (_polynomialOrderText == value) return;
                _polynomialOrderText = value;
                OnPropertyChanged();
            }
        }

        // Both rubber-band variants share the Segments knob, so the same
        // Visibility flag drives the inline TextBox for either.
        public bool IsRubberBand =>
            _baseline is BaselineMethod.RubberBand or BaselineMethod.RubberBandHull;

        public bool IsPolynomial => _baseline == BaselineMethod.Polynomial;

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

            // Failed parses fall back to defaults so a transient typo
            // (empty / mid-edit text) doesn't invalidate the whole region.
            // Out-of-range values silently clamp.
            var segments = int.TryParse(
                _rubberBandSegmentsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                ? s : 16;
            var order = int.TryParse(
                _polynomialOrderText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o)
                ? o : 2;

            var region = new IntegrationRegion
            {
                Label = _label.Trim(),
                XMin = xMin,
                XMax = xMax,
                BaselineMethod = _baseline,
                RubberBandSegments = Math.Clamp(segments, 2, 1024),
                PolynomialOrder = Math.Clamp(order, 1, 5),
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
                var native = string.IsNullOrWhiteSpace(dataset.RawYUnits) ? "?" : dataset.RawYUnits;
                tooltip =
                    $"Area = {result.Area.ToString("G6", CultureInfo.InvariantCulture)} (Absorbance)\n"
                    + $"Raw = {result.RawArea.ToString("G6", CultureInfo.InvariantCulture)}\n"
                    + $"Baseline = {result.BaselineArea.ToString("G6", CultureInfo.InvariantCulture)}\n"
                    + $"N = {result.PointCount}\n"
                    + $"Native YUNITS = {native}";
            }
            else if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance))
            {
                tooltip = "Absorbance に変換できないデータセットのため積分できません";
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
        _formattingDefaults = FormattingDefaultsStore.Load<GraphFormattingConfig>(
            FormattingConfigPath,
            FormattingConfigJsonOptions,
            msg => SetStatus(msg, true));
    }

    private void SaveFormattingDefaults()
    {
        FormattingDefaultsStore.Save(
            _formattingDefaults,
            FormattingConfigPath,
            FormattingConfigJsonOptions);
    }

    private GraphFormattingConfig CaptureFormattingConfigFromControls()
    {
        var config = new GraphFormattingConfig();
        // Pull all GraphFormattingConfigBase properties (font / ticks / frame /
        // background / aspect ratio / legend) from the shared panel, then layer
        // Spectrum-specific properties on top.
        GraphFormatPanel.Capture(config);

        // Title / axis label visibility lives in the standalone "グラフラベル"
        // section, not in GraphFormatPanel.
        config.ShowTitle = TitleVisibleCheckBox.IsChecked == true;
        config.TitleBold = TitleBoldCheckBox.IsChecked == true;
        config.AxisLabelBold = AxisLabelBoldCheckBox.IsChecked == true;

        // X-axis orientation / Y-axis display live in the Spectrum-only
        // SpectrumAxisDisplayPanel (sibling of GraphFormatPanel); surface
        // them through that panel's accessors.
        var invertTag = AxisDisplayPanel.InvertXAxisModeTag;
        config.InvertXAxisMode = string.IsNullOrWhiteSpace(invertTag)
            || invertTag.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : invertTag;
        var yDisplayTag = AxisDisplayPanel.YAxisDisplayModeTag;
        config.YAxisDisplayMode = string.IsNullOrWhiteSpace(yDisplayTag)
            ? null
            : yDisplayTag;

        config.EnabledIrPeakAssignmentLabels = _peakAssignmentVms
            .Where(vm => vm.IsEnabled)
            .Select(vm => vm.Label)
            .ToList();
        config.IntegrationRegions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .Where(region => region is not null)
            .Cast<IntegrationRegion>()
            .ToList();

        // Per-dataset line style controls live in their own panel.
        config.DefaultLineColorHex = LineColorPicker.HexValue;
        config.LineWidth = TryParsePositiveDouble(LineWidthTextBox.Text, out var lineWidth)
            ? lineWidth
            : GraphFormattingConfig.DefaultLineWidth;
        config.MarkerSize = TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var markerSize)
            ? markerSize
            : GraphFormattingConfig.DefaultMarkerSize;

        config.ShowLambdaMaxMarkers = ShowLambdaMaxCheckBox.IsChecked == true;
        config.LambdaMaxMinAbsorbance = TryParseNonNegativeDouble(LambdaMaxMinAbsorbanceTextBox.Text, out var lambdaMin)
            ? lambdaMin
            : 0.05;
        config.LambdaMaxCount = int.TryParse(LambdaMaxCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lambdaCount)
            && lambdaCount >= 0
            ? lambdaCount
            : 3;
        config.ManualLambdaMaxEntries = _manualLambdaMaxEntryVms.Select(vm => vm.ToModel()).ToList();
        config.ShowCloudPointMarkers = ShowCloudPointCheckBox.IsChecked == true;
        config.CloudPointMethod = GetSelectedCloudPointMethodConfigValue();
        config.CloudPointThresholdPercent = TryParseNonNegativeDouble(CloudPointThresholdTextBox.Text, out var cpThreshold)
            ? cpThreshold
            : 50.0;
        config.ShowCloudPointFitCurve = ShowSigmoidFitCurveCheckBox.IsChecked == true;
        config.ShowCloudPointFitParameters = ShowSigmoidFitParametersCheckBox.IsChecked == true;
        config.ShowTemperatureScanMetadata = ShowMetadataCheckBox.IsChecked == true;
        config.DefaultOutputDirectory = DefaultOutputDirectoryTextBox.Text;
        // Calibration has its own editor window — preserve whatever was
        // last saved there instead of clobbering it with a default.
        config.Calibration = _formattingConfig.Calibration;

        config.Normalize();
        return config;
    }

    private void ApplyFormattingConfigToControls(GraphFormattingConfig config)
    {
        config.Normalize();

        // GraphFormatPanel suppresses its own change events while writing.
        GraphFormatPanel.Apply(config);
        AxisDisplayPanel.SetInvertXAxisModeTag(config.InvertXAxisMode);
        AxisDisplayPanel.SetYAxisDisplayModeTag(config.YAxisDisplayMode);

        _suppressGraphAppearanceEvents = true;
        try
        {
            TitleVisibleCheckBox.IsChecked = config.ShowTitle;
            TitleBoldCheckBox.IsChecked = config.TitleBold;
            AxisLabelBoldCheckBox.IsChecked = config.AxisLabelBold;

            ApplyEnabledPeakAssignments(config.EnabledIrPeakAssignmentLabels);
            ApplyIntegrationRegions(config.IntegrationRegions);

            ShowLambdaMaxCheckBox.IsChecked = config.ShowLambdaMaxMarkers;
            LambdaMaxMinAbsorbanceTextBox.Text = config.LambdaMaxMinAbsorbance.ToString("0.###", CultureInfo.InvariantCulture);
            LambdaMaxCountTextBox.Text = config.LambdaMaxCount.ToString(CultureInfo.InvariantCulture);
            ApplyManualLambdaMaxEntries(config.ManualLambdaMaxEntries);
            ShowCloudPointCheckBox.IsChecked = config.ShowCloudPointMarkers;
            if (!SelectComboBoxItemByTag(CloudPointMethodComboBox, config.CloudPointMethod ?? "Midpoint"))
            {
                CloudPointMethodComboBox.SelectedIndex = 0;
            }
            CloudPointThresholdTextBox.Text = config.CloudPointThresholdPercent.ToString("0.##", CultureInfo.InvariantCulture);
            ShowSigmoidFitCurveCheckBox.IsChecked = config.ShowCloudPointFitCurve;
            ShowSigmoidFitParametersCheckBox.IsChecked = config.ShowCloudPointFitParameters;
            UpdateSigmoidPanelVisibility();
            ShowMetadataCheckBox.IsChecked = config.ShowTemperatureScanMetadata;
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

        DefaultOutputDirectoryTextBox.Text = config.DefaultOutputDirectory ?? string.Empty;

        UpdateCalibrationUi();
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
            Filter = "JASCO スペクトル (*.txt;*.csv)|*.txt;*.csv|JASCO TXT (*.txt)|*.txt|JASCO CSV (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
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

            // The axis range textboxes carry whatever the user's last mouse pan /
            // zoom synced into them. When the dataset itself is being replaced,
            // those stale values would otherwise override AutoScale on the new
            // data, so clear them here. Overlay mode keeps the existing view
            // because the user is usually comparing peaks at a chosen zoom.
            AxisRangePanel.SetXValues(null, null);
            AxisRangePanel.SetYValues(null, null);
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
        UpdateCalibrationUi();
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
        // Reset is the explicit "discard live edits, restore saved
        // defaults" flow, so push _formattingDefaults into the controls
        // and re-clone it as the new live config.
        ApplyFormattingConfigToControls(_formattingDefaults);
        _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);

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
            // "Save as defaults" promotes the current control state into
            // both the persisted snapshot AND the live config, so a
            // subsequent Reset bounces back to exactly the same view.
            _formattingDefaults = CaptureFormattingConfigFromControls();
            _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
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
        var entries = new List<SpectrumAnalysisExportEntry>();
        var plotEntries = GetDatasetsToPlotWithIndices();
        var yDisplayMode = GetSelectedYAxisDisplayMode();

        foreach (var (dataset, index) in plotEntries)
        {
            var displayName = GetCustomLegendName(index)
                ?? Path.GetFileNameWithoutExtension(dataset.SourceFilePath)
                ?? $"dataset {index + 1}";

            entries.Add(new SpectrumAnalysisExportEntry
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
            GeneratorName = "Spectrum Visualization",
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
            Title = "解析条件を読み込み",
            Filter = "Spectrum セッション (*.specjson;*.json)|*.specjson;*.json|すべてのファイル (*.*)|*.*",
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

    private SpectrumAnalysisSession BuildAnalysisSession()
    {
        // 環境設定（出力フォルダ）はセッションには含めず、ユーザーごとの
        // formatting_config.json にだけ保存する。これでセッションを別 PC や
        // 別ユーザーに渡しても、相手側の環境設定を上書きしない。
        var sessionFormatting = CaptureFormattingConfigFromControls();
        sessionFormatting.DefaultOutputDirectory = null;

        var session = new SpectrumAnalysisSession
        {
            Overlay = OverlayCheckBox.IsChecked == true,
            ActiveDatasetIndex = _activeIndex,
            Formatting = sessionFormatting,
            Labels = new AnalysisSessionLabels
            {
                Title = TitleTextBox.Text,
                XLabel = XLabelTextBox.Text,
                YLabel = YLabelTextBox.Text,
            },
            Axes = new AnalysisSessionAxes
            {
                XMin = AxisRangePanel.XMinValue,
                XMax = AxisRangePanel.XMaxValue,
                YMin = AxisRangePanel.YMinValue,
                YMax = AxisRangePanel.YMaxValue,
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

    private void ApplyAnalysisSession(SpectrumAnalysisSession session, List<string> warnings)
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
            // セッション内のすべてのデータが再読み込みに失敗した場合、UI 上で
            // 「今表示されているグラフが操作対象なのか」が曖昧にならないよう、
            // 削除ボタン側 (RemoveDatasetButton_Click) と同じ手順で空状態に
            // 戻す: 表示パスをクリア + 既存プロットを破棄して空プロットへ。
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
            UpdateCalibrationUi();
            return;
        }

        _activeIndex = Math.Clamp(session.ActiveDatasetIndex, 0, _loadedDatasets.Count - 1);
        _currentDataset = _loadedDatasets[_activeIndex];

        OverlayCheckBox.IsChecked = session.Overlay;

        if (session.Formatting is not null)
        {
            session.Formatting.Normalize();
            // 環境設定（出力フォルダ）はユーザーごとの formatting_config.json
            // に属するので、ローカルの defaults から復元してから live 側
            // (_formattingConfig) に流し込む。_formattingDefaults はユーザー
            // が「既定値として保存」or 検量線確定を押した時にだけ更新される
            // snapshot なので、ここでは触らない — そうしないと Reset ボタン
            // がセッションの書式に巻き戻る。
            session.Formatting.DefaultOutputDirectory = _formattingDefaults.DefaultOutputDirectory;
            _formattingConfig = session.Formatting;
            ApplyFormattingConfigToControls(_formattingConfig);
        }

        var labels = session.Labels;
        TitleTextBox.Text = labels.Title ?? string.Empty;
        XLabelTextBox.Text = labels.XLabel ?? string.Empty;
        YLabelTextBox.Text = labels.YLabel ?? string.Empty;

        var axes = session.Axes;
        AxisRangePanel.SetXValues(axes.XMin, axes.XMax);
        AxisRangePanel.SetYValues(axes.YMin, axes.YMax);

        FilePathTextBlock.Text = _loadedDatasets.Count > 1
            ? $"{_loadedDatasets.Count} files (latest: {_currentDataset.SourceFilePath})"
            : _currentDataset.SourceFilePath ?? string.Empty;

        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();
        UpdatePlotHostAspectRatio();
        PlotCurrentDataset();
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
            var saveFormat = GraphSaveHelpers.GetGraphSaveFormat(dialog.FileName, dialog.FilterIndex);
            var fileName = GraphSaveHelpers.EnsureGraphSaveFileExtension(dialog.FileName, saveFormat);
            var exportStyleScale = GetExportStyleScale();

            ApplyExportStyleScale(exportStyleScale);
            try
            {
                if (saveFormat == GraphSaveFormat.Svg)
                {
                    GraphSaveHelpers.SaveGraphSvg(_spectrumPlot.Plot, fileName, width, height);
                    SetStatus($"グラフをSVGで保存しました: {fileName} ({width:N0} x {height:N0})", false);
                    return;
                }

                GraphSaveHelpers.SaveGraphPng(_spectrumPlot.Plot, fileName, width, height, GraphSaveHelpers.ExportDpi);
                SetStatus($"グラフをPNGで保存しました: {fileName} ({width:N0} x {height:N0} px, {GraphSaveHelpers.ExportDpi} dpi)", false);
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

            // Permanent handlers that drive the edge-resize gesture for
            // existing integration regions. They no-op while the
            // add-region drag mode is active so the two gestures stay
            // mutually exclusive.
            _spectrumPlot.PreviewMouseMove += IntegrationResize_PreviewMouseMove;
            _spectrumPlot.PreviewMouseLeftButtonDown += IntegrationResize_PreviewMouseLeftButtonDown;
            _spectrumPlot.PreviewMouseLeftButtonUp += IntegrationResize_PreviewMouseLeftButtonUp;
            _spectrumPlot.PreviewMouseRightButtonDown += IntegrationResize_PreviewMouseRightButtonDown;
            PreviewKeyDown += IntegrationResize_PreviewKeyDown;

            PlotHost.Children.Clear();
            PlotHost.Children.Add(_spectrumPlot);

            _integrationDragOverlay = new Canvas
            {
                Background = null,
                IsHitTestVisible = false,
            };
            PlotHost.Children.Add(_integrationDragOverlay);

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

        _currentLegendAutoShow = ShouldShowLegend(plotEntries.Select(entry => entry.Index));
        ApplyLegend(_spectrumPlot.Plot, CaptureFormattingConfigFromControls(),
            autoShow: _currentLegendAutoShow);

        _spectrumPlot.Plot.Title(GetGraphTitle(Path.GetFileNameWithoutExtension(activeDataset.SourceFilePath) ?? "Spectrum"));
        _spectrumPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, activeDataset.XLabel));
        _spectrumPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, SpectrumYAxisConverter.GetDisplayYLabel(activeDataset, yDisplayMode)));
        _spectrumPlot.Plot.Axes.AutoScale();

        // IR convention: high wavenumbers on the left (4000 → 400 cm⁻¹).
        // The user can override this through the format panel (Auto / Inverted / Normal).
        var invertX = AxisDisplayPanel.InvertXAxisModeTag switch
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
        DrawIntegrationBaselines(plotEntries, yDisplayMode);
        DrawLambdaMaxMarkers(plotEntries, yRange);
        DrawCloudPointMarkers(plotEntries, yRange);
        DrawMetadataAnnotation(plotEntries);

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
        return GraphSaveHelpers.ExportDpi / GraphSaveHelpers.DisplayDpi;
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

        var xMin = AxisRangePanel.XMinValue;
        var xMax = AxisRangePanel.XMaxValue;
        var yMin = AxisRangePanel.YMinValue;
        var yMax = AxisRangePanel.YMaxValue;

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
            // Match GPC's per-dataset auto-palette behaviour: the picker's
            // "Auto" preview falls back to DefaultHex, so wire it to the
            // colour the plot will actually draw for this dataset slot.
            LineColorPicker.DefaultHex = AutoLineColors[_activeIndex % AutoLineColors.Length];
            LineColorPicker.SetHexValue(style.ColorHex);
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
            UpdateCalibrationUi();
            return;
        }

        _activeIndex = Math.Clamp(_activeIndex >= index ? _activeIndex - 1 : _activeIndex, 0, _loadedDatasets.Count - 1);
        _currentDataset = _loadedDatasets[_activeIndex];
        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();
        PlotCurrentDataset();
        UpdateCalibrationUi();
    }

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (_suppressStyleControlEvents) return;

        // Per-dataset line colour: null = "use auto palette",
        // "#RRGGBB" = explicit override. ColorPickerPanel already
        // owns the preset / hex / preview triplet, so we just mirror
        // its output into the active dataset's style record.
        ApplyDatasetStyle(style => style.ColorHex = LineColorPicker.HexValue);
        RefreshDatasetEntries();
        PlotCurrentDataset();
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

    private void GraphFormatPanel_GraphFormatChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        ApplyGraphAppearanceAndRefresh();
    }

    private void GraphFormatPanel_AspectRatioChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        // Resize PlotHost; the trailing GraphFormatChanged event (the panel
        // raises both for AspectRatio changes) handles SchedulePlotCurrentDataset
        // through ApplyGraphAppearanceAndRefresh.
        UpdatePlotHostAspectRatio();
    }

    private void AxisDisplayPanel_AxisOrientationChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        // X-axis flip needs a heavy redraw (limits flipped, IR override
        // re-evaluated), so route to the debounced replot rather than the
        // light Refresh path.
        SchedulePlotCurrentDataset();
    }

    private void AxisDisplayPanel_YAxisDisplayChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        // Native ↔ Absorbance ↔ Transmittance changes the underlying Y values
        // for every series, so go through the debounced replot.
        SchedulePlotCurrentDataset();
    }

    // Title / axis-label CheckBoxes still live in MainWindow (the standalone
    // "グラフラベル" Section is outside GraphFormatPanel's scope), so they keep
    // routing through this handler.
    private void GraphAppearanceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
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

    private void AxisRangePanel_Committed(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        if (_spectrumPlot is null || _currentDataset is null)
        {
            return;
        }

        // After "Reset to auto" the panel returns null for all four values.
        // The spectrum plot keeps the previous limits otherwise, so call
        // AutoScale() explicitly to match the old AutoAxisRangeButton flow.
        var allAuto = AxisRangePanel.XMinValue is null
            && AxisRangePanel.XMaxValue is null
            && AxisRangePanel.YMinValue is null
            && AxisRangePanel.YMaxValue is null;
        if (allAuto)
        {
            _spectrumPlot.Plot.Axes.AutoScale();
        }

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
        AxisRangePanel.SetXValues(
            double.IsFinite(limits.Left) ? limits.Left : null,
            double.IsFinite(limits.Right) ? limits.Right : null);
        AxisRangePanel.SetYValues(
            double.IsFinite(limits.Bottom) ? limits.Bottom : null,
            double.IsFinite(limits.Top) ? limits.Top : null);
    }


    private void PeakAssignmentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents)
        {
            return;
        }

        SchedulePlotCurrentDataset();
    }

    private void LambdaMaxOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void LambdaMaxNumericTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void CloudPointOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        UpdateSigmoidPanelVisibility();
        SchedulePlotCurrentDataset();
    }

    private void UpdateSigmoidPanelVisibility()
    {
        // Sigmoid-fit-specific options (overlay curve, k/R² in result text)
        // only matter when SigmoidFit is the selected method. The threshold
        // input is ignored by the fitter so dim it as a UX cue.
        var isSigmoid = GetSelectedComboBoxTag(CloudPointMethodComboBox) == "SigmoidFit";
        SigmoidFitOptionsPanel.Visibility = isSigmoid ? Visibility.Visible : Visibility.Collapsed;
        CloudPointThresholdPanel.IsEnabled = !isSigmoid;
    }

    private void CloudPointNumericTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void MetadataOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
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
        // Re-clicking the button while drag mode is active acts as a cancel.
        if (_isIntegrationDragMode)
        {
            ExitIntegrationDragMode(canceled: true);
            return;
        }

        if (!ConfirmAbsorbanceForIntegration())
        {
            return;
        }

        var newRegion = AddIntegrationRegionInternal();

        if (_spectrumPlot is not null && _loadedDatasets.Count > 0)
        {
            EnterIntegrationDragMode(newRegion);
        }
    }

    private IntegrationRegionVm AddIntegrationRegionInternal(double? xMin = null, double? xMax = null)
    {
        var vm = new IntegrationRegionVm
        {
            Label = $"region {_integrationRegionVms.Count + 1}",
            Baseline = BaselineMethod.Linear,
        };

        if (xMin is double xMinValue)
        {
            vm.XMinText = xMinValue.ToString("G6", CultureInfo.InvariantCulture);
        }

        if (xMax is double xMaxValue)
        {
            vm.XMaxText = xMaxValue.ToString("G6", CultureInfo.InvariantCulture);
        }

        vm.PropertyChanged += IntegrationRegionVm_PropertyChanged;
        _integrationRegionVms.Add(vm);
        UpdateIntegrationResults();
        SchedulePlotCurrentDataset();
        return vm;
    }

    /// <summary>
    /// When the Y axis is not Absorbance and the loaded dataset can be
    /// expressed as Absorbance (i.e. its native YUNITS is A or T%), prompt
    /// the user before adding the region. Returns false if the user
    /// cancels the whole operation.
    /// </summary>
    private bool ConfirmAbsorbanceForIntegration()
    {
        var displayMode = GetSelectedYAxisDisplayMode();
        if (displayMode == YAxisDisplayMode.Absorbance)
        {
            return true;
        }

        if (!CanAnyLoadedDatasetUseAbsorbance())
        {
            return true;
        }

        var dialog = new AbsorbanceConfirmDialog { Owner = this };
        dialog.ShowDialog();

        switch (dialog.Choice)
        {
            case AbsorbanceConfirmDialog.DialogChoice.SwitchAndAdd:
                AxisDisplayPanel.SetYAxisDisplayModeTag("Absorbance");
                return true;
            case AbsorbanceConfirmDialog.DialogChoice.AddWithoutSwitch:
                return true;
            default:
                return false;
        }
    }

    private bool CanAnyLoadedDatasetUseAbsorbance()
    {
        foreach (var dataset in _loadedDatasets)
        {
            if (SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance))
            {
                return true;
            }
        }
        return false;
    }

    private void EnterIntegrationDragMode(IntegrationRegionVm targetVm)
    {
        if (_spectrumPlot is null || _isIntegrationDragMode)
        {
            return;
        }

        _isIntegrationDragMode = true;
        _integrationDragStarted = false;
        _integrationDragTargetVm = targetVm;

        _spectrumPlot.Cursor = Cursors.Cross;
        _spectrumPlot.PreviewMouseLeftButtonDown += IntegrationDrag_MouseLeftButtonDown;
        _spectrumPlot.PreviewMouseMove += IntegrationDrag_MouseMove;
        _spectrumPlot.PreviewMouseLeftButtonUp += IntegrationDrag_MouseLeftButtonUp;
        _spectrumPlot.PreviewMouseRightButtonDown += IntegrationDrag_MouseRightButtonDown;
        PreviewKeyDown += IntegrationDrag_KeyDown;

        AddIntegrationRegionButton.Content = "✕ ドラッグ取消";
        SetStatus($"「{targetVm.Label}」をグラフ上でドラッグして範囲を指定（Esc / 右クリック / 同ボタン再押下でキャンセル）", false);
    }

    private void ExitIntegrationDragMode(bool canceled)
    {
        if (!_isIntegrationDragMode || _spectrumPlot is null)
        {
            return;
        }

        _isIntegrationDragMode = false;
        _integrationDragStarted = false;
        _integrationDragTargetVm = null;

        _spectrumPlot.Cursor = null;
        _spectrumPlot.PreviewMouseLeftButtonDown -= IntegrationDrag_MouseLeftButtonDown;
        _spectrumPlot.PreviewMouseMove -= IntegrationDrag_MouseMove;
        _spectrumPlot.PreviewMouseLeftButtonUp -= IntegrationDrag_MouseLeftButtonUp;
        _spectrumPlot.PreviewMouseRightButtonDown -= IntegrationDrag_MouseRightButtonDown;
        PreviewKeyDown -= IntegrationDrag_KeyDown;

        if (_spectrumPlot.IsMouseCaptured)
        {
            _spectrumPlot.ReleaseMouseCapture();
        }

        ClearIntegrationDragPreview();

        AddIntegrationRegionButton.Content = "+ 領域追加";
        if (canceled)
        {
            SetStatus("ドラッグ範囲指定をスキップしました（数値入力で編集できます）", false);
        }
    }

    private void ClearIntegrationDragPreview()
    {
        if (_integrationDragPreview is not null && _integrationDragOverlay is not null)
        {
            _integrationDragOverlay.Children.Remove(_integrationDragPreview);
            _integrationDragPreview = null;
        }
    }

    private void IntegrationDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_spectrumPlot is null || _integrationDragOverlay is null)
        {
            return;
        }

        _integrationDragStartPoint = e.GetPosition(_integrationDragOverlay);
        _integrationDragStarted = true;

        ClearIntegrationDragPreview();
        _integrationDragPreview = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(50, 0x94, 0xA3, 0xB8)),
            Width = 0,
            Height = _integrationDragOverlay.ActualHeight,
        };
        Canvas.SetLeft(_integrationDragPreview, _integrationDragStartPoint.X);
        Canvas.SetTop(_integrationDragPreview, 0);
        _integrationDragOverlay.Children.Add(_integrationDragPreview);

        _spectrumPlot.CaptureMouse();
        e.Handled = true;
    }

    private void IntegrationDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_integrationDragStarted || _integrationDragPreview is null || _integrationDragOverlay is null)
        {
            return;
        }

        var current = e.GetPosition(_integrationDragOverlay);
        var left = Math.Min(_integrationDragStartPoint.X, current.X);
        var width = Math.Abs(current.X - _integrationDragStartPoint.X);
        Canvas.SetLeft(_integrationDragPreview, left);
        Canvas.SetTop(_integrationDragPreview, 0);
        _integrationDragPreview.Width = width;
        _integrationDragPreview.Height = _integrationDragOverlay.ActualHeight;
        e.Handled = true;
    }

    private void IntegrationDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_spectrumPlot is null || _integrationDragTargetVm is null)
        {
            ExitIntegrationDragMode(canceled: true);
            return;
        }

        if (!_integrationDragStarted)
        {
            ExitIntegrationDragMode(canceled: true);
            return;
        }

        var endInPlot = e.GetPosition(_spectrumPlot);
        var startInPlot = _integrationDragStartPoint;

        // The pixel width threshold suppresses accidental clicks (no real drag).
        if (Math.Abs(endInPlot.X - startInPlot.X) < 3)
        {
            ExitIntegrationDragMode(canceled: true);
            e.Handled = true;
            return;
        }

        var c1 = _spectrumPlot.Plot.GetCoordinates(new ScottPlot.Pixel((float)startInPlot.X, (float)startInPlot.Y));
        var c2 = _spectrumPlot.Plot.GetCoordinates(new ScottPlot.Pixel((float)endInPlot.X, (float)endInPlot.Y));

        var x1 = Math.Min(c1.X, c2.X);
        var x2 = Math.Max(c1.X, c2.X);

        if (!double.IsFinite(x1) || !double.IsFinite(x2) || x1 == x2)
        {
            ExitIntegrationDragMode(canceled: true);
            e.Handled = true;
            return;
        }

        var targetLabel = _integrationDragTargetVm.Label;
        _integrationDragTargetVm.XMinText = x1.ToString("G6", CultureInfo.InvariantCulture);
        _integrationDragTargetVm.XMaxText = x2.ToString("G6", CultureInfo.InvariantCulture);
        SetStatus($"「{targetLabel}」の範囲を [{x1:G6}, {x2:G6}] に設定しました", false);
        ExitIntegrationDragMode(canceled: false);
        e.Handled = true;
    }

    private void IntegrationDrag_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ExitIntegrationDragMode(canceled: true);
        e.Handled = true;
    }

    private void IntegrationDrag_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitIntegrationDragMode(canceled: true);
            e.Handled = true;
        }
    }

    // -------------- Edge resize for existing integration regions --------------

    private void IntegrationResize_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_spectrumPlot is null) return;
        if (_isIntegrationDragMode) return;  // add-region mode handles its own move
        if (_isManualLambdaMaxAddMode) return; // manual λmax add owns the cursor / clicks

        var pos = e.GetPosition(_spectrumPlot);

        if (_isIntegrationResizing && _integrationResizeTargetVm is not null)
        {
            var coords = _spectrumPlot.Plot.GetCoordinates(
                new ScottPlot.Pixel((float)pos.X, (float)pos.Y));
            if (!double.IsFinite(coords.X)) return;

            var formatted = coords.X.ToString("G6", CultureInfo.InvariantCulture);
            if (_integrationResizeIsLeftEdge)
            {
                _integrationResizeTargetVm.XMinText = formatted;
            }
            else
            {
                _integrationResizeTargetVm.XMaxText = formatted;
            }
            e.Handled = true;
            return;
        }

        // Idle: hover detection so the user gets a visual cue when an
        // edge is grabbable.
        var hover = FindIntegrationEdgeAt(pos);
        _spectrumPlot.Cursor = hover.Vm is null ? null : Cursors.SizeWE;
    }

    private void IntegrationResize_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_spectrumPlot is null) return;
        if (_isIntegrationDragMode) return;
        if (_isIntegrationResizing) return;
        if (_isManualLambdaMaxAddMode) return;

        var pos = e.GetPosition(_spectrumPlot);
        var (vm, isLeft) = FindIntegrationEdgeAt(pos);
        if (vm is null) return;

        _isIntegrationResizing = true;
        _integrationResizeTargetVm = vm;
        _integrationResizeIsLeftEdge = isLeft;
        _integrationResizeOriginalText = isLeft ? vm.XMinText : vm.XMaxText;

        _spectrumPlot.Cursor = Cursors.SizeWE;
        _spectrumPlot.CaptureMouse();

        var side = isLeft ? "X Min" : "X Max";
        SetStatus($"「{vm.Label}」の {side} をドラッグ中（Esc / 右クリックで取消）", false);

        // Suppress ScottPlot's default pan-on-drag for this gesture.
        e.Handled = true;
    }

    private void IntegrationResize_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isIntegrationResizing) return;

        var label = _integrationResizeTargetVm?.Label;

        if (_spectrumPlot is { IsMouseCaptured: true })
        {
            _spectrumPlot.ReleaseMouseCapture();
        }

        _isIntegrationResizing = false;
        _integrationResizeTargetVm = null;
        _integrationResizeOriginalText = null;

        if (_spectrumPlot is not null)
        {
            _spectrumPlot.Cursor = null;
        }

        if (label is not null)
        {
            SetStatus($"「{label}」の範囲を更新しました", false);
        }

        e.Handled = true;
    }

    private void IntegrationResize_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isIntegrationResizing) return;

        CancelIntegrationResize();
        e.Handled = true;
    }

    private void IntegrationResize_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isIntegrationResizing) return;
        if (e.Key != Key.Escape) return;

        CancelIntegrationResize();
        e.Handled = true;
    }

    private void CancelIntegrationResize()
    {
        if (_integrationResizeTargetVm is not null && _integrationResizeOriginalText is not null)
        {
            // Roll the dragged edge back to where the gesture started.
            if (_integrationResizeIsLeftEdge)
            {
                _integrationResizeTargetVm.XMinText = _integrationResizeOriginalText;
            }
            else
            {
                _integrationResizeTargetVm.XMaxText = _integrationResizeOriginalText;
            }
        }

        if (_spectrumPlot is { IsMouseCaptured: true })
        {
            _spectrumPlot.ReleaseMouseCapture();
        }

        _isIntegrationResizing = false;
        _integrationResizeTargetVm = null;
        _integrationResizeOriginalText = null;

        if (_spectrumPlot is not null)
        {
            _spectrumPlot.Cursor = null;
        }

        SetStatus("ドラッグ操作を取り消しました", false);
    }

    // -------------- Manual λmax markers (click-to-add) --------------

    /// <summary>
    /// View-model wrapper for a manually-added λmax marker. Immutable; the
    /// list is rebuilt rather than mutated in place because the underlying
    /// model has no editable fields.
    /// </summary>
    private sealed class ManualLambdaMaxEntryVm
    {
        public required string DatasetKey { get; init; }
        public required double WavelengthNm { get; init; }
        public required string DisplayName { get; init; }

        public string DisplayText => string.Create(
            CultureInfo.InvariantCulture,
            $"{DisplayName}: λmax = {WavelengthNm:0.#} nm");

        public ManualLambdaMaxEntry ToModel() => new()
        {
            DatasetKey = DatasetKey,
            WavelengthNm = WavelengthNm,
        };
    }

    /// <summary>
    /// Stable per-dataset key, mirroring the calibration window's scheme so
    /// renames / reorderings round-trip through session files. Title takes
    /// precedence over the source path so a user-renamed dataset keeps its
    /// markers when the file is moved.
    /// </summary>
    private static string BuildLambdaMaxDatasetKey(SpectrumDataset dataset, int index)
    {
        if (!string.IsNullOrWhiteSpace(dataset.Title)) return dataset.Title!;
        if (!string.IsNullOrWhiteSpace(dataset.SourceFilePath)) return dataset.SourceFilePath!;
        return $"dataset#{index}";
    }

    private string ResolveDisplayNameForDatasetKey(string datasetKey)
    {
        for (var i = 0; i < _loadedDatasets.Count; i++)
        {
            var ds = _loadedDatasets[i];
            if (string.Equals(BuildLambdaMaxDatasetKey(ds, i), datasetKey, StringComparison.Ordinal))
            {
                return GetCustomLegendName(i)
                    ?? Path.GetFileNameWithoutExtension(ds.SourceFilePath)
                    ?? $"dataset {i + 1}";
            }
        }

        // Fallback for entries restored from a saved session before the
        // matching file is opened: take the filename portion of the key
        // when it looks like a path, otherwise the key itself.
        try
        {
            var name = Path.GetFileNameWithoutExtension(datasetKey);
            return string.IsNullOrWhiteSpace(name) ? datasetKey : name;
        }
        catch (ArgumentException)
        {
            return datasetKey;
        }
    }

    private void ApplyManualLambdaMaxEntries(IList<ManualLambdaMaxEntry>? entries)
    {
        _manualLambdaMaxEntryVms.Clear();
        if (entries is null)
        {
            UpdateManualLambdaMaxEmptyVisibility();
            return;
        }

        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.DatasetKey)) continue;
            if (!double.IsFinite(entry.WavelengthNm)) continue;

            _manualLambdaMaxEntryVms.Add(new ManualLambdaMaxEntryVm
            {
                DatasetKey = entry.DatasetKey,
                WavelengthNm = entry.WavelengthNm,
                DisplayName = ResolveDisplayNameForDatasetKey(entry.DatasetKey),
            });
        }

        UpdateManualLambdaMaxEmptyVisibility();
    }

    private void UpdateManualLambdaMaxEmptyVisibility()
    {
        ManualLambdaMaxEmptyTextBlock.Visibility = _manualLambdaMaxEntryVms.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearManualLambdaMaxButton.IsEnabled = _manualLambdaMaxEntryVms.Count > 0;
    }

    private void AddManualLambdaMaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isManualLambdaMaxAddMode)
        {
            ExitManualLambdaMaxAddMode(canceled: true);
            return;
        }

        EnterManualLambdaMaxAddMode();
    }

    private void EnterManualLambdaMaxAddMode()
    {
        if (_spectrumPlot is null) return;
        if (_currentDataset is null || !_currentDataset.IsWavelengthScan)
        {
            SetStatus("波長スキャンを選択してから手動 λmax を追加してください", true);
            return;
        }
        if (_isIntegrationDragMode || _isIntegrationResizing) return;

        _isManualLambdaMaxAddMode = true;
        _spectrumPlot.Cursor = Cursors.Cross;
        _spectrumPlot.PreviewMouseLeftButtonDown += ManualLambdaMaxAdd_PreviewMouseLeftButtonDown;
        _spectrumPlot.PreviewMouseRightButtonDown += ManualLambdaMaxAdd_PreviewMouseRightButtonDown;
        PreviewKeyDown += ManualLambdaMaxAdd_PreviewKeyDown;

        AddManualLambdaMaxButton.Content = "✕ クリック取消";
        SetStatus("グラフ上の λmax 位置をクリック（Esc / 右クリック / 同ボタン再押下でキャンセル）", false);
    }

    private void ExitManualLambdaMaxAddMode(bool canceled)
    {
        if (!_isManualLambdaMaxAddMode || _spectrumPlot is null) return;

        _isManualLambdaMaxAddMode = false;
        _spectrumPlot.Cursor = null;
        _spectrumPlot.PreviewMouseLeftButtonDown -= ManualLambdaMaxAdd_PreviewMouseLeftButtonDown;
        _spectrumPlot.PreviewMouseRightButtonDown -= ManualLambdaMaxAdd_PreviewMouseRightButtonDown;
        PreviewKeyDown -= ManualLambdaMaxAdd_PreviewKeyDown;

        AddManualLambdaMaxButton.Content = "+ クリックで追加";
        if (canceled)
        {
            SetStatus("手動 λmax 追加をキャンセルしました", false);
        }
    }

    private void ManualLambdaMaxAdd_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_spectrumPlot is null || _currentDataset is null)
        {
            ExitManualLambdaMaxAddMode(canceled: true);
            return;
        }

        var pos = e.GetPosition(_spectrumPlot);
        var coord = _spectrumPlot.Plot.GetCoordinates(
            new ScottPlot.Pixel((float)pos.X, (float)pos.Y));
        if (!double.IsFinite(coord.X))
        {
            ExitManualLambdaMaxAddMode(canceled: true);
            e.Handled = true;
            return;
        }

        var refined = LambdaMaxFinder.RefineManualPeak(_currentDataset, coord.X);
        if (refined is null)
        {
            SetStatus("クリック位置の近傍に有効なデータ点がありませんでした", true);
            ExitManualLambdaMaxAddMode(canceled: true);
            e.Handled = true;
            return;
        }

        var datasetIndex = _loadedDatasets.IndexOf(_currentDataset);
        if (datasetIndex < 0) datasetIndex = _activeIndex;

        var key = BuildLambdaMaxDatasetKey(_currentDataset, datasetIndex);
        var displayName = (datasetIndex >= 0 ? GetCustomLegendName(datasetIndex) : null)
            ?? Path.GetFileNameWithoutExtension(_currentDataset.SourceFilePath)
            ?? $"dataset {Math.Max(0, datasetIndex) + 1}";

        // De-duplicate against existing entries within ±0.05 nm so repeated
        // clicks on the same peak don't stack.
        var existing = _manualLambdaMaxEntryVms.FirstOrDefault(vm =>
            string.Equals(vm.DatasetKey, key, StringComparison.Ordinal)
            && Math.Abs(vm.WavelengthNm - refined.WavelengthNm) < 0.05);
        if (existing is null)
        {
            _manualLambdaMaxEntryVms.Add(new ManualLambdaMaxEntryVm
            {
                DatasetKey = key,
                WavelengthNm = refined.WavelengthNm,
                DisplayName = displayName,
            });
            UpdateManualLambdaMaxEmptyVisibility();
            SetStatus(
                string.Create(CultureInfo.InvariantCulture,
                    $"手動 λmax を {refined.WavelengthNm:0.#} nm に追加しました"),
                false);
            SchedulePlotCurrentDataset();
        }
        else
        {
            SetStatus("近接位置に既に手動マーカーがあるため追加をスキップしました", false);
        }

        ExitManualLambdaMaxAddMode(canceled: false);
        e.Handled = true;
    }

    private void ManualLambdaMaxAdd_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ExitManualLambdaMaxAddMode(canceled: true);
        e.Handled = true;
    }

    private void ManualLambdaMaxAdd_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ExitManualLambdaMaxAddMode(canceled: true);
        e.Handled = true;
    }

    private void RemoveManualLambdaMaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManualLambdaMaxEntryVm vm }) return;
        _manualLambdaMaxEntryVms.Remove(vm);
        UpdateManualLambdaMaxEmptyVisibility();
        SchedulePlotCurrentDataset();
    }

    private void ClearManualLambdaMaxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_manualLambdaMaxEntryVms.Count == 0) return;
        _manualLambdaMaxEntryVms.Clear();
        UpdateManualLambdaMaxEmptyVisibility();
        SetStatus("手動 λmax マーカーをすべて削除しました", false);
        SchedulePlotCurrentDataset();
    }

    private (IntegrationRegionVm? Vm, bool IsLeft) FindIntegrationEdgeAt(Point pos)
    {
        if (_spectrumPlot is null || _integrationRegionVms.Count == 0)
        {
            return (null, false);
        }

        IntegrationRegionVm? bestVm = null;
        var bestIsLeft = false;
        var bestDist = double.MaxValue;

        foreach (var vm in _integrationRegionVms)
        {
            if (!TryParseDouble(vm.XMinText, out var xMin)
                || !TryParseDouble(vm.XMaxText, out var xMax))
            {
                continue;
            }

            var pixelMin = _spectrumPlot.Plot.GetPixel(new ScottPlot.Coordinates(xMin, 0));
            var pixelMax = _spectrumPlot.Plot.GetPixel(new ScottPlot.Coordinates(xMax, 0));

            var dLeft = Math.Abs(pos.X - pixelMin.X);
            var dRight = Math.Abs(pos.X - pixelMax.X);

            if (dLeft <= IntegrationEdgeHitTolerancePixels && dLeft < bestDist)
            {
                bestVm = vm;
                bestIsLeft = true;
                bestDist = dLeft;
            }
            if (dRight <= IntegrationEdgeHitTolerancePixels && dRight < bestDist)
            {
                bestVm = vm;
                bestIsLeft = false;
                bestDist = dRight;
            }
        }

        return (bestVm, bestIsLeft);
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
        if (_isIntegrationResizing)
        {
            // Bypass the 200 ms debounce so the band visibly tracks the
            // mouse while the user is dragging an edge — without this the
            // rectangle only catches up after the cursor briefly stops.
            PlotCurrentDataset();
        }
        else
        {
            SchedulePlotCurrentDataset();
        }
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

        // Reflect legend visibility / position immediately. The debounced
        // SchedulePlotCurrentDataset redraws every series and would also
        // call ApplyLegend, but the user-visible delay is jarring for a
        // simple combo-box toggle; this short path makes the change feel
        // live while the heavy redraw catches up.
        ApplyLegend(_spectrumPlot.Plot, CaptureFormattingConfigFromControls(),
            autoShow: _currentLegendAutoShow);
        _spectrumPlot.Refresh();

        SchedulePlotCurrentDataset();
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
                RubberBandSegmentsText = region.RubberBandSegments.ToString(CultureInfo.InvariantCulture),
                PolynomialOrderText = region.PolynomialOrder.ToString(CultureInfo.InvariantCulture),
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

        UpdateLambdaMaxUi(dataset);
        UpdateCloudPointUi(dataset);
        UpdateMetadataUi(dataset);
    }

    private void UpdateLambdaMaxUi(SpectrumDataset? dataset)
    {
        var hasWavelengthScan = AnyDatasetMatches(static d => d.IsWavelengthScan)
                                || dataset?.IsWavelengthScan == true;
        ShowLambdaMaxCheckBox.IsEnabled = hasWavelengthScan;
        LambdaMaxMinAbsorbanceTextBox.IsEnabled = hasWavelengthScan;
        LambdaMaxCountTextBox.IsEnabled = hasWavelengthScan;
        LambdaMaxHintTextBlock.Visibility = hasWavelengthScan
            ? Visibility.Collapsed
            : Visibility.Visible;

        var canAddManual = hasWavelengthScan && _currentDataset?.IsWavelengthScan == true;
        AddManualLambdaMaxButton.IsEnabled = canAddManual || _isManualLambdaMaxAddMode;
        ClearManualLambdaMaxButton.IsEnabled = _manualLambdaMaxEntryVms.Count > 0;
        ManualLambdaMaxItemsControl.IsEnabled = hasWavelengthScan;

        // If the user switched to a non-wavelength-scan dataset while the
        // click-to-add mode was armed, bail out to avoid stale state.
        if (_isManualLambdaMaxAddMode && !canAddManual)
        {
            ExitManualLambdaMaxAddMode(canceled: true);
        }
    }

    private void UpdateCloudPointUi(SpectrumDataset? dataset)
    {
        var hasTemperatureScan = AnyDatasetMatches(static d => d.IsTemperatureScan)
                                 || dataset?.IsTemperatureScan == true;
        ShowCloudPointCheckBox.IsEnabled = hasTemperatureScan;
        CloudPointMethodComboBox.IsEnabled = hasTemperatureScan;
        CloudPointThresholdTextBox.IsEnabled = hasTemperatureScan;
        CloudPointHintTextBlock.Visibility = hasTemperatureScan
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (!hasTemperatureScan || ShowCloudPointCheckBox.IsChecked != true)
        {
            CloudPointResultTextBlock.Text = string.Empty;
            CloudPointResultTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMetadataUi(SpectrumDataset? dataset)
    {
        // Footer metadata only shows up on JASCO temperature scans (the
        // only file class we actively parse the Shift-JIS footer for).
        var hasTemperatureScan = AnyDatasetMatches(static d => d.IsTemperatureScan)
                                 || dataset?.IsTemperatureScan == true;
        ShowMetadataCheckBox.IsEnabled = hasTemperatureScan;
        MetadataHintTextBlock.Visibility = hasTemperatureScan
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private bool AnyDatasetMatches(Func<SpectrumDataset, bool> predicate)
    {
        for (var i = 0; i < _loadedDatasets.Count; i++)
        {
            if (predicate(_loadedDatasets[i])) return true;
        }
        return false;
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

    /// <summary>
    /// Faint per-dataset chord drawn from (XMin, Y(XMin)) to (XMax, Y(XMax))
    /// for every Linear-baseline region. Y values are sampled in the *displayed*
    /// space so the chord visually rests on the curve at both endpoints, even
    /// when the actual integration is computed in Absorbance space.
    /// </summary>
    private void DrawIntegrationBaselines(
        (SpectrumDataset Dataset, int Index)[] plotEntries,
        YAxisDisplayMode yDisplayMode)
    {
        if (_spectrumPlot is null || _integrationRegionVms.Count == 0 || plotEntries.Length == 0)
        {
            return;
        }

        var regions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .Where(region => region is not null && region.BaselineMethod != BaselineMethod.None)
            .Cast<IntegrationRegion>()
            .ToArray();

        if (regions.Length == 0)
        {
            return;
        }

        foreach (var (dataset, datasetIndex) in plotEntries)
        {
            // Skip datasets that the integrator itself would refuse — drawing a
            // baseline for them would imply an area we cannot actually compute.
            if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance))
            {
                continue;
            }

            var xs = dataset.XValues;
            if (xs.Length < 2)
            {
                continue;
            }

            var displayYs = SpectrumYAxisConverter.GetDisplayYValues(dataset, yDisplayMode);
            var datasetColor = ResolveDatasetColor(datasetIndex);

            foreach (var region in regions)
            {
                if (region.XMin < xs[0] || region.XMax > xs[^1])
                {
                    continue;
                }

                // Linear keeps the cheap chord; everything else samples the
                // actual baseline curve from the integrator (Absorbance space)
                // and converts to the active display space before plotting.
                if (region.BaselineMethod == BaselineMethod.Linear)
                {
                    var yAtMin = InterpolateY(xs, displayYs, region.XMin);
                    var yAtMax = InterpolateY(xs, displayYs, region.XMax);
                    if (yAtMin is null || yAtMax is null)
                    {
                        continue;
                    }

                    var line = _spectrumPlot.Plot.Add.Line(region.XMin, yAtMin.Value, region.XMax, yAtMax.Value);
                    line.LineStyle.Color = datasetColor.WithAlpha((byte)110);
                    line.LineStyle.Width = 1;
                    line.LineStyle.Pattern = ScottPlot.LinePattern.Solid;
                    line.MarkerStyle.IsVisible = false;
                    continue;
                }

                var curve = SpectrumIntegrator.BuildBaselineCurve(dataset, region);
                if (curve is null)
                {
                    continue;
                }

                var (gridX, baselineY) = curve.Value;
                var displayBaselineY = ConvertAbsorbanceBaselineToDisplay(baselineY, yDisplayMode);

                var scatter = _spectrumPlot.Plot.Add.Scatter(gridX, displayBaselineY);
                scatter.LineStyle.Color = datasetColor.WithAlpha((byte)110);
                scatter.LineStyle.Width = 1;
                scatter.LineStyle.Pattern = ScottPlot.LinePattern.Solid;
                scatter.MarkerStyle.IsVisible = false;
                scatter.LegendText = string.Empty;
            }
        }
    }

    /// <summary>
    /// Convert a baseline Y array (always in Absorbance space, as
    /// <see cref="SpectrumIntegrator.BuildBaselineCurve"/> returns it) into
    /// the currently displayed Y axis space so the plot overlay sits on the
    /// curve regardless of A / T toggle. Native mode is treated as A — the
    /// integrator only emits a curve when the dataset is A-compatible.
    /// </summary>
    private static double[] ConvertAbsorbanceBaselineToDisplay(double[] absorbanceY, YAxisDisplayMode displayMode)
    {
        if (displayMode != YAxisDisplayMode.Transmittance)
        {
            return absorbanceY;
        }

        var result = new double[absorbanceY.Length];
        for (var i = 0; i < absorbanceY.Length; i++)
        {
            result[i] = SpectrumYAxisConverter.AbsorbanceToTransmittancePercent(absorbanceY[i]);
        }

        return result;
    }

    private ScottPlot.Color ResolveDatasetColor(int datasetIndex)
    {
        string? hex = null;
        if (datasetIndex >= 0 && datasetIndex < _datasetStyles.Count)
        {
            hex = _datasetStyles[datasetIndex].ColorHex;
        }

        hex ??= AutoLineColors[Math.Max(0, datasetIndex) % AutoLineColors.Length];
        return ScottPlot.Color.FromHex(new[] { hex }).First();
    }

    private static double? InterpolateY(double[] xs, double[] ys, double x)
    {
        if (xs.Length < 2 || ys.Length != xs.Length || x < xs[0] || x > xs[^1])
        {
            return null;
        }

        var lo = 0;
        var hi = xs.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        if (xs[lo] == x) return ys[lo];
        if (xs[hi] == x) return ys[hi];

        var t = (x - xs[lo]) / (xs[hi] - xs[lo]);
        var y = ys[lo] + t * (ys[hi] - ys[lo]);
        return double.IsFinite(y) ? y : null;
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

    private LambdaMaxFinderConfig BuildLambdaMaxConfig()
    {
        var minAbs = TryParseNonNegativeDouble(LambdaMaxMinAbsorbanceTextBox.Text, out var parsed)
            ? parsed
            : 0.05;
        var maxCount = int.TryParse(LambdaMaxCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c >= 0
            ? c
            : 3;
        return new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = minAbs,
            MaxPeaks = maxCount,
            Window = 3,
        };
    }

    private CloudPointDetectionConfig BuildCloudPointConfig()
    {
        var threshold = TryParseNonNegativeDouble(CloudPointThresholdTextBox.Text, out var parsed)
            ? parsed
            : 50.0;
        var method = GetSelectedCloudPointMethod();
        return new CloudPointDetectionConfig
        {
            Method = method,
            TransmittanceThresholdPercent = threshold,
            SmoothingWindow = 3,
        };
    }

    private CloudPointMethod GetSelectedCloudPointMethod()
    {
        return GetSelectedComboBoxTag(CloudPointMethodComboBox) switch
        {
            "FirstDerivativePeak" => CloudPointMethod.FirstDerivativePeak,
            "SecondDerivativeExtremum" => CloudPointMethod.SecondDerivativeExtremum,
            "SigmoidFit" => CloudPointMethod.SigmoidFit,
            _ => CloudPointMethod.Midpoint,
        };
    }

    private string? GetSelectedCloudPointMethodConfigValue()
    {
        var tag = GetSelectedComboBoxTag(CloudPointMethodComboBox);
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private void DrawLambdaMaxMarkers(
        (SpectrumDataset Dataset, int Index)[] plotEntries,
        AxisDataRange yRange)
    {
        if (_spectrumPlot is null
            || ShowLambdaMaxCheckBox.IsChecked != true
            || plotEntries.Length == 0
            || !yRange.HasValue)
        {
            return;
        }

        var config = BuildLambdaMaxConfig();
        var axisLimits = _spectrumPlot.Plot.Axes.GetLimits();
        var ySpan = axisLimits.Top - axisLimits.Bottom;
        var labelOffset = ySpan > 0 ? ySpan * 0.04 : 0.05;
        var yDisplayMode = GetSelectedYAxisDisplayMode();

        foreach (var (dataset, datasetIndex) in plotEntries)
        {
            if (!dataset.IsWavelengthScan) continue;

            var color = ResolveDatasetColor(datasetIndex);
            var displayYs = SpectrumYAxisConverter.GetDisplayYValues(dataset, yDisplayMode);
            var xs = dataset.XValues;

            // -- Auto-detected peaks --
            var peaks = LambdaMaxFinder.Find(dataset, config);
            foreach (var peak in peaks)
            {
                if (!peak.HasResult) continue;

                // Marker the data-point Y in the *displayed* unit so the
                // dot sits visually on the curve even when the user picked
                // Transmittance display.
                var displayY = peak.SampleIndex >= 0 && peak.SampleIndex < displayYs.Length
                    ? displayYs[peak.SampleIndex]
                    : double.NaN;
                if (!double.IsFinite(displayY)) continue;

                DrawLambdaMaxMarker(
                    peak.WavelengthNm, displayY, color,
                    isManual: false, axisLimits, labelOffset);
            }

            // -- Manual markers --
            // Resolve Y by nearest-neighbour index lookup rather than
            // InterpolateY, because that helper assumes ascending X but
            // JASCO wavelength scans may be sampled either way.
            var datasetKey = BuildLambdaMaxDatasetKey(dataset, datasetIndex);
            foreach (var vm in _manualLambdaMaxEntryVms)
            {
                if (!string.Equals(vm.DatasetKey, datasetKey, StringComparison.Ordinal)) continue;
                if (xs.Length == 0) continue;

                var nearest = -1;
                var bestDist = double.PositiveInfinity;
                for (var i = 0; i < xs.Length; i++)
                {
                    var d = Math.Abs(xs[i] - vm.WavelengthNm);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        nearest = i;
                    }
                }
                if (nearest < 0) continue;
                var manualY = displayYs[nearest];
                if (!double.IsFinite(manualY)) continue;

                DrawLambdaMaxMarker(
                    vm.WavelengthNm, manualY, color,
                    isManual: true, axisLimits, labelOffset);
            }
        }
    }

    private void DrawLambdaMaxMarker(
        double wavelengthNm,
        double displayY,
        ScottPlot.Color color,
        bool isManual,
        ScottPlot.AxisLimits axisLimits,
        double labelOffset)
    {
        if (_spectrumPlot is null) return;

        var line = _spectrumPlot.Plot.Add.VerticalLine(wavelengthNm);
        line.LineStyle.Color = color.WithAlpha((byte)170);
        line.LineStyle.Pattern = isManual ? ScottPlot.LinePattern.Dashed : ScottPlot.LinePattern.Dotted;
        line.LineStyle.Width = 1;
        line.LegendText = string.Empty;

        var marker = _spectrumPlot.Plot.Add.Marker(wavelengthNm, displayY);
        // Manual markers use a filled triangle in the dataset colour so the
        // user can tell their click-added points apart from auto-detected
        // peaks at a glance.
        marker.MarkerStyle.Shape = isManual
            ? ScottPlot.MarkerShape.FilledTriangleDown
            : ScottPlot.MarkerShape.OpenTriangleDown;
        marker.MarkerStyle.Size = 8;
        marker.MarkerStyle.LineColor = color;
        marker.MarkerStyle.LineWidth = 1.5f;
        marker.MarkerStyle.FillColor = isManual ? color : ScottPlot.Colors.White;
        marker.LegendText = string.Empty;

        var labelText = isManual
            ? string.Create(CultureInfo.InvariantCulture, $"λmax = {wavelengthNm:0.#} nm (手動)")
            : string.Create(CultureInfo.InvariantCulture, $"λmax = {wavelengthNm:0.#} nm");
        var labelY = displayY + labelOffset;
        if (labelY > axisLimits.Top) labelY = displayY - labelOffset;

        var text = _spectrumPlot.Plot.Add.Text(labelText, wavelengthNm, labelY);
        text.LabelFontColor = color;
        text.LabelFontSize = 10;
        text.LabelAlignment = ScottPlot.Alignment.LowerCenter;
        text.LabelBold = false;
    }

    private void DrawCloudPointMarkers(
        (SpectrumDataset Dataset, int Index)[] plotEntries,
        AxisDataRange yRange)
    {
        CloudPointResultTextBlock.Visibility = Visibility.Collapsed;
        CloudPointResultTextBlock.Text = string.Empty;

        if (_spectrumPlot is null
            || ShowCloudPointCheckBox.IsChecked != true
            || plotEntries.Length == 0
            || !yRange.HasValue)
        {
            return;
        }

        var config = BuildCloudPointConfig();
        var axisLimits = _spectrumPlot.Plot.Axes.GetLimits();
        var ySpan = axisLimits.Top - axisLimits.Bottom;
        var yPad = ySpan > 0 ? ySpan * 100.0 : 1.0;
        var labelY = ySpan > 0 ? axisLimits.Top - ySpan * 0.05 : axisLimits.Top;
        var yDisplayMode = GetSelectedYAxisDisplayMode();

        var rows = new List<(SpectrumDataset Dataset, int Index, CloudPointResult Result, string DisplayName)>();
        foreach (var (dataset, datasetIndex) in plotEntries)
        {
            if (!dataset.IsTemperatureScan) continue;

            var result = CloudPointDetector.Detect(dataset, config);
            if (!result.HasResult) continue;

            var displayName = GetCustomLegendName(datasetIndex)
                ?? Path.GetFileNameWithoutExtension(dataset.SourceFilePath)
                ?? $"dataset {datasetIndex + 1}";
            rows.Add((dataset, datasetIndex, result, displayName));

            var color = ResolveDatasetColor(datasetIndex);
            var line = _spectrumPlot.Plot.Add.VerticalLine(result.TemperatureCelsius);
            line.LineStyle.Color = color.WithAlpha((byte)200);
            line.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
            line.LineStyle.Width = 1.5f;
            line.LegendText = string.Empty;

            // Marker on the curve at the detected Y (display space).
            var displayYs = SpectrumYAxisConverter.GetDisplayYValues(dataset, yDisplayMode);
            var markerY = InterpolateY(dataset.XValues, displayYs, result.TemperatureCelsius);
            if (markerY is { } my && double.IsFinite(my))
            {
                var marker = _spectrumPlot.Plot.Add.Marker(result.TemperatureCelsius, my);
                marker.MarkerStyle.Shape = ScottPlot.MarkerShape.FilledCircle;
                marker.MarkerStyle.Size = 7;
                marker.MarkerStyle.LineColor = color;
                marker.MarkerStyle.FillColor = color;
                marker.LegendText = string.Empty;
            }

            // Sigmoid fit: optionally overlay the fitted Boltzmann curve as a
            // dashed line in the dataset's colour. The fit returns predicted
            // Y values in transmittance %; convert back to the active display
            // mode so the overlay sits on top of the actual curve regardless
            // of the user's A↔T selection.
            if (result.Method == CloudPointMethod.SigmoidFit
                && result.FittedCurve is { } fit
                && ShowSigmoidFitCurveCheckBox.IsChecked == true)
            {
                var fitXs = dataset.XValues;
                if (fit.Count == fitXs.Length)
                {
                    var fittedDisplay = ConvertTransmittancePredictionToDisplay(dataset, fit, yDisplayMode);
                    var (cleanX, cleanY) = StripNonFinite(fitXs, fittedDisplay);
                    if (cleanX.Length >= 2)
                    {
                        var scatter = _spectrumPlot.Plot.Add.Scatter(cleanX, cleanY);
                        scatter.LineStyle.Color = color.WithAlpha((byte)180);
                        scatter.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
                        scatter.LineStyle.Width = 1.5f;
                        scatter.MarkerStyle.IsVisible = false;
                        scatter.LegendText = string.Empty;
                    }
                }
            }

            var labelText = string.Create(
                CultureInfo.InvariantCulture,
                $"Tc = {result.TemperatureCelsius:0.0} °C");
            var text = _spectrumPlot.Plot.Add.Text(labelText, result.TemperatureCelsius, labelY);
            text.LabelFontColor = color;
            text.LabelFontSize = 10;
            text.LabelAlignment = ScottPlot.Alignment.UpperCenter;
        }

        if (rows.Count == 0)
        {
            return;
        }

        // Result panel: per-dataset Tc + ΔT when both heating and cooling present.
        var lines = new List<string>(rows.Count + 1);
        foreach (var (_, _, result, name) in rows)
        {
            var dirLabel = result.Direction switch
            {
                ScanDirection.Heating => "昇温",
                ScanDirection.Cooling => "降温",
                _ => "方向不明",
            };
            var methodLabel = result.Method switch
            {
                CloudPointMethod.Midpoint => $"中点法 T={result.TransmittancePercentAtTc:0.#}%",
                CloudPointMethod.FirstDerivativePeak => "1次微分極大",
                CloudPointMethod.SecondDerivativeExtremum => "2次微分極大（オンセット）",
                CloudPointMethod.SigmoidFit => "シグモイドfit",
                _ => result.Method.ToString(),
            };
            var baseLine = string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1}, {2}): Tc = {3:0.00} °C",
                name, dirLabel, methodLabel, result.TemperatureCelsius);
            // For sigmoid fits, optionally append the slope k (in °C, with
            // sign matching the fit direction) and R² so the user can sanity-
            // check the fit quality from the analysis panel.
            if (result.Method == CloudPointMethod.SigmoidFit
                && ShowSigmoidFitParametersCheckBox.IsChecked == true
                && result.KSlopeCelsius is { } slope
                && result.RSquared is { } rsq)
            {
                baseLine += string.Format(
                    CultureInfo.InvariantCulture,
                    ", k = {0:0.00} °C, R² = {1:0.000}",
                    slope, rsq);
            }
            lines.Add(baseLine);
        }

        var heating = rows
            .Where(r => r.Result.Direction == ScanDirection.Heating)
            .Select(r => r.Result)
            .FirstOrDefault();
        var cooling = rows
            .Where(r => r.Result.Direction == ScanDirection.Cooling)
            .Select(r => r.Result)
            .FirstOrDefault();
        var delta = HysteresisAnalyzer.ComputeHysteresis(heating, cooling);
        if (double.IsFinite(delta))
        {
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "ヒステリシス ΔT = Tc(降温) − Tc(昇温) = {0:+0.00;-0.00;0.00} °C",
                delta));
        }

        CloudPointResultTextBlock.Text = string.Join(Environment.NewLine, lines);
        CloudPointResultTextBlock.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Translate the sigmoid fit's transmittance-% predictions into whatever
    /// Y unit the plot is currently rendering in, so the overlay sits flush
    /// on the actual data curve.
    /// </summary>
    private static double[] ConvertTransmittancePredictionToDisplay(
        SpectrumDataset dataset,
        IReadOnlyList<double> transmittancePercent,
        YAxisDisplayMode mode)
    {
        var targetMode = mode == YAxisDisplayMode.Native
            ? (dataset.IsAbsorbanceY ? YAxisDisplayMode.Absorbance : YAxisDisplayMode.Transmittance)
            : mode;

        var n = transmittancePercent.Count;
        var result = new double[n];
        if (targetMode == YAxisDisplayMode.Absorbance)
        {
            for (var i = 0; i < n; i++)
            {
                result[i] = SpectrumYAxisConverter.TransmittancePercentToAbsorbance(transmittancePercent[i]);
            }
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                result[i] = transmittancePercent[i];
            }
        }
        return result;
    }

    /// <summary>
    /// Drop any (x, y) pairs where either coordinate is non-finite (NaN /
    /// ±∞). ScottPlot's Scatter renderer otherwise draws spikes through the
    /// gaps.
    /// </summary>
    private static (double[] X, double[] Y) StripNonFinite(double[] xs, double[] ys)
    {
        var n = Math.Min(xs.Length, ys.Length);
        var bx = new List<double>(n);
        var by = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            if (double.IsFinite(xs[i]) && double.IsFinite(ys[i]))
            {
                bx.Add(xs[i]);
                by.Add(ys[i]);
            }
        }
        return (bx.ToArray(), by.ToArray());
    }

    private void DrawMetadataAnnotation((SpectrumDataset Dataset, int Index)[] plotEntries)
    {
        if (_spectrumPlot is null
            || ShowMetadataCheckBox.IsChecked != true
            || plotEntries.Length == 0)
        {
            return;
        }

        // Surface metadata only for temperature scans (the only file class
        // whose Shift-JIS footer we currently parse). Use the first matching
        // dataset; in heating/cooling pairs the instrument settings are
        // identical so this is rarely surprising.
        var dataset = plotEntries.Select(e => e.Dataset).FirstOrDefault(d => d.IsTemperatureScan);
        if (dataset is null) return;

        var lines = BuildMetadataLines(dataset);
        if (lines.Count == 0) return;

        var text = string.Join("\n", lines);
        var annotation = _spectrumPlot.Plot.Add.Annotation(text);
        annotation.Alignment = ScottPlot.Alignment.UpperRight;
        annotation.LabelFontSize = 10;
        // Pick a font that actually has Japanese glyphs. The user's main
        // plot font might be Arial (chosen for English axis labels), in
        // which case the annotation would render as tofu without this
        // override.
        annotation.LabelFontName = ScottPlot.Fonts.Detect(text);
        annotation.LabelFontColor = ScottPlot.Color.FromHex("#0F172A");
        annotation.LabelBackgroundColor = ScottPlot.Color.FromHex("#FFFFFF").WithAlpha((byte)220);
        annotation.LabelBorderColor = ScottPlot.Color.FromHex("#CBD5E1");
        annotation.LabelBorderWidth = 1;
        annotation.LabelPadding = 6;
        annotation.OffsetX = 8;
        annotation.OffsetY = 8;
    }

    private static List<string> BuildMetadataLines(SpectrumDataset dataset)
    {
        var lines = new List<string>(5);
        if (dataset.MeasurementWavelengthText is { } wavelength)
        {
            lines.Add($"測定波長: {wavelength}");
        }
        if (dataset.TemperatureRampRateText is { } ramp)
        {
            lines.Add($"温度勾配: {ramp}");
        }
        if (dataset.AccessoryName is { } accessory)
        {
            lines.Add($"付属品: {accessory}");
        }
        if (dataset.BandPassText is { } bandpass)
        {
            lines.Add($"バンド幅: {bandpass}");
        }
        if (dataset.PhotometricMode is { } mode)
        {
            lines.Add($"測光モード: {mode}");
        }
        return lines;
    }

    private YAxisDisplayMode GetSelectedYAxisDisplayMode()
    {
        return AxisDisplayPanel.YAxisDisplayModeTag switch
        {
            "Absorbance" => YAxisDisplayMode.Absorbance,
            "Transmittance" => YAxisDisplayMode.Transmittance,
            _ => YAxisDisplayMode.Native,
        };
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

    private double? GetSelectedAspectRatio() => GraphFormatPanel.AspectRatioValue;

    private void UpdatePlotHostAspectRatio()
        => PlotHostAspectRatio.Apply(PlotHost, PlotContainerBorder, GetSelectedAspectRatio());

    private (int Width, int Height) GetExportImageSize()
        => GraphSaveHelpers.GetExportImageSize(GetSelectedAspectRatio());

    // ===== Beer-Lambert calibration curve =====

    private void OpenCalibrationEditorButton_Click(object sender, RoutedEventArgs e)
    {
        var datasets = BuildCalibrationDatasetInputs();
        if (datasets.Count < 2)
        {
            SetStatus("検量線の作成には 2 件以上のデータセットが必要です（重ね描きで複数読み込んでください）", true);
            return;
        }

        var regions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .OfType<IntegrationRegion>()
            .ToList();

        var window = new CalibrationCurveWindow(
            _formattingConfig.Calibration,
            datasets,
            regions,
            GetDefaultOutputDirectoryIfExists())
        {
            Owner = this,
        };

        if (window.ShowDialog() == true)
        {
            // Calibration confirmation is treated as an explicit "save as
            // default" by the existing UX (one click both updates the
            // current calibration AND persists it), so write through to
            // both the live config and the saved defaults snapshot.
            _formattingConfig.Calibration = window.ResultConfig;
            _formattingDefaults.Calibration = window.ResultConfig;
            try
            {
                SaveFormattingDefaults();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                SetStatus($"検量線設定を保存できませんでした: {ex.Message}", true);
            }

            UpdateCalibrationUi();
            SetStatus("検量線を更新しました", false);
        }
    }

    private void ExportCalibrationResultsButton_Click(object sender, RoutedEventArgs e)
    {
        var calibration = _formattingConfig.Calibration;
        if (calibration is null)
        {
            SetStatus("検量線が未設定です", true);
            return;
        }

        var datasets = BuildCalibrationDatasetInputs();
        if (datasets.Count == 0)
        {
            SetStatus("出力できるデータセットがありません", true);
            return;
        }

        var regions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .OfType<IntegrationRegion>()
            .ToList();

        var (result, exportRows) = ComputeCalibrationFit(calibration, datasets, regions);
        if (!result.HasFit)
        {
            SetStatus("フィット未確定（濃度を 2 件以上入力してください）", true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "検量線結果を保存",
            Filter = "Excelブック (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            FileName = "calibration_curve",
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var export = new CalibrationExport
        {
            Config = calibration,
            Result = result,
            Rows = exportRows,
        };

        try
        {
            var ext = Path.GetExtension(dialog.FileName);
            if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase))
            {
                export.WriteCsv(dialog.FileName);
            }
            else
            {
                export.WriteXlsx(dialog.FileName);
            }

            SetStatus($"検量線結果を保存しました: {dialog.FileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"保存に失敗しました: {ex.Message}", true);
        }
    }

    private void UpdateCalibrationUi()
    {
        var hasMinimumDatasets = _loadedDatasets.Count >= 2;
        OpenCalibrationEditorButton.IsEnabled = hasMinimumDatasets;
        CalibrationHintTextBlock.Visibility = hasMinimumDatasets ? Visibility.Collapsed : Visibility.Visible;

        var calibration = _formattingConfig.Calibration;
        if (calibration is null || _loadedDatasets.Count == 0)
        {
            CalibrationSummaryBorder.Visibility = Visibility.Collapsed;
            ExportCalibrationResultsButton.Visibility = Visibility.Collapsed;
            return;
        }

        var datasets = BuildCalibrationDatasetInputs();
        var regions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .OfType<IntegrationRegion>()
            .ToList();
        var (result, _) = ComputeCalibrationFit(calibration, datasets, regions);

        if (result.HasFit)
        {
            var lines = new List<string>
            {
                $"モード: {SpectrumQuantifier.GetSignalLabel(calibration)}",
            };

            if (calibration.Mode == CalibrationQuantificationMode.SingleWavelength)
            {
                var epsText = double.IsFinite(result.EpsilonPerCmPerMolar)
                    ? result.EpsilonPerCmPerMolar.ToString("0.000E+0", CultureInfo.InvariantCulture)
                    : "—";
                lines.Add($"ε = {epsText} M⁻¹·cm⁻¹  (l = {calibration.PathLengthCm.ToString("0.###", CultureInfo.InvariantCulture)} cm)");
            }
            else
            {
                lines.Add($"slope = {result.Slope.ToString("0.###E+0", CultureInfo.InvariantCulture)}");
            }

            if (calibration.FitMode == CalibrationFitMode.WithIntercept && double.IsFinite(result.Intercept))
            {
                lines.Add($"intercept = {result.Intercept.ToString("0.####", CultureInfo.InvariantCulture)}");
            }

            var rSquaredText = double.IsFinite(result.RSquared)
                ? result.RSquared.ToString("0.0000", CultureInfo.InvariantCulture)
                : "—";
            lines.Add($"R² = {rSquaredText}    N = {result.N}");

            CalibrationSummaryTextBlock.Text = string.Join("\n", lines);
            CalibrationSummaryBorder.Visibility = Visibility.Visible;
            ExportCalibrationResultsButton.Visibility = Visibility.Visible;
        }
        else
        {
            CalibrationSummaryTextBlock.Text = "検量線が未確定（エディタで濃度を 2 件以上割り当ててください）";
            CalibrationSummaryBorder.Visibility = Visibility.Visible;
            ExportCalibrationResultsButton.Visibility = Visibility.Collapsed;
        }
    }

    private (CalibrationResult Result, IReadOnlyList<CalibrationExportRow> Rows) ComputeCalibrationFit(
        CalibrationCurveConfig calibration,
        IReadOnlyList<CalibrationCurveWindow.CalibrationDatasetInput> datasets,
        IReadOnlyList<IntegrationRegion> regions)
    {
        var savedByKey = calibration.Samples
            .GroupBy(s => s.DatasetKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var inputs = new List<CalibrationFitInput>(datasets.Count);
        var unitConcentrations = new List<double?>(datasets.Count);
        foreach (var ds in datasets)
        {
            savedByKey.TryGetValue(ds.DatasetKey, out var saved);
            var unitValue = saved?.ConcentrationInUnit;
            var molarValue = unitValue is { } u
                ? CalibrationUnitConverter.ToMolar(u, calibration.ConcentrationUnit, calibration.MolarMass)
                : null;
            unitConcentrations.Add(unitValue);
            inputs.Add(new CalibrationFitInput
            {
                DatasetKey = ds.DatasetKey,
                DisplayName = ds.DisplayName,
                ConcentrationMolar = molarValue,
                Signal = SpectrumQuantifier.Quantify(ds.Dataset, calibration, regions),
                IsExcluded = saved?.IsExcluded ?? false,
            });
        }

        var result = CalibrationFitter.Fit(
            inputs,
            calibration.FitMode,
            calibration.Mode,
            calibration.PathLengthCm);

        var rows = new List<CalibrationExportRow>(datasets.Count);
        for (var i = 0; i < datasets.Count; i++)
        {
            rows.Add(new CalibrationExportRow
            {
                DatasetName = datasets[i].DisplayName,
                ConcentrationInUnit = unitConcentrations[i],
                ConcentrationMolar = result.Points[i].ConcentrationMolar,
                Signal = result.Points[i].Signal,
                Predicted = result.Points[i].Predicted,
                Residual = result.Points[i].Residual,
                IsExcluded = result.Points[i].IsExcluded,
            });
        }

        return (result, rows);
    }

    private IReadOnlyList<CalibrationCurveWindow.CalibrationDatasetInput> BuildCalibrationDatasetInputs()
    {
        var result = new List<CalibrationCurveWindow.CalibrationDatasetInput>(_loadedDatasets.Count);
        for (var i = 0; i < _loadedDatasets.Count; i++)
        {
            var dataset = _loadedDatasets[i];
            var displayName = GetCustomLegendName(i)
                ?? Path.GetFileNameWithoutExtension(dataset.SourceFilePath)
                ?? $"dataset {i + 1}";
            var key = BuildCalibrationDatasetKey(dataset, displayName, i);
            result.Add(new CalibrationCurveWindow.CalibrationDatasetInput
            {
                DatasetKey = key,
                DisplayName = displayName,
                Dataset = dataset,
            });
        }

        return result;
    }

    private static string BuildCalibrationDatasetKey(SpectrumDataset dataset, string displayName, int index)
    {
        // Stable key for round-tripping per-sample state through the
        // session file. Title is preferred (set explicitly by the user
        // when renaming a dataset); otherwise the source path; otherwise
        // a synthetic fallback so duplicates don't collapse onto one row.
        if (!string.IsNullOrWhiteSpace(dataset.Title))
        {
            return dataset.Title!;
        }

        if (!string.IsNullOrWhiteSpace(dataset.SourceFilePath))
        {
            return dataset.SourceFilePath!;
        }

        return $"{displayName}#{index}";
    }
}

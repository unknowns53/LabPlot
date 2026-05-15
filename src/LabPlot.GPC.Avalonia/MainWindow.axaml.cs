using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
// Avalonia.Controls.Shapes.Path は System.IO.Path と衝突するので、必要な型のみエイリアス参照する。
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using GpcAnalyzer.Core;
using LabPlot.Core;
using LabPlot.Core.Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;
using ScottPlot.Avalonia;
using static LabPlot.Core.PlotAppearance;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.GPC.Avalonia;

/// <summary>
/// Avalonia 版 GPC Visualization のメインウィンドウ。WPF 版
/// <c>GPC_Visualization.MainWindow</c> (3131 行) を Avalonia API に翻訳した本実装。
/// 主な置換規則は次の通り（DLS.Avalonia Batch 3b と同方針）。
/// <list type="bullet">
///   <item>SaveFileDialog / OpenFileDialog / OpenFolderDialog → IStorageProvider の
///     SaveFilePickerAsync / OpenFilePickerAsync / OpenFolderPickerAsync (全 async)</item>
///   <item>ScottPlot.WPF.WpfPlot → ScottPlot.Avalonia.AvaPlot</item>
///   <item>InputBindings + RoutedUICommand → OnKeyDown オーバーライドで集中ディスパッチ</item>
///   <item>Visibility.Visible / Collapsed → IsVisible (bool)</item>
///   <item>DataFormats.FileDrop の string[] → DataFormats.Files の IStorageItem 列挙</item>
///   <item>WPF Adorner ベースの InsertionAdorner → AXAML 上の InsertionLine sibling
///     を Margin / IsVisible で位置決めする方式に置換</item>
///   <item>System.Windows.Threading.DispatcherTimer → Avalonia.Threading.DispatcherTimer</item>
/// </list>
/// 凡例マウスドラッグは Phase 7 Batch 6 step 3 で
/// <see cref="LabPlot.Core.Avalonia.Helpers.LegendDragController"/> として移植済み。
/// 凡例位置 / オフセット は GraphFormatPanel + ドラッグ操作の双方から制御できる。
/// </summary>
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

    // WPF 版と同じ AppData サブパス。両者は formatting_config.json を共有する設計。
    private static readonly string FormattingConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GPC_Visualization",
        "formatting_config.json");

    private readonly List<GpcDataset> _loadedDatasets = new();
    private readonly List<DatasetStyle> _datasetStyles = new();
    private readonly List<string?> _datasetSelectedPeakIds = new();

    // 重ね書き時の Row2 ItemsControl が表示する entries のキャッシュ。各行 ComboBox で
    // 個別ピークが切り替わったときに、ラベル・ピーク一覧を保持したまま統計値だけ
    // 差し替えて再構築するために保持する。単一データセット時 / 0 件時は null。
    private List<(string Label, MolecularWeightStatistics? Stats)>? _lastMultiDatasetEntries;
    private readonly ObservableCollection<DatasetEntryVm> _datasetEntries = new();
    private readonly Dictionary<MolecularWeightCacheKey, MolecularWeightDataset> _molecularWeightCache = new();
    private readonly Dictionary<PlotSeriesCacheKey, PlotSeriesData> _plotSeriesCache = new();
    private readonly DispatcherTimer _plotRefreshDebounceTimer = new() { Interval = PlotRefreshDebounceInterval };

    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private int _activeIndex = -1;
    private GpcDataset? _currentDataset;
    private CalibrationCurveSet? _calibrationCurveSet;
    private CalibrationCurve? _selectedCalibrationCurve;
    private string? _calibrationFilePath;
    private AvaPlot? _chromatogramPlot;
    private LegendDragController? _legendDragController;
    private bool _updatingCalibrationSelection;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressDatasetListEvents;
    private bool _currentLegendAutoShow;
    private bool _forceFullResolutionPlot;
    private bool _currentPlotUsesDownsampledData;
    private bool _suppressRepresentativePeakSelection;
    private MolecularWeightStatistics? _currentStatistics;

    // Phase 7 Batch 6 step 4: 内部 reorder は OS DragDrop layer を使わず
    // PointerCapture + 手動位置計算で実装する。Avalonia 11.3 の
    // DragDrop.DoDragDrop (obsolete) は custom DataFormat を渡しても
    // 受け取り側で Contains(format)=False となり drop イベントが reorder と
    // して認識されないことが実機検証で判明したため、OS layer を bypass する。
    private Point? _datasetDragStartPoint;
    private int? _datasetDragSourceIndex;
    private ListBoxItem? _datasetDragSourceContainer;
    private bool _isInternalReordering;
    private IPointer? _reorderCapturedPointer;
    private readonly DragGhostController _dragGhost = new();
    // ドラッグゴーストの「クリック位置オフセット」を保持。WPF の DoDragDrop と同じく
    // ドラッグ中に行が掴まれた点を保ったまま追従させるために使う。
    private Point _dragGhostPointerOffset;

    public MainWindow()
    {
        InitializeComponent();
        LoadFormattingDefaults();
        _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
        ApplyFormattingConfigToControls(_formattingConfig);
        DatasetListBox.ItemsSource = _datasetEntries;
        _plotRefreshDebounceTimer.Tick += PlotRefreshDebounceTimer_Tick;
        Opened += OnOpened;

        // Avalonia 11 の DragDrop は ListBoxItem など子要素の AllowDrop=False が
        // drop hit-test を吸収するため、Window レベルで bubble を待ち受ける必要がある。
        // OnAttachedToVisualTree よりも ctor 直後に登録した方が確実なので、
        // 公式サンプルと同じく InitializeComponent 直後に AddHandler する。
        // AllowDrop は XAML 側 DragDrop.AllowDrop="True" で Window に設定済み。
        AddHandler(DragDrop.DragOverEvent, OnDatasetDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDatasetDragLeave);
        AddHandler(DragDrop.DropEvent, OnDatasetDrop);

        // 内部 reorder 用の DatasetListBox 個別ハンドラも ctor で登録する。
        // 以前は OnAttachedToVisualTree に置いていたが、外部 D&D は動くのに
        // 内部 reorder だけ動かない症状を切り分けた結果、OnAttachedToVisualTree
        // 経由の登録が実機で発火しないケースを確認したため、ctor 末尾に集約する。
        // InitializeComponent 直後に DatasetListBox.ItemsSource= が成功している以上、
        // x:Name 解決は完了済みで AddHandler は安全。
        DatasetListBox.AddHandler(DragDrop.DragOverEvent, OnDatasetDragOver);
        DatasetListBox.AddHandler(DragDrop.DragLeaveEvent, OnDatasetDragLeave);
        DatasetListBox.AddHandler(DragDrop.DropEvent, OnDatasetDrop);
        // Avalonia の ListBox は内部で PointerPressed を消費し e.Handled=true を立てるため、
        // 既定の AddHandler では reorder ハンドラが呼ばれない。Tunnel | Bubble 双方を購読し、
        // handled なイベントも拾うことで確実にハンドラを動かす。
        const RoutingStrategies route = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        DatasetListBox.AddHandler(PointerPressedEvent, OnDatasetListBoxPointerPressed, route, handledEventsToo: true);
        DatasetListBox.AddHandler(PointerMovedEvent, OnDatasetListBoxPointerMoved, route, handledEventsToo: true);
        DatasetListBox.AddHandler(PointerReleasedEvent, OnDatasetListBoxPointerReleased, route, handledEventsToo: true);
        DatasetListBox.AddHandler(PointerCaptureLostEvent, OnDatasetListBoxPointerCaptureLost);
    }

    // Avalonia.Generators (NameGenerator + AvaloniaXamlLoader) が partial class に
    // InitializeComponent + x:Name フィールド代入を自動生成するので、ここでは
    // 手動定義しない。Phase 7 Batch 6 で発覚した「GraphFormatPanel フィールドが
    // null のまま ApplyFormattingConfigToControls が呼ばれて NRE」は手動メソッドが
    // ジェネレータをマスクしていたため。

    private void OnOpened(object? sender, EventArgs e)
    {
        // WPF 版 Loaded + Dispatcher.BeginInvoke(ApplicationIdle) の代替。
        // Window が描画スレッドに乗ってから AvaPlot を生成する。
        Dispatcher.UIThread.Post(() =>
        {
            InitializePlotControl();
            TryLoadDefaultCalibration();
        }, DispatcherPriority.Background);

        // v1.3 Batch A: XAML から StatusTextBlock の Text= 初期値を剥がしたので、
        // OnOpened で明示的に Info severity の初期メッセージを立てる。
        SetStatus("CSVまたはLabSolutions TXTを開いてください。", StatusSeverity.Info);
    }

    // WPF の InputBindings 群を OnKeyDown 1 メソッドに集約。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && shift)
        {
            switch (e.Key)
            {
                case Key.S: SaveSessionButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.O: LoadSessionButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
            }
        }
        else if (ctrl)
        {
            switch (e.Key)
            {
                case Key.O: OpenCsvButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.S: SaveGraphButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.E: ExportDataButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.R: AxisRangePanel.ResetToAuto(); e.Handled = true; return;
                case Key.G: GraphFormatPanel.TogglePlotGrid(); e.Handled = true; return;
                case Key.L: ToggleCheckBox(OverlayCheckBox); e.Handled = true; return;
                case Key.D1: CycleComboBoxSelection(SolventComboBox); e.Handled = true; return;
                case Key.D2: CycleComboBoxSelection(DetectorComboBox); e.Handled = true; return;
                case Key.D3: ToggleCheckBox(MolecularWeightCheckBox); e.Handled = true; return;
                case Key.D4: CycleComboBoxSelection(MolecularWeightYModeComboBox); e.Handled = true; return;
            }
        }
        else if (e.Key == Key.F2)
        {
            FocusLegendNameTextBox();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static void CycleComboBoxSelection(ComboBox comboBox)
    {
        if (comboBox is null || !comboBox.IsEnabled || comboBox.ItemCount <= 1)
        {
            return;
        }

        var current = comboBox.SelectedIndex;
        var next = (current + 1) % comboBox.ItemCount;
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

    private async Task<IStorageFolder?> GetDefaultStartLocationAsync(IStorageProvider sp)
    {
        var dir = FormattingDefaultsStore.GetExistingDefaultOutputDirectory(_formattingDefaults);
        if (string.IsNullOrEmpty(dir)) return null;
        try { return await sp.TryGetFolderFromPathAsync(dir); }
        catch { return null; }
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
            if (!double.IsFinite(value)) return;
            if (!HasValue) { Min = value; Max = value; HasValue = true; return; }
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
        }

        public void Include(IReadOnlyList<double> values)
        {
            for (var i = 0; i < values.Count; i++) Include(values[i]);
        }

        public void Include(AxisDataRange range)
        {
            if (!range.HasValue) return;
            if (!HasValue) { Min = range.Min; Max = range.Max; HasValue = true; return; }
            Min = Math.Min(Min, range.Min);
            Max = Math.Max(Max, range.Max);
        }
    }

    private DatasetStyle CreateDefaultDatasetStyle()
    {
        var style = new DatasetStyle();
        ApplyDefaultDatasetStyle(style);
        return style;
    }

    private void ApplyDefaultDatasetStyle(DatasetStyle style)
    {
        style.ColorHex = _formattingConfig.DefaultLineColorHex;
        style.LegendName = null;
        style.LineWidth = _formattingConfig.LineWidth;
        style.MarkerSize = _formattingConfig.MarkerSize;
    }

    // ItemTemplate からは ReflectionBinding 経由で参照される（compiled binding の
    // DataType として直接指定するなら public が必要なので public のまま）。
    public sealed class DatasetEntryVm
    {
        public string DisplayName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public SolidColorBrush ColorBrush { get; init; } = new(Colors.Gray);
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
        GraphFormatPanel.Capture(config);

        config.ShowTitle = TitleVisibleCheckBox.IsChecked == true;
        config.TitleBold = TitleBoldCheckBox.IsChecked == true;
        config.AxisLabelBold = AxisLabelBoldCheckBox.IsChecked == true;

        config.DefaultLineColorHex = LineColorPicker.HexValue;
        config.LineWidth = TryParsePositiveDouble(LineWidthTextBox.Text, out var lineWidth)
            ? lineWidth
            : GraphFormattingConfig.DefaultLineWidth;
        config.MarkerSize = TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var markerSize)
            ? markerSize
            : GraphFormattingConfig.DefaultMarkerSize;

        config.DefaultCalibrationFilePath = DefaultCalibrationPathTextBox.Text;
        config.DefaultOutputDirectory = DefaultOutputDirectoryTextBox.Text;

        config.Normalize();
        return config;
    }

    private void ApplyFormattingConfigToControls(GraphFormattingConfig config)
    {
        config.Normalize();

        GraphFormatPanel.Apply(config);

        _suppressGraphAppearanceEvents = true;
        try
        {
            TitleVisibleCheckBox.IsChecked = config.ShowTitle;
            TitleBoldCheckBox.IsChecked = config.TitleBold;
            AxisLabelBoldCheckBox.IsChecked = config.AxisLabelBold;
        }
        finally
        {
            _suppressGraphAppearanceEvents = false;
        }

        _suppressStyleControlEvents = true;
        try
        {
            LineColorPicker.SetHexValue(config.DefaultLineColorHex);
            LegendNameTextBox.Text = string.Empty;
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

    private async void BrowseDefaultCalibrationButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var current = DefaultCalibrationPathTextBox.Text?.Trim();
        IStorageFolder? startLocation = null;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var directory = Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                try { startLocation = await sp.TryGetFolderFromPathAsync(directory); } catch { }
            }
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "既定の較正曲線 JSON を選択",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All,
            },
            SuggestedStartLocation = startLocation,
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            DefaultCalibrationPathTextBox.Text = path;
        }
    }

    private async void BrowseDefaultOutputDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var current = DefaultOutputDirectoryTextBox.Text?.Trim();
        IStorageFolder? startLocation = null;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            try { startLocation = await sp.TryGetFolderFromPathAsync(current); } catch { }
        }

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "既定の出力フォルダを選択",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            DefaultOutputDirectoryTextBox.Text = path;
        }
    }

    private async void OpenCsvButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var allowMultiple = OverlayCheckBox.IsChecked == true;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = allowMultiple ? "GPCデータを開く（複数選択可）" : "GPCデータを開く",
            AllowMultiple = allowMultiple,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GPCデータ") { Patterns = new[] { "*.csv", "*.txt", "*.tsv" } },
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
                new FilePickerFileType("テキスト") { Patterns = new[] { "*.txt", "*.tsv" } },
                FilePickerFileTypes.All,
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (files.Count == 0) return;

        var fileNames = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToArray();
        if (fileNames.Length == 0) return;

        await ImportCsvFilesAsync(fileNames);
    }

    private async Task ImportCsvFilesAsync(string[] fileNames)
    {
        if (fileNames is null || fileNames.Length == 0) return;

        try
        {
            OpenCsvButton.IsEnabled = false;
            SetStatus("GPCデータを読み込み中です...", false);
            var busyMessage = fileNames.Length == 1
                ? "CSV を読み込み中…"
                : $"{fileNames.Length} ファイルを読み込み中…";
            BusyOverlay.Show(busyMessage);

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
            ShowError($"読み込みに失敗しました: {ex.Message}");
        }
        finally
        {
            OpenCsvButton.IsEnabled = true;
            BusyOverlay.Hide();
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

    private async void OpenCalibrationButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "較正曲線JSONを開く",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            _calibrationCurveSet = _standardCurveReader.Read(path);
            _calibrationFilePath = path;
            ClearComputedDataCaches();
            CalibrationPathTextBlock.Text = $"較正曲線: {path}";
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
            ShowError($"較正曲線の読み込みに失敗しました: {ex.Message}");
        }
    }

    private void SolventComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingCalibrationSelection) return;
        PopulateDetectorComboBox();
    }

    private void DetectorComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingCalibrationSelection) return;
        SelectCalibrationCurve();
    }

    private void MolecularWeightCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_currentDataset is null) return;

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

    private void MolecularWeightYModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_currentDataset is not null && MolecularWeightCheckBox.IsChecked == true)
        {
            PlotCurrentDataset();
        }
    }

    private void OverlayCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_currentDataset is not null)
        {
            PlotCurrentDataset();
        }
    }

    private void ResetGraphSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        TitleTextBox.Text = string.Empty;
        XLabelTextBox.Text = string.Empty;
        YLabelTextBox.Text = string.Empty;
        AxisRangePanel.SetXValues(null, null);
        AxisRangePanel.SetYValues(null, null);
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

        // v1.3 Batch B: 瞬間 OK 系フィードバックを Toast で軽く出す。
        Toast?.Show("既定値に戻しました", StatusSeverity.Success);
    }

    private void SaveDefaultFormattingButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _formattingDefaults = CaptureFormattingConfigFromControls();
            _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
            SaveFormattingDefaults();
            SetStatus($"書式の既定値を保存しました: {FormattingConfigPath}", StatusSeverity.Success);
            Toast?.Show("既定値を更新しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowError($"書式の既定値を保存できませんでした: {ex.Message}");
        }
    }

    private void SetGraphActionsEnabled(bool enabled)
    {
        SaveGraphButton.IsEnabled = enabled;
        ExportDataButton.IsEnabled = enabled;
        SaveSessionButton.IsEnabled = enabled;
    }

    private async void ExportDataButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_loadedDatasets.Count == 0)
        {
            ShowError("出力可能なデータがありません。");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset?.SourceFilePath) ?? "gpc_analysis";
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "解析結果を保存",
            SuggestedFileName = $"{defaultName}.xlsx",
            DefaultExtension = "xlsx",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Excelブック") { Patterns = new[] { "*.xlsx" } },
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var data = BuildAnalysisExport();
            if (data.Entries.Count == 0)
            {
                ShowError("出力可能なデータがありません。");
                return;
            }

            var format = GetAnalysisExportFormat(path);
            var fileName = EnsureAnalysisExportExtension(path, format);
            IAnalysisExporter exporter = format == AnalysisExportFormat.Csv
                ? new CsvAnalysisExporter()
                : new XlsxAnalysisExporter();
            exporter.Export(data, fileName);
            SetStatus($"解析結果を保存しました: {fileName}", StatusSeverity.Success);
            Toast?.Show("データを保存しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
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
                catch (InvalidDataException) { }
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

    private enum AnalysisExportFormat { Xlsx, Csv }

    private static AnalysisExportFormat GetAnalysisExportFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase)) return AnalysisExportFormat.Csv;
        return AnalysisExportFormat.Xlsx;
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

    private async void SaveSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_loadedDatasets.Count == 0)
        {
            ShowError("保存する状態がありません。");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset?.SourceFilePath) ?? "gpc_session";
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "解析条件を保存",
            SuggestedFileName = $"{defaultName}.gpcjson",
            DefaultExtension = "gpcjson",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("GPC 解析条件") { Patterns = new[] { "*.gpcjson" } },
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var session = BuildAnalysisSession();
            new AnalysisSessionStore<GpcAnalysisSession>().Save(session, path);
            SetStatus($"解析条件を保存しました: {path}", StatusSeverity.Success);
            Toast?.Show("解析条件を保存しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
        }
    }

    private async void LoadSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "解析条件を読み込み",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GPC 解析条件") { Patterns = new[] { "*.gpcjson", "*.json" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        GpcAnalysisSession session;
        try
        {
            session = new AnalysisSessionStore<GpcAnalysisSession>().Load(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            ShowError($"読み込みに失敗しました: {ex.Message}");
            return;
        }

        var warnings = new List<string>();
        ApplyAnalysisSession(session, warnings);

        if (warnings.Count == 0)
        {
            SetStatus($"解析条件を読み込みました: {path}", false);
        }
        else
        {
            ShowError($"解析条件を読み込みました（一部復元できない項目あり: {string.Join(" / ", warnings)})");
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
            session.Formatting.DefaultCalibrationFilePath = _formattingDefaults.DefaultCalibrationFilePath;
            session.Formatting.DefaultOutputDirectory = _formattingDefaults.DefaultOutputDirectory;
            _formattingConfig = session.Formatting;
            ApplyFormattingConfigToControls(_formattingConfig);
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
                var matchDetector = DetectorComboBox.Items.Cast<object?>()
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
        PlotCurrentDataset();
    }

    private void ApplyMolecularWeightYModeSelection(string yMode)
    {
        var targetTag = string.Equals(
            yMode,
            nameof(MolecularWeightYMode.DifferentialWeightFraction),
            StringComparison.OrdinalIgnoreCase) ? "DwdLogM" : "Signal";

        for (var i = 0; i < MolecularWeightYModeComboBox.Items.Count; i++)
        {
            if (MolecularWeightYModeComboBox.Items[i] is ComboBoxItem cbItem
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

    private async void SaveGraphButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentDataset is null || _chromatogramPlot is null)
        {
            ShowError("保存するグラフがありません。");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset.SourceFilePath) ?? "gpc_chromatogram";
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "グラフを保存",
            SuggestedFileName = $"{defaultName}.png",
            DefaultExtension = "png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG画像") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("SVGベクター画像") { Patterns = new[] { "*.svg" } },
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var (width, height) = GetExportImageSize();
            var saveFormat = GraphSaveHelpers.GetGraphSaveFormat(path);
            var fileName = GraphSaveHelpers.EnsureGraphSaveFileExtension(path, saveFormat);
            var exportStyleScale = GetExportStyleScale();
            var restoreDownsampledPlot = _currentPlotUsesDownsampledData;

            if (restoreDownsampledPlot)
            {
                _forceFullResolutionPlot = true;
                try { PlotCurrentDataset(); }
                finally { _forceFullResolutionPlot = false; }
            }

            ApplyExportStyleScale(exportStyleScale);
            try
            {
                if (saveFormat == GraphSaveFormat.Svg)
                {
                    GraphSaveHelpers.SaveGraphSvg(_chromatogramPlot.Plot, fileName, width, height);
                    SetStatus($"グラフをSVGで保存しました: {fileName} ({width:N0} x {height:N0})", StatusSeverity.Success);
                    Toast?.Show("SVG を保存しました", StatusSeverity.Success);
                    return;
                }

                GraphSaveHelpers.SaveGraphPng(_chromatogramPlot.Plot, fileName, width, height, GraphSaveHelpers.ExportDpi);
                SetStatus($"グラフをPNGで保存しました: {fileName} ({width:N0} x {height:N0} px, {GraphSaveHelpers.ExportDpi} dpi)", StatusSeverity.Success);
                Toast?.Show("PNG を保存しました", StatusSeverity.Success);
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
            ShowError($"保存に失敗しました: {ex.Message}");
        }
    }

    private void InitializePlotControl()
    {
        try
        {
            _chromatogramPlot = new AvaPlot();
            _chromatogramPlot.PointerReleased += ChromatogramPlot_PointerInteractionFinished;
            _chromatogramPlot.PointerWheelChanged += ChromatogramPlot_PointerInteractionFinished;
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_chromatogramPlot);

            // Phase 7 Batch 6 step 3: WPF 同等の凡例ドラッグ移動を有効化。
            _legendDragController = new LegendDragController(
                _chromatogramPlot,
                () => _formattingConfig.LegendPosition,
                () => (_formattingConfig.LegendOffsetX, _formattingConfig.LegendOffsetY),
                OnLegendDragCommit);
            _legendDragController.Attach();

            UpdatePlotHostAspectRatio();
            // 初期化成功時点でスケルトンを消す。placeholder TextBlock の文言は
            // InitializeEmptyPlot で SetState(EmptyReady) に切り替わる。
            PlotPlaceholderSkeleton.IsVisible = false;
            InitializeEmptyPlot();

            if (_currentDataset is not null)
            {
                PlotCurrentDataset();
                SetGraphActionsEnabled(true);
            }
        }
        catch (Exception ex)
        {
            PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.InitFailed);
            ShowError($"グラフ表示の初期化に失敗しました: {ex.Message}");
        }
    }

    private void InitializeEmptyPlot()
    {
        if (_chromatogramPlot is null) return;

        // データ無しの状態 — placeholder を「ファイルを読み込むと…」に切り替え。
        // 起動時 (InitializePlotControl 直後) と全データセット削除時の両方から呼ばれる。
        PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.EmptyReady);

        _chromatogramPlot.Plot.Title(DefaultLabels.PlaceholderTitle);
        _chromatogramPlot.Plot.XLabel(DefaultLabels.PlaceholderXLabel);
        _chromatogramPlot.Plot.YLabel(DefaultLabels.PlaceholderYLabel);
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

        // データを描画するので placeholder を非表示にする。
        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);

        var activeDataset = GetSelectedDetectorDataset(_currentDataset);
        var plotEntries = GetDatasetsToPlotWithIndices();
        if (MolecularWeightCheckBox.IsChecked == true)
        {
            if (_selectedCalibrationCurve is null)
            {
                ShowError("分子量表示には較正曲線、溶媒、検出器の選択が必要です。");
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
                ShowError($"分子量表示に失敗しました: {ex.Message}");
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
                if (!double.IsFinite(y)) continue;
                if (y < minY) { minY = y; minIndex = i; }
                if (y > maxY) { maxY = y; maxIndex = i; }
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
            ShowError("グラフ表示を初期化中です。少し待ってからもう一度お試しください。");
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

        _chromatogramPlot.Plot.Title(GetGraphTitle(Path.GetFileName(activeDataset.SourceFilePath) ?? DefaultLabels.ChromatogramFallbackTitle));
        _chromatogramPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, activeDataset.XLabel));
        _chromatogramPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, activeDataset.YLabel));
        _chromatogramPlot.Plot.Axes.AutoScale();
        if (!ApplyAxisLimits(xRange, yRange, false))
        {
            _chromatogramPlot.Refresh();
            return;
        }

        UpdateStatisticsForDatasets(entries
            .Select(en => (
                Label: GetStatsLabel(en.Dataset.SourceFilePath, en.Index),
                Stats: ApplyStoredSelectedPeak(en.Dataset.MolecularWeightStatistics, en.Index)))
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
            ShowError("グラフ表示を初期化中です。少し待ってからもう一度お試しください。");
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

        _chromatogramPlot.Plot.Title(GetGraphTitle(Path.GetFileName(activeDataset.SourceFilePath) ?? DefaultLabels.ChromatogramFallbackTitle));
        _chromatogramPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, string.Format(DefaultLabels.LogScaleXLabelFormat, activeDataset.XLabel)));
        _chromatogramPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, activeDataset.YLabel));
        _chromatogramPlot.Plot.Axes.AutoScale();
        if (!ApplyAxisLimits(xRange, yRange, true))
        {
            _chromatogramPlot.Refresh();
            return;
        }

        UpdateStatisticsForDatasets(entries
            .Select(en => (
                Label: GetStatsLabel(en.Dataset.SourceFilePath, en.Index),
                Stats: ApplyStoredSelectedPeak(en.Dataset.Statistics, en.Index)))
            .ToList());
        ApplyPlotAppearance();
        _chromatogramPlot.Refresh();
    }

    private void ApplyPlotAppearance(float scale = 1f)
    {
        if (_chromatogramPlot is null) return;
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
        if (_chromatogramPlot is null) return;
        ApplyPlotAppearance(scale);
        ApplyExistingSeriesStyles(scale);
    }

    private void ApplyExistingSeriesStyles(float scale)
    {
        if (_chromatogramPlot is null) return;

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
        return GraphSaveHelpers.ExportDpi / GraphSaveHelpers.DisplayDpi;
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
        if (!HasCustomLegendName(datasetIndex)) return null;
        return _datasetStyles[datasetIndex].LegendName!.Trim();
    }

    private string GetSeriesLegendText(GpcDataset dataset, string fallback, int datasetIndex)
    {
        var customName = GetCustomLegendName(datasetIndex);
        if (customName is not null) return customName;

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        var detector = string.IsNullOrWhiteSpace(dataset.Detector) ? string.Empty : $" / Detector {dataset.Detector}";
        return string.IsNullOrWhiteSpace(fileName) ? $"{fallback}{detector}" : $"{fileName}{detector}";
    }

    private string GetSeriesLegendText(MolecularWeightDataset dataset, string fallback, int datasetIndex)
    {
        var customName = GetCustomLegendName(datasetIndex);
        if (customName is not null) return customName;

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? fallback : $"{fileName} / {fallback}";
    }

    private bool ApplyAxisLimits(AxisDataRange xRange, AxisDataRange yRange, bool xIsMolecularWeight)
    {
        if (_chromatogramPlot is null) return false;

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
        if (!value.HasValue) return true;
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
        var title = TitleTextBox.Text?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(title) ? defaultTitle : title;
    }

    private static string GetGraphLabel(TextBox textBox, string defaultLabel)
    {
        var label = textBox.Text?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(label) ? defaultLabel : label;
    }

    private static readonly Regex StatisticsLineRegex = new(
        @"^(?:(?<label>.+?)   )?Mn:\s*(?<mn>\S+)\s+Mw:\s*(?<mw>\S+)\s+Ð:\s*(?<pdi>\S+?)(?:\s*\((?<src>[^)]+)\))?\s*$",
        RegexOptions.Compiled);

    private void SetStatisticsLine(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Contains('\n'))
        {
            ShowStatisticsFallback(text ?? string.Empty);
            return;
        }

        var match = StatisticsLineRegex.Match(text);
        if (!match.Success)
        {
            ShowStatisticsFallback(text);
            return;
        }

        var label = match.Groups["label"].Success ? match.Groups["label"].Value : string.Empty;
        if (string.IsNullOrEmpty(label))
        {
            StatisticsPeakLabel.IsVisible = false;
            StatisticsPeakLabel.Text = string.Empty;
        }
        else
        {
            StatisticsPeakLabel.Text = label;
            StatisticsPeakLabel.IsVisible = true;
        }

        MnChipValue.Text = match.Groups["mn"].Value;
        MwChipValue.Text = match.Groups["mw"].Value;
        DispersityChipValue.Text = match.Groups["pdi"].Value;

        if (match.Groups["src"].Success)
        {
            StatisticsSourceLabel.Text = $"({match.Groups["src"].Value})";
            StatisticsSourceLabel.IsVisible = true;
        }
        else
        {
            StatisticsSourceLabel.IsVisible = false;
            StatisticsSourceLabel.Text = string.Empty;
        }

        StatisticsChipPanel.IsVisible = true;
        StatisticsTextBlock.IsVisible = false;
    }

    private void ShowStatisticsFallback(string text)
    {
        // SetStatisticsLine の regex 不一致時の保険（基本的には到達しない）。
        StatisticsChipPanel.IsVisible = false;
        StatisticsTextBlock.IsVisible = true;
        StatisticsTextBlock.Text = text;
    }

    private void UpdateStatisticsText(MolecularWeightStatistics? statistics)
    {
        _currentStatistics = statistics;

        if (statistics is null || !statistics.HasAnyValue)
        {
            SetStatisticsLine("Mn: -   Mw: -   Ð: -");
            UpdateRepresentativePeakSelector(null);
            return;
        }

        if (statistics.Peaks.Count > 0)
        {
            SetStatisticsLine(FormatRepresentativeStatistics(statistics));
            UpdateRepresentativePeakSelector(statistics);
            return;
        }

        var source = statistics.Source == MolecularWeightStatisticsSource.DataFile ? "file" : "calc";
        SetStatisticsLine($"Mn: {FormatStatistic(statistics.Mn)}   Mw: {FormatStatistic(statistics.Mw)}   Ð: {FormatStatistic(statistics.Pdi)} ({source})");
        UpdateRepresentativePeakSelector(null);
    }

    private void UpdateStatisticsForDatasets(IReadOnlyList<(string Label, MolecularWeightStatistics? Stats)> entries)
    {
        if (entries.Count == 0)
        {
            _currentStatistics = null;
            ShowSingleStatisticsView();
            SetStatisticsLine("Mn: -   Mw: -   Ð: -");
            UpdateRepresentativePeakSelector(null);
            _lastMultiDatasetEntries = null;
            return;
        }

        if (entries.Count == 1)
        {
            ShowSingleStatisticsView();
            UpdateStatisticsText(entries[0].Stats);
            _lastMultiDatasetEntries = null;
            return;
        }

        // 重ね書きモード: ItemsControl にデータセットごとの 1 行を生成する。
        _currentStatistics = null;
        UpdateRepresentativePeakSelector(null);
        _lastMultiDatasetEntries = entries.ToList();
        ShowMultiStatisticsView();
        BuildDatasetStatisticsRows(_lastMultiDatasetEntries);
    }

    private void ShowSingleStatisticsView()
    {
        SingleStatisticsView.IsVisible = true;
        MultiStatisticsScroll.IsVisible = false;
        DatasetStatisticsList.Items.Clear();
    }

    private void ShowMultiStatisticsView()
    {
        SingleStatisticsView.IsVisible = false;
        MultiStatisticsScroll.IsVisible = true;
    }

    private void BuildDatasetStatisticsRows(IReadOnlyList<(string Label, MolecularWeightStatistics? Stats)> entries)
    {
        DatasetStatisticsList.Items.Clear();
        for (var i = 0; i < entries.Count; i++)
        {
            var row = BuildDatasetStatisticsRow(i, entries[i].Label, entries[i].Stats);
            DatasetStatisticsList.Items.Add(row);
        }
    }

    private Control BuildDatasetStatisticsRow(int datasetIndex, string label, MolecularWeightStatistics? stats)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,*,Auto,Auto,Auto,Auto"),
        };

        // [0] 凡例ドット — プロット線の色と統計行を一目で対応付けるため。
        var hex = (datasetIndex >= 0 && datasetIndex < _datasetStyles.Count
                ? _datasetStyles[datasetIndex].ColorHex
                : null)
            ?? AutoLineColors[Math.Max(0, datasetIndex) % AutoLineColors.Length];
        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = SolidColorBrush.Parse(hex),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        // [1] ファイル名 — 長いパスは省略表示。
        var fileName = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#0F172A"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 16, 0),
            MaxWidth = 220,
        };
        ToolTip.SetTip(fileName, label);
        Grid.SetColumn(fileName, 1);
        grid.Children.Add(fileName);

        // [3] [4] [5] Mn / Mw / Đ chip（コピー可能）。
        var mnChip = BuildStatChip("Mn", FormatStatistic(stats?.Mn));
        Grid.SetColumn(mnChip, 3);
        grid.Children.Add(mnChip);

        var mwChip = BuildStatChip("Mw", FormatStatistic(stats?.Mw));
        Grid.SetColumn(mwChip, 4);
        grid.Children.Add(mwChip);

        var pdiChip = BuildStatChip("Ð", FormatStatistic(stats?.Pdi));
        Grid.SetColumn(pdiChip, 5);
        grid.Children.Add(pdiChip);

        // [6] このデータセット専用のピーク選択 ComboBox。Tag で datasetIndex を引き取る。
        var combo = BuildDatasetRowPeakSelector(datasetIndex, stats);
        Grid.SetColumn(combo, 6);
        grid.Children.Add(combo);

        return grid;
    }

    private static Border BuildStatChip(string label, string value)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#64748B"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        stack.Children.Add(new SelectableTextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#0F172A"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#E2E8F0"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        };
    }

    private ComboBox BuildDatasetRowPeakSelector(int datasetIndex, MolecularWeightStatistics? stats)
    {
        var combo = new ComboBox
        {
            MinWidth = 140,
            Tag = datasetIndex,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 既存 RepresentativePeakComboBox と同じ ControlTheme を流用して見た目を揃える。
        if (this.TryFindResource("InputComboBoxStyle", out var resource) && resource is ControlTheme theme)
        {
            combo.Theme = theme;
        }

        if (stats is null || stats.Peaks.Count == 0)
        {
            combo.IsEnabled = false;
            combo.PlaceholderText = "—";
            return combo;
        }

        var auto = MolecularWeightStatistics.SelectAutoRepresentativePeak(stats.Peaks);
        combo.Items.Add(new ComboBoxItem
        {
            Content = auto is not null ? $"自動 (Peak #{auto.PeakId})" : "自動",
            Tag = null,
        });

        var orderedPeaks = stats.Peaks
            .OrderBy(peak => TryParsePeakNumber(peak.PeakId))
            .ThenBy(peak => peak.PeakId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var peak in orderedPeaks)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = FormatPeakComboBoxItem(peak),
                Tag = peak.PeakId,
            });
        }

        var selectedIndex = 0;
        if (!stats.IsAutoSelected)
        {
            for (var i = 1; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, stats.SelectedPeakId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        combo.SelectedIndex = selectedIndex;
        // SelectedIndex 設定後に登録 — 行構築フェーズの SelectionChanged を抑止するため。
        combo.SelectionChanged += DatasetRowPeakComboBox_SelectionChanged;
        return combo;
    }

    private void DatasetRowPeakComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Tag is not int datasetIndex) return;
        if (datasetIndex < 0 || datasetIndex >= _datasetSelectedPeakIds.Count) return;
        if (combo.SelectedItem is not ComboBoxItem item) return;

        var peakId = item.Tag as string;
        _datasetSelectedPeakIds[datasetIndex] = peakId;

        if (_lastMultiDatasetEntries is null) return;
        if (datasetIndex >= _lastMultiDatasetEntries.Count) return;

        var entry = _lastMultiDatasetEntries[datasetIndex];
        var updated = entry.Stats?.WithSelectedPeak(peakId);
        _lastMultiDatasetEntries[datasetIndex] = (entry.Label, updated);
        BuildDatasetStatisticsRows(_lastMultiDatasetEntries);
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
                RepresentativePeakPanel.IsVisible = false;
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
            RepresentativePeakPanel.IsVisible = true;
        }
        finally
        {
            _suppressRepresentativePeakSelection = false;
        }
    }

    private void RepresentativePeakComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressRepresentativePeakSelection) return;

        if (_currentStatistics is null || _currentStatistics.Peaks.Count == 0) return;
        if (RepresentativePeakComboBox.SelectedItem is not ComboBoxItem item) return;

        var peakId = item.Tag as string;
        if (_activeIndex >= 0 && _activeIndex < _datasetSelectedPeakIds.Count)
        {
            _datasetSelectedPeakIds[_activeIndex] = peakId;
        }
        var updated = _currentStatistics.WithSelectedPeak(peakId);
        _currentStatistics = updated;
        SetStatisticsLine(FormatRepresentativeStatistics(updated));
    }

    private MolecularWeightStatistics? ApplyStoredSelectedPeak(MolecularWeightStatistics? stats, int datasetIndex)
    {
        if (stats is null || stats.Peaks.Count == 0) return stats;
        if (datasetIndex < 0 || datasetIndex >= _datasetSelectedPeakIds.Count) return stats;

        var storedPeakId = _datasetSelectedPeakIds[datasetIndex];
        if (storedPeakId is null) return stats;

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
        if (!value.HasValue || !double.IsFinite(value.Value)) return "-";
        var absoluteValue = Math.Abs(value.Value);
        if (absoluteValue <= double.Epsilon) return "0";
        if (absoluteValue is >= 0.01 and < 10000)
        {
            return value.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }
        return value.Value.ToString("0.###E+0", CultureInfo.InvariantCulture);
    }

    private void SetMolecularWeightLogTicks()
    {
        if (_chromatogramPlot is null) return;

        var minExponent = (int)Math.Log10(MolecularWeightConverter.DefaultMinMolecularWeight);
        var maxExponent = (int)Math.Log10(MolecularWeightConverter.DefaultMaxMolecularWeight);

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
        if (_calibrationCurveSet is null) return;

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

        RefreshSolventDetectorBadge();
    }

    private void RefreshSolventDetectorBadge()
    {
        // 較正曲線が未選択 / 溶媒・検出器いずれか欠落のときはバッジを隠す。
        // 既存の SolventComboBox / DetectorComboBox は較正曲線選択前は IsEnabled=false なので、
        // ここでは _selectedCalibrationCurve を真の出所として使う。
        var curve = _selectedCalibrationCurve;
        var solvent = curve?.Solvent;
        var detector = curve?.Detector;

        if (curve is null || string.IsNullOrWhiteSpace(solvent) || string.IsNullOrWhiteSpace(detector))
        {
            SolventDetectorBadge.IsVisible = false;
            return;
        }

        SolventBadgeValue.Text = solvent;
        DetectorBadgeValue.Text = detector;
        SolventDetectorBadge.IsVisible = true;
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

    private void SetStatus(string message, bool isError = false)
    {
        StatusBar?.SetStatus(message, isError ? StatusSeverity.Error : StatusSeverity.Info);
        if (!isError)
        {
            ErrorBanner.Hide();
        }
    }

    // v1.3 Batch A: 4 段階 severity を明示したい呼び出し向け。Success / Warning を分けて出すために併設。
    private void SetStatus(string message, StatusSeverity severity)
    {
        StatusBar?.SetStatus(message, severity);
        if (severity != StatusSeverity.Error)
        {
            ErrorBanner.Hide();
        }
    }

    private void ShowError(string message)
    {
        ErrorBanner.Show(message);
        SetStatus(message, isError: true);
    }

    private void HideError()
    {
        ErrorBanner.Hide();
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
                    ColorBrush = new SolidColorBrush(HexToAvaloniaColor(hex)),
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

        DatasetListPlaceholder.IsVisible = _datasetEntries.Count == 0;
    }

    private void SyncStyleControlsFromActiveDataset()
    {
        _suppressStyleControlEvents = true;
        try
        {
            if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
            {
                LineColorPicker.DefaultHex = _formattingConfig.DefaultLineColorHex ?? AutoLineColors[0];
                LineColorPicker.SetHexValue(_formattingConfig.DefaultLineColorHex);
                LegendNameTextBox.Text = string.Empty;
                LineWidthTextBox.Text = _formattingConfig.FormatLineWidth();
                MarkerSizeTextBox.Text = _formattingConfig.FormatMarkerSize();
                ActiveDatasetLabel.Text = "(データ未選択)";
                return;
            }

            var style = _datasetStyles[_activeIndex];

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

    private void DatasetListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDatasetListEvents) return;

        var newIndex = DatasetListBox.SelectedIndex;
        if (newIndex < 0 || newIndex >= _loadedDatasets.Count) return;

        _activeIndex = newIndex;
        _currentDataset = _loadedDatasets[newIndex];
        FilePathTextBlock.Text = _currentDataset?.SourceFilePath ?? string.Empty;

        SyncStyleControlsFromActiveDataset();
        PlotCurrentDataset();
    }

    // ---------- Drag-reorder for ListBox: WPF PreviewMouse* + DoDragDrop の Avalonia 化 ----------

    private void OnDatasetListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual srcVisual && FindAncestor<Button>(srcVisual) is not null)
        {
            // 行内の削除ボタンクリックは drag を発火させない。
            _datasetDragStartPoint = null;
            _datasetDragSourceContainer = null;
            _datasetDragSourceIndex = null;
            return;
        }

        var item = e.Source is Visual v ? FindAncestor<ListBoxItem>(v) : null;
        if (item is null)
        {
            _datasetDragStartPoint = null;
            _datasetDragSourceContainer = null;
            _datasetDragSourceIndex = null;
            return;
        }

        if (!e.GetCurrentPoint(DatasetListBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _datasetDragStartPoint = e.GetPosition(DatasetListBox);
        _datasetDragSourceContainer = item;
        _datasetDragSourceIndex = DatasetListBox.IndexFromContainer(item);
        // 行内でクリックされた相対位置を覚えておく。ドラッグ中はこのオフセットを
        // 保ったままゴーストが動くので「掴んだ場所」が安定する。
        _dragGhostPointerOffset = e.GetPosition(item);
    }

    private void OnDatasetListBoxPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_datasetDragStartPoint is null
            || _datasetDragSourceContainer is null
            || _datasetDragSourceIndex is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(DatasetListBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(DatasetListBox);

        if (!_isInternalReordering)
        {
            var dx = current.X - _datasetDragStartPoint.Value.X;
            var dy = current.Y - _datasetDragStartPoint.Value.Y;
            // Avalonia には SystemParameters.MinimumHorizontal/VerticalDragDistance に相当する
            // 公開定数が無いので WPF と同じ 4 px 程度を直書き。
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

            var sourceIndex = _datasetDragSourceIndex.Value;
            if (sourceIndex < 0 || sourceIndex >= _datasetEntries.Count)
            {
                ResetReorderState();
                return;
            }

            // ドラッグ開始: Pointer を ListBox に capture して以降の PointerMoved /
            // PointerReleased を ListBox の Tunnel で確実に拾う。
            _isInternalReordering = true;
            e.Pointer.Capture(DatasetListBox);
            _reorderCapturedPointer = e.Pointer;

            // カーソル追従ゴースト: ItemTemplate を Build(dataContext) で再展開し
            // ベクター描画のままクローン Visual を作って OverlayLayer に乗せる。
            // RenderTargetBitmap 方式は Skia の SubpixelAntialias 制約で
            // テキストがぼやけるため採用しない。
            _dragGhost.Show(
                this,
                DatasetListBox.ItemTemplate,
                _datasetEntries[sourceIndex],
                _datasetDragSourceContainer.Bounds.Size,
                e.GetPosition(this),
                _dragGhostPointerOffset);
            _datasetDragSourceContainer.Opacity = 0.4;
        }

        // 移動中: insertion line を更新。source 行自身の上に重なったら隠す。
        _dragGhost.Move(e.GetPosition(this));
        var (targetItem, insertAbove) = ResolveDropTargetFromVisual(e.Source as Visual, e);
        if (targetItem is null || ReferenceEquals(targetItem, _datasetDragSourceContainer))
        {
            HideInsertionLine();
        }
        else
        {
            UpdateInsertionLine(targetItem, insertAbove);
        }
        e.Handled = true;
    }

    private void OnDatasetListBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isInternalReordering)
        {
            ResetReorderState();
            return;
        }

        var sourceIndex = _datasetDragSourceIndex ?? -1;
        var (targetItem, insertAbove) = ResolveDropTargetFromVisual(e.Source as Visual, e);

        int newIndex;
        if (targetItem is null)
        {
            newIndex = _datasetEntries.Count - 1;
        }
        else
        {
            var targetIndex = DatasetListBox.IndexFromContainer(targetItem);
            if (targetIndex < 0)
            {
                HideInsertionLine();
                ResetReorderState();
                e.Handled = true;
                return;
            }
            newIndex = insertAbove ? targetIndex : targetIndex + 1;
            if (newIndex > sourceIndex)
            {
                newIndex--;
            }
        }

        if (newIndex < 0) newIndex = 0;
        else if (newIndex >= _datasetEntries.Count) newIndex = _datasetEntries.Count - 1;

        HideInsertionLine();

        if (sourceIndex >= 0 && newIndex != sourceIndex)
        {
            MoveDataset(sourceIndex, newIndex);
        }

        ResetReorderState();
        e.Handled = true;
    }

    private void OnDatasetListBoxPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // ドラッグ中に capture が外れたら（ESC キーや他フォーカス移動）
        // 状態を巻き戻す。reorder は実行しない。
        HideInsertionLine();
        ResetReorderState();
    }

    private void ResetReorderState()
    {
        if (_datasetDragSourceContainer is not null)
        {
            _datasetDragSourceContainer.Opacity = 1.0;
        }
        if (_reorderCapturedPointer is { } pointer)
        {
            pointer.Capture(null);
            _reorderCapturedPointer = null;
        }
        // Release / CaptureLost / 早期 abort のすべてからここに合流するので、
        // ゴーストの破棄もここに集約する。Hide は冪等なので二重呼びに耐える。
        _dragGhost.Hide();
        _isInternalReordering = false;
        _datasetDragStartPoint = null;
        _datasetDragSourceContainer = null;
        _datasetDragSourceIndex = null;
    }

    private (ListBoxItem? Item, bool InsertAbove) ResolveDropTargetFromVisual(Visual? src, PointerEventArgs e)
    {
        // Pointer Capture を DatasetListBox に持たせている影響で、e.Source は
        // 常に capture 先 (DatasetListBox) になり、e.Source 経由の祖先探索は
        // 必ず null を返してしまう (= InsertionLine が出ず、Drop は末尾挿入の
        // フォールバックばかり走っていた)。Pointer 位置から ListBox の hit-test を
        // 自前実行することで、capture 中でも実際にカーソル下にある ListBoxItem を
        // 取れるようにする。Drag ghost は IsHitTestVisible=False + OverlayLayer
        // 上にいるので、この hit-test は ghost に邪魔されない。
        var posInListBox = e.GetPosition(DatasetListBox);
        var hit = DatasetListBox.InputHitTest(posInListBox) as Visual;
        var item = hit is null ? null : FindAncestor<ListBoxItem>(hit);
        if (item is null) return (null, false);
        var pos = e.GetPosition(item);
        var insertAbove = pos.Y < item.Bounds.Height / 2;
        return (item, insertAbove);
    }

    // Phase 7 Batch 6 step 4 以降、内部 reorder は OS DragDrop layer を介さず
    // 手動 PointerCapture で処理する。ここに残るのは外部ファイルドロップ
    // (Explorer から DatasetListBox へ xlsx / csv をドロップ) のハンドラのみ。
    // Phase 7 後始末 Batch 7a で Avalonia 11.3 の新 API
    // (DataTransfer / DataFormat.File / TryGetFilesAsync) に移行済み。
    private void OnDatasetDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer is not null && e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            ShowFileDropOverlay();
            e.Handled = true;
            return;
        }
        HideFileDropOverlay();
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDatasetDragLeave(object? sender, DragEventArgs e)
    {
        var pos = e.GetPosition(DatasetListBox);
        if (pos.X < 0 || pos.Y < 0
            || pos.X > DatasetListBox.Bounds.Width
            || pos.Y > DatasetListBox.Bounds.Height)
        {
            HideFileDropOverlay();
        }
    }

    private async void OnDatasetDrop(object? sender, DragEventArgs e)
    {
        HideFileDropOverlay();
        if (e.DataTransfer is null || !e.DataTransfer.Contains(DataFormat.File)) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0) return;
        e.Handled = true;
        await ImportCsvFilesAsync(paths);
    }

    private void UpdateInsertionLine(ListBoxItem item, bool insertAbove)
    {
        // ListBox 内座標での item 上端を取得し、InsertionLine の Margin top を
        // それに合わせる。InsertionLine は ListBox と同じ Grid 階層に置いてある。
        var transformPoint = item.TranslatePoint(new Point(0, 0), DatasetListBox);
        if (transformPoint is null) { HideInsertionLine(); return; }

        var listBoxTopInGrid = DatasetListBox.Bounds.Top;
        var itemTopInGrid = listBoxTopInGrid + transformPoint.Value.Y;
        // InsertionLine Grid Height = 12 (DropShadow blur 余白込み)、視覚中心は中央 6 px。
        // ライン中央が item 上端 (insertAbove) / 下端 (!insertAbove) に乗るよう 6 px ずらす。
        const double lineCenterOffset = 6;
        var lineTop = insertAbove
            ? itemTopInGrid - lineCenterOffset
            : itemTopInGrid + item.Bounds.Height - lineCenterOffset;

        InsertionLine.Margin = new Thickness(0, Math.Max(0, lineTop), 0, 0);
        InsertionLine.IsVisible = true;
    }

    private void HideInsertionLine()
    {
        InsertionLine.IsVisible = false;
    }

    private void ShowFileDropOverlay()
    {
        if (DatasetDropOverlay is not null) DatasetDropOverlay.IsVisible = true;
    }

    private void HideFileDropOverlay()
    {
        if (DatasetDropOverlay is not null) DatasetDropOverlay.IsVisible = false;
    }

    private void MoveDataset(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex
            || oldIndex < 0 || oldIndex >= _loadedDatasets.Count
            || newIndex < 0 || newIndex >= _loadedDatasets.Count)
        {
            return;
        }

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

    private static T? FindAncestor<T>(Visual? element) where T : class
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element.GetVisualParent();
        }
        return null;
    }

    private void RemoveDatasetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not DatasetEntryVm vm) return;

        var index = _datasetEntries.IndexOf(vm);
        if (index < 0 || index >= _loadedDatasets.Count) return;

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

        var style = _datasetStyles[_activeIndex];
        style.ColorHex = LineColorPicker.HexValue;

        RefreshDatasetEntries();
        SchedulePlotCurrentDataset();
    }

    private void LegendNameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count) return;

        var legendName = LegendNameTextBox.Text?.Trim() ?? string.Empty;
        _datasetStyles[_activeIndex].LegendName = string.IsNullOrWhiteSpace(legendName) ? null : legendName;
        SchedulePlotCurrentDataset();
    }

    private void LineWidthTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count) return;

        if (TryParsePositiveDouble(LineWidthTextBox.Text, out var width))
        {
            _datasetStyles[_activeIndex].LineWidth = width;
            SchedulePlotCurrentDataset();
        }
    }

    private void MarkerSizeTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count) return;

        if (TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var size))
        {
            _datasetStyles[_activeIndex].MarkerSize = size;
            SchedulePlotCurrentDataset();
        }
    }

    private void GraphFormatPanel_GraphFormatChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        ApplyGraphAppearanceAndRefresh();
    }

    private void GraphFormatPanel_AspectRatioChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        UpdatePlotHostAspectRatio();
    }

    private void GraphAppearanceCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        ApplyGraphAppearanceAndRefresh();
    }

    private void GraphLabelTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void AxisRangePanel_Committed(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        PlotCurrentDataset();
    }

    private void ChromatogramPlot_PointerInteractionFinished(object? sender, EventArgs e)
    {
        SyncAxisInputsFromPlot();
    }

    private void SyncAxisInputsFromPlot()
    {
        if (_chromatogramPlot is null || _currentDataset is null) return;

        var limits = _chromatogramPlot.Plot.Axes.GetLimits();
        if (!IsFiniteRange(limits.Left, limits.Right) || !IsFiniteRange(limits.Bottom, limits.Top)) return;

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

    private void PlotContainerBorder_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdatePlotHostAspectRatio();
    }

    private void OnLegendDragCommit(string position, double offsetX, double offsetY)
    {
        // The drag controller wrote Alignment + Margin during the move so
        // the legend already sits at the final spot. Persist the anchor +
        // offsets into _formattingConfig and the panel controls, then re-run
        // the normal appearance pass so any subsequent Plot* call picks up
        // the same placement via ComputeLegendMargin.
        _formattingConfig.LegendPosition = position;
        _formattingConfig.LegendOffsetX = offsetX;
        _formattingConfig.LegendOffsetY = offsetY;
        GraphFormatPanel.SyncLegendPlacement(position, offsetX, offsetY);
        ApplyGraphAppearanceAndRefresh();
    }

    private void ApplyGraphAppearanceAndRefresh()
    {
        if (_chromatogramPlot is null) return;

        ApplyPlotAppearance();
        ApplyLegend(_chromatogramPlot.Plot, CaptureFormattingConfigFromControls(),
            autoShow: _currentLegendAutoShow);
        _chromatogramPlot.Refresh();
    }

    private double? GetSelectedAspectRatio() => GraphFormatPanel.AspectRatioValue;

    private void UpdatePlotHostAspectRatio()
        => PlotHostAspectRatio.Apply(PlotHost, PlotContainerBorder, GetSelectedAspectRatio());

    private (int Width, int Height) GetExportImageSize()
        => GraphSaveHelpers.GetExportImageSize(GetSelectedAspectRatio());
}

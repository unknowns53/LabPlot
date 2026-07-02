using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LabPlot.Core;
using LabPlot.Core.Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;
using ScottPlot.Avalonia;
using SpectrumAnalyzer.Core;
using static LabPlot.Core.PlotAppearance;
using static LabPlot.Core.Avalonia.FormatHelpers;
using Path = System.IO.Path;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace LabPlot.Spectrum.Avalonia;

/// <summary>
/// Phase 7 Batch 5b: WPF 版 <c>Spectrum_Visualization.MainWindow</c> (4723 行) を
/// Avalonia API に翻訳した本実装。GPC.Avalonia / DLS.Avalonia の方針と同様、
/// SaveFileDialog → IStorageProvider, WpfPlot → AvaPlot, InputBindings → OnKeyDown,
/// Visibility → IsVisible, WPF Adorner → InsertionLine sibling、などへ置換している。
/// 凡例ドラッグは Phase 7 Batch 6 step 3 で
/// <see cref="LabPlot.Core.Avalonia.Helpers.LegendDragController"/> として移植済み。
/// </summary>
public partial class MainWindow : Window, IPortalFileOpener
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

    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private int _activeIndex = -1;
    private SpectrumDataset? _currentDataset;
    private AvaPlot? _spectrumPlot;
    private LegendDragController? _legendDragController;
    private PlotFastModeController? _plotFastModeController;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressDatasetListEvents;

    private bool _currentLegendAutoShow;

    // Phase 7 Batch 6 step 4: 内部 reorder は OS DragDrop layer を使わず
    // PointerCapture + 手動位置計算で実装する。Avalonia 11.3 の
    // DragDrop.DoDragDrop (obsolete) は custom DataFormat を渡しても
    // 受け取り側が認識せず drop が reorder として処理されないことが
    // 実機検証で判明したため、OS layer を bypass する。
    private Point? _datasetDragStartPoint;
    private int? _datasetDragSourceIndex;
    private ListBoxItem? _datasetDragSourceContainer;
    private bool _isInternalReordering;
    private IPointer? _reorderCapturedPointer;
    private readonly DragGhostController _dragGhost = new();
    private Point _dragGhostPointerOffset;

    // Mouse-drag region selection for the integration feature.
    private Canvas? _integrationDragOverlay;
    private Rectangle? _integrationDragPreview;
    private bool _isIntegrationDragMode;
    private bool _integrationDragStarted;
    private Point _integrationDragStartPoint;
    private IntegrationRegionVm? _integrationDragTargetVm;

    // Edge-resize for already-defined integration regions.
    private const double IntegrationEdgeHitTolerancePixels = 5.0;
    private bool _isIntegrationResizing;
    private IntegrationRegionVm? _integrationResizeTargetVm;
    private bool _integrationResizeIsLeftEdge;
    private string? _integrationResizeOriginalText;

    // Click-to-add manual λmax markers.
    private readonly ObservableCollection<ManualLambdaMaxEntryVm> _manualLambdaMaxEntryVms = new();
    private bool _isManualLambdaMaxAddMode;

    // Click-to-add manual IR peak markers.
    private readonly ObservableCollection<ManualIrPeakEntryVm> _manualIrPeakEntryVms = new();
    private bool _isManualIrPeakAddMode;

    public MainWindow()
    {
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
        ManualIrPeakItemsControl.ItemsSource = _manualIrPeakEntryVms;
        UpdateManualIrPeakEmptyVisibility();
        _plotRefreshDebounceTimer.Tick += PlotRefreshDebounceTimer_Tick;
        Opened += OnOpened;

        // ListBox の DragDrop / Pointer 系 routed event を ctor 末尾で集約登録する。
        // OnAttachedToVisualTree 経由の登録は実機で発火しないケースがあるので、
        // GPC / DLS と同じく ctor に集約。
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

    // Avalonia.Generators が partial class に InitializeComponent + x:Name フィールド代入を
    // 自動生成するので手動定義しない（Phase 7 Batch 6 で発覚した null フィールド NRE 対策）。

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(InitializePlotControl, DispatcherPriority.Background);

        // v1.3 Batch A: XAML から StatusTextBlock の Text= 初期値を剥がしたので、
        // OnOpened で明示的に Info severity の初期メッセージを立てる。
        SetStatus("JASCO TXT を開いてください。", StatusSeverity.Info);

        // v1.3 Batch E: 最近開いた一覧を ComboBox に流す。
        RefreshRecentFilesUi();

        // macOS では "Ctrl+O" のような tooltip 表記を "Cmd+O" に置換 (Windows / Linux は noop)。
        KeyboardShortcuts.LocalizeTooltipsForMac(this);
    }

    // v1.3 Batch E: 最近開いたファイル MRU。
    // 2026-05-25: 選択直後に SelectedIndex=-1 へ戻すと placeholder に潰れて
    // 「今どのファイルを開いているか」が一目で分からなかったため、直近で開いた
    // ファイルを選択状態のまま残す方針に変更。
    private const string RecentFilesAppKey = "spectrum";
    private bool _suppressRecentFilesEvents;
    private string? _lastLoadedFilePath;
    private MissingFileWatcher? _missingFileWatcher;

    private void RefreshRecentFilesUi()
    {
        if (RecentFilesComboBox is null) return;
        var entries = RecentFilesStore.Load(RecentFilesAppKey);
        _suppressRecentFilesEvents = true;
        try
        {
            RecentFilesComboBox.ItemsSource = entries.Select(BuildRecentFilesEntry).ToArray();
            RecentFilesComboBox.SelectedIndex = ResolveRecentFilesSelectedIndex(entries);
            RecentFilesComboBox.IsEnabled = entries.Count > 0;
            RecentFilesComboBox.PlaceholderText = entries.Count > 0 ? "選択して再読み込み" : "(履歴なし)";
        }
        finally
        {
            _suppressRecentFilesEvents = false;
        }
    }

    private int ResolveRecentFilesSelectedIndex(IReadOnlyList<string> entries)
    {
        if (string.IsNullOrEmpty(_lastLoadedFilePath)) return -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i], _lastLoadedFilePath, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static ComboBoxItem BuildRecentFilesEntry(string path)
    {
        var item = new ComboBoxItem { Content = Path.GetFileName(path), Tag = path };
        ToolTip.SetTip(item, path);
        return item;
    }

    private void RecentFilesComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressRecentFilesEvents) return;
        if (RecentFilesComboBox.SelectedItem is not ComboBoxItem item) return;
        var path = item.Tag as string;
        if (string.IsNullOrWhiteSpace(path)) return;
        _ = ImportSpectrumFilesAsync(new[] { path });
    }

    // 履歴 ComboBox の右クリックメニュー → 「履歴をクリア」。
    // 履歴 (MRU) と表示中のプロット・データセットの寿命を揃える (GPC / DLS と同じ方針)。
    //
    // v1.3.5: 旧実装は確認なしで履歴 + データセット + プロットを一気に消していた。
    //         GPC / DLS と同じく ConfirmDialog を挟み、誤って作業中のグラフを消す事故を防ぐ。
    private async void ClearRecentFilesMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmDialog.ShowAsync(
            this,
            title: "履歴とプロットをクリアしますか?",
            message: "最近開いたファイルの履歴と、現在表示中のグラフ・データセットをすべて破棄します。",
            confirmLabel: "クリア",
            isDestructive: true);
        if (!confirmed) return;

        RecentFilesStore.Clear(RecentFilesAppKey);
        _lastLoadedFilePath = null;

        _currentDataset = null;
        _loadedDatasets.Clear();
        _datasetStyles.Clear();
        _activeIndex = -1;
        if (_spectrumPlot is not null)
        {
            _spectrumPlot.Plot.Clear();
            _spectrumPlot.Refresh();
        }
        RefreshDatasetEntries();
        SetGraphActionsEnabled(false);

        _missingFileWatcher?.Watch(null);

        RefreshRecentFilesUi();
        SetStatus("最近開いたファイルの履歴とプロットをクリアしました。", StatusSeverity.Info);
    }

    // 読み込み中のファイルが OS 側で削除 / リネームされた瞬間に MissingFileWatcher から
    // UI スレッド経由で呼ばれる。GPC / DLS と同方針で MRU 履歴は触らず、表示中の
    // プロットとデータセット内部状態だけクリアする。
    private void OnLoadedFileMissing()
    {
        var name = string.IsNullOrEmpty(_lastLoadedFilePath)
            ? "ファイル"
            : Path.GetFileName(_lastLoadedFilePath);

        _missingFileWatcher?.Watch(null);
        _lastLoadedFilePath = null;
        _currentDataset = null;
        _loadedDatasets.Clear();
        _datasetStyles.Clear();
        _activeIndex = -1;
        if (_spectrumPlot is not null)
        {
            _spectrumPlot.Plot.Clear();
            _spectrumPlot.Refresh();
        }
        RefreshDatasetEntries();
        SetGraphActionsEnabled(false);

        SetStatus($"{name} が削除されたためプロットをクリアしました。", StatusSeverity.Info);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 直近セッションのウィンドウサイズ・位置を復元する。
        WindowStateStore.ApplyTo(this, RecentFilesAppKey);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        WindowStateStore.PersistFrom(this, RecentFilesAppKey);
        _missingFileWatcher?.Dispose();
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var cmd = e.HasCommandModifier();
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (cmd && shift)
        {
            switch (e.Key)
            {
                case Key.S: SaveSessionButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.O: LoadSessionButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
            }
        }
        else if (cmd)
        {
            switch (e.Key)
            {
                case Key.O: OpenSpectrumButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.S: SaveGraphButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.E: ExportDataButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.R: AxisRangePanel.ResetToAuto(); e.Handled = true; return;
                case Key.G: GraphFormatPanel.TogglePlotGrid(); e.Handled = true; return;
                case Key.L: ToggleCheckBox(OverlayCheckBox); e.Handled = true; return;
            }
        }
        else if (e.Key == Key.F2)
        {
            FocusLegendNameTextBox();
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.F1)
        {
            // v1.3 Batch D: F1 で共通のショートカット一覧を Modal で開く。
            global::LabPlot.Core.Avalonia.KeyboardShortcutsWindow.ShowFor(this, global::LabPlot.Core.Avalonia.AppKind.Spectrum);
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.Escape)
        {
            // Forward to active modes; harmless if all are inactive.
            if (_isIntegrationDragMode) { ExitIntegrationDragMode(canceled: true); e.Handled = true; return; }
            if (_isIntegrationResizing) { CancelIntegrationResize(); e.Handled = true; return; }
            if (_isManualLambdaMaxAddMode) { ExitManualLambdaMaxAddMode(canceled: true); e.Handled = true; return; }
            if (_isManualIrPeakAddMode) { ExitManualIrPeakAddMode(canceled: true); e.Handled = true; return; }
        }

        base.OnKeyDown(e);
    }

    private static void ToggleCheckBox(CheckBox checkBox)
    {
        if (checkBox is null || !checkBox.IsEnabled) return;
        checkBox.IsChecked = checkBox.IsChecked != true;
    }

    private void FocusLegendNameTextBox()
    {
        if (LegendNameTextBox is null || !LegendNameTextBox.IsEnabled) return;
        LegendNameTextBox.Focus();
        LegendNameTextBox.SelectAll();
    }

    private async Task<IStorageFolder?> GetDefaultStartLocationAsync(IStorageProvider sp)
    {
        // ユーザ設定の DefaultOutputDirectory がなければ、macOS は ~/Documents に
        // フォールバック (docs §7.4 の SuggestedStartLocation null → ~ 落ちを回避)。
        var dir = FormattingDefaultsStore.GetEffectiveDefaultOutputDirectory(_formattingDefaults);
        if (string.IsNullOrEmpty(dir)) return null;
        try { return await sp.TryGetFolderFromPathAsync(dir); }
        catch { return null; }
    }

    private string? GetDefaultOutputDirectoryIfExists()
        => FormattingDefaultsStore.GetExistingDefaultOutputDirectory(_formattingDefaults);

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

    private enum AnalysisExportFormat { Csv, Xlsx }

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
                if (_isEnabled == value) return;
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
                ColorBrush = new SolidColorBrush(HexToAvaloniaColor(assignment.ColorHex)),
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
            set { if (_label == value) return; _label = value; OnPropertyChanged(); }
        }

        public string XMinText
        {
            get => _xMinText;
            set { if (_xMinText == value) return; _xMinText = value; OnPropertyChanged(); }
        }

        public string XMaxText
        {
            get => _xMaxText;
            set { if (_xMaxText == value) return; _xMaxText = value; OnPropertyChanged(); }
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
            set { if (_rubberBandSegmentsText == value) return; _rubberBandSegmentsText = value; OnPropertyChanged(); }
        }

        public string PolynomialOrderText
        {
            get => _polynomialOrderText;
            set { if (_polynomialOrderText == value) return; _polynomialOrderText = value; OnPropertyChanged(); }
        }

        public bool IsRubberBand =>
            _baseline is BaselineMethod.RubberBand or BaselineMethod.RubberBandHull;

        public bool IsPolynomial => _baseline == BaselineMethod.Polynomial;

        public IntegrationRegion? ToModel()
        {
            if (string.IsNullOrWhiteSpace(_label)) return null;
            if (!TryParseDouble(_xMinText, out var xMin) || !TryParseDouble(_xMaxText, out var xMax))
            {
                return null;
            }

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
        GraphFormatPanel.Capture(config);

        config.ShowTitle = TitleVisibleCheckBox.IsChecked == true;
        config.TitleBold = TitleBoldCheckBox.IsChecked == true;
        config.AxisLabelBold = AxisLabelBoldCheckBox.IsChecked == true;

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

        config.ShowIrPeakMarkers = ShowIrPeakCheckBox.IsChecked == true;
        config.IrPeakMinAbsorbance = TryParseNonNegativeDouble(IrPeakMinAbsorbanceTextBox.Text, out var irPeakMin)
            ? irPeakMin
            : 0.05;
        config.IrPeakMinProminence = TryParseNonNegativeDouble(IrPeakMinProminenceTextBox.Text, out var irPeakProm)
            ? irPeakProm
            : 0.02;
        config.IrPeakCount = int.TryParse(IrPeakCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var irPeakCount)
            && irPeakCount >= 0
            ? irPeakCount
            : 5;
        config.ManualIrPeakEntries = _manualIrPeakEntryVms.Select(vm => vm.ToModel()).ToList();

        config.ShowCloudPointMarkers = ShowCloudPointCheckBox.IsChecked == true;
        config.CloudPointMethod = GetSelectedCloudPointMethodConfigValue();
        config.CloudPointThresholdPercent = TryParseNonNegativeDouble(CloudPointThresholdTextBox.Text, out var cpThreshold)
            ? cpThreshold
            : 50.0;
        config.ShowCloudPointFitCurve = ShowSigmoidFitCurveCheckBox.IsChecked == true;
        config.ShowCloudPointFitParameters = ShowSigmoidFitParametersCheckBox.IsChecked == true;
        config.ShowTemperatureScanMetadata = ShowMetadataCheckBox.IsChecked == true;
        config.DefaultOutputDirectory = DefaultOutputDirectoryTextBox.Text;
        config.Calibration = _formattingConfig.Calibration;

        config.Normalize();
        return config;
    }

    private void ApplyFormattingConfigToControls(GraphFormattingConfig config)
    {
        config.Normalize();

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

            ShowIrPeakCheckBox.IsChecked = config.ShowIrPeakMarkers;
            IrPeakMinAbsorbanceTextBox.Text = config.IrPeakMinAbsorbance.ToString("0.###", CultureInfo.InvariantCulture);
            IrPeakMinProminenceTextBox.Text = config.IrPeakMinProminence.ToString("0.###", CultureInfo.InvariantCulture);
            IrPeakCountTextBox.Text = config.IrPeakCount.ToString(CultureInfo.InvariantCulture);
            ApplyManualIrPeakEntries(config.ManualIrPeakEntries);

            ShowCloudPointCheckBox.IsChecked = config.ShowCloudPointMarkers;
            if (!SelectComboBoxByTag(CloudPointMethodComboBox, config.CloudPointMethod ?? "Midpoint"))
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
            LegendNameTextBox.Text = string.Empty;
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

    private async void OpenSpectrumButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var allowMultiple = OverlayCheckBox.IsChecked == true;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = allowMultiple
                ? "JASCO スペクトルを開く（複数選択可）"
                : "JASCO スペクトルを開く",
            AllowMultiple = allowMultiple,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JASCO スペクトル") { Patterns = new[] { "*.txt", "*.csv" } },
                new FilePickerFileType("JASCO TXT") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("JASCO CSV") { Patterns = new[] { "*.csv" } },
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

        await ImportSpectrumFilesAsync(fileNames);
    }

    /// <summary>
    /// <see cref="IPortalFileOpener.OpenFilesAsync"/> の実装。Portal からのファイル
    /// drop / 最近開いたファイルクリックの 1 本道として、Window が表示完了する
    /// (Loaded) まで待ってから既存の <see cref="ImportSpectrumFilesAsync"/> に流す。
    /// </summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0) return;
        await this.WhenLoadedAsync();
        await ImportSpectrumFilesAsync(filePaths.ToArray());
    }

    private async Task ImportSpectrumFilesAsync(string[] fileNames)
    {
        if (fileNames is null || fileNames.Length == 0) return;

        try
        {
            OpenSpectrumButton.IsEnabled = false;
            SetStatus("スペクトルデータを読み込み中です...", false);
            var busyMessage = fileNames.Length == 1
                ? "スペクトルを読み込み中…"
                : $"{fileNames.Length} ファイルを読み込み中…";
            BusyOverlay.Show(busyMessage);

            // GPC PR #11 と同じパターン: 直列の Task.Run(Select(...).ToArray()) は
            // I/O と Shift-JIS デコード + parse を 1 スレッドに直列化してしまうため、
            // 複数ファイル open 時の待ち時間がファイル数に線形比例していた。
            // Task.WhenAll で各 Read() を独立 Task として走らせ、結果の順序は
            // Select の入力順を保つ (WhenAll は配列順を維持)。
            var datasets = await Task.WhenAll(
                fileNames.Select(fileName => Task.Run(() => _reader.Read(fileName))));
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

            // v1.3 Batch E: 読み込み成功時のみ MRU に追加する。
            foreach (var fileName in fileNames.Reverse())
            {
                RecentFilesStore.Add(RecentFilesAppKey, fileName);
            }
            // MRU の最上位 (= fileNames[0]) を選択状態のまま残し、現在開いているファイルを可視化する。
            _lastLoadedFilePath = fileNames[0];
            (_missingFileWatcher ??= new MissingFileWatcher(OnLoadedFileMissing)).Watch(_lastLoadedFilePath);
            RefreshRecentFilesUi();

            // v1.3 Batch H: タイトルバー Subtitle と Window Title にファイル名を反映。
            var primaryName = Path.GetFileName(fileNames[0]);
            var subtitle = fileNames.Length == 1 ? primaryName : $"{primaryName} 他 {fileNames.Length - 1} 件";
            if (MainTitleBar is not null) MainTitleBar.Subtitle = subtitle;
            Title = $"Spectrum Analyzer — {subtitle}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            // v1.3.5: GPC と同方針で「読み込み失敗時の既存グラフ温存」に揃える。
            //         await Task.WhenAll は 1 ファイル失敗で全 Task をまとめて throw するため、
            //         AddLoadedDataset / PlotCurrentDataset 到達前に脱出し partial 書込は発生
            //         しない。既存 dataset を全 Clear する旧挙動はユーザー予期に反するため撤去。
            ShowError($"読み込みに失敗しました: {ex.Message}");
        }
        finally
        {
            OpenSpectrumButton.IsEnabled = true;
            BusyOverlay.Hide();
        }
    }

    private void AddLoadedDataset(SpectrumDataset dataset)
    {
        var overlay = OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0;
        if (!overlay)
        {
            _loadedDatasets.Clear();
            _datasetStyles.Clear();

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

    private void OverlayCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_currentDataset is not null)
        {
            PlotCurrentDataset();
        }
    }

    private async void ResetGraphSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        // v1.3 Batch I: 破壊的操作なので確認ダイアログを挟む。
        var confirmed = await ConfirmDialog.ShowAsync(
            this,
            title: "既定値に戻す",
            message: "現在のグラフ書式・線スタイル・軸範囲を、保存されている既定値に戻します。よろしいですか?",
            confirmLabel: "戻す",
            isDestructive: true);
        if (!confirmed) return;

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

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset?.SourceFilePath) ?? "spectrum_analysis";
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

    private static AnalysisExportFormat GetAnalysisExportFormat(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)) return AnalysisExportFormat.Csv;
        return AnalysisExportFormat.Xlsx;
    }

    private static string EnsureAnalysisExportExtension(string filePath, AnalysisExportFormat format)
    {
        var extension = format == AnalysisExportFormat.Csv ? ".csv" : ".xlsx";
        return Path.ChangeExtension(filePath, extension);
    }

    private async void SaveSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_loadedDatasets.Count == 0)
        {
            ShowError("保存できる解析がありません。");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset?.SourceFilePath) ?? "spectrum_session";
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "解析条件を保存",
            SuggestedFileName = $"{defaultName}.specjson",
            DefaultExtension = "specjson",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Spectrum セッション") { Patterns = new[] { "*.specjson" } },
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
            _sessionStore.Save(session, path);
            SetStatus($"解析条件を保存しました: {path}", StatusSeverity.Success);
            Toast?.Show("解析条件を保存しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
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
                new FilePickerFileType("Spectrum セッション") { Patterns = new[] { "*.specjson", "*.json" } },
                FilePickerFileTypes.All,
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var session = _sessionStore.Load(path);
            var warnings = new List<string>();
            ApplyAnalysisSession(session, warnings);

            if (warnings.Count == 0)
            {
                SetStatus($"解析条件を読み込みました: {path}", false);
            }
            else
            {
                SetStatus($"解析条件を読み込みましたが、一部に注意があります: {string.Join(" / ", warnings)}", true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FileNotFoundException)
        {
            ShowError($"読込に失敗しました: {ex.Message}");
        }
    }

    private SpectrumAnalysisSession BuildAnalysisSession()
    {
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
            session.Datasets.Add(SpectrumSessionMapper.ToSessionDataset(dataset, style));
        }

        return session;
    }

    private void ApplyAnalysisSession(SpectrumAnalysisSession session, List<string> warnings)
    {
        var loaded = new List<SpectrumDataset>();
        var styles = new List<DatasetStyle>();

        foreach (var entry in session.Datasets)
        {
            if (string.IsNullOrWhiteSpace(entry.SourceFilePath)) continue;

            try
            {
                var dataset = _reader.Read(entry.SourceFilePath);
                loaded.Add(dataset);
                styles.Add(SpectrumSessionMapper.ToDatasetStyle(entry.Style));
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

    private async void SaveGraphButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentDataset is null || _spectrumPlot is null)
        {
            ShowError("保存するグラフがありません。");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset.SourceFilePath) ?? "spectrum";
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

            ApplyExportStyleScale(exportStyleScale);
            try
            {
                if (saveFormat == GraphSaveFormat.Svg)
                {
                    GraphSaveHelpers.SaveGraphSvg(_spectrumPlot.Plot, fileName, width, height);
                    SetStatus($"グラフをSVGで保存しました: {fileName} ({width:N0} x {height:N0})", StatusSeverity.Success);
                    Toast?.Show("SVG を保存しました", StatusSeverity.Success);
                    return;
                }

                GraphSaveHelpers.SaveGraphPng(_spectrumPlot.Plot, fileName, width, height, GraphSaveHelpers.ExportDpi);
                SetStatus($"グラフをPNGで保存しました: {fileName} ({width:N0} x {height:N0} px, {GraphSaveHelpers.ExportDpi} dpi)", StatusSeverity.Success);
                Toast?.Show("PNG を保存しました", StatusSeverity.Success);
            }
            finally
            {
                ApplyExportStyleScale(1f);
                _spectrumPlot.Refresh();
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
            _spectrumPlot = new AvaPlot();
            _spectrumPlot.PointerReleased += SpectrumPlot_PointerInteractionFinished;
            _spectrumPlot.PointerWheelChanged += SpectrumPlot_PointerInteractionFinished;

            // Phase 7 Batch 6 step 3: WPF 同等の凡例ドラッグ移動を有効化。
            // 積分領域 drag (下の 3 つの AddHandler) より先に Attach することで、
            // 同じ Tunnel フェーズで Subscribe 順が早くなり、凡例 hit を先に拾える。
            _legendDragController = new LegendDragController(
                _spectrumPlot,
                () => _formattingConfig.LegendPosition,
                () => (_formattingConfig.LegendOffsetX, _formattingConfig.LegendOffsetY),
                OnLegendDragCommit);
            _legendDragController.Attach();

            // パン / ホイールズーム操作中だけ AA を切って描画を軽くする。
            _plotFastModeController = new PlotFastModeController(
                _spectrumPlot,
                () => _spectrumPlot!.Plot.GetPlottables());
            _plotFastModeController.Attach();

            PlotContextMenu.Apply(_spectrumPlot, () => SaveGraphButton_Click(this, new RoutedEventArgs()));

            // Permanent handlers driving edge-resize for existing integration regions.
            _spectrumPlot.AddHandler(PointerMovedEvent, IntegrationResize_PointerMoved, RoutingStrategies.Tunnel);
            _spectrumPlot.AddHandler(PointerPressedEvent, IntegrationResize_PointerPressed, RoutingStrategies.Tunnel);
            _spectrumPlot.AddHandler(PointerReleasedEvent, IntegrationResize_PointerReleased, RoutingStrategies.Tunnel);

            PlotHost.Children.Clear();
            PlotHost.Children.Add(_spectrumPlot);

            _integrationDragOverlay = new Canvas
            {
                Background = null,
                IsHitTestVisible = false,
            };
            PlotHost.Children.Add(_integrationDragOverlay);

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
        if (_spectrumPlot is null) return;

        // データ無しの状態 — placeholder を「ファイルを読み込むと…」に切り替え。
        // 起動時 (InitializePlotControl 直後) と全データセット削除時の両方から呼ばれる。
        PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.EmptyReady);

        // 全データセット削除パスから呼ばれる時、ScottPlot.Plot に残っている Scatter 要素を
        // 明示的に消さないと「空状態のラベルだけ書き換えて、過去のデータ曲線が残ったまま」
        // というゴースト描画になる。DLS 版に揃える (GPC も同 fix を入れている)。
        _spectrumPlot.Plot.Clear();

        _spectrumPlot.Plot.Title(DefaultLabels.PlaceholderTitle);
        _spectrumPlot.Plot.XLabel(DefaultLabels.PlaceholderXLabel);
        _spectrumPlot.Plot.YLabel(DefaultLabels.PlaceholderYLabel);
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

        // データを描画するので placeholder を非表示にする。
        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);

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

        _spectrumPlot.Plot.Title(GetGraphTitle(Path.GetFileNameWithoutExtension(activeDataset.SourceFilePath) ?? DefaultLabels.SpectrumFallbackTitle));
        _spectrumPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, activeDataset.XLabel));
        _spectrumPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, SpectrumYAxisConverter.GetDisplayYLabel(activeDataset, yDisplayMode)));
        _spectrumPlot.Plot.Axes.AutoScale();

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
        DrawIrPeakMarkers(plotEntries, yRange);
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
        if (_spectrumPlot is null) return;
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
        if (_spectrumPlot is null) return;
        ApplyPlotAppearance(scale);
        ApplyExistingSeriesStyles(scale);
    }

    private void ApplyExistingSeriesStyles(float scale)
    {
        if (_spectrumPlot is null) return;

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
        if (!HasCustomLegendName(datasetIndex)) return null;
        return _datasetStyles[datasetIndex].LegendName!.Trim();
    }

    private string GetSeriesLegendText(SpectrumDataset dataset, string fallback, int datasetIndex)
    {
        var customName = GetCustomLegendName(datasetIndex);
        if (customName is not null) return customName;

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }

    private bool ApplyAxisLimits(AxisDataRange xRange, AxisDataRange yRange, bool invertX = false)
    {
        if (_spectrumPlot is null) return false;

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

            (min, max) = (max, min);
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

    private void SetStatus(string message, bool isError)
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
                    ColorBrush = new SolidColorBrush(HexToAvaloniaColor(hex)),
                });
            }

            DatasetListPlaceholder.IsVisible = _datasetEntries.Count == 0;

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

    private void DatasetListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDatasetListEvents) return;

        var index = DatasetListBox.SelectedIndex;
        if (index < 0 || index >= _loadedDatasets.Count) return;

        _activeIndex = index;
        _currentDataset = _loadedDatasets[index];
        SyncStyleControlsFromActiveDataset();
        PlotCurrentDataset();
    }

    // ---------- Drag-reorder ----------

    private void OnDatasetListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual srcVisual && FindAncestor<Button>(srcVisual) is not null)
        {
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
        // 行内でクリックされた相対位置を記録。ドラッグ中はこのオフセットを保持して
        // ゴーストが「掴んだ場所」を維持したまま追従する。
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
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

            var sourceIndex = _datasetDragSourceIndex.Value;
            if (sourceIndex < 0 || sourceIndex >= _datasetEntries.Count)
            {
                ResetReorderState();
                return;
            }

            _isInternalReordering = true;
            e.Pointer.Capture(DatasetListBox);
            _reorderCapturedPointer = e.Pointer;

            // カーソル追従ゴースト: ItemTemplate を Build(dataContext) でクローン
            // した Visual を OverlayLayer に乗せる。ベクター描画なのでぼやけない。
            _dragGhost.Show(
                this,
                DatasetListBox.ItemTemplate,
                _datasetEntries[sourceIndex],
                _datasetDragSourceContainer.Bounds.Size,
                e.GetPosition(this),
                _dragGhostPointerOffset);
            _datasetDragSourceContainer.Opacity = 0.4;
        }

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
            if (newIndex > sourceIndex) newIndex--;
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
        // フォールバックばかり走る)。Pointer 位置から自前で hit-test する。
        // Drag ghost は IsHitTestVisible=False + OverlayLayer 上にいるので、
        // この hit-test は ghost に邪魔されない。
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
    // (Explorer から DatasetListBox へ TXT をドロップ) のハンドラのみ。
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
        await ImportSpectrumFilesAsync(paths);
    }

    private void UpdateInsertionLine(ListBoxItem item, bool insertAbove)
    {
        var transformPoint = item.TranslatePoint(new Point(0, 0), DatasetListBox);
        if (transformPoint is null) { HideInsertionLine(); return; }

        var listBoxTopInGrid = DatasetListBox.Bounds.Top;
        var itemTopInGrid = listBoxTopInGrid + transformPoint.Value.Y;
        var lineTop = insertAbove
            ? itemTopInGrid - 3
            : itemTopInGrid + item.Bounds.Height - 3;

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
        if (sender is not Button { Tag: DatasetEntryVm vm }) return;

        var index = _datasetEntries.IndexOf(vm);
        if (index < 0 || index >= _loadedDatasets.Count) return;

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

    // ---------- Style control handlers ----------

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        ApplyDatasetStyle(style => style.ColorHex = LineColorPicker.HexValue);
        RefreshDatasetEntries();
        PlotCurrentDataset();
    }

    private void LegendNameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;

        DatasetStyleCommit.CommitLegendName(LegendNameTextBox, value =>
            ApplyDatasetStyle(style => style.LegendName = value));
        RefreshDatasetEntries();
        SchedulePlotCurrentDataset();
    }

    private void LineWidthTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;

        if (DatasetStyleCommit.TryCommitPositiveDouble(LineWidthTextBox, value =>
                ApplyDatasetStyle(style => style.LineWidth = value)))
        {
            SchedulePlotCurrentDataset();
        }
    }

    private void MarkerSizeTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;

        if (DatasetStyleCommit.TryCommitNonNegativeDouble(MarkerSizeTextBox, value =>
                ApplyDatasetStyle(style => style.MarkerSize = value)))
        {
            SchedulePlotCurrentDataset();
        }
    }

    private void ApplyDatasetStyle(Action<DatasetStyle> mutate)
    {
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count) return;
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
        UpdatePlotHostAspectRatio();
    }

    private void AxisDisplayPanel_AxisOrientationChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void AxisDisplayPanel_YAxisDisplayChanged(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
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
        if (_spectrumPlot is null || _currentDataset is null) return;

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

    private void SpectrumPlot_PointerInteractionFinished(object? sender, EventArgs e)
    {
        SyncAxisInputsFromPlot();
    }

    private void SyncAxisInputsFromPlot()
    {
        if (_spectrumPlot is null) return;

        var limits = _spectrumPlot.Plot.Axes.GetLimits();
        AxisRangePanel.SetXValues(
            double.IsFinite(limits.Left) ? limits.Left : null,
            double.IsFinite(limits.Right) ? limits.Right : null);
        AxisRangePanel.SetYValues(
            double.IsFinite(limits.Bottom) ? limits.Bottom : null,
            double.IsFinite(limits.Top) ? limits.Top : null);
    }

    private void PeakAssignmentCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void LambdaMaxOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void LambdaMaxNumericTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void IrPeakOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void IrPeakNumericTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void CloudPointOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        UpdateSigmoidPanelVisibility();
        SchedulePlotCurrentDataset();
    }

    private void UpdateSigmoidPanelVisibility()
    {
        var isSigmoid = GetComboBoxTag(CloudPointMethodComboBox) == "SigmoidFit";
        SigmoidFitOptionsPanel.IsVisible = isSigmoid;
        CloudPointThresholdPanel.IsEnabled = !isSigmoid;
    }

    private void CloudPointNumericTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void MetadataOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        SchedulePlotCurrentDataset();
    }

    private void PeakAssignmentEnableAllButton_Click(object? sender, RoutedEventArgs e)
    {
        SetAllPeakAssignmentsEnabled(true);
    }

    private void PeakAssignmentDisableAllButton_Click(object? sender, RoutedEventArgs e)
    {
        SetAllPeakAssignmentsEnabled(false);
    }

    private void SetAllPeakAssignmentsEnabled(bool enabled)
    {
        foreach (var vm in _peakAssignmentVms)
        {
            vm.IsEnabled = enabled;
        }
    }

    // ---------- Integration: add region (drag) ----------

    private async void AddIntegrationRegionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isIntegrationDragMode)
        {
            ExitIntegrationDragMode(canceled: true);
            return;
        }

        if (!await ConfirmAbsorbanceForIntegrationAsync()) return;

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

    private async Task<bool> ConfirmAbsorbanceForIntegrationAsync()
    {
        var displayMode = GetSelectedYAxisDisplayMode();
        if (displayMode == YAxisDisplayMode.Absorbance) return true;

        if (!CanAnyLoadedDatasetUseAbsorbance()) return true;

        var dialog = new AbsorbanceConfirmDialog();
        await dialog.ShowDialog(this);

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
        if (_spectrumPlot is null || _isIntegrationDragMode) return;

        _isIntegrationDragMode = true;
        _integrationDragStarted = false;
        _integrationDragTargetVm = targetVm;

        _spectrumPlot.Cursor = new Cursor(StandardCursorType.Cross);
        _spectrumPlot.AddHandler(PointerPressedEvent, IntegrationDrag_PointerPressed, RoutingStrategies.Tunnel);
        _spectrumPlot.AddHandler(PointerMovedEvent, IntegrationDrag_PointerMoved, RoutingStrategies.Tunnel);
        _spectrumPlot.AddHandler(PointerReleasedEvent, IntegrationDrag_PointerReleased, RoutingStrategies.Tunnel);

        AddIntegrationRegionButton.Content = "✕ ドラッグ取消";
        SetStatus($"「{targetVm.Label}」をグラフ上でドラッグして範囲を指定（Esc / 右クリック / 同ボタン再押下でキャンセル）", false);
    }

    private void ExitIntegrationDragMode(bool canceled)
    {
        if (!_isIntegrationDragMode || _spectrumPlot is null) return;

        _isIntegrationDragMode = false;
        _integrationDragStarted = false;
        _integrationDragTargetVm = null;

        _spectrumPlot.Cursor = null;
        _spectrumPlot.RemoveHandler(PointerPressedEvent, IntegrationDrag_PointerPressed);
        _spectrumPlot.RemoveHandler(PointerMovedEvent, IntegrationDrag_PointerMoved);
        _spectrumPlot.RemoveHandler(PointerReleasedEvent, IntegrationDrag_PointerReleased);

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

    private void IntegrationDrag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_spectrumPlot is null || _integrationDragOverlay is null) return;

        var props = e.GetCurrentPoint(_spectrumPlot).Properties;
        if (props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            ExitIntegrationDragMode(canceled: true);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        _integrationDragStartPoint = e.GetPosition(_integrationDragOverlay);
        _integrationDragStarted = true;

        ClearIntegrationDragPreview();
        _integrationDragPreview = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            StrokeThickness = 1,
            StrokeDashArray = new AvaloniaList<double> { 4, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(50, 0x94, 0xA3, 0xB8)),
            Width = 0,
            Height = _integrationDragOverlay.Bounds.Height,
        };
        Canvas.SetLeft(_integrationDragPreview, _integrationDragStartPoint.X);
        Canvas.SetTop(_integrationDragPreview, 0);
        _integrationDragOverlay.Children.Add(_integrationDragPreview);

        e.Pointer.Capture(_spectrumPlot);
        e.Handled = true;
    }

    private void IntegrationDrag_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_integrationDragStarted || _integrationDragPreview is null || _integrationDragOverlay is null) return;

        var current = e.GetPosition(_integrationDragOverlay);
        var left = Math.Min(_integrationDragStartPoint.X, current.X);
        var width = Math.Abs(current.X - _integrationDragStartPoint.X);
        Canvas.SetLeft(_integrationDragPreview, left);
        Canvas.SetTop(_integrationDragPreview, 0);
        _integrationDragPreview.Width = width;
        _integrationDragPreview.Height = _integrationDragOverlay.Bounds.Height;
        e.Handled = true;
    }

    private void IntegrationDrag_PointerReleased(object? sender, PointerReleasedEventArgs e)
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
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ---------- Edge resize ----------

    private void IntegrationResize_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_spectrumPlot is null) return;
        if (_isIntegrationDragMode) return;
        if (_isManualLambdaMaxAddMode) return;
        if (_isManualIrPeakAddMode) return;

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

        var hover = FindIntegrationEdgeAt(pos);
        _spectrumPlot.Cursor = hover.Vm is null ? null : new Cursor(StandardCursorType.SizeWestEast);
    }

    private void IntegrationResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_spectrumPlot is null) return;
        if (_isIntegrationDragMode) return;
        if (_isIntegrationResizing) return;
        if (_isManualLambdaMaxAddMode) return;
        if (_isManualIrPeakAddMode) return;

        var props = e.GetCurrentPoint(_spectrumPlot).Properties;

        if (props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed && _isIntegrationResizing)
        {
            CancelIntegrationResize();
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        var pos = e.GetPosition(_spectrumPlot);
        var (vm, isLeft) = FindIntegrationEdgeAt(pos);
        if (vm is null) return;

        _isIntegrationResizing = true;
        _integrationResizeTargetVm = vm;
        _integrationResizeIsLeftEdge = isLeft;
        _integrationResizeOriginalText = isLeft ? vm.XMinText : vm.XMaxText;

        _spectrumPlot.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        e.Pointer.Capture(_spectrumPlot);

        var side = isLeft ? "X Min" : "X Max";
        SetStatus($"「{vm.Label}」の {side} をドラッグ中（Esc / 右クリックで取消）", false);

        e.Handled = true;
    }

    private void IntegrationResize_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isIntegrationResizing) return;

        var label = _integrationResizeTargetVm?.Label;

        e.Pointer.Capture(null);

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

    private void CancelIntegrationResize()
    {
        if (_integrationResizeTargetVm is not null && _integrationResizeOriginalText is not null)
        {
            if (_integrationResizeIsLeftEdge)
            {
                _integrationResizeTargetVm.XMinText = _integrationResizeOriginalText;
            }
            else
            {
                _integrationResizeTargetVm.XMaxText = _integrationResizeOriginalText;
            }
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

    // ---------- Manual λmax markers ----------

    internal sealed class ManualLambdaMaxEntryVm
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
        if (entries is null) { UpdateManualLambdaMaxEmptyVisibility(); return; }

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
        ManualLambdaMaxEmptyTextBlock.IsVisible = _manualLambdaMaxEntryVms.Count == 0;
        ClearManualLambdaMaxButton.IsEnabled = _manualLambdaMaxEntryVms.Count > 0;
    }

    private void AddManualLambdaMaxButton_Click(object? sender, RoutedEventArgs e)
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
        _spectrumPlot.Cursor = new Cursor(StandardCursorType.Cross);
        _spectrumPlot.AddHandler(PointerPressedEvent, ManualLambdaMaxAdd_PointerPressed, RoutingStrategies.Tunnel);

        AddManualLambdaMaxButton.Content = "✕ クリック取消";
        SetStatus("グラフ上の λmax 位置をクリック（Esc / 右クリック / 同ボタン再押下でキャンセル）", false);
    }

    private void ExitManualLambdaMaxAddMode(bool canceled)
    {
        if (!_isManualLambdaMaxAddMode || _spectrumPlot is null) return;

        _isManualLambdaMaxAddMode = false;
        _spectrumPlot.Cursor = null;
        _spectrumPlot.RemoveHandler(PointerPressedEvent, ManualLambdaMaxAdd_PointerPressed);

        AddManualLambdaMaxButton.Content = "+ クリックで追加";
        if (canceled)
        {
            SetStatus("手動 λmax 追加をキャンセルしました", false);
        }
    }

    private void ManualLambdaMaxAdd_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_spectrumPlot is null || _currentDataset is null)
        {
            ExitManualLambdaMaxAddMode(canceled: true);
            return;
        }

        var props = e.GetCurrentPoint(_spectrumPlot).Properties;
        if (props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            ExitManualLambdaMaxAddMode(canceled: true);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

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

    private void RemoveManualLambdaMaxButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManualLambdaMaxEntryVm vm }) return;
        _manualLambdaMaxEntryVms.Remove(vm);
        UpdateManualLambdaMaxEmptyVisibility();
        SchedulePlotCurrentDataset();
    }

    private void ClearManualLambdaMaxButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_manualLambdaMaxEntryVms.Count == 0) return;
        _manualLambdaMaxEntryVms.Clear();
        UpdateManualLambdaMaxEmptyVisibility();
        SetStatus("手動 λmax マーカーをすべて削除しました", false);
        SchedulePlotCurrentDataset();
    }

    // ---------- Manual IR peak markers ----------

    internal sealed class ManualIrPeakEntryVm
    {
        public required string DatasetKey { get; init; }
        public required double WavenumberCm1 { get; init; }
        public required string DisplayName { get; init; }

        public string DisplayText => string.Create(
            CultureInfo.InvariantCulture,
            $"{DisplayName}: {WavenumberCm1:0} cm⁻¹");

        public ManualIrPeakEntry ToModel() => new()
        {
            DatasetKey = DatasetKey,
            WavenumberCm1 = WavenumberCm1,
        };
    }

    private static string BuildIrPeakDatasetKey(SpectrumDataset dataset, int index)
        => BuildLambdaMaxDatasetKey(dataset, index);

    private void ApplyManualIrPeakEntries(IList<ManualIrPeakEntry>? entries)
    {
        _manualIrPeakEntryVms.Clear();
        if (entries is null) { UpdateManualIrPeakEmptyVisibility(); return; }

        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (string.IsNullOrWhiteSpace(entry.DatasetKey)) continue;
            if (!double.IsFinite(entry.WavenumberCm1)) continue;

            _manualIrPeakEntryVms.Add(new ManualIrPeakEntryVm
            {
                DatasetKey = entry.DatasetKey,
                WavenumberCm1 = entry.WavenumberCm1,
                DisplayName = ResolveDisplayNameForDatasetKey(entry.DatasetKey),
            });
        }

        UpdateManualIrPeakEmptyVisibility();
    }

    private void UpdateManualIrPeakEmptyVisibility()
    {
        ManualIrPeakEmptyTextBlock.IsVisible = _manualIrPeakEntryVms.Count == 0;
        ClearManualIrPeakButton.IsEnabled = _manualIrPeakEntryVms.Count > 0;
    }

    private void AddManualIrPeakButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isManualIrPeakAddMode)
        {
            ExitManualIrPeakAddMode(canceled: true);
            return;
        }

        EnterManualIrPeakAddMode();
    }

    private void EnterManualIrPeakAddMode()
    {
        if (_spectrumPlot is null) return;
        if (_currentDataset is null || !_currentDataset.IsWavenumberAxis)
        {
            SetStatus("IR スペクトルを選択してから手動ピークを追加してください", true);
            return;
        }
        if (_isIntegrationDragMode || _isIntegrationResizing) return;

        if (_isManualLambdaMaxAddMode)
        {
            ExitManualLambdaMaxAddMode(canceled: true);
        }

        _isManualIrPeakAddMode = true;
        _spectrumPlot.Cursor = new Cursor(StandardCursorType.Cross);
        _spectrumPlot.AddHandler(PointerPressedEvent, ManualIrPeakAdd_PointerPressed, RoutingStrategies.Tunnel);

        AddManualIrPeakButton.Content = "✕ クリック取消";
        SetStatus("グラフ上のピーク位置をクリック（Esc / 右クリック / 同ボタン再押下でキャンセル）", false);
    }

    private void ExitManualIrPeakAddMode(bool canceled)
    {
        if (!_isManualIrPeakAddMode || _spectrumPlot is null) return;

        _isManualIrPeakAddMode = false;
        _spectrumPlot.Cursor = null;
        _spectrumPlot.RemoveHandler(PointerPressedEvent, ManualIrPeakAdd_PointerPressed);

        AddManualIrPeakButton.Content = "+ クリックで追加";
        if (canceled)
        {
            SetStatus("手動ピーク追加をキャンセルしました", false);
        }
    }

    private void ManualIrPeakAdd_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_spectrumPlot is null || _currentDataset is null)
        {
            ExitManualIrPeakAddMode(canceled: true);
            return;
        }

        var props = e.GetCurrentPoint(_spectrumPlot).Properties;
        if (props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            ExitManualIrPeakAddMode(canceled: true);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        var pos = e.GetPosition(_spectrumPlot);
        var coord = _spectrumPlot.Plot.GetCoordinates(
            new ScottPlot.Pixel((float)pos.X, (float)pos.Y));
        if (!double.IsFinite(coord.X))
        {
            ExitManualIrPeakAddMode(canceled: true);
            e.Handled = true;
            return;
        }

        var refined = IrPeakFinder.RefineManualPeak(_currentDataset, coord.X);
        if (refined is null)
        {
            SetStatus("クリック位置の近傍に有効なデータ点がありませんでした", true);
            ExitManualIrPeakAddMode(canceled: true);
            e.Handled = true;
            return;
        }

        var datasetIndex = _loadedDatasets.IndexOf(_currentDataset);
        if (datasetIndex < 0) datasetIndex = _activeIndex;

        var key = BuildIrPeakDatasetKey(_currentDataset, datasetIndex);
        var displayName = (datasetIndex >= 0 ? GetCustomLegendName(datasetIndex) : null)
            ?? Path.GetFileNameWithoutExtension(_currentDataset.SourceFilePath)
            ?? $"dataset {Math.Max(0, datasetIndex) + 1}";

        var existing = _manualIrPeakEntryVms.FirstOrDefault(vm =>
            string.Equals(vm.DatasetKey, key, StringComparison.Ordinal)
            && Math.Abs(vm.WavenumberCm1 - refined.WavenumberCm1) < 1.0);
        if (existing is null)
        {
            _manualIrPeakEntryVms.Add(new ManualIrPeakEntryVm
            {
                DatasetKey = key,
                WavenumberCm1 = refined.WavenumberCm1,
                DisplayName = displayName,
            });
            UpdateManualIrPeakEmptyVisibility();
            SetStatus(
                string.Create(CultureInfo.InvariantCulture,
                    $"手動ピークを {refined.WavenumberCm1:0} cm⁻¹ に追加しました"),
                false);
            SchedulePlotCurrentDataset();
        }
        else
        {
            SetStatus("近接位置に既に手動マーカーがあるため追加をスキップしました", false);
        }

        ExitManualIrPeakAddMode(canceled: false);
        e.Handled = true;
    }

    private void RemoveManualIrPeakButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManualIrPeakEntryVm vm }) return;
        _manualIrPeakEntryVms.Remove(vm);
        UpdateManualIrPeakEmptyVisibility();
        SchedulePlotCurrentDataset();
    }

    private void ClearManualIrPeakButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_manualIrPeakEntryVms.Count == 0) return;
        _manualIrPeakEntryVms.Clear();
        UpdateManualIrPeakEmptyVisibility();
        SetStatus("手動ピークマーカーをすべて削除しました", false);
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

    private void RemoveIntegrationRegionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control fe && fe.DataContext is IntegrationRegionVm vm)
        {
            vm.PropertyChanged -= IntegrationRegionVm_PropertyChanged;
            _integrationRegionVms.Remove(vm);
            UpdateIntegrationResults();
            SchedulePlotCurrentDataset();
        }
    }

    private void ClearIntegrationRegionsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_integrationRegionVms.Count == 0) return;

        foreach (var vm in _integrationRegionVms)
        {
            vm.PropertyChanged -= IntegrationRegionVm_PropertyChanged;
        }

        _integrationRegionVms.Clear();
        UpdateIntegrationResults();
        SchedulePlotCurrentDataset();
    }

    private async void ExportIntegrationResultsButton_Click(object? sender, RoutedEventArgs e)
    {
        var validRegions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .Where(region => region is not null)
            .Cast<IntegrationRegion>()
            .ToArray();

        if (validRegions.Length == 0)
        {
            ShowError("出力できる積分結果がありません（領域を追加してください）");
            return;
        }

        var datasets = GetDatasetsToPlotWithIndices();
        if (datasets.Length == 0)
        {
            ShowError("データセットが読み込まれていません");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "積分結果を保存",
            SuggestedFileName = "integration_results.xlsx",
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
            var extension = Path.GetExtension(path);
            if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                export.WriteCsv(path);
            }
            else
            {
                export.WriteXlsx(path);
            }

            SetStatus($"積分結果を保存しました: {path}", StatusSeverity.Success);
            Toast?.Show("積分結果を保存しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
        }
    }

    private void IntegrationRegionVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;

        UpdateIntegrationResults();
        if (_isIntegrationResizing)
        {
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

        IntegrationResultEmptyHintTextBlock.IsVisible = _integrationResultRowVms.Count == 0;
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
        if (_spectrumPlot is null) return;

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
            if (region is null || !region.IsValid) continue;

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
        PeakAssignmentHintTextBlock.IsVisible = !enabled;

        UpdateLambdaMaxUi(dataset);
        UpdateIrPeakUi(dataset);
        UpdateCloudPointUi(dataset);
        UpdateMetadataUi(dataset);
    }

    private void UpdateIrPeakUi(SpectrumDataset? dataset)
    {
        var hasIrScan = AnyDatasetMatches(static d => d.IsWavenumberAxis)
                        || dataset?.IsWavenumberAxis == true;
        ShowIrPeakCheckBox.IsEnabled = hasIrScan;
        IrPeakMinAbsorbanceTextBox.IsEnabled = hasIrScan;
        IrPeakMinProminenceTextBox.IsEnabled = hasIrScan;
        IrPeakCountTextBox.IsEnabled = hasIrScan;
        IrPeakHintTextBlock.IsVisible = !hasIrScan;

        var canAddManual = hasIrScan && _currentDataset?.IsWavenumberAxis == true;
        AddManualIrPeakButton.IsEnabled = canAddManual || _isManualIrPeakAddMode;
        ClearManualIrPeakButton.IsEnabled = _manualIrPeakEntryVms.Count > 0;
        ManualIrPeakItemsControl.IsEnabled = hasIrScan;

        if (_isManualIrPeakAddMode && !canAddManual)
        {
            ExitManualIrPeakAddMode(canceled: true);
        }
    }

    private void UpdateLambdaMaxUi(SpectrumDataset? dataset)
    {
        var hasWavelengthScan = AnyDatasetMatches(static d => d.IsWavelengthScan)
                                || dataset?.IsWavelengthScan == true;
        ShowLambdaMaxCheckBox.IsEnabled = hasWavelengthScan;
        LambdaMaxMinAbsorbanceTextBox.IsEnabled = hasWavelengthScan;
        LambdaMaxCountTextBox.IsEnabled = hasWavelengthScan;
        LambdaMaxHintTextBlock.IsVisible = !hasWavelengthScan;

        var canAddManual = hasWavelengthScan && _currentDataset?.IsWavelengthScan == true;
        AddManualLambdaMaxButton.IsEnabled = canAddManual || _isManualLambdaMaxAddMode;
        ClearManualLambdaMaxButton.IsEnabled = _manualLambdaMaxEntryVms.Count > 0;
        ManualLambdaMaxItemsControl.IsEnabled = hasWavelengthScan;

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
        CloudPointHintTextBlock.IsVisible = !hasTemperatureScan;

        if (!hasTemperatureScan || ShowCloudPointCheckBox.IsChecked != true)
        {
            CloudPointResultTextBlock.Text = string.Empty;
            CloudPointResultTextBlock.IsVisible = false;
            CopyCloudPointResultButton.IsVisible = false;
        }
    }

    private void UpdateMetadataUi(SpectrumDataset? dataset)
    {
        var hasTemperatureScan = AnyDatasetMatches(static d => d.IsTemperatureScan)
                                 || dataset?.IsTemperatureScan == true;
        ShowMetadataCheckBox.IsEnabled = hasTemperatureScan;
        MetadataHintTextBlock.IsVisible = !hasTemperatureScan;
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
        if (_spectrumPlot is null || _integrationRegionVms.Count == 0 || !yRange.HasValue) return;

        var axisLimits = _spectrumPlot.Plot.Axes.GetLimits();
        var bandBottom = axisLimits.Bottom;
        var bandTop = axisLimits.Top;
        var ySpan = bandTop - bandBottom;
        var yPad = ySpan > 0 ? ySpan * 100.0 : 1.0;

        var color = ScottPlot.Color.FromHex("94A3B8");

        foreach (var vm in _integrationRegionVms)
        {
            var region = vm.ToModel();
            if (region is null) continue;

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

    private void DrawIntegrationBaselines(
        (SpectrumDataset Dataset, int Index)[] plotEntries,
        YAxisDisplayMode yDisplayMode)
    {
        if (_spectrumPlot is null || _integrationRegionVms.Count == 0 || plotEntries.Length == 0) return;

        var regions = _integrationRegionVms
            .Select(vm => vm.ToModel())
            .Where(region => region is not null && region.BaselineMethod != BaselineMethod.None)
            .Cast<IntegrationRegion>()
            .ToArray();

        if (regions.Length == 0) return;

        foreach (var (dataset, datasetIndex) in plotEntries)
        {
            if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance)) continue;

            var xs = dataset.XValues;
            if (xs.Length < 2) continue;

            var displayYs = SpectrumYAxisConverter.GetDisplayYValues(dataset, yDisplayMode);
            var datasetColor = ResolveDatasetColor(datasetIndex);

            foreach (var region in regions)
            {
                if (region.XMin < xs[0] || region.XMax > xs[^1]) continue;

                if (region.BaselineMethod == BaselineMethod.Linear)
                {
                    var yAtMin = InterpolateY(xs, displayYs, region.XMin);
                    var yAtMax = InterpolateY(xs, displayYs, region.XMax);
                    if (yAtMin is null || yAtMax is null) continue;

                    var line = _spectrumPlot.Plot.Add.Line(region.XMin, yAtMin.Value, region.XMax, yAtMax.Value);
                    line.LineStyle.Color = datasetColor.WithAlpha((byte)110);
                    line.LineStyle.Width = 1;
                    line.LineStyle.Pattern = ScottPlot.LinePattern.Solid;
                    line.MarkerStyle.IsVisible = false;
                    continue;
                }

                var curve = SpectrumIntegrator.BuildBaselineCurve(dataset, region);
                if (curve is null) continue;

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

    private static double[] ConvertAbsorbanceBaselineToDisplay(double[] absorbanceY, YAxisDisplayMode displayMode)
    {
        if (displayMode != YAxisDisplayMode.Transmittance) return absorbanceY;

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
        if (xs.Length < 2 || ys.Length != xs.Length || x < xs[0] || x > xs[^1]) return null;

        var lo = 0;
        var hi = xs.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] <= x) lo = mid;
            else hi = mid;
        }

        if (xs[lo] == x) return ys[lo];
        if (xs[hi] == x) return ys[hi];

        var t = (x - xs[lo]) / (xs[hi] - xs[lo]);
        var y = ys[lo] + t * (ys[hi] - ys[lo]);
        return double.IsFinite(y) ? y : null;
    }

    private void DrawPeakAssignments(SpectrumDataset dataset, AxisDataRange yRange)
    {
        if (_spectrumPlot is null || !dataset.IsInfraredSpectrum || !yRange.HasValue) return;

        var axisLimits = _spectrumPlot.Plot.Axes.GetLimits();
        var bandBottom = axisLimits.Bottom;
        var bandTop = axisLimits.Top;
        var ySpan = bandTop - bandBottom;
        var yPad = ySpan > 0 ? ySpan * 100.0 : 1.0;
        var labelY = ySpan > 0 ? bandTop - ySpan * 0.02 : bandTop;

        foreach (var vm in _peakAssignmentVms)
        {
            if (!vm.IsEnabled) continue;

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
        var minAbs = TryParseNonNegativeDouble(LambdaMaxMinAbsorbanceTextBox.Text, out var parsed) ? parsed : 0.05;
        var maxCount = int.TryParse(LambdaMaxCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c >= 0
            ? c : 3;
        return new LambdaMaxFinderConfig
        {
            MinimumAbsorbance = minAbs,
            MaxPeaks = maxCount,
            Window = 3,
        };
    }

    private IrPeakFinderConfig BuildIrPeakConfig()
    {
        var minAbs = TryParseNonNegativeDouble(IrPeakMinAbsorbanceTextBox.Text, out var parsed) ? parsed : 0.05;
        var minProm = TryParseNonNegativeDouble(IrPeakMinProminenceTextBox.Text, out var parsedProm) ? parsedProm : 0.02;
        var maxCount = int.TryParse(IrPeakCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c >= 0
            ? c : 5;
        return new IrPeakFinderConfig
        {
            MinimumAbsorbance = minAbs,
            MinimumProminence = minProm,
            MaxPeaks = maxCount,
        };
    }

    private CloudPointDetectionConfig BuildCloudPointConfig()
    {
        var threshold = TryParseNonNegativeDouble(CloudPointThresholdTextBox.Text, out var parsed) ? parsed : 50.0;
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
        return GetComboBoxTag(CloudPointMethodComboBox) switch
        {
            "FirstDerivativePeak" => CloudPointMethod.FirstDerivativePeak,
            "SecondDerivativeExtremum" => CloudPointMethod.SecondDerivativeExtremum,
            "SigmoidFit" => CloudPointMethod.SigmoidFit,
            _ => CloudPointMethod.Midpoint,
        };
    }

    private string? GetSelectedCloudPointMethodConfigValue()
    {
        var tag = GetComboBoxTag(CloudPointMethodComboBox);
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    private void DrawIrPeakMarkers(
        (SpectrumDataset Dataset, int Index)[] plotEntries,
        AxisDataRange yRange)
    {
        if (_spectrumPlot is null
            || ShowIrPeakCheckBox.IsChecked != true
            || plotEntries.Length == 0
            || !yRange.HasValue)
        {
            return;
        }

        var config = BuildIrPeakConfig();
        var axisLimits = _spectrumPlot.Plot.Axes.GetLimits();
        var ySpan = axisLimits.Top - axisLimits.Bottom;
        var labelOffset = ySpan > 0 ? ySpan * 0.04 : 0.05;
        var yDisplayMode = GetSelectedYAxisDisplayMode();

        foreach (var (dataset, datasetIndex) in plotEntries)
        {
            if (!dataset.IsWavenumberAxis) continue;

            var color = ResolveDatasetColor(datasetIndex);
            var displayYs = SpectrumYAxisConverter.GetDisplayYValues(dataset, yDisplayMode);
            var xs = dataset.XValues;

            var peaks = IrPeakFinder.Find(dataset, config);
            foreach (var peak in peaks)
            {
                if (!peak.HasResult) continue;

                var displayY = peak.SampleIndex >= 0 && peak.SampleIndex < displayYs.Length
                    ? displayYs[peak.SampleIndex]
                    : double.NaN;
                if (!double.IsFinite(displayY)) continue;

                DrawIrPeakMarker(peak.WavenumberCm1, displayY, color, isManual: false, axisLimits, labelOffset);
            }

            var datasetKey = BuildIrPeakDatasetKey(dataset, datasetIndex);
            foreach (var vm in _manualIrPeakEntryVms)
            {
                if (!string.Equals(vm.DatasetKey, datasetKey, StringComparison.Ordinal)) continue;
                if (xs.Length == 0) continue;

                var nearest = -1;
                var bestDist = double.PositiveInfinity;
                for (var i = 0; i < xs.Length; i++)
                {
                    var d = Math.Abs(xs[i] - vm.WavenumberCm1);
                    if (d < bestDist) { bestDist = d; nearest = i; }
                }
                if (nearest < 0) continue;
                var manualY = displayYs[nearest];
                if (!double.IsFinite(manualY)) continue;

                DrawIrPeakMarker(vm.WavenumberCm1, manualY, color, isManual: true, axisLimits, labelOffset);
            }
        }
    }

    private void DrawIrPeakMarker(
        double wavenumberCm1, double displayY, ScottPlot.Color color,
        bool isManual, ScottPlot.AxisLimits axisLimits, double labelOffset)
    {
        if (_spectrumPlot is null) return;

        var line = _spectrumPlot.Plot.Add.VerticalLine(wavenumberCm1);
        line.LineStyle.Color = color.WithAlpha((byte)170);
        line.LineStyle.Pattern = isManual ? ScottPlot.LinePattern.Dashed : ScottPlot.LinePattern.Dotted;
        line.LineStyle.Width = 1;
        line.LegendText = string.Empty;

        var marker = _spectrumPlot.Plot.Add.Marker(wavenumberCm1, displayY);
        marker.MarkerStyle.Shape = isManual
            ? ScottPlot.MarkerShape.FilledTriangleDown
            : ScottPlot.MarkerShape.OpenTriangleDown;
        marker.MarkerStyle.Size = 8;
        marker.MarkerStyle.LineColor = color;
        marker.MarkerStyle.LineWidth = 1.5f;
        marker.MarkerStyle.FillColor = isManual ? color : ScottPlot.Colors.White;
        marker.LegendText = string.Empty;

        var labelText = isManual
            ? string.Create(CultureInfo.InvariantCulture, $"{wavenumberCm1:0} cm⁻¹ (手動)")
            : string.Create(CultureInfo.InvariantCulture, $"{wavenumberCm1:0} cm⁻¹");
        var labelY = displayY + labelOffset;
        if (labelY > axisLimits.Top) labelY = displayY - labelOffset;

        var text = _spectrumPlot.Plot.Add.Text(labelText, wavenumberCm1, labelY);
        text.LabelFontColor = color;
        text.LabelFontSize = 10;
        text.LabelAlignment = ScottPlot.Alignment.LowerCenter;
        text.LabelBold = false;
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

            var peaks = LambdaMaxFinder.Find(dataset, config);
            foreach (var peak in peaks)
            {
                if (!peak.HasResult) continue;

                var displayY = peak.SampleIndex >= 0 && peak.SampleIndex < displayYs.Length
                    ? displayYs[peak.SampleIndex]
                    : double.NaN;
                if (!double.IsFinite(displayY)) continue;

                DrawLambdaMaxMarker(peak.WavelengthNm, displayY, color, isManual: false, axisLimits, labelOffset);
            }

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
                    if (d < bestDist) { bestDist = d; nearest = i; }
                }
                if (nearest < 0) continue;
                var manualY = displayYs[nearest];
                if (!double.IsFinite(manualY)) continue;

                DrawLambdaMaxMarker(vm.WavelengthNm, manualY, color, isManual: true, axisLimits, labelOffset);
            }
        }
    }

    private void DrawLambdaMaxMarker(
        double wavelengthNm, double displayY, ScottPlot.Color color,
        bool isManual, ScottPlot.AxisLimits axisLimits, double labelOffset)
    {
        if (_spectrumPlot is null) return;

        var line = _spectrumPlot.Plot.Add.VerticalLine(wavelengthNm);
        line.LineStyle.Color = color.WithAlpha((byte)170);
        line.LineStyle.Pattern = isManual ? ScottPlot.LinePattern.Dashed : ScottPlot.LinePattern.Dotted;
        line.LineStyle.Width = 1;
        line.LegendText = string.Empty;

        var marker = _spectrumPlot.Plot.Add.Marker(wavelengthNm, displayY);
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
        CloudPointResultTextBlock.IsVisible = false;
        CloudPointResultTextBlock.Text = string.Empty;
        CopyCloudPointResultButton.IsVisible = false;

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

        if (rows.Count == 0) return;

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

        var heating = rows.Where(r => r.Result.Direction == ScanDirection.Heating).Select(r => r.Result).FirstOrDefault();
        var cooling = rows.Where(r => r.Result.Direction == ScanDirection.Cooling).Select(r => r.Result).FirstOrDefault();
        var delta = HysteresisAnalyzer.ComputeHysteresis(heating, cooling);
        if (double.IsFinite(delta))
        {
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "ヒステリシス ΔT = Tc(降温) − Tc(昇温) = {0:+0.00;-0.00;0.00} °C",
                delta));
        }

        CloudPointResultTextBlock.Text = string.Join(Environment.NewLine, lines);
        CloudPointResultTextBlock.IsVisible = true;
        // v1.3 Batch J: 結果が出ているときだけ「結果コピー」を有効にする。
        CopyCloudPointResultButton.IsVisible = true;
    }

    // v1.3 Batch J: Cloud Point の Tc / k / R² ブロックをそのままクリップボードへ。
    // 複数行 (Tc / 遷移幅 / R² / ヒステリシス) を改行付きで丸ごとコピーする。
    private async void CopyCloudPointResultButton_Click(object? sender, RoutedEventArgs e)
    {
        var text = CloudPointResultTextBlock.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            Toast?.Show("コピーできる結果がありません", StatusSeverity.Warning);
            return;
        }
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Toast?.Show("クリップボードを利用できません", StatusSeverity.Error);
                return;
            }
            await clipboard.SetTextAsync(text);
            Toast?.Show("曇点解析結果をコピーしました", StatusSeverity.Success);
        }
        catch (Exception)
        {
            Toast?.Show("コピーに失敗しました", StatusSeverity.Error);
        }
    }

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

        var dataset = plotEntries.Select(e => e.Dataset).FirstOrDefault(d => d.IsTemperatureScan);
        if (dataset is null) return;

        var lines = BuildMetadataLines(dataset);
        if (lines.Count == 0) return;

        var text = string.Join("\n", lines);
        var annotation = _spectrumPlot.Plot.Add.Annotation(text);
        annotation.Alignment = ScottPlot.Alignment.UpperRight;
        annotation.LabelFontSize = 10;
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
        if (dataset.MeasurementWavelengthText is { } wavelength) lines.Add($"測定波長: {wavelength}");
        if (dataset.TemperatureRampRateText is { } ramp) lines.Add($"温度勾配: {ramp}");
        if (dataset.AccessoryName is { } accessory) lines.Add($"付属品: {accessory}");
        if (dataset.BandPassText is { } bandpass) lines.Add($"バンド幅: {bandpass}");
        if (dataset.PhotometricMode is { } mode) lines.Add($"測光モード: {mode}");
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

    private double? GetSelectedAspectRatio() => GraphFormatPanel.AspectRatioValue;

    private void UpdatePlotHostAspectRatio()
        => PlotHostAspectRatio.Apply(PlotHost, PlotContainerBorder, GetSelectedAspectRatio());

    private (int Width, int Height) GetExportImageSize()
        => GraphSaveHelpers.GetExportImageSize(GetSelectedAspectRatio());

    // ---------- Calibration ----------

    private async void OpenCalibrationEditorButton_Click(object? sender, RoutedEventArgs e)
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
            GetDefaultOutputDirectoryIfExists());

        var ok = await window.ShowDialog<bool?>(this);
        if (ok == true)
        {
            _formattingConfig.Calibration = window.ResultConfig;
            _formattingDefaults.Calibration = window.ResultConfig;
            try
            {
                SaveFormattingDefaults();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ShowError($"検量線設定を保存できませんでした: {ex.Message}");
            }

            UpdateCalibrationUi();
            SetStatus("検量線を更新しました", false);
        }
    }

    private async void ExportCalibrationResultsButton_Click(object? sender, RoutedEventArgs e)
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
            ShowError("出力できるデータセットがありません");
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

        var sp = StorageProvider;
        if (sp is null) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "検量線結果を保存",
            SuggestedFileName = "calibration_curve.xlsx",
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

        var export = new CalibrationExport
        {
            Config = calibration,
            Result = result,
            Rows = exportRows,
        };

        try
        {
            var ext = Path.GetExtension(path);
            if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase))
            {
                export.WriteCsv(path);
            }
            else
            {
                export.WriteXlsx(path);
            }

            SetStatus($"検量線結果を保存しました: {path}", StatusSeverity.Success);
            Toast?.Show("検量線を保存しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
        }
    }

    private void UpdateCalibrationUi()
    {
        var hasMinimumDatasets = _loadedDatasets.Count >= 2;
        OpenCalibrationEditorButton.IsEnabled = hasMinimumDatasets;
        CalibrationHintTextBlock.IsVisible = !hasMinimumDatasets;

        var calibration = _formattingConfig.Calibration;
        if (calibration is null || _loadedDatasets.Count == 0)
        {
            CalibrationSummaryBorder.IsVisible = false;
            ExportCalibrationResultsButton.IsVisible = false;
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
            CalibrationSummaryBorder.IsVisible = true;
            ExportCalibrationResultsButton.IsVisible = true;
        }
        else
        {
            CalibrationSummaryTextBlock.Text = "検量線が未確定（エディタで濃度を 2 件以上割り当ててください）";
            CalibrationSummaryBorder.IsVisible = true;
            ExportCalibrationResultsButton.IsVisible = false;
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
        if (!string.IsNullOrWhiteSpace(dataset.Title)) return dataset.Title!;
        if (!string.IsNullOrWhiteSpace(dataset.SourceFilePath)) return dataset.SourceFilePath!;
        return $"{displayName}#{index}";
    }
}

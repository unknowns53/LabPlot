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
public partial class MainWindow : Window, IPortalFileOpener
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
        AppDataPaths.GetApplicationDataPath(),
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
    // Tracks the Scatter plottables this MainWindow added during the most
    // recent PlotCurrentDataset() so we can clear them precisely via
    // ClearScatterPool() (Plot.Remove per entry) instead of the broader
    // Plot.Clear() that wipes axes / title / legend state too. ScottPlot
    // 5.1.58 does not expose a setter on Scatter.Data, so true in-place
    // recycling is not currently possible — see ClearScatterPool() doc.
    private readonly List<ScottPlot.Plottables.Scatter> _scatterPool = new();
    private LegendDragController? _legendDragController;
    private PlotFastModeController? _plotFastModeController;
    private bool _updatingCalibrationSelection;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressDatasetListEvents;
    private bool _currentLegendAutoShow;
    private bool _forceFullResolutionPlot;
    private bool _currentPlotUsesDownsampledData;
    private bool _suppressRepresentativePeakSelection;
    private MolecularWeightStatistics? _currentStatistics;

    // サイドバータブ (データ / 仕上げ) の切替。XAML の RadioButton 初期値
    // (IsChecked="True") が InitializeComponent 実行中に IsCheckedChanged を
    // 発火させ、その時点ではまだ DataTabPanel / FormatTabPanel の x:Name
    // フィールドが代入されていないため、ガードなしで参照すると NRE になる。
    // InitializeComponent 完了後に true にし、それまではハンドラを早期 return
    // させる (Data Viewer MainWindow と同じ方式)。
    private bool _sidebarTabsInitialized;

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
        _sidebarTabsInitialized = true;
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

    // ---------- Sidebar tabs (データ / 仕上げ) ----------

    private void SidebarTabRadioButton_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (!_sidebarTabsInitialized) return;
        // RadioButton はグループ内で 1 個が checked になるたび、他方の unchecked
        // イベントも飛んでくる。checked になった側だけを見れば重複処理を避けられる。
        if (sender is not RadioButton { IsChecked: true } radio) return;

        var showDataTab = ReferenceEquals(radio, DataTabRadioButton);
        DataTabPanel.IsVisible = showDataTab;
        FormatTabPanel.IsVisible = !showDataTab;
        SidebarScrollViewer.ScrollToHome();
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

        // v1.3 Batch E: 最近開いた一覧を ComboBox に流す。
        RefreshRecentFilesUi();
    }

    // v1.3 Batch E: 最近開いたファイル MRU。
    // 2026-05-25: 選択後に SelectedIndex=-1 へリセットしていたが、placeholder に潰れて
    // 「今どのファイルを開いているか」が一目で分からなかった。直近で開いたファイルを
    // 選択状態のまま保持する方針に変更。
    private const string RecentFilesAppKey = "gpc";
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
        _ = ImportCsvFilesAsync(new[] { path });
    }

    // 履歴 ComboBox の右クリックメニュー → 「履歴をクリア」。
    // 履歴 (MRU) と表示中のプロット・データセットの寿命を揃える。
    // 履歴だけ消えてグラフが残ると「リセットしたつもり」が画面に痕跡を
    // 残す形になり、ファイル削除と組み合わせると消し方が無くなるため。
    //
    // v1.3.5: 旧実装は確認なしで履歴 + データセット + プロットを一気に消していた。
    //         「履歴クリア」程度のクリック (右クリックメニュー 1 発) で
    //         作業中のグラフまで消えるのは破壊的すぎるため ConfirmDialog を挟む。
    //         Spectrum / DLS と同方針。
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
        _datasetSelectedPeakIds.Clear();
        ClearComputedDataCaches();
        _activeIndex = -1;
        if (_chromatogramPlot is not null)
        {
            _chromatogramPlot.Plot.Clear();
            _chromatogramPlot.Refresh();
        }
        RefreshDatasetEntries();
        SetGraphActionsEnabled(false);
        UpdateStatisticsText((MolecularWeightStatistics?)null);

        _missingFileWatcher?.Watch(null);

        RefreshRecentFilesUi();
        SetStatus("最近開いたファイルの履歴とプロットをクリアしました。", StatusSeverity.Info);
    }

    // 読み込み中のファイルが OS 側で削除 / リネームされた瞬間に MissingFileWatcher から
    // UI スレッド経由で呼ばれる。MRU 履歴は触らず、現在表示中のプロットとデータセット
    // 内部状態だけクリアする (一時的な移動かもしれないので履歴は残し、ユーザが MRU
    // から再選択した時に「ファイルがありません」エラーで気付けば十分とする方針)。
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
        _datasetSelectedPeakIds.Clear();
        ClearComputedDataCaches();
        _activeIndex = -1;
        if (_chromatogramPlot is not null)
        {
            _chromatogramPlot.Plot.Clear();
            _chromatogramPlot.Refresh();
        }
        RefreshDatasetEntries();
        SetGraphActionsEnabled(false);
        UpdateStatisticsText((MolecularWeightStatistics?)null);

        SetStatus($"{name} が削除されたためプロットをクリアしました。", StatusSeverity.Info);
    }

    // WPF の InputBindings 群を OnKeyDown 1 メソッドに集約。
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 直近セッションのウィンドウサイズ・位置を復元する。
        WindowStateStore.ApplyTo(this, RecentFilesAppKey);
        // macOS では "Ctrl+O" のような tooltip 表記を "Cmd+O" に置換 (Windows / Linux は noop)。
        KeyboardShortcuts.LocalizeTooltipsForMac(this);
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
        else if (e.Key == Key.F1)
        {
            // v1.3 Batch D: F1 で共通のショートカット一覧を Modal で開く。
            global::LabPlot.Core.Avalonia.KeyboardShortcutsWindow.ShowFor(this, global::LabPlot.Core.Avalonia.AppKind.Gpc);
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
            SetStatus($"既定の較正曲線が見つかりませんでした: {Path.GetFileName(path)}", true);
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
            SetStatus($"既定の較正曲線を読み込みました: {Path.GetFileName(path)}", false);
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
        // ユーザ設定の DefaultOutputDirectory がなければ、macOS は ~/Documents に
        // フォールバック (docs §7.4 の SuggestedStartLocation null → ~ 落ちを回避)。
        var dir = FormattingDefaultsStore.GetEffectiveDefaultOutputDirectory(_formattingDefaults);
        if (string.IsNullOrEmpty(dir)) return null;
        try { return await sp.TryGetFolderFromPathAsync(dir); }
        catch { return null; }
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

    /// <summary>
    /// <see cref="IPortalFileOpener.OpenFilesAsync"/> の実装。Portal からのファイル
    /// drop / 最近開いたファイルクリックの 1 本道として、Window が表示完了する
    /// (Loaded) まで待ってから既存の <see cref="ImportCsvFilesAsync"/> に流す。
    /// </summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0) return;
        await this.WhenLoadedAsync();
        await ImportCsvFilesAsync(filePaths.ToArray());
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

            // Parse files in parallel via ThreadPool tasks. CsvGpcDataReader
            // holds no instance state (only a static readonly Encoding), so
            // Read() can be called concurrently. Task.WhenAll preserves the
            // input order in the returned array and lets the first
            // IOException / InvalidDataException / ArgumentException surface
            // unwrapped to the existing catch below.
            var datasets = await Task.WhenAll(
                fileNames.Select(fileName => Task.Run(() => _reader.Read(fileName))));
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

            // v1.3 Batch E: 読み込み成功時のみ MRU に追加する。複数ファイル一括の場合は
            // 全件を新しい順で先頭にスタックする。
            foreach (var fileName in fileNames.Reverse())
            {
                RecentFilesStore.Add(RecentFilesAppKey, fileName);
            }
            // MRU の最上位 (= fileNames[0]) を選択状態のまま残し、現在開いているファイルを可視化する。
            _lastLoadedFilePath = fileNames[0];
            (_missingFileWatcher ??= new MissingFileWatcher(OnLoadedFileMissing)).Watch(_lastLoadedFilePath);
            RefreshRecentFilesUi();

            // v1.3 Batch H: タイトルバー Subtitle と Window Title にファイル名を反映。
            // 複数選択時は "n ファイル (一番上の名前)" のような表現に。
            var primaryName = Path.GetFileName(fileNames[0]);
            var subtitle = fileNames.Length == 1 ? primaryName : $"{primaryName} 他 {fileNames.Length - 1} 件";
            if (MainTitleBar is not null) MainTitleBar.Subtitle = subtitle;
            Title = $"GPC Analyzer — {subtitle}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            // v1.3.5: 旧実装は失敗時に _loadedDatasets / _datasetStyles を全 Clear し
            //         「読み込み失敗 → 既存グラフも消失」というユーザーの予期に反する挙動を
            //         していた。await Task.WhenAll は 1 ファイル失敗で全 Task をまとめて
            //         throw するため AddLoadedDataset 開始前に確実に脱出し、partial 書込は
            //         発生しない。よって既存 dataset / グラフは保持したまま ShowError のみ
            //         で return し、ユーザーが直前まで作業していた状態を温存する。
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
            SetStatus($"解析条件を読み込みました: {Path.GetFileName(path)}", false);
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

            datasets.Add(GpcSessionMapper.ToSessionDataset(dataset, style, selectedPeakId));
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
                _datasetStyles.Add(GpcSessionMapper.ToDatasetStyle(sessionDataset.Style));
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

        // v1.3 Batch K: chip 値はカウントアップアニメで補間表示。数値 parse できない
        // 場合 ("-" など) は NumberCountUp 側でアニメをスキップして即時表示する。
        ApplyChipValueAnimated(MnChipValue, match.Groups["mn"].Value, v => GpcResultFormat.FormatMolecularWeight(v));
        ApplyChipValueAnimated(MwChipValue, match.Groups["mw"].Value, v => GpcResultFormat.FormatMolecularWeight(v));
        ApplyChipValueAnimated(DispersityChipValue, match.Groups["pdi"].Value, v => GpcResultFormat.FormatRatio(v));

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
        SetStatisticsLine($"Mn: {GpcResultFormat.FormatMolecularWeight(statistics.Mn)}   Mw: {GpcResultFormat.FormatMolecularWeight(statistics.Mw)}   Ð: {GpcResultFormat.FormatRatio(statistics.Pdi)} ({source})");
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
        var mnChip = BuildStatChip("Mn", GpcResultFormat.FormatMolecularWeight(stats?.Mn));
        Grid.SetColumn(mnChip, 3);
        grid.Children.Add(mnChip);

        var mwChip = BuildStatChip("Mw", GpcResultFormat.FormatMolecularWeight(stats?.Mw));
        Grid.SetColumn(mwChip, 4);
        grid.Children.Add(mwChip);

        var pdiChip = BuildStatChip("Ð", GpcResultFormat.FormatRatio(stats?.Pdi));
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

        return $"{label}   Mn: {GpcResultFormat.FormatMolecularWeight(statistics.Mn)}   Mw: {GpcResultFormat.FormatMolecularWeight(statistics.Mw)}   Ð: {GpcResultFormat.FormatRatio(statistics.Pdi)}";
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

    // v1.3 Batch J: 単一データセット表示時の Mn / Mw / Đ + 代表ピーク名を Tab 区切りで
    // クリップボードへコピーする。重ね描き時は MultiStatisticsScroll 側の各行 chip から
    // 個別 SelectableTextBlock 経由でコピーできるので、ここでは単一表示パスのみ対応。
    // チップの表示テキスト (MnChipValue.Text 等) は分子量チップ表示専用の桁区切り整数書式に
    // なっているため読み取らず、_currentStatistics の生値を従来どおりの書式で整形する。
    private async void CopyStatisticsButton_Click(object? sender, RoutedEventArgs e)
    {
        var peakLabel = string.IsNullOrWhiteSpace(StatisticsPeakLabel.Text) ? "(代表ピーク)" : StatisticsPeakLabel.Text;
        var lines = new[]
        {
            $"ピーク\t{peakLabel}",
            $"Mn\t{GpcResultFormat.FormatRatio(_currentStatistics?.Mn)}",
            $"Mw\t{GpcResultFormat.FormatRatio(_currentStatistics?.Mw)}",
            $"Ð\t{GpcResultFormat.FormatRatio(_currentStatistics?.Pdi)}",
        };
        await CopyResultLinesAsync("分子量統計", lines);
    }

    private async Task CopyResultLinesAsync(string label, string[] lines)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Toast?.Show("クリップボードを利用できません", StatusSeverity.Error);
                return;
            }
            var text = string.Join('\n', lines);
            await clipboard.SetTextAsync(text);
            Toast?.Show($"{label}をコピーしました", StatusSeverity.Success);
        }
        catch (Exception)
        {
            Toast?.Show("コピーに失敗しました", StatusSeverity.Error);
        }
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
            pieces.Add($"Mw {GpcResultFormat.FormatMolecularWeight(peak.Mw)}");
        }
        if (peak.Percent.HasValue && double.IsFinite(peak.Percent.Value))
        {
            pieces.Add($"{GpcResultFormat.FormatRatio(peak.Percent)}%");
        }
        return string.Join("   ", pieces);
    }

    private static int TryParsePeakNumber(string peakId)
    {
        return int.TryParse(peakId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.MaxValue;
    }

    // v1.3 Batch K: NumberCountUp で中間フレームを描くときに使う formatter。呼び出し側が
    // Mn/Mw (分子量: GpcResultFormat.FormatMolecularWeight) と Đ (比率: GpcResultFormat.FormatRatio)
    // のどちらを使うか指定する。
    private static void ApplyChipValueAnimated(TextBlock target, string newText, Func<double, string> formatter)
    {
        if (double.TryParse(newText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var toValue)
            && double.IsFinite(toValue))
        {
            NumberCountUp.Animate(target, toValue, formatter);
        }
        else
        {
            // "-" / 空文字 / 単位付きで parse 不能なら、補間せず即時表示にする。
            NumberCountUp.Cancel(target, newText);
        }
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
        if (!ApplyDatasetStyle(style => style.ColorHex = LineColorPicker.HexValue)) return;

        RefreshDatasetEntries();
        SchedulePlotCurrentDataset();
    }

    private void LegendNameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;

        DatasetStyleCommit.CommitLegendName(LegendNameTextBox, value =>
            ApplyDatasetStyle(style => style.LegendName = value));
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

    /// <summary>
    /// active dataset の Style を mutate する共通ラッパ。Spectrum 側の同名 helper
    /// と揃えて 4 ハンドラの active-index ガード重複を排除。戻り値の bool は
    /// 「実際に mutate したか」(LineColor の RefreshDatasetEntries 抑止用に必要)。
    /// </summary>
    private bool ApplyDatasetStyle(Action<DatasetStyle> mutate)
    {
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count) return false;
        mutate(_datasetStyles[_activeIndex]);
        return true;
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

    private void AxisRangePanel_CaptureCurrentRangeRequested(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        // SetXValues / SetYValues は commit を抑止して書き込むだけなので、
        // 手動範囲の適用経路 (PlotCurrentDataset) を明示的に流して欄とプロットを揃える。
        if (SyncAxisInputsFromPlot())
        {
            PlotCurrentDataset();
        }
    }

    private bool SyncAxisInputsFromPlot()
    {
        if (_chromatogramPlot is null || _currentDataset is null) return false;

        var limits = _chromatogramPlot.Plot.Axes.GetLimits();
        if (!IsFiniteRange(limits.Left, limits.Right) || !IsFiniteRange(limits.Bottom, limits.Top)) return false;

        var xIsMolecularWeight = MolecularWeightCheckBox.IsChecked == true && _selectedCalibrationCurve is not null;
        var xMin = xIsMolecularWeight ? Math.Pow(10, limits.Left) : limits.Left;
        var xMax = xIsMolecularWeight ? Math.Pow(10, limits.Right) : limits.Right;

        AxisRangePanel.SetXValues(xMin, xMax);
        AxisRangePanel.SetYValues(limits.Bottom, limits.Top);
        return true;
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

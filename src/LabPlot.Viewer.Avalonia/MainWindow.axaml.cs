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
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DataViewer.Core;
using LabPlot.Core;
using LabPlot.Core.Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;
using ScottPlot.Avalonia;
using static LabPlot.Core.PlotAppearance;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.Viewer.Avalonia;

/// <summary>
/// 汎用データビューアのメインウィンドウ。任意の表形式データ
/// (CSV / TSV / セミコロン / 空白区切り / xlsx) を読み込み、
/// 「最初の数値列 = X、残りの数値列 = Y 系列」の自動マッピングで重ね描きする。
/// GPC / Spectrum / DLS と同じ Core.Avalonia 共通基盤
/// (CustomTitleBar / GraphFormatPanel / AxisRangePanel / StatusBar /
/// LegendDragController / WindowStateStore) の上に構築する。
/// </summary>
public partial class MainWindow : Window, IPortalFileOpener
{
    private readonly DelimitedTextTableReader _textReader = new();
    private readonly XlsxTableReader _xlsxReader = new();

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

    private const string WindowStateAppKey = "viewer";
    private static readonly TimeSpan PlotRefreshDebounceInterval = TimeSpan.FromMilliseconds(200);
    private static readonly JsonSerializerOptions FormattingConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly string FormattingConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Data_Viewer",
        "formatting_config.json");

    /// <summary>読み込んだ 1 テーブル分の表示状態 (X 列選択 + 系列リスト)。</summary>
    internal sealed class LoadedTable
    {
        public required ViewerTable Table { get; init; }
        public required string DisplayName { get; init; }
        public int XColumnIndex { get; set; }
        public List<SeriesState> Series { get; } = new();
    }

    /// <summary>Y 系列候補 1 列分の状態。スタイルは共有の AnalysisSessionStyle を流用する。</summary>
    internal sealed class SeriesState
    {
        public required int ColumnIndex { get; init; }
        public required string ColumnName { get; init; }
        public bool IsVisible { get; set; } = true;
        public bool UseRightAxis { get; set; }
        public ViewerChartType ChartType { get; set; } = ViewerChartType.Line;
        public SeriesTransform Transform { get; set; } = SeriesTransform.Identity;
        public AnalysisSessionStyle Style { get; } = new();

        /// <summary>描画順・凡例順・auto 色決定の基準になるフラット表示順。
        /// テーブル物理位置ではなくこの値の昇順 (安定ソート) で並べる。</summary>
        public int DisplayOrder { get; set; }
    }

    // ItemTemplate の CompiledBinding DataType から参照されるため public。
    public sealed class TableEntryVm
    {
        public string DisplayName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public SolidColorBrush ColorBrush { get; init; } = new(Colors.Gray);
    }

    /// <summary>「系列」セクションのフラット一覧 1 行ぶんの表示状態。IsVisible
    /// チェックボックスは TwoWay CompiledBinding + Click ハンドラで SeriesState
    /// へ反映する。UseRightAxis は行では読み取り専用のバッジ表示にのみ使い、
    /// 割り当ての変更はスタイルパネルの RightAxisCheckBox (RightAxisCheckBox_Click)
    /// が担う。INotifyPropertyChanged にしているのは
    /// 色・表示名の変更をコレクション再構築なしで反映するため (選択保持と
    /// 将来のインライン編集フォーカス保持のため)。</summary>
    public sealed class SeriesListRowVm : INotifyPropertyChanged
    {
        private string _displayName = string.Empty;
        private bool _isVisible;
        private bool _useRightAxis;
        private SolidColorBrush _colorBrush = new(Colors.Gray);
        private bool _isEditing;

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName == value) return;
                _displayName = value;
                OnPropertyChanged();
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                OnPropertyChanged();
            }
        }

        public bool UseRightAxis
        {
            get => _useRightAxis;
            set
            {
                if (_useRightAxis == value) return;
                _useRightAxis = value;
                OnPropertyChanged();
            }
        }

        public SolidColorBrush ColorBrush
        {
            get => _colorBrush;
            set
            {
                if (ReferenceEquals(_colorBrush, value)) return;
                _colorBrush = value;
                OnPropertyChanged();
            }
        }

        /// <summary>ツールチップ用の「ファイル名 / 列名」。</summary>
        public string SourceHint { get; init; } = string.Empty;

        /// <summary>行のインライン rename が編集モード中かどうか。TextBlock⇔TextBox の
        /// IsVisible トグルを駆動する。</summary>
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing == value) return;
                _isEditing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotEditing));
            }
        }

        /// <summary>TextBlock の IsVisible バインディング用 (CompiledBinding は "!" 否定
        /// 演算子を使わず素直なプロパティを参照する方針に合わせる)。</summary>
        public bool IsNotEditing => !IsEditing;

        /// <summary>インライン rename の TextBox に最後にプログラムから書き込んだ文字列。
        /// Avalonia TextBox の遅延 TextChanged echo ("1 文字目だけ動く" バグ) を、
        /// TextChanged で受け取った値がこれと完全一致するかで判定し skip するために使う。
        /// UI に出さないので INotifyPropertyChanged 通知は不要。</summary>
        internal string? LastCommittedEditText { get; set; }

        internal LoadedTable? Table { get; init; }
        internal SeriesState? State { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly List<LoadedTable> _loadedTables = new();
    private readonly ObservableCollection<TableEntryVm> _tableEntries = new();

    // SeriesState.DisplayOrder の採番カウンタ。新規ロード・貼り付けのたびに
    // 増分し、常に既存系列より大きい値を割り当てる (末尾に付く)。セッション
    // 復元後は最大値+1 へ再計算する (ApplyAnalysisSessionAsync 末尾)。
    private int _nextSeriesDisplayOrder;
    private readonly DispatcherTimer _plotRefreshDebounceTimer = new() { Interval = PlotRefreshDebounceInterval };

    // RefreshPlot が描画した系列を、実際のプロット要素の参照ごと保持する。
    // エクスポート時のスケール再適用 (ApplyExportStyleScale) で、種別 (散布図 /
    // 棒) に応じた書式を直接当て直す。棒が混ざると GetPlottables の OfType 並び
    // 依存では対応が崩れるため、参照を抱えて突き合わせ不要にしている。
    private readonly List<PlottedSeries> _plottedSeriesStyles = new();

    /// <summary>マーカー表示種別へ切替時、サイズ 0 のままだと点が出ないので使う既定値。</summary>
    private const double DefaultMarkerSizeForMarkers = 5;

    /// <summary>1 系列ぶんの描画データ (変換・log・有限ペア抽出まで済ませた状態)。</summary>
    private sealed class RenderItem
    {
        public required double[] Xs { get; init; }
        public required double[] Ys { get; init; }
        public required AnalysisSessionStyle Style { get; init; }
        public required int AutoColorIndex { get; init; }
        public required ViewerChartType ChartType { get; init; }
        public required bool UseRightAxis { get; init; }
        public required string LegendText { get; init; }
        public required string ColumnName { get; init; }

        // 折れ線 (マーカー無し) かつ X 厳密昇順の系列だけ true。ScottPlot の
        // SignalXY は Scatter より高速だが X 昇順前提の折れ線特化型なので、
        // 対象外の系列は従来どおり Scatter で描く (SeriesPlotKindSelector 判定)。
        public required bool UseSignalXY { get; init; }
    }

    /// <summary>描画済みプロット要素とその書式の対応 (エクスポート再スケール用)。</summary>
    private readonly record struct PlottedSeries(
        ScottPlot.IPlottable Plottable,
        AnalysisSessionStyle Style,
        int AutoColorIndex,
        ViewerChartType ChartType);

    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private AvaPlot? _plot;
    private LegendDragController? _legendDragController;
    private PlotFastModeController? _plotFastModeController;
    private int _activeTableIndex = -1;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressTableListEvents;
    private bool _suppressMappingEvents;
    private bool _suppressSeriesListEvents;
    private bool _currentLegendAutoShow;
    private bool _rightAxisInUse;
    private string _currentRightAxisDefaultLabel = "Y2";

    // RefreshPlot が確定させたタイトル/軸ラベルの既定値。ラベル軽量パス
    // (ApplyGraphLabels) が RefreshPlot を経由せずに GetGraphTitle /
    // GetGraphLabel を呼び直せるようキャッシュする (_currentRightAxisDefaultLabel
    // と同じ前例パターン)。
    private string _currentDefaultTitle = string.Empty;
    private string _currentDefaultXLabel = string.Empty;
    private string _currentDefaultYLabel = string.Empty;

    private int _clipboardTableCount;
    private double? _y2Min;
    private double? _y2Max;

    // RefreshPlot が最後に計算した軸データレンジ。Y2 範囲欄の軽量パス
    // (Y2RangeTextBox_TextChanged) が AutoScale を経ずに ApplyAxisLimits を
    // 呼び直せるようキャッシュする。AxisDataRange は mutable struct なので
    // (Include() が自分自身を書き換える) プロパティではなく plain field で持つ。
    private AxisDataRange _lastXRange;
    private AxisDataRange _lastYRange;
    private AxisDataRange _lastY2Range;

    // サイドバータブ (データ / 仕上げ) の切替。XAML の RadioButton 初期値
    // (IsChecked="True") が InitializeComponent 実行中に IsCheckedChanged を
    // 発火させ、その時点ではまだ DataTabPanel / FormatTabPanel の x:Name
    // フィールドが代入されていないため、ガードなしで参照すると NRE になる。
    // InitializeComponent 完了後に true にし、それまではハンドラを早期 return
    // させる (DLS AnalysisWindow の _initialized と同じ方式)。
    private bool _sidebarTabsInitialized;

    // X 列 ComboBox の表示順 → 実カラム index の対応表 (numeric 列のみ並ぶ)。
    private readonly List<int> _xComboColumnIndexes = new();

    // 「系列」セクションのフラット一覧。EnumerateSeriesInDisplayOrder() の
    // 順序で再構築する (RefreshSeriesList)。複数選択に対応し、選択集合全体は
    // _selectedSeriesRows、スタイルパネルに値を表示する代表 (末尾選択) は
    // _activeSeriesRow が持つ。
    private readonly ObservableCollection<SeriesListRowVm> _seriesListRows = new();
    private readonly List<SeriesListRowVm> _selectedSeriesRows = new();
    private SeriesListRowVm? _activeSeriesRow;

    // インライン rename (Batch 4) 用の状態。suppress フラグはプログラムによる
    // TextBox.Text 書き換え中に TextChanged ハンドラを黙らせる。snapshot は
    // Escape でのロールバック用に、編集開始時点の LegendName を 1 件分だけ覚える
    // (同時に編集できる行は常に 1 件のため 1 変数で足りる)。
    private bool _suppressInlineRenameEvents;
    private string? _inlineRenameSnapshot;

    // 内部 reorder は GPC と同じく OS DragDrop layer を使わず
    // PointerCapture + 手動位置計算で行う (Avalonia 11.3 の DoDragDrop は
    // custom DataFormat の drop 認識が実機で効かないため)。
    private Point? _tableDragStartPoint;
    private int? _tableDragSourceIndex;
    private ListBoxItem? _tableDragSourceContainer;
    private bool _isInternalReordering;
    private IPointer? _reorderCapturedPointer;
    private readonly DragGhostController _dragGhost = new();
    private Point _dragGhostPointerOffset;

    // 系列一覧の内部 reorder。テーブル一覧と同じ PointerCapture + DragGhost 方式
    // だが、ドラッグ開始をハンドル (SeriesDragHandle) 上の Press に限定する点が
    // 異なる (行のどこでも開始できるとチェックボックス・インライン編集・
    // Ctrl/Shift 選択クリックと競合するため)。_dragGhost / _dragGhostPointerOffset
    // はテーブル側と共有する (ドラッグは同時に 1 件しか進行しない)。
    private Point? _seriesDragStartPoint;
    private SeriesListRowVm? _seriesDragSourceRow;
    private ListBoxItem? _seriesDragSourceContainer;
    private bool _isSeriesInternalReordering;
    private IPointer? _seriesReorderCapturedPointer;

    public MainWindow()
    {
        InitializeComponent();
        _sidebarTabsInitialized = true;
        LoadFormattingDefaults();
        _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
        ApplyFormattingConfigToControls(_formattingConfig);
        TableListBox.ItemsSource = _tableEntries;
        SeriesListBox.ItemsSource = _seriesListRows;
        _plotRefreshDebounceTimer.Tick += PlotRefreshDebounceTimer_Tick;

        // 外部ファイル D&D: 子要素の AllowDrop=False が hit-test を吸収するので
        // Window レベルでも bubble を待ち受ける (GPC と同配線)。
        AddHandler(DragDrop.DragOverEvent, OnTableDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnTableDragLeave);
        AddHandler(DragDrop.DropEvent, OnTableDrop);
        TableListBox.AddHandler(DragDrop.DragOverEvent, OnTableDragOver);
        TableListBox.AddHandler(DragDrop.DragLeaveEvent, OnTableDragLeave);
        TableListBox.AddHandler(DragDrop.DropEvent, OnTableDrop);

        // 内部 reorder: ListBox が PointerPressed を消費するため Tunnel | Bubble +
        // handledEventsToo で確実に拾う。
        const RoutingStrategies route = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        TableListBox.AddHandler(PointerPressedEvent, OnTableListBoxPointerPressed, route, handledEventsToo: true);
        TableListBox.AddHandler(PointerMovedEvent, OnTableListBoxPointerMoved, route, handledEventsToo: true);
        TableListBox.AddHandler(PointerReleasedEvent, OnTableListBoxPointerReleased, route, handledEventsToo: true);
        TableListBox.AddHandler(PointerCaptureLostEvent, OnTableListBoxPointerCaptureLost);

        // 系列一覧の内部 reorder も同じ配線 (ハンドル上の Press に限定するため、
        // 判定自体は OnSeriesListBoxPointerPressed 内で行う)。
        SeriesListBox.AddHandler(PointerPressedEvent, OnSeriesListBoxPointerPressed, route, handledEventsToo: true);
        SeriesListBox.AddHandler(PointerMovedEvent, OnSeriesListBoxPointerMoved, route, handledEventsToo: true);
        SeriesListBox.AddHandler(PointerReleasedEvent, OnSeriesListBoxPointerReleased, route, handledEventsToo: true);
        SeriesListBox.AddHandler(PointerCaptureLostEvent, OnSeriesListBoxPointerCaptureLost);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowStateStore.ApplyTo(this, WindowStateAppKey);
        KeyboardShortcuts.LocalizeTooltipsForMac(this);

        Dispatcher.UIThread.Post(InitializePlotControl, DispatcherPriority.Background);
        SetStatus("CSV / TSV / xlsx の表形式データを開いてください。", StatusSeverity.Info);
        RefreshRecentFilesUi();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        WindowStateStore.PersistFrom(this, WindowStateAppKey);
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
                case Key.B: ToggleRenderBenchmarkOverlay(); e.Handled = true; return;
            }
        }
        else if (cmd)
        {
            switch (e.Key)
            {
                case Key.O: OpenFileButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.S: SaveGraphButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.E: ExportDataButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.R: AxisRangePanel.ResetToAuto(); e.Handled = true; return;
                case Key.G: GraphFormatPanel.TogglePlotGrid(); e.Handled = true; return;
                case Key.V: _ = PasteFromClipboardAsync(); e.Handled = true; return;
            }
        }
        else if (e.Key == Key.F1)
        {
            global::LabPlot.Core.Avalonia.KeyboardShortcutsWindow.ShowFor(this, global::LabPlot.Core.Avalonia.AppKind.Viewer);
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.F2)
        {
            BeginInlineRename(_activeSeriesRow);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // ---------- Plot bootstrap ----------

    private void InitializePlotControl()
    {
        try
        {
            _plot = new AvaPlot();
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_plot);

            _legendDragController = new LegendDragController(
                _plot,
                () => _formattingConfig.LegendPosition,
                () => (_formattingConfig.LegendOffsetX, _formattingConfig.LegendOffsetY),
                OnLegendDragCommit);
            _legendDragController.Attach();

            _plotFastModeController = new PlotFastModeController(
                _plot,
                () => _plottedSeriesStyles.Select(p => p.Plottable));
            _plotFastModeController.Attach();

            UpdatePlotHostAspectRatio();
            PlotPlaceholderSkeleton.IsVisible = false;
            InitializeEmptyPlot();

            // MRU/ポータル起点のロードは OpenFilesAsync が Window.Loaded 直後に走るため、
            // Background 優先度で post したこの初期化より先にテーブルが読み込まれることがある
            // (RefreshPlot は _plot がまだ null で no-op する)。GPC/Spectrum の
            // InitializePlotControl と同じく、初期化完了時点でデータが既にあれば描き直す。
            if (_loadedTables.Count > 0)
            {
                RefreshPlot();
            }
        }
        catch (Exception ex)
        {
            PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.InitFailed);
            ShowError($"グラフ表示の初期化に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// デバッグ用: ScottPlot 標準の描画時間ベンチマーク表示 (フレーム時間 / FPS) を on/off する。
    /// Ctrl+Shift+B から呼ばれる。perf/viewer-render-speed 計測用の一時トグルで、UI には出さない。
    /// </summary>
    private void ToggleRenderBenchmarkOverlay()
    {
        if (_plot is null) return;
        _plot.Plot.Benchmark.IsVisible = !_plot.Plot.Benchmark.IsVisible;
        _plot.Refresh();
    }

    private void InitializeEmptyPlot()
    {
        if (_plot is null) return;

        PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.EmptyReady);
        _plot.Plot.Clear();
        _plottedSeriesStyles.Clear();
        _plot.Plot.Title("Data Viewer");
        _plot.Plot.XLabel("X");
        _plot.Plot.YLabel("Y");
        ApplyPlotAppearance();
        _plot.Refresh();
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

    // ---------- File open / import ----------

    private async void OpenFileButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "表形式データを開く（複数選択可）",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("表形式データ") { Patterns = new[] { "*.csv", "*.tsv", "*.txt", "*.xlsx" } },
                new FilePickerFileType("CSV / テキスト") { Patterns = new[] { "*.csv", "*.tsv", "*.txt" } },
                new FilePickerFileType("Excelブック") { Patterns = new[] { "*.xlsx" } },
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

        await ImportDataFilesAsync(fileNames);
    }

    /// <summary>
    /// <see cref="IPortalFileOpener.OpenFilesAsync"/>: Portal のカード drop /
    /// 最近開いたファイルから呼ばれる単一入口。
    /// </summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0) return;
        await this.WhenLoadedAsync();
        await ImportDataFilesAsync(filePaths.ToArray());
    }

    private async Task ImportDataFilesAsync(string[] fileNames)
    {
        if (fileNames is null || fileNames.Length == 0) return;

        try
        {
            OpenFileButton.IsEnabled = false;
            SetStatus("表形式データを読み込み中です...", false);
            BusyOverlay.Show(fileNames.Length == 1
                ? "ファイルを読み込み中…"
                : $"{fileNames.Length} ファイルを読み込み中…");

            // 各リーダーはインスタンス状態を持たないので並列 Read で問題ない。
            var tableSets = await Task.WhenAll(
                fileNames.Select(fileName => Task.Run(() => ReadTableSet(fileName))));

            var addedTables = 0;
            var addedRows = 0;
            foreach (var tableSet in tableSets)
            {
                IReadOnlyList<ViewerTable> tables = tableSet.Tables;
                if (tables.Count > 1)
                {
                    // 複数シートの xlsx はどのシートを読み込むか選ばせる (既定全選択)。
                    var fileName = Path.GetFileName(tables[0].SourceFilePath) ?? "ブック";
                    var selected = await SheetSelectionDialog.ShowAsync(this, fileName, tables);
                    if (selected is null) continue;
                    tables = selected;
                }

                foreach (var table in tables)
                {
                    if (TryAddLoadedTable(table))
                    {
                        addedTables++;
                        addedRows += table.RowCount;
                    }
                }
            }

            if (addedTables == 0)
            {
                SetStatus("プロットできる数値列が見つかりませんでした。", true);
                return;
            }

            ActivateLastLoadedTable();
            SetStatus(addedTables == 1
                ? $"{addedRows:N0} 行のデータを読み込みました。"
                : $"{addedTables} テーブル / {addedRows:N0} 行のデータを読み込みました。", false);

            RegisterRecentFiles(fileNames);
            var primaryName = Path.GetFileName(fileNames[0]);
            var subtitle = fileNames.Length == 1 ? primaryName : $"{primaryName} 他 {fileNames.Length - 1} 件";
            if (MainTitleBar is not null) MainTitleBar.Subtitle = subtitle;
            Title = $"Data Viewer — {subtitle}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            ShowError($"読み込みに失敗しました: {ex.Message}");
        }
        finally
        {
            BusyOverlay.Hide();
            OpenFileButton.IsEnabled = true;
        }
    }

    private ViewerTableSet ReadTableSet(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        IViewerDataReader reader = extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? _xlsxReader
            : _textReader;
        return reader.Read(fileName);
    }

    /// <summary>
    /// テーブルを自動マッピングして読み込みリストへ追加する。数値列ゼロの
    /// テーブルは false を返してスキップ (xlsx の説明シートなど)。系列は
    /// 全数値列ぶん作り、X 列の付け替え (列マッピングセクション) に備える。
    /// </summary>
    private bool TryAddLoadedTable(ViewerTable table, string? displayNameOverride = null)
    {
        ColumnMapping mapping;
        try
        {
            mapping = ColumnMappingInference.Infer(table);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        var fileName = Path.GetFileName(table.SourceFilePath);
        var displayName = displayNameOverride ?? ((fileName, table.SheetName) switch
        {
            (null or "", _) => $"テーブル {_loadedTables.Count + 1}",
            (_, null or "") => fileName!,
            _ => $"{fileName} [{table.SheetName}]",
        });

        var loaded = new LoadedTable
        {
            Table = table,
            DisplayName = displayName,
            XColumnIndex = mapping.XColumnIndex,
        };

        var autoEnabled = mapping.YColumnIndexes.ToHashSet();
        for (var col = 0; col < table.Columns.Count; col++)
        {
            if (!table.Columns[col].IsNumeric)
            {
                continue;
            }

            loaded.Series.Add(new SeriesState
            {
                ColumnIndex = col,
                ColumnName = table.Columns[col].Name,
                IsVisible = autoEnabled.Contains(col),
                DisplayOrder = _nextSeriesDisplayOrder++,
            });
        }

        _loadedTables.Add(loaded);
        return true;
    }

    /// <summary>読み込み直後の共通 UI 更新: 最後のテーブルをアクティブ化して再描画。</summary>
    private void ActivateLastLoadedTable()
    {
        _activeTableIndex = _loadedTables.Count - 1;
        RefreshTableEntries();
        RefreshXColumnPanel();
        RefreshSeriesList();
        RefreshPlot();
    }

    // ---------- Clipboard paste ----------

    private async Task PasteFromClipboardAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                Toast?.Show("クリップボードを利用できません", StatusSeverity.Error);
                return;
            }

            var text = await clipboard.TryGetTextAsync();
            var table = ClipboardTableParser.Parse(text);
            var displayName = $"クリップボード {++_clipboardTableCount}";
            if (!TryAddLoadedTable(table, displayName))
            {
                _clipboardTableCount--;
                SetStatus("貼り付けた表にプロットできる数値列がありません。", true);
                return;
            }

            ActivateLastLoadedTable();
            Toast?.Show("クリップボードの表を読み込みました", StatusSeverity.Success);
            SetStatus($"{displayName}: {table.RowCount:N0} 行 × {table.Columns.Count} 列を読み込みました。", false);
        }
        catch (InvalidDataException ex)
        {
            SetStatus($"貼り付けできませんでした: {ex.Message}", true);
            Toast?.Show("表として解釈できませんでした", StatusSeverity.Warning);
        }
    }

    // ---------- X column panel ----------

    /// <summary>選択中テーブルの X 列 ComboBox を作り直す (Y 列の表示 / 非表示・
    /// 右軸などは「系列」セクションのフラット一覧 (RefreshSeriesList) が担う)。</summary>
    private void RefreshXColumnPanel()
    {
        _suppressMappingEvents = true;
        try
        {
            _xComboColumnIndexes.Clear();

            if (ActiveTable is not { } loaded)
            {
                XColumnComboBox.ItemsSource = null;
                XColumnComboBox.IsEnabled = false;
                MappingTableLabel.Text = "(テーブル未選択)";
                return;
            }

            var numericNames = new List<string>();
            for (var col = 0; col < loaded.Table.Columns.Count; col++)
            {
                if (!loaded.Table.Columns[col].IsNumeric) continue;
                _xComboColumnIndexes.Add(col);
                numericNames.Add(loaded.Table.Columns[col].Name);
            }

            XColumnComboBox.ItemsSource = numericNames;
            XColumnComboBox.IsEnabled = numericNames.Count > 1;
            XColumnComboBox.SelectedIndex = _xComboColumnIndexes.IndexOf(loaded.XColumnIndex);

            MappingTableLabel.Text = $"({loaded.DisplayName})";
        }
        finally
        {
            _suppressMappingEvents = false;
        }
    }

    private void XColumnComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMappingEvents) return;

        var comboIndex = XColumnComboBox.SelectedIndex;
        if (ActiveTable is not { } loaded
            || comboIndex < 0
            || comboIndex >= _xComboColumnIndexes.Count)
        {
            return;
        }

        var newXColumn = _xComboColumnIndexes[comboIndex];
        if (newXColumn == loaded.XColumnIndex) return;

        loaded.XColumnIndex = newXColumn;
        RefreshXColumnPanel();
        RefreshSeriesList();
        RefreshPlot();
    }

    // ---------- Series list (「系列」セクション: 全ファイル横断のフラット一覧) ----------

    /// <summary>
    /// 全テーブルの系列を表示順でフラットに列挙し直す。編集中の選択
    /// (SeriesState 参照) を、可能なら再構築後の行へ引き継ぐ。
    /// </summary>
    private void RefreshSeriesList()
    {
        // 編集中の行がある間はコレクションを再構築しない。Clear() で行 VM を
        // 作り直すと TextBox のフォーカスと入力中の文字列が破壊されるため
        // (インライン rename 中は他要因での再構築を止める防御)。
        if (_seriesListRows.Any(r => r.IsEditing)) return;

        _suppressSeriesListEvents = true;
        try
        {
            // previousState: 選択集合の全 SeriesState を集めておき、再構築後の行と
            // 参照突き合わせで再選択する (複数選択保持)。active も同様に SeriesState
            // 単位で覚えておく。
            var previousSelectedStates = new HashSet<SeriesState>(
                _selectedSeriesRows.Where(r => r.State is not null).Select(r => r.State!));
            var previousActiveState = _activeSeriesRow?.State;
            _seriesListRows.Clear();

            foreach (var (table, series) in EnumerateSeriesInDisplayOrder())
            {
                // X 列に割り当てた列は Y からは描かない (RefreshPlot と同じ規約)。
                // 数値列がそれ 1 本しかないテーブルだけは行番号 X 用に残す。
                if (series.ColumnIndex == table.XColumnIndex && table.Series.Count > 1)
                {
                    continue;
                }

                var autoIndex = GetSeriesAutoColorIndex(series);
                var hex = series.Style.ColorHex ?? AutoLineColors[autoIndex % AutoLineColors.Length];
                _seriesListRows.Add(new SeriesListRowVm
                {
                    DisplayName = GetSeriesLegendText(table, series),
                    IsVisible = series.IsVisible,
                    UseRightAxis = series.UseRightAxis,
                    ColorBrush = new SolidColorBrush(HexToAvaloniaColor(hex)),
                    SourceHint = $"{table.DisplayName} / {series.ColumnName}",
                    Table = table,
                    State = series,
                });
            }

            SeriesListPlaceholder.IsVisible = _seriesListRows.Count == 0;

            // SelectionMode="Multiple" の ListBox は SelectedItem への直接代入だと
            // 選択が安定しないため、DLS の復元処理と同じく SelectedItems の
            // Clear + Add で選択を組み立てる。選択集合は previousSelectedStates に
            // 残っている行を全件拾い直す。
            var restoredRows = _seriesListRows
                .Where(row => row.State is not null && previousSelectedStates.Contains(row.State))
                .ToList();
            SeriesListBox.SelectedItems?.Clear();
            _selectedSeriesRows.Clear();
            foreach (var row in restoredRows)
            {
                SeriesListBox.SelectedItems?.Add(row);
                _selectedSeriesRows.Add(row);
            }

            var restoredActive = previousActiveState is null
                ? null
                : restoredRows.FirstOrDefault(row => ReferenceEquals(row.State, previousActiveState));
            _activeSeriesRow = restoredActive ?? restoredRows.LastOrDefault();
        }
        finally
        {
            _suppressSeriesListEvents = false;
        }

        SyncStyleControlsFromSelection();
    }

    private void SeriesListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSeriesListEvents) return;

        _selectedSeriesRows.Clear();
        if (SeriesListBox.SelectedItems is { } selectedItems)
        {
            _selectedSeriesRows.AddRange(selectedItems.Cast<SeriesListRowVm>());
        }

        // active (スタイルパネルに値を表示する代表) の決定: このクリックで新しく
        // 選択に加わった行の末尾を優先する。追加が無い操作 (Shift 範囲縮小や
        // Ctrl クリックでの選択解除など) では、現 active が選択集合に残っていれば
        // それを維持し、残っていなければ選択集合の末尾へフォールバックする。
        var addedTail = e.AddedItems.Cast<SeriesListRowVm>().LastOrDefault();
        if (addedTail is not null)
        {
            _activeSeriesRow = addedTail;
        }
        else if (_activeSeriesRow is null || !_selectedSeriesRows.Contains(_activeSeriesRow))
        {
            _activeSeriesRow = _selectedSeriesRows.LastOrDefault();
        }

        SyncStyleControlsFromSelection();
    }

    private void SeriesVisibleCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (_suppressSeriesListEvents) return;
        if (sender is not CheckBox { Tag: SeriesListRowVm { State: { } state } row } checkBox) return;

        state.IsVisible = checkBox.IsChecked == true;
        row.IsVisible = state.IsVisible;
        RefreshTableEntries();
        RefreshPlot();
    }

    /// <summary>
    /// 色・凡例名の変更を、選択中の行 VM 全件の INotifyPropertyChanged 経由で
    /// 反映する (コレクション再構築だと選択やスクロール位置が壊れるため)。色は
    /// 選択全件の Style が変わり得るので全行のスウォッチを更新する。凡例名は
    /// active 1 件しか Style を変えないが、他行は再計算しても値が変わらないので
    /// 呼び出し元での場合分けを避けるためまとめて呼んでよい。
    /// </summary>
    private void RefreshSelectedSeriesListRowVisuals()
    {
        foreach (var row in _selectedSeriesRows)
        {
            if (row is not { State: { } state, Table: { } table }) continue;

            var autoIndex = GetSeriesAutoColorIndex(state);
            var hex = state.Style.ColorHex ?? AutoLineColors[autoIndex % AutoLineColors.Length];
            row.ColorBrush = new SolidColorBrush(HexToAvaloniaColor(hex));
            row.DisplayName = GetSeriesLegendText(table, state);
        }
    }

    // ---------- Axis scale (log / 右軸) ----------

    private void AxisScaleCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        RefreshPlot();
    }

    private void Y2RangeTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;

        _y2Min = ParseOptionalDouble(Y2MinTextBox.Text);
        _y2Max = ParseOptionalDouble(Y2MaxTextBox.Text);

        // 右軸使用中は、既存プロットのまま ApplyAxisLimits だけを呼び直す軽量
        // パスへ流す。ApplyAxisLimits は範囲欄が空なら何もしない設計なので、
        // AutoScale を経由しないぶんズームが保持される (Batch 3 の狙い)。
        // 右軸未使用、またはプロット未初期化のときは従来どおりフル再構築。
        if (_plot is not null && _rightAxisInUse)
        {
            var xLog = XLogCheckBox.IsChecked == true;
            var yLog = YLogCheckBox.IsChecked == true;
            var y2Log = Y2LogCheckBox.IsChecked == true;
            ApplyAxisLimits(_lastXRange, _lastYRange, _lastY2Range, xLog, yLog, y2Log);
            _plot.Refresh();
            return;
        }

        SchedulePlotRefresh();
    }

    private static double? ParseOptionalDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
            ? value
            : null;
    }

    // ---------- Series transforms ----------

    private void NormalizeCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (ActiveSeries is not { } series) return;

        series.Transform = series.Transform with { Normalize = NormalizeCheckBox.IsChecked == true };
        SchedulePlotRefresh();
    }

    private void TransformTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (ActiveSeries is not { } series) return;

        if (sender == YOffsetTextBox)
        {
            var text = YOffsetTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                series.Transform = series.Transform with { YOffset = 0 };
            }
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset)
                && double.IsFinite(offset))
            {
                series.Transform = series.Transform with { YOffset = offset };
            }
            else
            {
                return;
            }
        }
        else
        {
            var text = SmoothingWindowTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                series.Transform = series.Transform with { SmoothingWindow = 0 };
            }
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var window)
                && window >= 0 && window <= 9999)
            {
                series.Transform = series.Transform with { SmoothingWindow = window };
            }
            else
            {
                return;
            }
        }

        SchedulePlotRefresh();
    }

    // ---------- Table list ----------

    private void RefreshTableEntries()
    {
        _suppressTableListEvents = true;
        try
        {
            _tableEntries.Clear();
            for (var i = 0; i < _loadedTables.Count; i++)
            {
                var loaded = _loadedTables[i];
                var firstVisible = loaded.Series.FirstOrDefault(static series => series.IsVisible);
                var hex = firstVisible?.Style.ColorHex
                    ?? (loaded.Series.Count > 0
                        ? AutoLineColors[GetSeriesAutoColorIndex(loaded.Series[0]) % AutoLineColors.Length]
                        : AutoLineColors[0]);
                _tableEntries.Add(new TableEntryVm
                {
                    DisplayName = loaded.DisplayName,
                    FullPath = loaded.Table.SourceFilePath ?? string.Empty,
                    ColorBrush = new SolidColorBrush(HexToAvaloniaColor(hex)),
                });
            }

            TableListBox.SelectedIndex = _tableEntries.Count > 0
                ? Math.Clamp(_activeTableIndex, 0, _tableEntries.Count - 1)
                : -1;
        }
        finally
        {
            _suppressTableListEvents = false;
        }

        TableListPlaceholder.IsVisible = _tableEntries.Count == 0;
    }

    /// <summary>
    /// 全テーブルの全系列 (非表示含む) を表示順 (DisplayOrder 昇順・安定ソート)
    /// でフラットに列挙する。描画順・凡例順・auto 色決定はすべてこの順序に従う。
    /// </summary>
    private IEnumerable<(LoadedTable Table, SeriesState Series)> EnumerateSeriesInDisplayOrder()
        => SeriesOrderPlanner.FlattenInDisplayOrder(
            _loadedTables.Select(t => t.Series.Select(s => (Table: t, Series: s))),
            x => x.Series.DisplayOrder);

    /// <summary>
    /// 系列を EnumerateSeriesInDisplayOrder() の通し番号 (フラット index) へ変換する。
    /// auto 色の割り当てだけでなく、系列一覧 D&amp;D で「表示行 → フラット index」を
    /// 解決する際にも使う (系列一覧は X 列に割り当てた列を除いたフィルタ済み表示
    /// なので、行 index とフラット index は一致しない)。見つからない場合は -1。
    /// </summary>
    private int GetFlatSeriesOrdinal(SeriesState series)
    {
        var ordinal = 0;
        foreach (var entry in EnumerateSeriesInDisplayOrder())
        {
            if (ReferenceEquals(entry.Series, series)) return ordinal;
            ordinal++;
        }

        return -1;
    }

    /// <summary>auto 色をフラット表示順の通し番号で割り当てるための系列序数。</summary>
    private int GetSeriesAutoColorIndex(SeriesState series)
    {
        var ordinal = GetFlatSeriesOrdinal(series);
        return ordinal >= 0 ? ordinal : 0;
    }

    /// <summary>
    /// ColorHex 未設定 (auto 色) の全系列へ、現在の解決色をその場で固定する。
    /// 並べ替え後も auto 色が変わって見えないよう、並べ替え前に一度だけ呼ぶ。
    /// </summary>
    private void FreezeAutoColorsForAllSeries()
    {
        var ordinal = 0;
        foreach (var entry in EnumerateSeriesInDisplayOrder())
        {
            var style = entry.Series.Style;
            if (string.IsNullOrEmpty(style.ColorHex))
            {
                style.ColorHex = AutoLineColors[ordinal % AutoLineColors.Length];
            }

            ordinal++;
        }
    }

    /// <summary>
    /// テーブル一覧の選択は「X 列セクションでどのテーブルを編集するか」だけを
    /// 決める。系列一覧側の選択とは独立させる (テーブル切替で系列選択が
    /// 勝手に変わらないようにする)。
    /// </summary>
    private void TableListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTableListEvents) return;

        var newIndex = TableListBox.SelectedIndex;
        if (newIndex < 0 || newIndex >= _loadedTables.Count) return;

        _activeTableIndex = newIndex;
        RefreshXColumnPanel();
    }

    private void RemoveTableButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TableEntryVm entry }) return;
        var index = _tableEntries.IndexOf(entry);
        if (index < 0 || index >= _loadedTables.Count) return;

        _loadedTables.RemoveAt(index);
        if (_loadedTables.Count == 0)
        {
            _activeTableIndex = -1;
            MainTitleBar.Subtitle = "Tabular data viewer";
            Title = "Data Viewer";
        }
        else
        {
            _activeTableIndex = Math.Clamp(_activeTableIndex, 0, _loadedTables.Count - 1);
        }

        RefreshTableEntries();
        RefreshXColumnPanel();
        RefreshSeriesList();
        RefreshPlot();
        SetStatus("テーブルを削除しました。", StatusSeverity.Info);
    }

    // ---------- External file drag & drop ----------

    private void OnTableDragOver(object? sender, DragEventArgs e)
    {
        // 内部 reorder 中は OS D&D を一切受け付けない。
        if (_isInternalReordering) return;

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

    private void OnTableDragLeave(object? sender, DragEventArgs e)
    {
        var pos = e.GetPosition(TableListBox);
        if (pos.X < 0 || pos.Y < 0
            || pos.X > TableListBox.Bounds.Width
            || pos.Y > TableListBox.Bounds.Height)
        {
            HideFileDropOverlay();
        }
    }

    private async void OnTableDrop(object? sender, DragEventArgs e)
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
        await ImportDataFilesAsync(paths);
    }

    private void ShowFileDropOverlay()
    {
        TableDropOverlay.IsVisible = true;
    }

    private void HideFileDropOverlay()
    {
        TableDropOverlay.IsVisible = false;
    }

    // ---------- Table drag-reorder (PointerCapture + DragGhost, GPC と同方式) ----------

    private void OnTableListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual srcVisual && FindAncestor<Button>(srcVisual) is not null)
        {
            // 行内の削除ボタンクリックは drag を発火させない。
            _tableDragStartPoint = null;
            _tableDragSourceContainer = null;
            _tableDragSourceIndex = null;
            return;
        }

        var item = e.Source is Visual v ? FindAncestor<ListBoxItem>(v) : null;
        if (item is null)
        {
            _tableDragStartPoint = null;
            _tableDragSourceContainer = null;
            _tableDragSourceIndex = null;
            return;
        }

        if (!e.GetCurrentPoint(TableListBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _tableDragStartPoint = e.GetPosition(TableListBox);
        _tableDragSourceContainer = item;
        _tableDragSourceIndex = TableListBox.IndexFromContainer(item);
        _dragGhostPointerOffset = e.GetPosition(item);
    }

    private void OnTableListBoxPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tableDragStartPoint is null
            || _tableDragSourceContainer is null
            || _tableDragSourceIndex is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(TableListBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(TableListBox);

        if (!_isInternalReordering)
        {
            var dx = current.X - _tableDragStartPoint.Value.X;
            var dy = current.Y - _tableDragStartPoint.Value.Y;
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

            var sourceIndex = _tableDragSourceIndex.Value;
            if (sourceIndex < 0 || sourceIndex >= _tableEntries.Count)
            {
                ResetTableReorderState();
                return;
            }

            _isInternalReordering = true;
            e.Pointer.Capture(TableListBox);
            _reorderCapturedPointer = e.Pointer;

            _dragGhost.Show(
                this,
                TableListBox.ItemTemplate,
                _tableEntries[sourceIndex],
                _tableDragSourceContainer.Bounds.Size,
                e.GetPosition(this),
                _dragGhostPointerOffset);
            _tableDragSourceContainer.Opacity = 0.4;
        }

        _dragGhost.Move(e.GetPosition(this));
        var (targetItem, insertAbove) = ResolveDropTargetFromVisual(e, TableListBox);
        if (targetItem is null || ReferenceEquals(targetItem, _tableDragSourceContainer))
        {
            HideInsertionLine(InsertionLine);
        }
        else
        {
            UpdateInsertionLine(targetItem, insertAbove, TableListBox, InsertionLine);
        }

        e.Handled = true;
    }

    private void OnTableListBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isInternalReordering)
        {
            ResetTableReorderState();
            return;
        }

        var sourceIndex = _tableDragSourceIndex ?? -1;
        var (targetItem, insertAbove) = ResolveDropTargetFromVisual(e, TableListBox);

        int newIndex;
        if (targetItem is null)
        {
            newIndex = _tableEntries.Count - 1;
        }
        else
        {
            var targetIndex = TableListBox.IndexFromContainer(targetItem);
            if (targetIndex < 0)
            {
                HideInsertionLine(InsertionLine);
                ResetTableReorderState();
                e.Handled = true;
                return;
            }

            newIndex = insertAbove ? targetIndex : targetIndex + 1;
            if (newIndex > sourceIndex)
            {
                newIndex--;
            }
        }

        newIndex = Math.Clamp(newIndex, 0, Math.Max(0, _tableEntries.Count - 1));
        HideInsertionLine(InsertionLine);

        if (sourceIndex >= 0 && newIndex != sourceIndex)
        {
            MoveTable(sourceIndex, newIndex);
        }

        ResetTableReorderState();
        e.Handled = true;
    }

    private void OnTableListBoxPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        HideInsertionLine(InsertionLine);
        ResetTableReorderState();
    }

    private void ResetTableReorderState()
    {
        if (_tableDragSourceContainer is not null)
        {
            _tableDragSourceContainer.Opacity = 1.0;
        }

        if (_reorderCapturedPointer is { } pointer)
        {
            pointer.Capture(null);
            _reorderCapturedPointer = null;
        }

        _dragGhost.Hide();
        _isInternalReordering = false;
        _tableDragStartPoint = null;
        _tableDragSourceContainer = null;
        _tableDragSourceIndex = null;
    }

    private void MoveTable(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex
            || oldIndex < 0 || oldIndex >= _loadedTables.Count
            || newIndex < 0 || newIndex >= _loadedTables.Count)
        {
            return;
        }

        // auto 色は系列通し番号に依存するため、並べ替えで色が変わらないよう
        // 現在の解決色を移動前に固定する。
        FreezeAutoColorsForAllSeries();

        var table = _loadedTables[oldIndex];
        _loadedTables.RemoveAt(oldIndex);
        _loadedTables.Insert(newIndex, table);
        _activeTableIndex = newIndex;

        RefreshTableEntries();
        RefreshXColumnPanel();
        RefreshSeriesList();
        RefreshPlot();
    }

    // ---------- Series list drag-reorder (Table 側と同じ PointerCapture + DragGhost
    // 方式だが、ドラッグ開始をハンドル (SeriesDragHandle) 上の Press に限定する) ----------

    private void OnSeriesListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _seriesDragStartPoint = null;
        _seriesDragSourceContainer = null;
        _seriesDragSourceRow = null;

        if (e.Source is not Visual srcVisual) return;

        // ドラッグ開始はハンドル上の Press に限定する (Table D&D の「行のどこでも
        // 開始できる」から意図的に外し、チェックボックス・インライン編集・
        // Ctrl/Shift 選択クリックと入力領域を分離する)。
        if (FindAncestor<Border>(srcVisual, b => b.Name == "SeriesDragHandle") is null) return;

        var item = FindAncestor<ListBoxItem>(srcVisual);
        if (item is null) return;

        // 編集中の行 (ドラッグ対象自身か、別行かを問わず) があれば先に確定する。
        // RefreshSeriesList() は「編集中の行が 1 件でもあれば再構築しない」という
        // ガードを持つため、別行が編集中のまま MoveSeries を実行すると一覧の見た目
        // だけ並べ替え結果が反映されない (プロット / 凡例は先に更新済みなのに一覧が
        // 古いまま、という不整合が起きる)。BeginInlineRename の「別行編集中なら
        // 先に確定してから切り替える」と同じ流儀で、ドラッグ開始前にも確定させる。
        var currentlyEditing = _seriesListRows.FirstOrDefault(r => r.IsEditing);
        if (currentlyEditing is not null)
        {
            CommitInlineRename(currentlyEditing);
        }

        if (!e.GetCurrentPoint(SeriesListBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _seriesDragStartPoint = e.GetPosition(SeriesListBox);
        _seriesDragSourceContainer = item;
        _seriesDragSourceRow = item.DataContext as SeriesListRowVm;
        _dragGhostPointerOffset = e.GetPosition(item);
    }

    private void OnSeriesListBoxPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_seriesDragStartPoint is null
            || _seriesDragSourceContainer is null
            || _seriesDragSourceRow is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(SeriesListBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(SeriesListBox);

        if (!_isSeriesInternalReordering)
        {
            var dx = current.X - _seriesDragStartPoint.Value.X;
            var dy = current.Y - _seriesDragStartPoint.Value.Y;
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

            var sourceIndex = SeriesListBox.IndexFromContainer(_seriesDragSourceContainer);
            if (sourceIndex < 0 || sourceIndex >= _seriesListRows.Count)
            {
                ResetSeriesReorderState();
                return;
            }

            _isSeriesInternalReordering = true;
            e.Pointer.Capture(SeriesListBox);
            _seriesReorderCapturedPointer = e.Pointer;

            _dragGhost.Show(
                this,
                SeriesListBox.ItemTemplate,
                _seriesDragSourceRow,
                _seriesDragSourceContainer.Bounds.Size,
                e.GetPosition(this),
                _dragGhostPointerOffset);
            _seriesDragSourceContainer.Opacity = 0.4;
        }

        _dragGhost.Move(e.GetPosition(this));
        var (targetItem, insertAbove) = ResolveDropTargetFromVisual(e, SeriesListBox);
        if (targetItem is null || ReferenceEquals(targetItem, _seriesDragSourceContainer))
        {
            HideInsertionLine(SeriesInsertionLine);
        }
        else
        {
            UpdateInsertionLine(targetItem, insertAbove, SeriesListBox, SeriesInsertionLine);
        }

        e.Handled = true;
    }

    private void OnSeriesListBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSeriesInternalReordering)
        {
            ResetSeriesReorderState();
            return;
        }

        var (targetItem, insertAbove) = ResolveDropTargetFromVisual(e, SeriesListBox);
        HideInsertionLine(SeriesInsertionLine);

        // 系列一覧は X 列に割り当てた列を除いたフィルタ済み表示なので、行 index
        // ではなく SeriesState 参照から EnumerateSeriesInDisplayOrder() 上のフラット
        // index を引き直してから MoveSeries に渡す。
        if (_seriesDragSourceRow?.State is { } sourceState)
        {
            var oldFlatIndex = GetFlatSeriesOrdinal(sourceState);
            var flatCount = _loadedTables.Sum(t => t.Series.Count);

            int? newFlatIndex;
            if (targetItem is null)
            {
                // 一覧の末尾 (行が無い場所) へのドロップは、フラット順の末尾を意味する。
                newFlatIndex = flatCount - 1;
            }
            else
            {
                var targetIndex = SeriesListBox.IndexFromContainer(targetItem);
                var targetState = targetIndex >= 0 && targetIndex < _seriesListRows.Count
                    ? _seriesListRows[targetIndex].State
                    : null;

                if (targetState is null)
                {
                    newFlatIndex = null;
                }
                else
                {
                    var targetFlatIndex = GetFlatSeriesOrdinal(targetState);
                    newFlatIndex = insertAbove ? targetFlatIndex : targetFlatIndex + 1;
                    if (newFlatIndex > oldFlatIndex)
                    {
                        newFlatIndex--;
                    }
                }
            }

            if (oldFlatIndex >= 0 && newFlatIndex is { } nf && flatCount > 0)
            {
                var clamped = Math.Clamp(nf, 0, flatCount - 1);
                if (clamped != oldFlatIndex)
                {
                    MoveSeries(oldFlatIndex, clamped);
                }
            }
        }

        ResetSeriesReorderState();
        e.Handled = true;
    }

    private void OnSeriesListBoxPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        HideInsertionLine(SeriesInsertionLine);
        ResetSeriesReorderState();
    }

    private void ResetSeriesReorderState()
    {
        if (_seriesDragSourceContainer is not null)
        {
            _seriesDragSourceContainer.Opacity = 1.0;
        }

        if (_seriesReorderCapturedPointer is { } pointer)
        {
            pointer.Capture(null);
            _seriesReorderCapturedPointer = null;
        }

        _dragGhost.Hide();
        _isSeriesInternalReordering = false;
        _seriesDragStartPoint = null;
        _seriesDragSourceContainer = null;
        _seriesDragSourceRow = null;
    }

    /// <summary>
    /// フラット表示順 (EnumerateSeriesInDisplayOrder() 通し番号) で oldFlatIndex の
    /// 系列を newFlatIndex へ移動する。描画順・凡例順・エクスポート順すべてが従う
    /// DisplayOrder を全系列ぶん振り直す。
    /// </summary>
    private void MoveSeries(int oldFlatIndex, int newFlatIndex)
    {
        var flat = EnumerateSeriesInDisplayOrder().Select(entry => entry.Series).ToList();
        if (oldFlatIndex == newFlatIndex
            || oldFlatIndex < 0 || oldFlatIndex >= flat.Count
            || newFlatIndex < 0 || newFlatIndex >= flat.Count)
        {
            return;
        }

        // auto 色は系列通し番号に依存するため、並べ替えで色が変わらないよう
        // 現在の解決色を移動前に固定する (MoveTable と同じ理由)。
        FreezeAutoColorsForAllSeries();

        var reordered = SeriesOrderPlanner.Move(flat, oldFlatIndex, newFlatIndex);
        for (var i = 0; i < reordered.Count; i++)
        {
            reordered[i].DisplayOrder = i;
        }

        _nextSeriesDisplayOrder = reordered.Count;

        RefreshSeriesList();
        RefreshTableEntries();
        RefreshPlot();
    }

    private (ListBoxItem? Item, bool InsertAbove) ResolveDropTargetFromVisual(PointerEventArgs e, ListBox listBox)
    {
        // PointerCapture 中は e.Source が常に capture 先になるため、Pointer 位置から
        // ListBox の hit-test を自前実行する (GPC と同じ理由)。
        var posInListBox = e.GetPosition(listBox);
        var hit = listBox.InputHitTest(posInListBox) as Visual;
        var item = hit is null ? null : FindAncestor<ListBoxItem>(hit);
        if (item is null) return (null, false);
        var pos = e.GetPosition(item);
        var insertAbove = pos.Y < item.Bounds.Height / 2;
        return (item, insertAbove);
    }

    private static void UpdateInsertionLine(ListBoxItem item, bool insertAbove, ListBox listBox, Grid insertionLine)
    {
        var transformPoint = item.TranslatePoint(new Point(0, 0), listBox);
        if (transformPoint is null)
        {
            HideInsertionLine(insertionLine);
            return;
        }

        var listBoxTopInGrid = listBox.Bounds.Top;
        var itemTopInGrid = listBoxTopInGrid + transformPoint.Value.Y;
        const double lineCenterOffset = 6;
        var lineTop = insertAbove
            ? itemTopInGrid - lineCenterOffset
            : itemTopInGrid + item.Bounds.Height - lineCenterOffset;

        insertionLine.Margin = new Thickness(0, Math.Max(0, lineTop), 0, 0);
        insertionLine.IsVisible = true;
    }

    private static void HideInsertionLine(Grid insertionLine)
    {
        insertionLine.IsVisible = false;
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

    private static T? FindAncestor<T>(Visual? element, Func<T, bool> predicate) where T : class
    {
        while (element is not null)
        {
            if (element is T match && predicate(match)) return match;
            element = element.GetVisualParent();
        }

        return null;
    }

    // ---------- Series style ----------

    private LoadedTable? ActiveTable =>
        _activeTableIndex >= 0 && _activeTableIndex < _loadedTables.Count
            ? _loadedTables[_activeTableIndex]
            : null;

    /// <summary>系列一覧で選択中の系列のうち、スタイルパネルに値を表示する代表
    /// (末尾選択)。スタイル編集は選択集合全体 (_selectedSeriesRows) へ適用する。</summary>
    private SeriesState? ActiveSeries => _activeSeriesRow?.State;

    private void ChartTypeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (_selectedSeriesRows.Count == 0) return;

        var type = (ViewerChartType)Math.Clamp(ChartTypeComboBox.SelectedIndex, 0, 3);
        foreach (var row in _selectedSeriesRows)
        {
            if (row.State is not { } series) continue;

            series.ChartType = type;
            NormalizeMarkerSizeIfNeeded(series);
        }

        RefreshPlot();
    }

    /// <summary>選択中系列全件の右軸割り当てを一括切替する。UseRightAxis は
    /// AnalysisSessionStyle ではなく SeriesState 側のプロパティなので
    /// ApplySeriesStyleEditToSelection は使わず、色や種別の一括適用と同型の
    /// ループを直接書く。行の右軸バッジ表示用に行 VM 側も合わせて更新する。</summary>
    private void RightAxisCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (_selectedSeriesRows.Count == 0) return;

        var useRightAxis = RightAxisCheckBox.IsChecked == true;
        foreach (var row in _selectedSeriesRows)
        {
            if (row.State is not { } series) continue;

            series.UseRightAxis = useRightAxis;
            row.UseRightAxis = useRightAxis;
        }

        RefreshPlot();
    }

    /// <summary>
    /// マーカー必須種別 (Markers / LineMarkers) でサイズ 0 以下のままだと点が
    /// 出ないので既定値へ補正し、入力欄にも反映する。ChartType 切替直後と、
    /// MarkerSize テキストボックスの編集確定 (フォーカス喪失) 時の両方から
    /// 同じ補正を通すことで表示 (テキストボックス) と実描画の食い違いを防ぐ。
    /// TextChanged からは呼ばない ("0.5" 入力途中の "0" を補正して入力を壊す
    /// ため)。補正したら true を返す。
    /// </summary>
    private bool NormalizeMarkerSizeIfNeeded(SeriesState series)
    {
        if (!series.ChartType.ShowsMarkers() || series.Style.MarkerSize > 0) return false;

        series.Style.MarkerSize = DefaultMarkerSizeForMarkers;
        if (ActiveSeries != series) return true;

        // Text の書き換えは _suppressStyleControlEvents を張ってから行う。
        // Avalonia の TextBox は遅延 TextChanged echo を発することがあるが
        // (過去の「1 文字目だけ動く」バグの真因)、正規化後は MarkerSize > 0
        // になっているため echo が再入しても NormalizeMarkerSizeIfNeeded は
        // 早期 return するだけで無限ループや値の巻き戻りは起きない。
        _suppressStyleControlEvents = true;
        try
        {
            MarkerSizeTextBox.Text = series.Style.MarkerSize.ToString("0.##", CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }

        return true;
    }

    /// <summary>選択中系列の値をスタイル編集パネルへ反映する。表示は常に
    /// active (末尾選択) 基準 (混在時の indeterminate 表示はしない)。未選択時は
    /// プレースホルダ値で無効化する。</summary>
    private void SyncStyleControlsFromSelection()
    {
        _suppressStyleControlEvents = true;
        try
        {
            var selectionCount = _selectedSeriesRows.Count;
            SeriesStyleBulkHintLabel.IsVisible = selectionCount >= 2;
            if (selectionCount >= 2)
            {
                SeriesStyleBulkHintLabel.Text = $"変更内容は選択中の {selectionCount} 件すべてに適用されます";
            }

            if (ActiveSeries is not { } series)
            {
                LineColorPicker.DefaultHex = AutoLineColors[0];
                LineColorPicker.SetHexValue(null);
                ChartTypeComboBox.SelectedIndex = 0;
                ChartTypeComboBox.IsEnabled = false;
                RightAxisCheckBox.IsChecked = false;
                RightAxisCheckBox.IsEnabled = false;
                LineWidthTextBox.Text = _formattingConfig.FormatLineWidth();
                MarkerSizeTextBox.Text = _formattingConfig.FormatMarkerSize();
                NormalizeCheckBox.IsChecked = false;
                YOffsetTextBox.Text = "0";
                SmoothingWindowTextBox.Text = "0";
                SeriesSelectionSummaryLabel.Text = "(系列未選択)";
                return;
            }

            var autoIndex = GetSeriesAutoColorIndex(series);
            LineColorPicker.DefaultHex = AutoLineColors[autoIndex % AutoLineColors.Length];
            LineColorPicker.SetHexValue(series.Style.ColorHex);
            ChartTypeComboBox.SelectedIndex = (int)series.ChartType;
            ChartTypeComboBox.IsEnabled = true;
            RightAxisCheckBox.IsChecked = series.UseRightAxis;
            RightAxisCheckBox.IsEnabled = true;
            LineWidthTextBox.Text = series.Style.LineWidth.ToString("0.##", CultureInfo.InvariantCulture);
            MarkerSizeTextBox.Text = series.Style.MarkerSize.ToString("0.##", CultureInfo.InvariantCulture);
            NormalizeCheckBox.IsChecked = series.Transform.Normalize;
            YOffsetTextBox.Text = series.Transform.YOffset.ToString("0.######", CultureInfo.InvariantCulture);
            SmoothingWindowTextBox.Text = series.Transform.SmoothingWindow.ToString(CultureInfo.InvariantCulture);
            SeriesSelectionSummaryLabel.Text = selectionCount >= 2
                ? $"({selectionCount} 件選択中)"
                : $"({_activeSeriesRow?.DisplayName})";
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }
    }

    /// <summary>選択中の系列全件 (_selectedSeriesRows) の Style へ mutate を適用する。
    /// 0 件選択時は false を返して呼び出し元に何もさせない。色 / 種別 / 線幅 /
    /// マーカーサイズはこの一括版を使うが、凡例名 (重複防止のため active 限定) と
    /// 変換 (スコープ外) はこの経由を使わず active 1 件のみを直接編集する。</summary>
    private bool ApplySeriesStyleEditToSelection(Action<AnalysisSessionStyle> mutate)
    {
        if (_selectedSeriesRows.Count == 0) return false;

        foreach (var row in _selectedSeriesRows)
        {
            if (row.State is { } series)
            {
                mutate(series.Style);
            }
        }

        return true;
    }

    /// <summary>選択中系列の色 / 線幅 / マーカーサイズ変更を、既存 plottable への
    /// 直接更新 + <see cref="AvaPlot.Refresh"/> だけで反映する軽量パス
    /// (ApplyGraphAppearanceAndRefresh と同じ「再構築なし」の流儀)。
    /// <see cref="_plottedSeriesStyles"/> は Style の参照 (ReferenceEquals) で
    /// 突き合わせるので、非表示系列や X 列に割り当てた系列 (RefreshPlot が
    /// そもそも描画しなかったもの) は自動的にヒットせず false を返す。
    /// 呼び出し元はその場合フル再構築 (SchedulePlotRefresh) へフォールバックする。
    /// </summary>
    private bool TryRefreshSelectedSeriesStyleLight()
    {
        if (_plot is null || _selectedSeriesRows.Count == 0) return false;

        var hit = false;
        foreach (var row in _selectedSeriesRows)
        {
            if (row.State is not { } state) continue;

            var plotted = _plottedSeriesStyles.FirstOrDefault(p => ReferenceEquals(p.Style, state.Style));
            if (plotted.Plottable is null) continue;

            if (plotted.Plottable is ScottPlot.IHasLine hasLine and ScottPlot.IHasMarker hasMarker)
            {
                ApplyLineMarkerStyle(hasLine, hasMarker, plotted.Style, plotted.AutoColorIndex, plotted.ChartType);
                hit = true;
            }
            else if (plotted.Plottable is ScottPlot.Plottables.BarPlot barPlot)
            {
                ApplyBarStyle(barPlot, plotted.Style, plotted.AutoColorIndex);
                hit = true;
            }
        }

        if (hit) _plot.Refresh();
        return hit;
    }

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (!ApplySeriesStyleEditToSelection(style => style.ColorHex = LineColorPicker.HexValue)) return;

        RefreshTableEntries();
        RefreshSelectedSeriesListRowVisuals();
        if (!TryRefreshSelectedSeriesStyleLight()) SchedulePlotRefresh();
    }

    // ---------- 系列名インライン rename (一覧表示名=凡例名) ----------

    /// <summary>行の表示名パネルをダブルタップしたときのハンドラ。Tag から行 VM を
    /// 取り出し編集を開始する。</summary>
    private void SeriesNameTextBlock_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { Tag: SeriesListRowVm row }) return;

        BeginInlineRename(row);
    }

    /// <summary>系列一覧の行コンテナから、インライン編集用 TextBox (Tag に自身の
    /// 行 VM を持つ) を探す。仮想化オフ (ItemsPanel=StackPanel) なので
    /// ContainerFromItem は常に安定して非 null を返す。</summary>
    private TextBox? FindSeriesNameEditBox(SeriesListRowVm row)
    {
        if (SeriesListBox.ContainerFromItem(row) is not Control container) return null;

        return container.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(tb => ReferenceEquals(tb.Tag, row));
    }

    /// <summary>系列名のインライン編集を開始する。別行が編集中なら先に確定してから
    /// 切り替える (同時に 2 行が編集状態になることはない)。</summary>
    private void BeginInlineRename(SeriesListRowVm? row)
    {
        if (row is null) return;

        var currentlyEditing = _seriesListRows.FirstOrDefault(r => r.IsEditing);
        if (currentlyEditing is not null)
        {
            if (ReferenceEquals(currentlyEditing, row)) return;
            CommitInlineRename(currentlyEditing);
        }

        // ダブルクリックで未選択の行から編集を始めた場合は、その行を単一選択にする
        // (F2 は常に active = 選択済みの行が対象なのでここは自明に通る)。
        if (!_selectedSeriesRows.Contains(row))
        {
            SeriesListBox.SelectedItems?.Clear();
            SeriesListBox.SelectedItems?.Add(row);
        }

        _inlineRenameSnapshot = row.State?.Style.LegendName;

        // 「今見えている名前」(= 一覧の DisplayName = GetSeriesLegendText の結果、
        // 既定名を含む) を編集対象にする。ユーザーが今見ている文字列をそのまま
        // 編集する体験にするため、LegendName の生値 (null の場合あり) は使わない。
        var editText = row.DisplayName;

        _suppressInlineRenameEvents = true;
        try
        {
            row.LastCommittedEditText = editText;
            row.IsEditing = true;

            if (FindSeriesNameEditBox(row) is { } textBox)
            {
                textBox.Text = editText;
            }
        }
        finally
        {
            _suppressInlineRenameEvents = false;
        }

        // Focus/SelectAll はレイアウト・IsVisible の反映後でないと効かないことが
        // あるため Loaded 優先度まで遅延させる。
        Dispatcher.UIThread.Post(() =>
        {
            if (FindSeriesNameEditBox(row) is { } textBox)
            {
                textBox.Focus();
                textBox.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>系列名インライン rename の軽量パス。<see cref="_plottedSeriesStyles"/>
    /// を Style の参照 (ReferenceEquals) で突き合わせ、ヒットした plottable の
    /// 凡例文字列を直接書き換える (再構築なし)。凡例自動表示判定
    /// (<see cref="_currentLegendAutoShow"/>) も、系列名変更でカスタム凡例名の
    /// 有無が変わり得るためここで再計算する。非表示系列や X 列系列など、
    /// そもそも RefreshPlot が描画していない系列はヒットせず false を返し、
    /// 呼び出し元はフル再構築 (SchedulePlotRefresh) へフォールバックする。</summary>
    private bool TryUpdatePlottedLegendText(SeriesState state, LoadedTable table)
    {
        if (_plot is null) return false;

        var plotted = _plottedSeriesStyles.FirstOrDefault(p => ReferenceEquals(p.Style, state.Style));
        if (plotted.Plottable is not ScottPlot.IHasLegendText hasLegendText) return false;

        hasLegendText.LegendText = GetSeriesLegendText(table, state);

        _currentLegendAutoShow = LegendVisibility.ShouldAutoShow(
            _plottedSeriesStyles.Count,
            _plottedSeriesStyles.Any(p => !string.IsNullOrWhiteSpace(p.Style.LegendName)));
        ApplyLegend(_plot.Plot, CaptureFormattingConfigFromControls(), autoShow: _currentLegendAutoShow);
        _plot.Refresh();
        return true;
    }

    /// <summary>編集を確定する。表示名を GetSeriesLegendText で再計算し、
    /// スタイルパネルのヘッダ表示も合わせて更新する。</summary>
    private void CommitInlineRename(SeriesListRowVm row)
    {
        if (!row.IsEditing) return;

        row.IsEditing = false;
        if (row is { State: { } state, Table: { } table })
        {
            row.DisplayName = GetSeriesLegendText(table, state);
        }

        _inlineRenameSnapshot = null;
        SyncStyleControlsFromSelection();
    }

    /// <summary>編集を破棄し、開始時点の LegendName へロールバックする
    /// (タイピング中に凡例へ反映済みの内容も含めて元に戻す)。</summary>
    private void CancelInlineRename(SeriesListRowVm row)
    {
        if (!row.IsEditing) return;

        if (row.State is { } state)
        {
            state.Style.LegendName = _inlineRenameSnapshot;
        }

        row.IsEditing = false;
        if (row is { State: { } s, Table: { } table })
        {
            row.DisplayName = GetSeriesLegendText(table, s);
            if (!TryUpdatePlottedLegendText(s, table)) SchedulePlotRefresh();
        }
        else
        {
            SchedulePlotRefresh();
        }

        _inlineRenameSnapshot = null;
    }

    private void SeriesNameEditBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressInlineRenameEvents) return;
        if (sender is not TextBox { Tag: SeriesListRowVm { State: { } state } row } box) return;
        if (!row.IsEditing) return;

        // Avalonia TextBox の遅延 TextChanged echo 対策: プログラムが直前に書いた
        // 文字列 (LastCommittedEditText) と完全一致するなら、ユーザー入力ではなく
        // 遅延 echo と判定して skip する ("1 文字目だけ動く" 既知バグの対策)。
        if (box.Text == row.LastCommittedEditText) return;

        DatasetStyleCommit.CommitLegendName(box, value => state.Style.LegendName = value);
        row.LastCommittedEditText = box.Text;

        if (row.Table is not { } table || !TryUpdatePlottedLegendText(state, table))
        {
            SchedulePlotRefresh();
        }
    }

    private void SeriesNameEditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: SeriesListRowVm row }) return;

        if (e.Key == Key.Enter)
        {
            CommitInlineRename(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelInlineRename(row);
            e.Handled = true;
        }
    }

    private void SeriesNameEditBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: SeriesListRowVm row }) return;

        // Enter/Escape で先に Commit/Cancel 済みなら IsEditing は既に false。
        // その後にも発生する LostFocus での二重処理を防ぐ。
        if (!row.IsEditing) return;

        CommitInlineRename(row);
    }

    private void StyleNumberTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;

        var committed = sender == LineWidthTextBox
            ? DatasetStyleCommit.TryCommitPositiveDouble(LineWidthTextBox, value =>
                ApplySeriesStyleEditToSelection(style => style.LineWidth = value))
            : DatasetStyleCommit.TryCommitNonNegativeDouble(MarkerSizeTextBox, value =>
                ApplySeriesStyleEditToSelection(style => style.MarkerSize = value));
        if (committed)
        {
            if (!TryRefreshSelectedSeriesStyleLight()) SchedulePlotRefresh();
        }
    }

    private void MarkerSizeTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (_selectedSeriesRows.Count == 0) return;

        // MarkerSize に 0 を直接入力したまま編集を終えた場合も、ChartType
        // 切替時と同じ既定値補正を通し、テキストボックス表示と実描画の
        // 食い違いを防ぐ。TextChanged で補正すると "0.5" 入力途中の "0" を
        // 壊すため、編集確定 (フォーカス喪失) 時にだけ行う。選択全件に対して
        // 補正するが、テキストボックスへの書き戻しは active 分のみ
        // (NormalizeMarkerSizeIfNeeded 側のガード) なので表示は崩れない。
        var normalized = false;
        foreach (var row in _selectedSeriesRows)
        {
            if (row.State is { } series && NormalizeMarkerSizeIfNeeded(series))
            {
                normalized = true;
            }
        }

        if (normalized)
        {
            if (!TryRefreshSelectedSeriesStyleLight()) SchedulePlotRefresh();
        }
    }

    // ---------- Plot rendering ----------

    private void SchedulePlotRefresh()
    {
        _plotRefreshDebounceTimer.Stop();
        _plotRefreshDebounceTimer.Start();
    }

    private void PlotRefreshDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _plotRefreshDebounceTimer.Stop();
        RefreshPlot();
    }

    private void RefreshPlot()
    {
        _plotRefreshDebounceTimer.Stop();
        if (_plot is null) return;

        if (_loadedTables.Count == 0)
        {
            InitializeEmptyPlot();
            SetGraphActionsEnabled(false);
            return;
        }

        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);
        _plot.Plot.Clear();
        _plottedSeriesStyles.Clear();

        var xLog = XLogCheckBox.IsChecked == true;
        var yLog = YLogCheckBox.IsChecked == true;
        var y2Log = Y2LogCheckBox.IsChecked == true;

        _lastXRange = new AxisDataRange();
        _lastYRange = new AxisDataRange();
        _lastY2Range = new AxisDataRange();
        var hasCustomLegendName = false;
        string? firstSeriesName = null;
        string? firstRightSeriesName = null;
        _rightAxisInUse = false;

        // 1) 描画データを先に組み立てる (変換・log・有限ペア抽出まで)。棒の
        //    ドッジ幅を確定するには全系列を見渡す必要があるため一括化する。
        //    フラット表示順を一度だけ列挙し、その通し番号をそのまま auto 色の
        //    序数に使う (非表示系列も数えるので旧実装の通し番号と一致する)。
        var items = new List<RenderItem>();
        var seriesOrdinal = 0;
        foreach (var (loaded, series) in EnumerateSeriesInDisplayOrder())
        {
            var autoColorIndex = seriesOrdinal++;
            if (!series.IsVisible) continue;

            // X に割り当てた列は Y からは描かない。数値列がそれ 1 本しか
            // ないテーブルだけは行番号を X にして値の推移を見せる。
            var isXColumn = series.ColumnIndex == loaded.XColumnIndex;
            if (isXColumn && loaded.Series.Count > 1) continue;

            var xColumn = loaded.Table.Columns[loaded.XColumnIndex];
            var yColumn = loaded.Table.Columns[series.ColumnIndex];
            var xValues = isXColumn
                ? Enumerable.Range(1, yColumn.Values.Length).Select(static i => (double)i).ToArray()
                : xColumn.Values;

            // 変換は系列単位の非破壊適用 → log は表示変換として最後に挟む。
            var yValues = SeriesTransformer.Apply(yColumn.Values, series.Transform);
            if (xLog)
            {
                xValues = LogAxisHelper.ToLog10(xValues);
            }

            var seriesLog = series.UseRightAxis ? y2Log : yLog;
            if (seriesLog)
            {
                yValues = LogAxisHelper.ToLog10(yValues);
            }

            var (xs, ys) = ExtractFinitePairs(xValues, yValues);
            if (xs.Length == 0) continue;

            items.Add(new RenderItem
            {
                Xs = xs,
                Ys = ys,
                Style = series.Style,
                AutoColorIndex = autoColorIndex,
                ChartType = series.ChartType,
                UseRightAxis = series.UseRightAxis,
                LegendText = GetSeriesLegendText(loaded, series),
                ColumnName = series.ColumnName,
                UseSignalXY = SeriesPlotKindSelector.ShouldUseSignalXY(series.ChartType, xs),
            });
        }

        // 2) 棒系列は Excel 風に幅を詰めて横並び (ドッジ) させる。カテゴリ幅は
        //    全棒系列の X 間隔から推定し、各棒へ通し番号でオフセットを割り当てる。
        var barItems = items.Where(static item => item.ChartType == ViewerChartType.Bar).ToList();
        var barGroupWidth = BarLayout.EstimateGroupWidth(barItems.Select(static item => (IReadOnlyList<double>)item.Xs));

        // 3) 描画。散布図系 (折れ線 / マーカー / 両方) は Scatter が基本だが、線の
        //    み・X 厳密昇順の折れ線だけ高速な SignalXY を使う。棒は Bars。
        foreach (var item in items)
        {
            ScottPlot.IPlottable plottable;
            double[] xsForRange;

            if (item.ChartType == ViewerChartType.Bar)
            {
                var ordinal = barItems.IndexOf(item);
                var slot = BarLayout.ComputeSlot(ordinal, barItems.Count, barGroupWidth);
                var color = ResolveSeriesColor(item.Style, item.AutoColorIndex);
                var bars = new List<ScottPlot.Bar>(item.Xs.Length);
                xsForRange = new double[item.Xs.Length];
                for (var i = 0; i < item.Xs.Length; i++)
                {
                    var pos = item.Xs[i] + slot.Offset;
                    xsForRange[i] = pos;
                    bars.Add(new ScottPlot.Bar
                    {
                        Position = pos,
                        Value = item.Ys[i],
                        ValueBase = 0,
                        Size = slot.Size,
                        FillColor = color,
                    });
                }

                var barPlot = _plot.Plot.Add.Bars(bars);
                barPlot.LegendText = item.LegendText;
                plottable = barPlot;
            }
            else if (item.UseSignalXY)
            {
                var signal = _plot.Plot.Add.SignalXY(item.Xs, item.Ys);
                signal.LegendText = item.LegendText;
                ApplyLineMarkerStyle(signal, signal, item.Style, item.AutoColorIndex, item.ChartType);
                xsForRange = item.Xs;
                plottable = signal;
            }
            else
            {
                var scatter = _plot.Plot.Add.Scatter(item.Xs, item.Ys);
                scatter.LegendText = item.LegendText;
                ApplyLineMarkerStyle(scatter, scatter, item.Style, item.AutoColorIndex, item.ChartType);
                xsForRange = item.Xs;
                plottable = scatter;
            }

            _plottedSeriesStyles.Add(new PlottedSeries(plottable, item.Style, item.AutoColorIndex, item.ChartType));

            _lastXRange.Include(xsForRange);
            if (item.UseRightAxis)
            {
                plottable.Axes.YAxis = _plot.Plot.Axes.Right;
                _lastY2Range.Include(item.Ys);
                if (item.ChartType == ViewerChartType.Bar) _lastY2Range.Include(0);
                _rightAxisInUse = true;
                firstRightSeriesName ??= item.ColumnName;
            }
            else
            {
                _lastYRange.Include(item.Ys);
                if (item.ChartType == ViewerChartType.Bar) _lastYRange.Include(0);
                firstSeriesName ??= item.ColumnName;
            }

            hasCustomLegendName |= !string.IsNullOrWhiteSpace(item.Style.LegendName);
        }

        var plottedCount = items.Count;

        // log 軸は decade ticks (NumericManual)、linear は automatic に戻す。
        _plot.Plot.Axes.Bottom.TickGenerator = xLog
            ? LogAxisHelper.CreateDecadeTicks(_lastXRange.Min, _lastXRange.Max)
            : new ScottPlot.TickGenerators.NumericAutomatic();
        _plot.Plot.Axes.Left.TickGenerator = yLog
            ? LogAxisHelper.CreateDecadeTicks(_lastYRange.Min, _lastYRange.Max)
            : new ScottPlot.TickGenerators.NumericAutomatic();
        _plot.Plot.Axes.Right.TickGenerator = _rightAxisInUse && y2Log
            ? LogAxisHelper.CreateDecadeTicks(_lastY2Range.Min, _lastY2Range.Max)
            : new ScottPlot.TickGenerators.NumericAutomatic();

        _currentLegendAutoShow = LegendVisibility.ShouldAutoShow(plottedCount, hasCustomLegendName);
        ApplyLegend(_plot.Plot, CaptureFormattingConfigFromControls(), autoShow: _currentLegendAutoShow);

        var activeTable = ActiveTable ?? _loadedTables[0];
        _currentDefaultTitle = _loadedTables.Count == 1
            ? activeTable.DisplayName
            : $"{_loadedTables.Count} tables";
        _currentDefaultXLabel = activeTable.Series.Count == 1
            && activeTable.Series[0].ColumnIndex == activeTable.XColumnIndex
            ? "Index"
            : activeTable.Table.Columns[activeTable.XColumnIndex].Name;
        _currentDefaultYLabel = firstSeriesName ?? "Value";
        _currentRightAxisDefaultLabel = firstRightSeriesName ?? "Y2";
        ApplyGraphLabels();
        _plot.Plot.Axes.AutoScale();
        ApplyAxisLimits(_lastXRange, _lastYRange, _lastY2Range, xLog, yLog, y2Log);
        ApplyPlotAppearance();
        _plot.Refresh();

        SetGraphActionsEnabled(plottedCount > 0);
        if (plottedCount == 0)
        {
            SetStatus("表示中の系列がありません。", StatusSeverity.Warning);
        }
    }

    private static (double[] Xs, double[] Ys) ExtractFinitePairs(double[] xValues, double[] yValues)
    {
        var count = Math.Min(xValues.Length, yValues.Length);
        var xs = new List<double>(count);
        var ys = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            if (double.IsFinite(xValues[i]) && double.IsFinite(yValues[i]))
            {
                xs.Add(xValues[i]);
                ys.Add(yValues[i]);
            }
        }

        return (xs.ToArray(), ys.ToArray());
    }

    private string GetSeriesLegendText(LoadedTable table, SeriesState series)
        => SeriesLegendTextResolver.Resolve(
            series.Style.LegendName,
            series.ColumnName,
            table.DisplayName,
            multipleTablesLoaded: _loadedTables.Count > 1);

    private ScottPlot.Color ResolveSeriesColor(AnalysisSessionStyle style, int autoColorIndex)
    {
        var hex = style.ColorHex ?? AutoLineColors[autoColorIndex % AutoLineColors.Length];
        return ScottPlot.Color.FromHex(new[] { hex }).First();
    }

    /// <summary>
    /// 散布図系 (Scatter / SignalXY。折れ線 / マーカーのみ / 折れ線＋マーカー)
    /// の書式を当てる。線・マーカーの可否は種別が決め、サイズは系列スタイル値を
    /// 使う。マーカー必須種別でサイズ 0 のときは既定値へ落として点が消えない
    /// ようにする。<paramref name="hasLine"/> / <paramref name="hasMarker"/> は
    /// 同一プロット要素を指すことを想定 (Scatter / SignalXY はともに両
    /// interface を実装するのでこの分解で渡せる)。Scatter.Color は
    /// LineColor/MarkerColor へ同時代入する糖衣プロパティなので、個別代入でも
    /// 見た目は変わらない。
    /// </summary>
    private void ApplyLineMarkerStyle(
        ScottPlot.IHasLine hasLine,
        ScottPlot.IHasMarker hasMarker,
        AnalysisSessionStyle style,
        int autoColorIndex,
        ViewerChartType chartType,
        float scale = 1f)
    {
        var color = ResolveSeriesColor(style, autoColorIndex);
        hasLine.LineWidth = chartType.ShowsLine() ? (float)style.LineWidth * scale : 0f;
        hasLine.LineColor = color;
        var markerSize = chartType.ShowsMarkers()
            ? (style.MarkerSize > 0 ? style.MarkerSize : DefaultMarkerSizeForMarkers)
            : 0;
        hasMarker.MarkerSize = (float)markerSize * scale;
        hasMarker.MarkerColor = color;
    }

    private void ApplyBarStyle(
        ScottPlot.Plottables.BarPlot barPlot,
        AnalysisSessionStyle style,
        int autoColorIndex)
    {
        var color = ResolveSeriesColor(style, autoColorIndex);
        foreach (var bar in barPlot.Bars)
        {
            bar.FillColor = color;
        }
    }

    private void ApplyAxisLimits(
        AxisDataRange xRange,
        AxisDataRange yRange,
        AxisDataRange y2Range,
        bool xLog,
        bool yLog,
        bool y2Log)
    {
        if (_plot is null) return;

        // AxisRangePanel / Y2 欄の入力は常にデータ単位。log 軸のときだけ
        // SetLimits 直前で log10 に変換する (0 以下は無効として弾く)。
        var xMin = AxisRangePanel.XMinValue;
        var xMax = AxisRangePanel.XMaxValue;
        var yMin = AxisRangePanel.YMinValue;
        var yMax = AxisRangePanel.YMaxValue;

        if ((xMin.HasValue || xMax.HasValue)
            && TryConvertLogLimit(ref xMin, xLog, "X Min")
            && TryConvertLogLimit(ref xMax, xLog, "X Max")
            && TryGetRequestedRange(xRange, xMin, xMax, "X", out var left, out var right))
        {
            _plot.Plot.Axes.SetLimitsX(left, right);
        }

        if ((yMin.HasValue || yMax.HasValue)
            && TryConvertLogLimit(ref yMin, yLog, "Y Min")
            && TryConvertLogLimit(ref yMax, yLog, "Y Max")
            && TryGetRequestedRange(yRange, yMin, yMax, "Y", out var bottom, out var top))
        {
            _plot.Plot.Axes.SetLimitsY(bottom, top);
        }

        if (_rightAxisInUse && (_y2Min.HasValue || _y2Max.HasValue))
        {
            var y2Min = _y2Min;
            var y2Max = _y2Max;
            if (TryConvertLogLimit(ref y2Min, y2Log, "右 Y Min")
                && TryConvertLogLimit(ref y2Max, y2Log, "右 Y Max")
                && TryGetRequestedRange(y2Range, y2Min, y2Max, "右 Y", out var y2Bottom, out var y2Top))
            {
                _plot.Plot.Axes.Right.Min = y2Bottom;
                _plot.Plot.Axes.Right.Max = y2Top;
            }
        }
    }

    private bool TryConvertLogLimit(ref double? value, bool isLog, string label)
    {
        if (!value.HasValue || !isLog) return true;
        if (value.Value <= 0)
        {
            SetStatus($"log 軸の {label} は正の値にしてください。", true);
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
            SetStatus($"{axisName} 軸範囲を決定できませんでした。", true);
            return false;
        }

        if (min >= max)
        {
            SetStatus($"{axisName} Min は {axisName} Max より小さい値にしてください。", true);
            return false;
        }

        return true;
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

    /// <summary>タイトル / X / Y ラベルを、現在のテキストボックス値 (空欄なら
    /// RefreshPlot がキャッシュした既定値) で適用する軽量パス。フル再構築の
    /// RefreshPlot と、見た目だけの変更 (GraphLabelTextBox_TextChanged) の
    /// 双方から呼ぶことで二重実装を防ぐ。プロット未初期化時は何もしない。</summary>
    private void ApplyGraphLabels()
    {
        if (_plot is null) return;

        _plot.Plot.Title(GetGraphTitle(_currentDefaultTitle));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, _currentDefaultXLabel));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, _currentDefaultYLabel));
    }

    private void SetGraphActionsEnabled(bool enabled)
    {
        SaveGraphButton.IsEnabled = enabled && _plot is not null;
        ExportDataButton.IsEnabled = enabled;
        SaveSessionButton.IsEnabled = _loadedTables.Count > 0;
    }

    // ---------- Formatting config ----------

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

        // 系列スタイル欄は選択中系列の編集用なので、アプリ既定の線スタイルは
        // 読み込んだ既定値をそのまま引き継ぐ。
        config.DefaultLineColorHex = _formattingConfig.DefaultLineColorHex;
        config.LineWidth = _formattingConfig.LineWidth;
        config.MarkerSize = _formattingConfig.MarkerSize;
        config.DefaultOutputDirectory = _formattingConfig.DefaultOutputDirectory;

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
    }

    private void ResetGraphSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
        ApplyFormattingConfigToControls(_formattingConfig);
        UpdatePlotHostAspectRatio();
        RefreshPlot();
        SetStatus("グラフ書式を既定値に戻しました。", StatusSeverity.Info);
    }

    private void SaveDefaultFormattingButton_Click(object? sender, RoutedEventArgs e)
    {
        _formattingDefaults = CaptureFormattingConfigFromControls();
        SaveFormattingDefaults();
        Toast?.Show("既定値を保存しました", StatusSeverity.Success);
        SetStatus("現在のグラフ書式を既定値として保存しました。", StatusSeverity.Success);
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

        // プロットが空 (未読み込み) のときは _currentDefaultXxx が未確定なので
        // フル再構築 (RefreshPlot → InitializeEmptyPlot) に任せる。
        if (_plot is null || _plottedSeriesStyles.Count == 0)
        {
            SchedulePlotRefresh();
            return;
        }

        // タイトル/軸ラベルは既存 plottable の再構築なしに書き換えられる見た目
        // だけの変更。既存プロット要素はそのままズームも保持したまま
        // ApplyGraphLabels + ApplyGraphAppearanceAndRefresh の軽量パスへ流す。
        ApplyGraphLabels();
        ApplyGraphAppearanceAndRefresh();
    }

    private void AxisRangePanel_Committed(object? sender, EventArgs e)
    {
        if (_suppressGraphAppearanceEvents) return;
        RefreshPlot();
    }

    private void OnLegendDragCommit(string position, double offsetX, double offsetY)
    {
        _formattingConfig.LegendPosition = position;
        _formattingConfig.LegendOffsetX = offsetX;
        _formattingConfig.LegendOffsetY = offsetY;
        GraphFormatPanel.SyncLegendPlacement(position, offsetX, offsetY);
        ApplyGraphAppearanceAndRefresh();
    }

    private void ApplyGraphAppearanceAndRefresh()
    {
        if (_plot is null) return;

        ApplyPlotAppearance();
        ApplyLegend(_plot.Plot, CaptureFormattingConfigFromControls(), autoShow: _currentLegendAutoShow);
        _plot.Refresh();
    }

    private void ApplyPlotAppearance(float scale = 1f)
    {
        if (_plot is null) return;
        var config = CaptureFormattingConfigFromControls();
        ApplyAll(_plot.Plot, config, scale);
        ApplyRightAxisAppearance(config, scale);
    }

    /// <summary>
    /// 共有 PlotAppearance.ApplyAll は Left / Bottom しか整形しないので、
    /// 右軸使用時だけモジュール側で同じ規約 (フォントサイズ・tick・
    /// PaddingBetweenTickAndAxisLabels の Y 係数 0.55+2) を右軸へ写す。
    /// 未使用時は tick ラベルと tick 線を消して従来の右フレーム線だけ残す。
    /// </summary>
    private void ApplyRightAxisAppearance(GraphFormattingConfig config, float scale)
    {
        if (_plot is null) return;
        var right = _plot.Plot.Axes.Right;

        if (!_rightAxisInUse)
        {
            right.Label.Text = string.Empty;
            right.TickLabelStyle.IsVisible = false;
            ConfigureTickMarkStyle(right.MajorTickStyle, MajorTickLengthBase, (float)config.TickWidth, scale, visible: false);
            ConfigureTickMarkStyle(right.MinorTickStyle, MinorTickLengthBase, (float)config.TickWidth, scale, visible: false);
            return;
        }

        var fontSize = (float)config.FontSize * scale;
        right.Label.Text = GetGraphLabel(Y2LabelTextBox, _currentRightAxisDefaultLabel);
        right.Label.FontSize = fontSize;
        right.Label.Bold = config.AxisLabelBold;
        right.Label.Font = null;
        right.TickLabelStyle.IsVisible = true;
        right.TickLabelStyle.FontSize = Math.Max(6 * scale, fontSize - scale);
        right.TickLabelStyle.Font = null;

        ConfigureTickMarkStyle(right.MajorTickStyle, MajorTickLengthBase, (float)config.TickWidth, scale, config.ShowMajorTicks);
        ConfigureTickMarkStyle(right.MinorTickStyle, MinorTickLengthBase, (float)config.TickWidth, scale, config.ShowMinorTicks);

        if (right.TickGenerator is ScottPlot.TickGenerators.NumericAutomatic rightAuto)
        {
            rightAuto.TickDensity = config.TickDensity;
        }

        // 軸ラベルと目盛数字の間隔は左 Y 軸と同じ係数 (FontSize × 0.55 + 2)。
        float yGap = MathF.Max(5f * scale, fontSize * 0.55f + 2f * scale);
        float minor = 3f * scale;
        if (right is ScottPlot.AxisPanels.YAxisBase yRight)
        {
            yRight.PaddingBetweenTickAndAxisLabels = new ScottPlot.PixelPadding(yGap, yGap, minor, minor);
        }

        // 右軸を使っている間はフレーム OFF でも軸線が必要。
        right.FrameLineStyle.IsVisible = true;
    }

    private void PlotContainerBorder_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdatePlotHostAspectRatio();
    }

    private double? GetSelectedAspectRatio() => GraphFormatPanel.AspectRatioValue;

    private void UpdatePlotHostAspectRatio()
        => PlotHostAspectRatio.Apply(PlotHost, PlotContainerBorder, GetSelectedAspectRatio());

    // ---------- Graph export ----------

    private async void SaveGraphButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_plot is null || _loadedTables.Count == 0)
        {
            ShowError("保存するグラフがありません。");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = ActiveTable is { } table
            ? Path.GetFileNameWithoutExtension(table.Table.SourceFilePath) ?? "data_viewer"
            : "data_viewer";
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
            var (width, height) = GraphSaveHelpers.GetExportImageSize(GetSelectedAspectRatio());
            var saveFormat = GraphSaveHelpers.GetGraphSaveFormat(path);
            var fileName = GraphSaveHelpers.EnsureGraphSaveFileExtension(path, saveFormat);
            var exportStyleScale = GraphSaveHelpers.ExportDpi / GraphSaveHelpers.DisplayDpi;

            ApplyExportStyleScale(exportStyleScale);
            try
            {
                if (saveFormat == GraphSaveFormat.Svg)
                {
                    GraphSaveHelpers.SaveGraphSvg(_plot.Plot, fileName, width, height);
                    SetStatus($"グラフをSVGで保存しました: {fileName} ({width:N0} x {height:N0})", StatusSeverity.Success);
                    Toast?.Show("SVG を保存しました", StatusSeverity.Success);
                    return;
                }

                GraphSaveHelpers.SaveGraphPng(_plot.Plot, fileName, width, height, GraphSaveHelpers.ExportDpi);
                SetStatus($"グラフをPNGで保存しました: {fileName} ({width:N0} x {height:N0} px, {GraphSaveHelpers.ExportDpi} dpi)", StatusSeverity.Success);
                Toast?.Show("PNG を保存しました", StatusSeverity.Success);
            }
            finally
            {
                ApplyExportStyleScale(1f);
                _plot.Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
        }
    }

    private void ApplyExportStyleScale(float scale)
    {
        if (_plot is null) return;

        ApplyPlotAppearance(scale);
        foreach (var plotted in _plottedSeriesStyles)
        {
            switch (plotted.Plottable)
            {
                case ScottPlot.Plottables.Scatter scatter:
                    ApplyLineMarkerStyle(scatter, scatter, plotted.Style, plotted.AutoColorIndex, plotted.ChartType, scale);
                    break;
                case ScottPlot.Plottables.SignalXY signal:
                    ApplyLineMarkerStyle(signal, signal, plotted.Style, plotted.AutoColorIndex, plotted.ChartType, scale);
                    break;
                case ScottPlot.Plottables.BarPlot barPlot:
                    ApplyBarStyle(barPlot, plotted.Style, plotted.AutoColorIndex);
                    break;
            }
        }
    }

    private async Task<IStorageFolder?> GetDefaultStartLocationAsync(IStorageProvider sp)
    {
        var dir = FormattingDefaultsStore.GetEffectiveDefaultOutputDirectory(_formattingDefaults);
        if (string.IsNullOrEmpty(dir)) return null;
        try { return await sp.TryGetFolderFromPathAsync(dir); }
        catch { return null; }
    }

    // ---------- Status helpers ----------

    private void SetStatus(string message, bool isError = false)
    {
        StatusBar?.SetStatus(message, isError ? StatusSeverity.Error : StatusSeverity.Info);
        if (!isError)
        {
            ErrorBanner.Hide();
        }
    }

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
}

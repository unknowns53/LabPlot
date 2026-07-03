using System;
using System.Collections.Generic;
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
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DlsAnalyzer.Core;
using LabPlot.Core;
using LabPlot.Core.Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;
using ScottPlot.Avalonia;
using static LabPlot.Core.PlotAppearance;
using static LabPlot.Core.Avalonia.FormatHelpers;

namespace LabPlot.DLS.Avalonia;

/// <summary>
/// Avalonia 版 DLS Analyzer のメインウィンドウ。WPF 版 LabPlot.DLS.MainWindow.xaml.cs
/// (2167 行) を Avalonia API に翻訳した本実装。WPF 専用 API は次の方針で置き換えた：
/// <list type="bullet">
///   <item>SaveFileDialog / OpenFileDialog / OpenFolderDialog → IStorageProvider の
///     SaveFilePickerAsync / OpenFilePickerAsync / OpenFolderPickerAsync (全 async)</item>
///   <item>ScottPlot.WPF.WpfPlot → ScottPlot.Avalonia.AvaPlot</item>
///   <item>InputBindings + RoutedUICommand → OnKeyDown オーバーライドで集中ディスパッチ</item>
///   <item>Keyboard.ClearFocus / FocusManager.SetFocusedElement → Window.Focus()</item>
///   <item>Visibility.Visible / Collapsed → IsVisible (bool)</item>
///   <item>DataFormats.FileDrop の string[] → DataFormats.Files の IStorageItem 列挙</item>
///   <item>ScottPlot.Color の Avalonia.Media.Color 変換は LabPlot.Core.Avalonia.FormatHelpers.HexToAvaloniaColor</item>
/// </list>
/// 凡例マウスドラッグは Phase 7 Batch 6 step 3 で
/// <see cref="LabPlot.Core.Avalonia.Helpers.LegendDragController"/> として移植済み。
/// LegendPosition / LegendOffsetX/Y は GraphFormatPanel + ドラッグ操作の双方から制御できる。
/// </summary>
public partial class MainWindow : Window, IDlsAnalysisHost, IPortalFileOpener
{
    private static readonly string[] AutoLineColors =
    [
        "#2563EB", "#DC2626", "#16A34A", "#EA580C", "#7C3AED", "#0891B2", "#4B5563",
    ];

    private static readonly JsonSerializerOptions FormattingConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly string FormattingConfigPath = Path.Combine(
        AppDataPaths.GetApplicationDataPath(),
        "LabPlot.DLS",
        "formatting_config.json");

    private readonly ZetasizerXlsxReader _reader = new();
    private readonly List<DlsDataset> _datasets = new();
    private readonly List<DlsDataset> _selectedDatasets = new();
    private readonly List<DlsDatasetItem> _datasetItems = new();
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private AvaPlot? _plot;
    private LegendDragController? _legendDragController;
    private PlotFastModeController? _plotFastModeController;

    // GPC PR #12 と同パターン: 直近の Plot 描画で追加した plottable を追跡し、
    // 次回 refresh のときに `Plot.Clear()` で全体リセットする代わりに、ここに
    // 入っているものだけを `Plot.Remove()` する。Title / axes / legend setting
    // など Plot side の global state は維持される。DLS は GPC と違って Scatter
    // 以外に ScatterPoints / ScatterLine も使うので IPlottable で受ける。
    // ScottPlot 5.1.58 の `Scatter.Data` は読み取り専用なので「dataset 数が
    // 変わらないとき plottable をリサイクル」までは到達できず、現状は単純な
    // pool 管理のみ。ScottPlot 公開 API が拡張されたら inplace data swap に
    // 進められる。
    private readonly List<ScottPlot.IPlottable> _plottablePool = new();

    // Phase 7 Batch 6 step 4: 内部 reorder 状態。GPC / Spectrum と同方針で
    // OS DragDrop layer は使わず PointerCapture + 手動位置計算で実装する。
    private Point? _datasetDragStartPoint;
    private int? _datasetDragSourceIndex;
    private ListBoxItem? _datasetDragSourceContainer;
    private bool _isInternalReordering;
    private IPointer? _reorderCapturedPointer;
    private readonly DragGhostController _dragGhost = new();
    private Point _dragGhostPointerOffset;

    private DistributionMode _selectedMode = DistributionMode.Number;
    private int _selectedRunIndex;
    private int _activeItemIndex = -1;
    private bool _suppressRunComboEvents;
    private bool _suppressFormattingEvents;
    private bool _suppressStyleControlEvents;
    private string? _currentWorkbookPath;
    private AnalysisWindow? _analysisWindow;

    // サイドバータブ (データ / 仕上げ) の切替。XAML の RadioButton 初期値
    // (IsChecked="True") が InitializeComponent 実行中に IsCheckedChanged を
    // 発火させ、その時点ではまだ DataTabPanel / FormatTabPanel の x:Name
    // フィールドが代入されていないため、ガードなしで参照すると NRE になる。
    // InitializeComponent 完了後に true にし、それまではハンドラを早期 return
    // させる (GPC / Spectrum / Data Viewer MainWindow と同じ方式)。
    private bool _sidebarTabsInitialized;

    public MainWindow()
    {
        InitializeComponent();
        _sidebarTabsInitialized = true;
        LoadFormattingDefaults();
        _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
        Opened += OnOpened;

        // ListBox の DragDrop 系 routed event は XAML 属性経由で配線できないので
        // AddHandler で明示的に繋ぐ。AllowDrop は XAML で `DragDrop.AllowDrop="True"` 済み。
        // OnAttachedToVisualTree 経由の登録は実機で発火しないケースがあるので、
        // GPC と同じく ctor 末尾に集約する。
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

    // Avalonia.Generators が partial class に InitializeComponent + x:Name フィールド代入を
    // 自動生成するので手動定義しない（Phase 7 Batch 6 で発覚した null フィールド NRE 対策）。

    // WPF の Loaded イベント相当。Window.Opened は最初に画面に表示されるとき
    // 1 回だけ発火し、ApplyTemplate / 子ツリー構築完了後に呼ばれる。
    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            _plot = new AvaPlot();
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_plot);

            // Phase 7 Batch 6 step 3: WPF 同等の凡例ドラッグ移動を有効化。
            _legendDragController = new LegendDragController(
                _plot,
                () => _formattingConfig.LegendPosition,
                () => (_formattingConfig.LegendOffsetX, _formattingConfig.LegendOffsetY),
                OnLegendDragCommit);
            _legendDragController.Attach();

            // パン / ホイールズーム操作中だけ AA を切って描画を軽くする。
            _plotFastModeController = new PlotFastModeController(
                _plot,
                () => _plottablePool);
            _plotFastModeController.Attach();

            PlotContextMenu.Apply(_plot, () => _ = SaveGraphAsync());

            ApplyFormattingConfigToControls(_formattingConfig);
            SyncStyleControlsFromActiveItem();
            _selectedMode = DistributionModeFromTag(_formattingConfig.DefaultDistributionMode);
            SelectComboBoxByTag(DistributionTypeComboBox, _formattingConfig.DefaultDistributionMode);
            _selectedRunIndex = Math.Max(0, _formattingConfig.DefaultRunIndex);

            // 初期化成功時点でスケルトンを消す。placeholder TextBlock の文言は
            // InitializeEmptyPlot で SetState(EmptyReady) に切り替わる。
            PlotPlaceholderSkeleton.IsVisible = false;
            InitializeEmptyPlot();
            UpdatePlotHostAspectRatio();

            // v1.3 Batch A: 初期メッセージは Info severity で出す。XAML 上の Text= 初期値が
            // StatusBar 化で消えたため、起動完了時点で明示的にセットする。
            SetStatus("DLS xlsx を開いてください。", StatusSeverity.Info);

            // v1.3 Batch E: 最近開いた一覧を ComboBox に流す。
            RefreshRecentFilesUi();
        }
        catch (Exception ex)
        {
            PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.InitFailed);
            ShowError($"グラフ表示の初期化に失敗しました: {ex.Message}");
        }
    }

    // WPF の InputBindings / RoutedUICommand 配列を OnKeyDown 1 メソッドに集約。
    // 修飾キー判定は KeyboardShortcuts.HasCommandModifier 経由で OS 別に出し分ける (macOS = Cmd)。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var cmd = e.HasCommandModifier();
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (cmd && shift)
        {
            switch (e.Key)
            {
                case Key.S: _ = SaveSessionAsync(); e.Handled = true; return;
                case Key.O: _ = LoadSessionAsync(); e.Handled = true; return;
            }
        }
        else if (cmd)
        {
            switch (e.Key)
            {
                case Key.O: _ = OpenWorkbookAsync(); e.Handled = true; return;
                case Key.S: _ = SaveGraphAsync(); e.Handled = true; return;
                case Key.E: _ = ExportAnalysisAsync(); e.Handled = true; return;
                case Key.R: AxisRangePanel.ResetToAuto(); e.Handled = true; return;
                case Key.G: GraphFormatPanel.TogglePlotGrid(); e.Handled = true; return;
                // v1.3.5: Ctrl/Cmd+L は GPC/Spectrum で Overlay 切替に予約されているため、
                //         DLS 固有の「全選択/全解除」を Ctrl/Cmd+A に移し、3 モジュール間の
                //         ホットキー意味衝突を解消する。
                case Key.A: ToggleAllDatasets(); e.Handled = true; return;
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
            global::LabPlot.Core.Avalonia.KeyboardShortcutsWindow.ShowFor(this, global::LabPlot.Core.Avalonia.AppKind.Dls);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void ToggleAllDatasets()
    {
        if (DatasetListBox is null || DatasetListBox.ItemCount == 0) return;
        if (DatasetListBox.SelectedItems is { Count: var sc } && sc == DatasetListBox.ItemCount)
            DatasetListBox.SelectedItems.Clear();
        else
            DatasetListBox.SelectAll();
    }

    private void FocusLegendNameTextBox()
    {
        if (LegendNameTextBox is null || !LegendNameTextBox.IsEnabled) return;
        LegendNameTextBox.Focus();
        LegendNameTextBox.SelectAll();
    }

    private void InitializeEmptyPlot()
    {
        if (_plot is null) return;

        // データ無しの状態 — placeholder を「ファイルを読み込むと…」に切り替え。
        // 起動時 (OnOpened 直後) と全データセット解除時の両方から呼ばれる。
        PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.EmptyReady);

        ClearPlottablePool();
        // 既定でタイトル無し — モード名 (例: "Particle Size Distribution") を
        // 自動表示すると、書式パネル上部のペインヘッダと二重に見えるため、
        // ユーザーが書式パネルのタイトル欄に入力した場合のみ表示する。
        _plot.Plot.Title(GetGraphTitle(string.Empty));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultLabels.GetDefaultXLabel(_selectedMode)));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, DefaultLabels.GetModeLabel(_selectedMode)));
        ApplyLogXTicksForMode(_selectedMode);
        if (_selectedMode == DistributionMode.Correlation)
            _plot.Plot.Axes.SetLimits(Math.Log10(0.5), Math.Log10(10000), 0, 1.05);
        else
            _plot.Plot.Axes.SetLimits(Math.Log10(0.3), Math.Log10(10000), 0, 30);
        ApplyPlotAppearance();
        ApplyLegend(0);
        _plot.Refresh();
    }

    // ---------- File open / drag-drop ----------

    private void OpenButton_Click(object? sender, RoutedEventArgs e) => _ = OpenWorkbookAsync();

    // v1.3 Batch E: 最近開いたファイル MRU。RecentFilesStore に永続化、UI から再 open する。
    // 2026-05-25: 選択直後に SelectedIndex=-1 へ戻すと placeholder に潰れて
    // 「今どのファイルを開いているか」が一目で分からなくなるため、ロード成功した
    // ファイルを選択状態のまま残す方針に変更。同一ファイルの再ロードは「開く」ボタン側で。
    private const string RecentFilesAppKey = "dls";
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
        var item = new ComboBoxItem
        {
            Content = Path.GetFileName(path),
            Tag = path,
        };
        ToolTip.SetTip(item, path);
        return item;
    }

    private void RecentFilesComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressRecentFilesEvents) return;
        if (RecentFilesComboBox.SelectedItem is not ComboBoxItem item) return;
        var path = item.Tag as string;
        if (string.IsNullOrWhiteSpace(path)) return;
        _ = ImportWorkbookAsync(path);
    }

    // 履歴 ComboBox の右クリックメニュー → 「履歴をクリア」。RecentFilesStore.Clear で永続化
    // ファイルを消し、UI を空状態に戻す。履歴 (MRU) と表示中のプロット・データセットの寿命を
    // 揃える (GPC / Spectrum と同じ方針)。
    //
    // v1.3.5: 旧実装は Confirm を省略し Toast 通知のみだったが、右クリックメニュー 1 発で
    //         作業中のグラフが消えるのは破壊的すぎるため、GPC / Spectrum と揃えて
    //         ConfirmDialog を挟む。
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

        _datasets.Clear();
        _datasetItems.Clear();
        _activeItemIndex = -1;
        _currentWorkbookPath = null;
        DatasetListBox.ItemsSource = _datasetItems;
        DatasetCountText.Text = string.Empty;
        InitializeEmptyPlot();

        _missingFileWatcher?.Watch(null);

        RefreshRecentFilesUi();
        SetStatus("最近開いたファイルの履歴とプロットをクリアしました。", StatusSeverity.Info);
    }

    // 読み込み中の xlsx が OS 側で削除 / リネームされた瞬間に MissingFileWatcher から
    // UI スレッド経由で呼ばれる。GPC / Spectrum と同方針で MRU 履歴は触らず、表示中の
    // プロットとデータセット内部状態だけクリアする。
    private void OnLoadedFileMissing()
    {
        var name = string.IsNullOrEmpty(_lastLoadedFilePath)
            ? "ファイル"
            : Path.GetFileName(_lastLoadedFilePath);

        _missingFileWatcher?.Watch(null);
        _lastLoadedFilePath = null;
        _datasets.Clear();
        _datasetItems.Clear();
        _activeItemIndex = -1;
        _currentWorkbookPath = null;
        DatasetListBox.ItemsSource = _datasetItems;
        DatasetCountText.Text = string.Empty;
        InitializeEmptyPlot();

        SetStatus($"{name} が削除されたためプロットをクリアしました。", StatusSeverity.Info);
    }

    private async Task OpenWorkbookAsync()
    {
        var sp = StorageProvider;
        if (sp is null) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Zetasizer xlsx を開く",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excel ファイル") { Patterns = new[] { "*.xlsx" } },
                FilePickerFileTypes.All,
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        await ImportWorkbookAsync(path);
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

    /// <summary>
    /// <see cref="IPortalFileOpener.OpenFilesAsync"/> の実装。Portal からのファイル
    /// drop / 最近開いたファイルクリックの 1 本道として、Window が表示完了する
    /// (Loaded) まで待ってから既存の <see cref="ImportWorkbookAsync"/> に流す。
    /// DLS は単一 xlsx しか扱えないので、複数渡された場合は先頭のみ採用する。
    /// </summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0) return;
        await this.WhenLoadedAsync();
        await ImportWorkbookAsync(filePaths[0]);
    }

    private async Task ImportWorkbookAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            BusyOverlay.Show("xlsx を読み込み中…");
            var loaded = await Task.Run(() => _reader.Read(filePath));
            _datasets.Clear();
            foreach (var ds in loaded) _datasets.Add(ds);

            _datasetItems.Clear();
            foreach (var ds in _datasets) _datasetItems.Add(new DlsDatasetItem(ds));
            NormalizeSharedMetadataAcrossItems();

            _currentWorkbookPath = filePath;
            DatasetListBox.ItemsSource = null;
            DatasetListBox.ItemsSource = _datasetItems;
            UpdateDatasetListPlaceholder();

            // v1.3 Batch E: 読み込み成功時のみ MRU に追加して ComboBox を更新する。
            // 直近で開いたファイル名が ComboBox に表示され続けるよう _lastLoadedFilePath で保持。
            RecentFilesStore.Add(RecentFilesAppKey, filePath);
            _lastLoadedFilePath = filePath;
            (_missingFileWatcher ??= new MissingFileWatcher(OnLoadedFileMissing)).Watch(_lastLoadedFilePath);
            RefreshRecentFilesUi();

            // v1.3 Batch H: タイトルバー Subtitle と Window Title にファイル名を反映。
            var fileNameOnly = Path.GetFileName(filePath);
            if (MainTitleBar is not null) MainTitleBar.Subtitle = fileNameOnly;
            Title = $"DLS Analyzer — {fileNameOnly}";
            DatasetCountText.Text = _datasets.Count == 0
                ? "粒径分布シートが見つかりませんでした"
                : $"{_datasets.Count} シート読み込み済み（{fileNameOnly}）";

            HideError();
            SetStatus(_datasets.Count == 0
                ? $"粒径分布シートが見つかりませんでした: {fileNameOnly}"
                : $"{_datasets.Count} シートを読み込みました: {fileNameOnly}");

            // Notify AnalysisWindow before SelectionChanged fires so the child
            // refreshes its result panels with the new dataset list.
            RaiseAnalysisDataChanged();

            if (_datasets.Count > 0) DatasetListBox.SelectedIndex = 0;
            else ClearActiveDatasets();
        }
        catch (Exception ex)
        {
            ShowError($"読み込みに失敗しました: {ex.Message}");
        }
        finally
        {
            BusyOverlay.Hide();
        }
    }

    // Phase 7 後始末 Batch 7a で Avalonia 11.3 の新 API
    // (DataTransfer / DataFormat.File / TryGetFilesAsync) に移行済み。
    private void OnDatasetDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer is not null && e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            ShowFileDropOverlay();
        }
        else
        {
            HideFileDropOverlay();
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDatasetDragLeave(object? sender, DragEventArgs e)
    {
        // Avalonia の DragLeave は領域を出入りするたびに発火しがちなので、
        // 単純に毎回隠す（Drop 直前の最後の DragOver で再表示される）。
        HideFileDropOverlay();
    }

    private void OnDatasetDrop(object? sender, DragEventArgs e)
    {
        HideFileDropOverlay();
        if (e.DataTransfer is null || !e.DataTransfer.Contains(DataFormat.File)) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        var first = files.FirstOrDefault();
        if (first is null) return;
        var path = first.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        e.Handled = true;
        _ = ImportWorkbookAsync(path);
    }

    // ---------- Drag-reorder (Phase 7 Batch 6 step 4 で新規追加) ----------
    // GPC / Spectrum と同じ手動 PointerCapture 方式。WPF DLS 自体には reorder
    // 機能が無かったが、Avalonia 版では LegendDragController と同じく Avalonia
    // 専用の追加機能として実装する。

    private void OnDatasetListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual srcVisual && FindAncestor<Button>(srcVisual) is not null)
        {
            ResetReorderState();
            return;
        }

        var item = e.Source is Visual v ? FindAncestor<ListBoxItem>(v) : null;
        if (item is null)
        {
            ResetReorderState();
            return;
        }

        if (!e.GetCurrentPoint(DatasetListBox).Properties.IsLeftButtonPressed) return;

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

        if (!e.GetCurrentPoint(DatasetListBox).Properties.IsLeftButtonPressed) return;

        var current = e.GetPosition(DatasetListBox);

        if (!_isInternalReordering)
        {
            var dx = current.X - _datasetDragStartPoint.Value.X;
            var dy = current.Y - _datasetDragStartPoint.Value.Y;
            if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

            var sourceIndex = _datasetDragSourceIndex.Value;
            if (sourceIndex < 0 || sourceIndex >= _datasetItems.Count)
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
                _datasetItems[sourceIndex],
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
            newIndex = _datasetItems.Count - 1;
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
        else if (newIndex >= _datasetItems.Count) newIndex = _datasetItems.Count - 1;

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

    private static T? FindAncestor<T>(Visual? element) where T : class
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element.GetVisualParent();
        }
        return null;
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

    private void MoveDataset(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex
            || oldIndex < 0 || oldIndex >= _datasets.Count
            || newIndex < 0 || newIndex >= _datasets.Count)
        {
            return;
        }

        // 元の選択状態を一旦記憶しておき、reorder 後に復元する。SelectedDatasets
        // はオブジェクト参照ベースなので、index が変わっても同じインスタンスを
        // SelectedItems に戻せば追従できる。
        var previouslySelected = _selectedDatasets.ToList();

        var dataset = _datasets[oldIndex];
        _datasets.RemoveAt(oldIndex);
        _datasets.Insert(newIndex, dataset);

        var item = _datasetItems[oldIndex];
        _datasetItems.RemoveAt(oldIndex);
        _datasetItems.Insert(newIndex, item);

        // Avalonia の ListBox は List<T> を ItemsSource にしただけだと
        // INotifyCollectionChanged が無く reorder が UI に反映されないので、
        // 一度 null にしてから再 bind して強制再描画する。SelectedItems は
        // この瞬間にクリアされるが、直後に元の選択を復元する。
        _suppressFormattingEvents = true;
        try
        {
            DatasetListBox.ItemsSource = null;
            DatasetListBox.ItemsSource = _datasetItems;

            DatasetListBox.SelectedItems?.Clear();
            foreach (var ds in previouslySelected)
            {
                var idx = _datasets.IndexOf(ds);
                if (idx >= 0 && idx < _datasetItems.Count)
                {
                    DatasetListBox.SelectedItems?.Add(_datasetItems[idx]);
                }
            }
        }
        finally
        {
            _suppressFormattingEvents = false;
        }

        RefreshPlot();
    }

    private void ShowFileDropOverlay() => DatasetDropOverlay.IsVisible = true;
    private void HideFileDropOverlay() => DatasetDropOverlay.IsVisible = false;

    // ---------- Save graph / export ----------

    private void SaveGraphButton_Click(object? sender, RoutedEventArgs e) => _ = SaveGraphAsync();
    private void ExportButton_Click(object? sender, RoutedEventArgs e) => _ = ExportAnalysisAsync();

    private async Task SaveGraphAsync()
    {
        if (_plot is null || _selectedDatasets.Count == 0)
        {
            ShowError("出力可能なデータがありません。");
            return;
        }
        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = $"{Path.GetFileNameWithoutExtension(GetCurrentWorkbookHint())}_dls";
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
            var saveFormat = GraphSaveHelpers.GetGraphSaveFormat(path);
            var fileName = GraphSaveHelpers.EnsureGraphSaveFileExtension(path, saveFormat);
            var (width, height) = GraphSaveHelpers.GetExportImageSize(GraphFormatPanel.AspectRatioValue);
            var exportStyleScale = GraphSaveHelpers.ExportDpi / GraphSaveHelpers.DisplayDpi;

            ApplyExportStyleScale(exportStyleScale);
            try
            {
                if (saveFormat == GraphSaveFormat.Svg)
                {
                    GraphSaveHelpers.SaveGraphSvg(_plot.Plot, fileName, width, height);
                    SetStatus($"グラフをSVGで保存しました: {fileName} ({width:N0} x {height:N0})", StatusSeverity.Success);
                    Toast?.Show("SVG を保存しました", StatusSeverity.Success);
                }
                else
                {
                    GraphSaveHelpers.SaveGraphPng(_plot.Plot, fileName, width, height, GraphSaveHelpers.ExportDpi);
                    SetStatus($"グラフをPNGで保存しました: {fileName} ({width:N0} x {height:N0} px, {GraphSaveHelpers.ExportDpi} dpi)", StatusSeverity.Success);
                    Toast?.Show("PNG を保存しました", StatusSeverity.Success);
                }
                HideError();
            }
            finally
            {
                ApplyExportStyleScale(1f);
                _plot.Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"グラフの保存に失敗しました: {ex.Message}");
        }
    }

    private async Task ExportAnalysisAsync()
    {
        if (_selectedDatasets.Count == 0)
        {
            ShowError("出力可能なデータがありません。");
            return;
        }
        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = $"{Path.GetFileNameWithoutExtension(GetCurrentWorkbookHint())}_dls.xlsx";
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "解析結果を保存",
            SuggestedFileName = string.IsNullOrWhiteSpace(defaultName) ? "dls_export.xlsx" : defaultName,
            DefaultExtension = "xlsx",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Excel ファイル") { Patterns = new[] { "*.xlsx" } },
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

            var format = GetExportFormat(path);
            var fileName = EnsureExportExtension(path, format);
            IAnalysisExporter exporter = format == ExportFormat.Csv
                ? new DlsCsvAnalysisExporter()
                : new DlsXlsxAnalysisExporter();
            exporter.Export(data, fileName);
            HideError();
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
        var entries = new List<DlsAnalysisExportEntry>();
        var modeName = _selectedMode.ToString();
        var xLabel = DefaultLabels.GetDefaultXLabel(_selectedMode);
        var yLabel = DefaultLabels.GetModeLabel(_selectedMode);

        foreach (var dataset in _selectedDatasets)
        {
            var datasetIdx = _datasets.IndexOf(dataset);
            var item = (datasetIdx >= 0 && datasetIdx < _datasetItems.Count)
                ? _datasetItems[datasetIdx]
                : null;

            var series = GetSeries(dataset, _selectedMode);
            var (xs, ys) = ResolveSeriesPoints(series);

            CumulantResult? cumulant = null;
            double? hydrodynamicDiameterNm = null;
            if (item is not null)
            {
                var outcome = CumulantAnalyzer.Analyze(
                    dataset.Correlation,
                    item.Cumulant.FitRangeMinMicroseconds,
                    item.Cumulant.FitRangeMaxMicroseconds);
                if (outcome.Success && outcome.Result is not null)
                {
                    cumulant = outcome.Result;
                    var size = StokesEinstein.Compute(
                        cumulant.FirstCumulantPerMicrosecond,
                        item.Metadata.TemperatureCelsius,
                        item.Metadata.ViscosityMpas,
                        item.Metadata.RefractiveIndex,
                        item.Metadata.WavelengthNm,
                        item.Metadata.ScatteringAngleDegrees);
                    if (size.Success) hydrodynamicDiameterNm = size.HydrodynamicDiameterNm;
                }
            }

            entries.Add(new DlsAnalysisExportEntry
            {
                DisplayName = dataset.SheetName,
                DistributionMode = modeName,
                XLabel = xLabel,
                YLabel = yLabel,
                Xs = xs,
                Ys = ys,
                Cumulant = cumulant,
                HydrodynamicDiameterNm = hydrodynamicDiameterNm,
                TemperatureCelsius = item?.Metadata.TemperatureCelsius,
                Solvent = item?.Metadata.Solvent,
                ConcentrationMgPerMl = item?.Metadata.ConcentrationMgPerMl,
                RefractiveIndex = item?.Metadata.RefractiveIndex,
                ViscosityMpas = item?.Metadata.ViscosityMpas,
                WavelengthNm = item?.Metadata.WavelengthNm,
                ScatteringAngleDegrees = item?.Metadata.ScatteringAngleDegrees,
            });
        }

        return new AnalysisExport
        {
            Entries = entries,
            GeneratorName = "LabPlot DLS",
        };
    }

    private static (IReadOnlyList<double> Xs, IReadOnlyList<double> Ys) ResolveSeriesPoints(DataSeries? series)
    {
        if (series is null || series.RunCount == 0)
            return (Array.Empty<double>(), Array.Empty<double>());
        var run = series.Runs[Math.Clamp(series.ActiveRunIndex, 0, series.RunCount - 1)];
        var n = Math.Min(run.Count, series.Xs.Count);
        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = series.Xs[i];
            ys[i] = run[i];
        }
        return (xs, ys);
    }

    private enum ExportFormat { Xlsx, Csv }

    private static ExportFormat GetExportFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase)) return ExportFormat.Csv;
        return ExportFormat.Xlsx;
    }

    private static string EnsureExportExtension(string filePath, ExportFormat format)
    {
        var expected = format == ExportFormat.Csv ? ".csv" : ".xlsx";
        if (string.Equals(Path.GetExtension(filePath), expected, StringComparison.OrdinalIgnoreCase))
            return filePath;
        return Path.ChangeExtension(filePath, expected);
    }

    private void UpdateExportButtonState()
    {
        var hasData = _selectedDatasets.Count > 0;
        ExportButton.IsEnabled = hasData;
        SaveGraphButton.IsEnabled = hasData;
        SaveSessionButton.IsEnabled = _datasetItems.Count > 0
            && !string.IsNullOrEmpty(_currentWorkbookPath);
    }

    // ---------- Session save / load ----------

    private void SaveSessionButton_Click(object? sender, RoutedEventArgs e) => _ = SaveSessionAsync();
    private void LoadSessionButton_Click(object? sender, RoutedEventArgs e) => _ = LoadSessionAsync();

    private async Task SaveSessionAsync()
    {
        if (_datasetItems.Count == 0 || string.IsNullOrEmpty(_currentWorkbookPath))
        {
            ShowError("保存する状態がありません。");
            return;
        }
        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = $"{Path.GetFileNameWithoutExtension(_currentWorkbookPath)}_session.dlsjson";
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "解析条件を保存",
            SuggestedFileName = defaultName,
            DefaultExtension = "dlsjson",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("DLS 解析条件") { Patterns = new[] { "*.dlsjson" } },
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var session = BuildSession();
            new AnalysisSessionStore<DlsAnalysisSession>().Save(session, path);
            HideError();
            SetStatus($"解析条件を保存しました: {path}", StatusSeverity.Success);
            Toast?.Show("解析条件を保存しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
        }
    }

    private async Task LoadSessionAsync()
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "解析条件を読み込み",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DLS 解析条件") { Patterns = new[] { "*.dlsjson", "*.json" } },
                FilePickerFileTypes.All,
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        DlsAnalysisSession session;
        try
        {
            session = new AnalysisSessionStore<DlsAnalysisSession>().Load(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or FileNotFoundException)
        {
            ShowError($"読み込みに失敗しました: {ex.Message}");
            return;
        }

        var warnings = new List<string>();
        await ApplySessionAsync(session, warnings);

        if (warnings.Count > 0)
            ShowError($"一部復元できない項目あり: {string.Join(" / ", warnings)}");
        else
            HideError();
    }

    private DlsAnalysisSession BuildSession()
    {
        // per-dataset の Style / Metadata / Cumulant 転写は DlsSessionMapper に集約。
        // 新フィールドを追加するときは Mapper だけ更新すれば save / load 両経路に反映される。
        var sourceFilePath = _currentWorkbookPath ?? string.Empty;
        var sessionDatasets = new List<DlsAnalysisSessionDataset>(_datasetItems.Count);
        for (int i = 0; i < _datasetItems.Count; i++)
        {
            var item = _datasetItems[i];
            sessionDatasets.Add(DlsSessionMapper.ToSessionDataset(
                item, sourceFilePath, selected: _selectedDatasets.Contains(item.Dataset)));
        }

        return new DlsAnalysisSession
        {
            WorkbookPath = _currentWorkbookPath ?? string.Empty,
            Datasets = sessionDatasets,
            Axes = new AnalysisSessionAxes
            {
                XMin = AxisRangePanel.XMinValue,
                XMax = AxisRangePanel.XMaxValue,
                YMin = AxisRangePanel.YMinValue,
                YMax = AxisRangePanel.YMaxValue,
            },
            Formatting = BuildSessionFormatting(),
            Labels = new AnalysisSessionLabels
            {
                Title = TitleTextBox.Text ?? string.Empty,
                XLabel = XLabelTextBox.Text ?? string.Empty,
                YLabel = YLabelTextBox.Text ?? string.Empty,
            },
            SelectedDistributionMode = _selectedMode.ToString(),
            SelectedRunIndex = _selectedRunIndex,
            ActiveDatasetIndex = _activeItemIndex,
            Overlay = _selectedDatasets.Count > 1,
        };
    }

    // 戻り値 Task は LoadSessionAsync に async/await 経路を提供するためで、
    // 中身は同期処理（reader.Read は I/O だが xlsx 1 ファイル分なので
    // ImportWorkbookAsync 同様に Task.Run でオフロードしてもいいが、
    // 既存セッションの再読み込みは BusyOverlay 無しで十分速い）。
    private Task ApplySessionAsync(DlsAnalysisSession session, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(session.WorkbookPath))
        {
            warnings.Add("xlsx ファイルパスが空です");
            return Task.CompletedTask;
        }
        if (!File.Exists(session.WorkbookPath))
        {
            warnings.Add($"xlsx ファイルが見つかりません ({session.WorkbookPath})");
            return Task.CompletedTask;
        }

        try
        {
            var loaded = _reader.Read(session.WorkbookPath);
            _datasets.Clear();
            foreach (var ds in loaded) _datasets.Add(ds);
            _datasetItems.Clear();
            foreach (var ds in _datasets) _datasetItems.Add(new DlsDatasetItem(ds));

            _currentWorkbookPath = session.WorkbookPath;
            DatasetListBox.ItemsSource = null;
            DatasetListBox.ItemsSource = _datasetItems;
            UpdateDatasetListPlaceholder();
            DatasetCountText.Text =
                $"{_datasets.Count} シート読み込み済み（{Path.GetFileName(session.WorkbookPath)}）";
        }
        catch (Exception ex)
        {
            warnings.Add($"xlsx 再読み込み失敗: {ex.Message}");
            return Task.CompletedTask;
        }

        foreach (var sessionDs in session.Datasets)
        {
            var item = _datasetItems.FirstOrDefault(it =>
                string.Equals(it.SheetName, sessionDs.SheetName, StringComparison.Ordinal));
            if (item is null)
            {
                warnings.Add($"シート '{sessionDs.SheetName}' が見つかりません");
                continue;
            }

            // per-dataset の Style / Metadata / Cumulant 反映は DlsSessionMapper に集約 (Save と双方向対)。
            DlsSessionMapper.ApplyToItem(sessionDs, item);
        }

        NormalizeSharedMetadataAcrossItems();

        _selectedMode = DistributionModeFromTag(session.SelectedDistributionMode);
        SelectComboBoxByTag(DistributionTypeComboBox, session.SelectedDistributionMode);
        _selectedRunIndex = Math.Max(0, session.SelectedRunIndex);

        if (session.Formatting is not null)
        {
            session.Formatting.Normalize();
            session.Formatting.DefaultOutputDirectory = _formattingDefaults.DefaultOutputDirectory;
            _formattingConfig = session.Formatting;
            ApplyFormattingConfigToControls(_formattingConfig);
            UpdatePlotHostAspectRatio();
        }

        _suppressFormattingEvents = true;
        try
        {
            TitleTextBox.Text = session.Labels.Title ?? string.Empty;
            XLabelTextBox.Text = session.Labels.XLabel ?? string.Empty;
            YLabelTextBox.Text = session.Labels.YLabel ?? string.Empty;
            AxisRangePanel.SetXValues(session.Axes.XMin, session.Axes.XMax);
            AxisRangePanel.SetYValues(session.Axes.YMin, session.Axes.YMax);
        }
        finally
        {
            _suppressFormattingEvents = false;
        }

        DatasetListBox.SelectedItems?.Clear();
        foreach (var sessionDs in session.Datasets.Where(d => d.Selected))
        {
            var item = _datasetItems.FirstOrDefault(it =>
                string.Equals(it.SheetName, sessionDs.SheetName, StringComparison.Ordinal));
            if (item is not null) DatasetListBox.SelectedItems?.Add(item);
        }

        if (_selectedDatasets.Count == 0)
        {
            _activeItemIndex = -1;
            SyncStyleControlsFromActiveItem();
            UpdateExportButtonState();
            InitializeEmptyPlot();
        }

        RaiseAnalysisDataChanged();
        RaiseActiveItemChanged();

        return Task.CompletedTask;
    }

    private string GetCurrentWorkbookHint() => _currentWorkbookPath ?? "dls";

    // ---------- Dataset selection ----------

    private void DatasetListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _selectedDatasets.Clear();
        foreach (var item in _datasetItems)
        {
            if (DatasetListBox.SelectedItems?.Contains(item) == true)
                _selectedDatasets.Add(item.Dataset);
        }

        DlsDatasetItem? activeItem = null;
        foreach (var added in e.AddedItems)
        {
            if (added is DlsDatasetItem item) activeItem = item;
        }
        if (activeItem is null && DatasetListBox.SelectedItems is { Count: > 0 } sels)
        {
            activeItem = sels[sels.Count - 1] as DlsDatasetItem;
        }
        _activeItemIndex = activeItem is null ? -1 : _datasetItems.IndexOf(activeItem);
        SyncStyleControlsFromActiveItem();

        UpdateRunCombo();
        UpdateDistributionTypeAvailability();
        UpdateExportButtonState();
        RefreshPlot();
        RaiseActiveItemChanged();
    }

    private void SyncStyleControlsFromActiveItem()
    {
        bool hasActive = _activeItemIndex >= 0 && _activeItemIndex < _datasetItems.Count;
        LineColorPicker.IsEnabled = hasActive;
        LegendNameTextBox.IsEnabled = hasActive;
        LineWidthTextBox.IsEnabled = hasActive;
        MarkerSizeTextBox.IsEnabled = hasActive;

        if (!hasActive)
        {
            ActiveDatasetLabel.Text = "(選択中シート)";
            return;
        }

        var item = _datasetItems[_activeItemIndex];
        ActiveDatasetLabel.Text = $"({item.SheetName})";

        _suppressStyleControlEvents = true;
        try
        {
            LineColorPicker.DefaultHex = AutoLineColors[_activeItemIndex % AutoLineColors.Length];
            LineColorPicker.SetHexValue(item.Style.ColorHex);
            LegendNameTextBox.Text = item.Style.LegendName ?? string.Empty;
            LineWidthTextBox.Text = FormatDouble(item.Style.LineWidth);
            MarkerSizeTextBox.Text = FormatDouble(item.Style.MarkerSize);
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }
    }

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        _datasetItems[_activeItemIndex].Style.ColorHex = LineColorPicker.HexValue;
        RefreshPlot();
    }

    private void LegendNameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var legendName = (LegendNameTextBox.Text ?? string.Empty).Trim();
        _datasetItems[_activeItemIndex].Style.LegendName =
            string.IsNullOrWhiteSpace(legendName) ? null : legendName;
        RefreshPlot();
    }

    private void LineWidthTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        if (TryParsePositiveDouble(LineWidthTextBox.Text, out var width))
        {
            _datasetItems[_activeItemIndex].Style.LineWidth = width;
            RefreshPlot();
        }
    }

    private void MarkerSizeTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        if (TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var size))
        {
            _datasetItems[_activeItemIndex].Style.MarkerSize = size;
            RefreshPlot();
        }
    }

    // 測定条件 (Metadata) 編集 UI は AnalysisWindow Tab 1 へ移管済み。
    // SyncMetadataControlsFromActiveItem / Metadata*_TextChanged / *_LostFocus /
    // MetadataTextBox_KeyDown / CommitStringMetadata / CommitNumericMetadata /
    // ApplyMetadataValue / NumericConstraint enum / _suppressMetadataControlEvents は
    // AnalysisWindow.axaml.cs (Tab 1: Measurement metadata セクション) を参照。
    // 子側のメタデータ commit 経路は IDlsAnalysisHost.RequestAnalysisDataChanged()
    // (= RaiseAnalysisDataChanged) を経由して親に再描画ヒントを返す。

    // ファイル読み込み直後に共通フィールド (溶媒・屈折率・粘度・波長・散乱角) を全シートで揃える。
    // 「最初に non-null が見つかったシートの値」を伝播させ、なければ既定値を用いる。
    // .dlsjson 由来は元から全シート同値なので no-op、xlsx 直読みでバラバラだった場合のみ正規化される。
    private void NormalizeSharedMetadataAcrossItems()
    {
        if (_datasetItems.Count == 0) return;

        string? solvent = null;
        double? refractiveIndex = null;
        double? viscosity = null;
        double? wavelength = null;
        double? scatteringAngle = null;

        foreach (var it in _datasetItems)
        {
            solvent ??= it.Metadata.Solvent;
            refractiveIndex ??= it.Metadata.RefractiveIndex;
            viscosity ??= it.Metadata.ViscosityMpas;
            wavelength ??= it.Metadata.WavelengthNm;
            scatteringAngle ??= it.Metadata.ScatteringAngleDegrees;
        }

        wavelength ??= DlsDatasetMetadataState.DefaultWavelengthNm;
        scatteringAngle ??= DlsDatasetMetadataState.DefaultScatteringAngleDegrees;

        foreach (var it in _datasetItems)
        {
            it.Metadata.Solvent = solvent;
            it.Metadata.RefractiveIndex = refractiveIndex;
            it.Metadata.ViscosityMpas = viscosity;
            it.Metadata.WavelengthNm = wavelength;
            it.Metadata.ScatteringAngleDegrees = scatteringAngle;
        }
    }

    // ---------- Display / format ----------

    private void DistributionTypeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (DistributionTypeComboBox.SelectedItem is not ComboBoxItem item) return;
        _selectedMode = DistributionModeFromTag(item.Tag as string);
        UpdateRunCombo();
        RefreshPlot();
    }

    private void RunComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressRunComboEvents) return;
        _selectedRunIndex = Math.Max(0, RunComboBox.SelectedIndex);
        RefreshPlot();
    }

    private void FormatTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void FormatComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void FormatCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void GraphLabelTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        RefreshPlot();
    }

    private void AxisRangePanel_Committed(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void AxisRangePanel_CaptureCurrentRangeRequested(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        if (_plot is null || _selectedDatasets.Count == 0) return;

        // 軸範囲欄は粒径 (nm) / % 前提。相関関数・温度ランプ・濃度シリーズでは
        // X 軸の単位が異なり手動範囲の適用対象外なので取り込まない。
        if (_selectedMode is DistributionMode.Correlation
            or DistributionMode.TemperatureRamp
            or DistributionMode.ConcentrationSeries)
        {
            Toast?.Show("軸範囲欄は粒径分布プロット専用のため、このモードでは取り込めません", StatusSeverity.Warning);
            return;
        }

        var limits = _plot.Plot.Axes.GetLimits();
        if (!double.IsFinite(limits.Left) || !double.IsFinite(limits.Right) || limits.Left >= limits.Right
            || !double.IsFinite(limits.Bottom) || !double.IsFinite(limits.Top) || limits.Bottom >= limits.Top)
        {
            return;
        }

        // 粒径分布のプロット X 座標は log10(nm) なので Pow10 で実寸へ戻す
        // (ApplyPlotAppearance の Manual 適用が Log10 する逆変換)。
        AxisRangePanel.SetXValues(Math.Pow(10, limits.Left), Math.Pow(10, limits.Right));
        AxisRangePanel.SetYValues(limits.Bottom, limits.Top);

        // 手動 commit と同じ経路で config へ反映して欄とプロットを揃える。
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void GraphFormatPanel_GraphFormatChanged(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void GraphFormatPanel_AspectRatioChanged(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        if (!_suppressFormattingEvents)
            _formattingConfig = CaptureFormattingConfigFromControls();
        UpdatePlotHostAspectRatio();
    }

    private void PlotContainerBorder_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdatePlotHostAspectRatio();

    private void UpdatePlotHostAspectRatio()
        => PlotHostAspectRatio.Apply(PlotHost, PlotContainerBorder, GraphFormatPanel.AspectRatioValue);

    private void UpdateDatasetListPlaceholder()
        => DatasetListPlaceholder.IsVisible = _datasetItems.Count == 0;

    private void ClearActiveDatasets()
    {
        _selectedDatasets.Clear();
        _activeItemIndex = -1;
        SyncStyleControlsFromActiveItem();
        UpdateExportButtonState();
        InitializeEmptyPlot();
        RaiseActiveItemChanged();
    }

    private void UpdateDistributionTypeAvailability()
    {
        for (int i = 0; i < DistributionTypeComboBox.ItemCount; i++)
        {
            if (DistributionTypeComboBox.Items[i] is not ComboBoxItem item) continue;
            var mode = DistributionModeFromTag(item.Tag as string);
            item.IsEnabled = _selectedDatasets.Count == 0
                || _selectedDatasets.Any(ds => GetSeries(ds, mode) is not null);
        }
    }

    private void UpdateRunCombo()
    {
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

            var series = GetSeries(_selectedDatasets[0], _selectedMode);
            if (series is null || series.RunCount == 0)
            {
                RunComboBox.IsEnabled = false;
                _selectedRunIndex = 0;
                return;
            }

            for (int i = 0; i < series.RunCount; i++)
                RunComboBox.Items.Add(new ComboBoxItem { Content = $"Run {i + 1}" });

            RunComboBox.IsEnabled = series.RunCount > 1;
            _selectedRunIndex = Math.Clamp(_selectedRunIndex, 0, series.RunCount - 1);
            RunComboBox.SelectedIndex = _selectedRunIndex;
        }
        finally { _suppressRunComboEvents = false; }
    }

    // ---------- Plot ----------

    private void OnLegendDragCommit(string position, double offsetX, double offsetY)
    {
        // The drag controller already wrote Alignment + Margin during the
        // move, so by the time we arrive here the user sees the legend at
        // the final position. Persist the anchor + offsets into the live
        // formatting config and let the panel controls catch up, then run
        // RefreshPlot so any subsequent ApplyAll keeps the same placement
        // via ComputeLegendMargin.
        _formattingConfig.LegendPosition = position;
        _formattingConfig.LegendOffsetX = offsetX;
        _formattingConfig.LegendOffsetY = offsetY;
        GraphFormatPanel.SyncLegendPlacement(position, offsetX, offsetY);
        RefreshPlot();
    }

    private void RefreshPlot()
    {
        if (_plot is null) return;

        if (_selectedMode == DistributionMode.TemperatureRamp)
        {
            RefreshTemperatureRampPlot();
            return;
        }

        if (_selectedMode == DistributionMode.ConcentrationSeries)
        {
            RefreshConcentrationSeriesPlot();
            return;
        }

        if (_selectedMode == DistributionMode.SizeDistributionInversion)
        {
            RefreshSizeDistributionInversionPlot();
            return;
        }

        if (_selectedDatasets.Count == 0)
        {
            InitializeEmptyPlot();
            return;
        }

        // データを描画するので placeholder を非表示にする。
        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);

        ClearPlottablePool();

        var seriesCount = 0;
        foreach (var dataset in _selectedDatasets)
        {
            var series = GetSeries(dataset, _selectedMode);
            if (series is null || series.RunCount == 0) continue;

            var runIndex = _selectedDatasets.Count == 1
                ? Math.Clamp(_selectedRunIndex, 0, series.RunCount - 1)
                : Math.Clamp(series.ActiveRunIndex, 0, series.RunCount - 1);
            var run = series.Runs[runIndex];
            var rawXs = series.Xs;
            var n = Math.Min(run.Count, rawXs.Count);
            if (n == 0) continue;

            var xs = new double[n];
            var ys = new double[n];
            for (int p = 0; p < n; p++)
            {
                xs[p] = Math.Log10(Math.Max(rawXs[p], 1e-6));
                ys[p] = run[p];
            }

            var datasetIdx = _datasets.IndexOf(dataset);
            var style = (datasetIdx >= 0 && datasetIdx < _datasetItems.Count)
                ? _datasetItems[datasetIdx].Style
                : null;

            var scatter = _plot.Plot.Add.Scatter(xs, ys);
            _plottablePool.Add(scatter);
            scatter.LineWidth = (float)(style?.LineWidth ?? _formattingConfig.LineWidth);
            scatter.MarkerSize = (float)(style?.MarkerSize ?? _formattingConfig.MarkerSize);
            ApplyDatasetColor(scatter, dataset);
            var customLegendName = style?.LegendName;
            scatter.LegendText = string.IsNullOrWhiteSpace(customLegendName)
                ? dataset.SheetName
                : customLegendName!;
            seriesCount++;
        }

        for (int i = 0; i < _datasetItems.Count; i++)
            _datasetItems[i].ColorBrush = ResolveDatasetBrush(i);

        if (seriesCount == 0)
        {
            _plot.Plot.Title(GetGraphTitle($"{DefaultLabels.GetModeLabel(_selectedMode)}{DefaultLabels.NoDataSuffix}"));
            _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultLabels.GetDefaultXLabel(_selectedMode)));
            _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, DefaultLabels.GetModeLabel(_selectedMode)));
            ApplyLogXTicksForMode(_selectedMode);
            if (_selectedMode == DistributionMode.Correlation)
                _plot.Plot.Axes.SetLimits(Math.Log10(0.5), Math.Log10(10000), 0, 1.05);
            else
                _plot.Plot.Axes.SetLimits(Math.Log10(0.3), Math.Log10(10000), 0, 30);
            ApplyPlotAppearance();
            ApplyLegend(0);
            _plot.Refresh();
            return;
        }

        _plot.Plot.Title(GetGraphTitle(BuildTitle()));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultLabels.GetDefaultXLabel(_selectedMode)));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, DefaultLabels.GetModeLabel(_selectedMode)));
        ApplyLogXTicksForMode(_selectedMode);
        _plot.Plot.Axes.AutoScale();
        ApplyPlotAppearance();
        ApplyLegend(seriesCount);
        _plot.Refresh();
    }

    private string BuildTitle()
    {
        if (_selectedMode == DistributionMode.TemperatureRamp
            || _selectedMode == DistributionMode.ConcentrationSeries)
            return DefaultLabels.GetPlotTypeLabel(_selectedMode);

        if (_selectedDatasets.Count == 1)
        {
            var dataset = _selectedDatasets[0];
            var series = GetSeries(dataset, _selectedMode);
            var runLabel = series is { RunCount: > 1 }
                ? $", Run {Math.Clamp(_selectedRunIndex, 0, series.RunCount - 1) + 1}"
                : string.Empty;
            return $"{dataset.SheetName} ({DefaultLabels.GetModeLabel(_selectedMode)}{runLabel})";
        }

        return $"{DefaultLabels.GetPlotTypeLabel(_selectedMode)} ({DefaultLabels.GetModeLabel(_selectedMode)}, {_selectedDatasets.Count} datasets)";
    }

    // ---------- Temperature ramp (Boltzmann fit across loaded sheets) ----------

    private void RefreshTemperatureRampPlot()
    {
        if (_plot is null) return;
        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);

        ClearPlottablePool();

        // Color refresh keeps the dataset list dots (sidebar) consistent
        // with the rest of the app even though the ramp plot itself does
        // not draw per-dataset series.
        for (int i = 0; i < _datasetItems.Count; i++)
            _datasetItems[i].ColorBrush = ResolveDatasetBrush(i);

        var (points, eligibleCount, missingTemp, missingFit) = BuildTemperatureRampPoints();
        var outcome = TemperatureRampAnalyzer.Analyze(points);
        // Result text is rendered by AnalysisWindow via AnalysisDataChanged;
        // here we only build the plot. eligibleCount / missingTemp / missingFit
        // are intentionally unused on the parent side now.
        _ = (eligibleCount, missingTemp, missingFit);

        _plot.Plot.Title(GetGraphTitle(BuildTitle()));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultLabels.GetDefaultXLabel(_selectedMode)));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, DefaultLabels.GetModeLabel(_selectedMode)));
        ApplyLogXTicksForMode(_selectedMode);
        // Restore default Y tick generator since correlation / size axes
        // may have left a NumericManual one in place.
        _plot.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();

        if (points.Count == 0)
        {
            _plot.Plot.Axes.SetLimits(20, 40, 0, 200);
            ApplyPlotAppearance();
            ApplyLegend(0);
            _plot.Refresh();
            return;
        }

        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].TemperatureCelsius;
            ys[i] = points[i].DiameterNm;
        }
        var scatter = _plot.Plot.Add.ScatterPoints(xs, ys);
        _plottablePool.Add(scatter);
        scatter.MarkerSize = (float)(_formattingConfig.MarkerSize * 1.4);
        scatter.LegendText = "data";
        if (!string.IsNullOrWhiteSpace(_formattingConfig.DefaultLineColorHex))
            scatter.MarkerStyle.FillColor = ScottPlot.Color.FromHex(new[] { _formattingConfig.DefaultLineColorHex }).First();

        if (outcome.Success && outcome.Result is not null)
        {
            var tMin = points.Min(p => p.TemperatureCelsius);
            var tMax = points.Max(p => p.TemperatureCelsius);
            var span = Math.Max(tMax - tMin, 1.0);
            var t0 = tMin - span * 0.1;
            var t1 = tMax + span * 0.1;
            const int FitSampleCount = 200;
            var fitX = new double[FitSampleCount];
            var fitY = new double[FitSampleCount];
            for (int i = 0; i < FitSampleCount; i++)
            {
                fitX[i] = t0 + (t1 - t0) * i / (FitSampleCount - 1);
                fitY[i] = TemperatureRampAnalyzer.Predict(fitX[i], outcome.Result);
            }
            var line = _plot.Plot.Add.ScatterLine(fitX, fitY);
            _plottablePool.Add(line);
            line.LineWidth = (float)Math.Max(_formattingConfig.LineWidth, 2.0);
            line.LegendText = "Boltzmann fit";
        }

        _plot.Plot.Axes.AutoScale();
        ApplyPlotAppearance();
        ApplyLegend(outcome.Success ? 2 : 1);
        _plot.Refresh();
    }

    private (List<TemperatureRampPoint> Points, int EligibleCount, int MissingTemp, int MissingFit) BuildTemperatureRampPoints()
    {
        var points = new List<TemperatureRampPoint>(_datasetItems.Count);
        int missingTemp = 0;
        int missingFit = 0;
        foreach (var item in _datasetItems)
        {
            var t = item.Metadata.TemperatureCelsius;
            if (t is null || !double.IsFinite(t.Value))
            {
                missingTemp++;
                continue;
            }

            var cumulant = CumulantAnalyzer.Analyze(
                item.Dataset.Correlation,
                item.Cumulant.FitRangeMinMicroseconds,
                item.Cumulant.FitRangeMaxMicroseconds);
            if (!cumulant.Success || cumulant.Result is null)
            {
                missingFit++;
                continue;
            }

            var size = StokesEinstein.Compute(
                cumulant.Result.FirstCumulantPerMicrosecond,
                item.Metadata.TemperatureCelsius,
                item.Metadata.ViscosityMpas,
                item.Metadata.RefractiveIndex,
                item.Metadata.WavelengthNm,
                item.Metadata.ScatteringAngleDegrees);
            if (!size.Success || size.HydrodynamicDiameterNm is null)
            {
                missingFit++;
                continue;
            }

            points.Add(new TemperatureRampPoint(t.Value, size.HydrodynamicDiameterNm.Value));
        }
        return (points, points.Count, missingTemp, missingFit);
    }

    // 温度ランプ結果テキストは AnalysisWindow へ移管済み (UpdateRampDisplay)。

    // ---------- Concentration series (D vs c linear fit across loaded sheets) ----------

    /// <summary>μm²/s display unit for the diffusion coefficient axis (D × 1e12).</summary>
    private const double DiffusionDisplayScale = 1e12;

    private void RefreshConcentrationSeriesPlot()
    {
        if (_plot is null) return;
        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);

        ClearPlottablePool();

        for (int i = 0; i < _datasetItems.Count; i++)
            _datasetItems[i].ColorBrush = ResolveDatasetBrush(i);

        var (points, refTemperatureCelsius, refViscosityMpas, multiTemperature, multiViscosity,
             eligibleCount, missingConc, missingFit) = BuildConcentrationSeriesPoints();

        ConcentrationSeriesOutcome outcome;
        if (points.Count == 0
            || double.IsNaN(refTemperatureCelsius) || double.IsNaN(refViscosityMpas))
        {
            outcome = ConcentrationSeriesOutcome.Fail("有効な (c, D) 点がありません");
        }
        else
        {
            outcome = ConcentrationSeriesAnalyzer.Analyze(
                points, refTemperatureCelsius, refViscosityMpas);
        }

        // Result text is rendered by AnalysisWindow via AnalysisDataChanged;
        // here we only build the plot.
        _ = (eligibleCount, missingConc, missingFit, multiTemperature, multiViscosity);

        _plot.Plot.Title(GetGraphTitle(BuildTitle()));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultLabels.GetDefaultXLabel(_selectedMode)));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, DefaultLabels.GetModeLabel(_selectedMode)));
        ApplyLogXTicksForMode(_selectedMode);
        _plot.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();

        if (points.Count == 0)
        {
            _plot.Plot.Axes.SetLimits(0, 10, 0, 100);
            ApplyPlotAppearance();
            ApplyLegend(0);
            _plot.Refresh();
            return;
        }

        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].ConcentrationMgPerMl;
            // Convert m²/s to μm²/s for the on-screen y axis. D is typically
            // 4-50 μm²/s for synthetic / biological samples and reads more
            // naturally than 4-50 e-12 m²/s.
            ys[i] = points[i].DiffusionCoefficientM2PerSecond * DiffusionDisplayScale;
        }
        var scatter = _plot.Plot.Add.ScatterPoints(xs, ys);
        _plottablePool.Add(scatter);
        scatter.MarkerSize = (float)(_formattingConfig.MarkerSize * 1.4);
        scatter.LegendText = "data";
        if (!string.IsNullOrWhiteSpace(_formattingConfig.DefaultLineColorHex))
            scatter.MarkerStyle.FillColor = ScottPlot.Color.FromHex(new[] { _formattingConfig.DefaultLineColorHex }).First();

        if (outcome.Success && outcome.Result is not null)
        {
            var cMin = points.Min(p => p.ConcentrationMgPerMl);
            var cMax = points.Max(p => p.ConcentrationMgPerMl);
            var span = Math.Max(cMax - cMin, 1.0);
            // Anchor the fit line slightly past 0 mg/mL so the intercept
            // (D₀) is visible on the plot, and a touch past c_max for symmetry.
            var c0 = Math.Max(0.0, cMin - span * 0.1);
            var c1 = cMax + span * 0.1;
            const int FitSampleCount = 50;
            var fitX = new double[FitSampleCount];
            var fitY = new double[FitSampleCount];
            for (int i = 0; i < FitSampleCount; i++)
            {
                fitX[i] = c0 + (c1 - c0) * i / (FitSampleCount - 1);
                fitY[i] = ConcentrationSeriesAnalyzer.Predict(fitX[i], outcome.Result) * DiffusionDisplayScale;
            }
            var line = _plot.Plot.Add.ScatterLine(fitX, fitY);
            _plottablePool.Add(line);
            line.LineWidth = (float)Math.Max(_formattingConfig.LineWidth, 2.0);
            line.LegendText = "linear fit";
        }

        _plot.Plot.Axes.AutoScale();
        ApplyPlotAppearance();
        ApplyLegend(outcome.Success ? 2 : 1);
        _plot.Refresh();
    }

    private (List<ConcentrationSeriesPoint> Points,
             double ReferenceTemperatureCelsius,
             double ReferenceViscosityMpas,
             bool MultipleTemperatures,
             bool MultipleViscosities,
             int EligibleCount,
             int MissingConcentration,
             int MissingFit) BuildConcentrationSeriesPoints()
    {
        var points = new List<ConcentrationSeriesPoint>(_datasetItems.Count);
        var temperatures = new List<double>(_datasetItems.Count);
        var viscosities = new List<double>(_datasetItems.Count);
        int missingConc = 0;
        int missingFit = 0;

        foreach (var item in _datasetItems)
        {
            var c = item.Metadata.ConcentrationMgPerMl;
            if (c is null || !double.IsFinite(c.Value) || c.Value < 0)
            {
                missingConc++;
                continue;
            }

            var cumulant = CumulantAnalyzer.Analyze(
                item.Dataset.Correlation,
                item.Cumulant.FitRangeMinMicroseconds,
                item.Cumulant.FitRangeMaxMicroseconds);
            if (!cumulant.Success || cumulant.Result is null)
            {
                missingFit++;
                continue;
            }

            var size = StokesEinstein.Compute(
                cumulant.Result.FirstCumulantPerMicrosecond,
                item.Metadata.TemperatureCelsius,
                item.Metadata.ViscosityMpas,
                item.Metadata.RefractiveIndex,
                item.Metadata.WavelengthNm,
                item.Metadata.ScatteringAngleDegrees);
            if (!size.Success || size.DiffusionCoefficientM2PerSecond is null)
            {
                missingFit++;
                continue;
            }

            points.Add(new ConcentrationSeriesPoint(c.Value, size.DiffusionCoefficientM2PerSecond.Value));
            if (item.Metadata.TemperatureCelsius is double t) temperatures.Add(t);
            if (item.Metadata.ViscosityMpas is double eta) viscosities.Add(eta);
        }

        // Median is robust to a single mistyped sheet; we also flag when
        // the underlying values are not unanimous so the user can decide
        // whether the spread is intentional (across runs at very different
        // temperatures) or a data-entry error.
        var refT = temperatures.Count > 0 ? Median(temperatures) : double.NaN;
        var refEta = viscosities.Count > 0 ? Median(viscosities) : double.NaN;
        var multiT = HasSignificantSpread(temperatures, relativeTolerance: 0.005);
        var multiEta = HasSignificantSpread(viscosities, relativeTolerance: 0.01);

        return (points, refT, refEta, multiT, multiEta, points.Count, missingConc, missingFit);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }

    private static bool HasSignificantSpread(List<double> values, double relativeTolerance)
    {
        if (values.Count < 2) return false;
        var min = values.Min();
        var max = values.Max();
        if (min <= 0) return max - min > relativeTolerance;
        return (max - min) / min > relativeTolerance;
    }

    // 濃度シリーズ結果テキストは AnalysisWindow へ移管済み (UpdateConcentrationDisplay)。

    // ---------- Size distribution inversion (CONTIN-style NNLS per sheet) ----------

    private void RefreshSizeDistributionInversionPlot()
    {
        if (_plot is null) return;

        if (_selectedDatasets.Count == 0)
        {
            InitializeEmptyPlot();
            _analysisWindow?.OnInversionComputed(
                null, null, 0, 0, new HashSet<string>(), _selectedDatasets);
            return;
        }

        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);
        ClearPlottablePool();

        for (int i = 0; i < _datasetItems.Count; i++)
            _datasetItems[i].ColorBrush = ResolveDatasetBrush(i);

        var inversionWeight = _analysisWindow?.InversionWeight ?? DistributionMode.Intensity;
        var weightLabel = inversionWeight switch
        {
            DistributionMode.Number => DefaultLabels.NumberYLabel,
            DistributionMode.Volume => DefaultLabels.VolumeYLabel,
            _ => DefaultLabels.IntensityYLabel,
        };

        // Single-line title that names the active sheet (or count) plus the
        // selected sub-weight so the saved PNG identifies the run.
        string title;
        if (_selectedDatasets.Count == 1)
            title = $"{_selectedDatasets[0].SheetName} (CONTIN-like, {weightLabel})";
        else
            title = $"{DefaultLabels.SizeDistributionInversionTitle} ({weightLabel}, {_selectedDatasets.Count} datasets)";

        _plot.Plot.Title(GetGraphTitle(title));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultLabels.SizeXLabel));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, weightLabel));
        ApplyLogXTicksForMode(_selectedMode);
        _plot.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();

        SizeDistributionInversionOutcome? activeOutcome = null;
        DlsDatasetItem? activeItem = null;
        int seriesCount = 0;
        int failedCount = 0;
        int missingMetaCount = 0;
        var failureReasons = new HashSet<string>();

        // Run inversion per selected sheet so overlay still works (each
        // sheet keeps its own optics / solvent metadata). This is the
        // expensive path: 16 NNLS calls per sheet, ~200 ms each on a
        // 60-bin grid; acceptable for the typical 1-3 selected sheets but
        // a future improvement is to cache per (sheet, alpha) and run on
        // a background thread.
        var options = _analysisWindow?.BuildInversionOptions() ?? new SizeDistributionInverterOptions();
        for (int datasetIdx = 0; datasetIdx < _selectedDatasets.Count; datasetIdx++)
        {
            var dataset = _selectedDatasets[datasetIdx];
            var item = FindItemForDataset(dataset);
            if (item is null) continue;

            var outcome = SizeDistributionInverter.Invert(
                dataset.Correlation,
                item.Metadata.TemperatureCelsius,
                item.Metadata.ViscosityMpas,
                item.Metadata.RefractiveIndex,
                item.Metadata.WavelengthNm,
                item.Metadata.ScatteringAngleDegrees,
                options);

            if (datasetIdx == 0)
            {
                activeOutcome = outcome;
                activeItem = item;
            }

            if (!outcome.Success || outcome.Result is null)
            {
                if (outcome.MissingFields.Count > 0) missingMetaCount++;
                else failedCount++;
                if (!string.IsNullOrEmpty(outcome.FailureReason))
                    failureReasons.Add(outcome.FailureReason!);
                continue;
            }

            var bins = outcome.Result.Bins;
            var xs = new double[bins.Count];
            var ys = new double[bins.Count];
            for (int i = 0; i < bins.Count; i++)
            {
                xs[i] = Math.Log10(Math.Max(bins[i].DiameterNm, 1e-6));
                ys[i] = inversionWeight switch
                {
                    DistributionMode.Number => bins[i].NumberWeight,
                    DistributionMode.Volume => bins[i].VolumeWeight,
                    _ => bins[i].IntensityWeight,
                };
            }

            var indexInDatasets = _datasets.IndexOf(dataset);
            var style = (indexInDatasets >= 0 && indexInDatasets < _datasetItems.Count)
                ? _datasetItems[indexInDatasets].Style
                : null;

            var scatter = _plot.Plot.Add.Scatter(xs, ys);
            _plottablePool.Add(scatter);
            scatter.LineWidth = (float)(style?.LineWidth ?? _formattingConfig.LineWidth);
            scatter.MarkerSize = (float)(style?.MarkerSize ?? _formattingConfig.MarkerSize);
            ApplyDatasetColor(scatter, dataset);
            var customLegendName = style?.LegendName;
            scatter.LegendText = string.IsNullOrWhiteSpace(customLegendName)
                ? dataset.SheetName
                : customLegendName!;
            seriesCount++;
        }

        _analysisWindow?.OnInversionComputed(
            activeOutcome, activeItem, failedCount, missingMetaCount, failureReasons, _selectedDatasets);

        if (seriesCount == 0)
        {
            _plot.Plot.Axes.SetLimits(Math.Log10(0.3), Math.Log10(10000), 0, 30);
            ApplyPlotAppearance();
            ApplyLegend(0);
            _plot.Refresh();
            return;
        }

        _plot.Plot.Axes.AutoScale();
        ApplyPlotAppearance();
        ApplyLegend(seriesCount);
        _plot.Refresh();
    }

    private DlsDatasetItem? FindItemForDataset(DlsDataset dataset)
    {
        var idx = _datasets.IndexOf(dataset);
        return idx >= 0 && idx < _datasetItems.Count ? _datasetItems[idx] : null;
    }

    // CONTIN 結果テキスト (UpdateInversionDisplay) と入力 UI handler (InversionWeightComboBox_*
    // / InversionAlphaAutoCheckBox_* / InversionAlphaTextBox_*) と BuildInversionOptions
    // は AnalysisWindow へ移管済み。親は子から InversionWeight / BuildInversionOptions を
    // pull し、計算結果は OnInversionComputed で push する。

    private void ApplyDatasetColor(ScottPlot.Plottables.Scatter scatter, DlsDataset dataset)
    {
        var index = _datasets.IndexOf(dataset);
        if (index < 0) index = 0;

        if (index < _datasetItems.Count
            && !string.IsNullOrWhiteSpace(_datasetItems[index].Style.ColorHex))
        {
            scatter.Color = ScottPlot.Color.FromHex(new[] { _datasetItems[index].Style.ColorHex! }).First();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_formattingConfig.DefaultLineColorHex))
        {
            scatter.Color = ScottPlot.Color.FromHex(new[] { _formattingConfig.DefaultLineColorHex }).First();
            return;
        }

        var hex = AutoLineColors[index % AutoLineColors.Length];
        scatter.Color = ScottPlot.Color.FromHex(new[] { hex }).First();
    }

    private SolidColorBrush ResolveDatasetBrush(int datasetIndex)
    {
        string hex;
        if (datasetIndex >= 0 && datasetIndex < _datasetItems.Count
            && !string.IsNullOrWhiteSpace(_datasetItems[datasetIndex].Style.ColorHex))
        {
            hex = _datasetItems[datasetIndex].Style.ColorHex!;
        }
        else if (!string.IsNullOrWhiteSpace(_formattingConfig.DefaultLineColorHex))
        {
            hex = _formattingConfig.DefaultLineColorHex!;
        }
        else
        {
            var i = Math.Max(0, datasetIndex);
            hex = AutoLineColors[i % AutoLineColors.Length];
        }
        return new SolidColorBrush(HexToAvaloniaColor(hex));
    }

    private const int SizeAxisMinExponent = -1;
    private const int SizeAxisMaxExponent = 4;
    private const int CorrelationAxisMinExponent = -1;
    private const int CorrelationAxisMaxExponent = 7;

    private void ApplyLogXTicks(int minExponent, int maxExponent)
    {
        if (_plot is null) return;

        var generator = new ScottPlot.TickGenerators.NumericManual();
        for (int exponent = minExponent; exponent <= maxExponent; exponent++)
        {
            var label = exponent switch
            {
                < 0 => Math.Pow(10, exponent).ToString("0.#", CultureInfo.InvariantCulture),
                _ => Math.Pow(10, exponent).ToString("0", CultureInfo.InvariantCulture),
            };
            generator.AddMajor(exponent, label);
            if (exponent < maxExponent)
            {
                for (int multiplier = 2; multiplier <= 9; multiplier++)
                    generator.AddMinor(exponent + Math.Log10(multiplier));
            }
        }
        _plot.Plot.Axes.Bottom.TickGenerator = generator;
    }

    private void ApplyLogXTicksForMode(DistributionMode mode)
    {
        if (mode == DistributionMode.Correlation)
            ApplyLogXTicks(CorrelationAxisMinExponent, CorrelationAxisMaxExponent);
        else if (mode == DistributionMode.TemperatureRamp
                 || mode == DistributionMode.ConcentrationSeries)
            ApplyLinearXTicks();
        else
            ApplyLogXTicks(SizeAxisMinExponent, SizeAxisMaxExponent);
    }

    private void ApplyLinearXTicks()
    {
        if (_plot is null) return;
        // Reset to ScottPlot's default automatic tick generator so the X
        // axis renders 25 / 27 / 29 ... in linear °C rather than the
        // log-spaced exponents used by size and correlation modes.
        _plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
    }

    private void ApplyExportStyleScale(float scale)
    {
        if (_plot is null) return;
        ApplyPlotAppearance(scale);
        ApplyExistingSeriesStyles(scale);
    }

    private void ApplyExistingSeriesStyles(float scale)
    {
        if (_plot is null) return;

        var scatters = _plot.Plot
            .GetPlottables()
            .OfType<ScottPlot.Plottables.Scatter>()
            .ToArray();

        var scatterIdx = 0;
        foreach (var dataset in _selectedDatasets)
        {
            if (scatterIdx >= scatters.Length) break;
            var series = GetSeries(dataset, _selectedMode);
            if (series is null || series.RunCount == 0) continue;

            var datasetIdx = _datasets.IndexOf(dataset);
            var style = (datasetIdx >= 0 && datasetIdx < _datasetItems.Count)
                ? _datasetItems[datasetIdx].Style
                : null;
            var baseLineWidth = (float)(style?.LineWidth ?? _formattingConfig.LineWidth);
            var baseMarkerSize = (float)(style?.MarkerSize ?? _formattingConfig.MarkerSize);
            scatters[scatterIdx].LineWidth = baseLineWidth * scale;
            scatters[scatterIdx].MarkerSize = baseMarkerSize * scale;
            scatterIdx++;
        }
    }

    private void ApplyPlotAppearance(float scale = 1f)
    {
        if (_plot is null) return;
        var plot = _plot.Plot;

        ApplyAll(plot, _formattingConfig, scale);

        if (_selectedMode == DistributionMode.Correlation) return;

        if (_formattingConfig.XAxisMode == "Manual")
        {
            var xMinLog = Math.Log10(Math.Max(_formattingConfig.XAxisMinNm, 1e-6));
            var xMaxLog = Math.Log10(Math.Max(_formattingConfig.XAxisMaxNm, 1e-6));
            plot.Axes.SetLimitsX(xMinLog, xMaxLog);
        }
        if (_formattingConfig.YAxisMode == "Manual")
            plot.Axes.SetLimitsY(_formattingConfig.YAxisMinPercent, _formattingConfig.YAxisMaxPercent);
    }

    private void ApplyLegend(int seriesCount)
    {
        if (_plot is null) return;

        bool hasCustomLegendName = false;
        if (seriesCount > 0)
        {
            foreach (var ds in _selectedDatasets)
            {
                var idx = _datasets.IndexOf(ds);
                if (idx >= 0 && idx < _datasetItems.Count
                    && !string.IsNullOrWhiteSpace(_datasetItems[idx].Style.LegendName))
                {
                    hasCustomLegendName = true;
                    break;
                }
            }
        }

        var autoShow = seriesCount >= 2 || (seriesCount > 0 && hasCustomLegendName);
        PlotAppearance.ApplyLegend(_plot.Plot, _formattingConfig, autoShow);
    }

    // ---------- Formatting config capture / apply ----------

    private GraphFormattingConfig CaptureFormattingConfigFromControls()
    {
        var config = new GraphFormattingConfig();
        GraphFormatPanel.Capture(config);

        config.ShowTitle = TitleVisibleCheckBox.IsChecked == true;
        config.TitleBold = TitleBoldCheckBox.IsChecked == true;
        config.AxisLabelBold = AxisLabelBoldCheckBox.IsChecked == true;

        config.DefaultLineColorHex = _formattingConfig.DefaultLineColorHex;
        config.LineWidth = _formattingConfig.LineWidth;
        config.MarkerSize = _formattingConfig.MarkerSize;

        var outputDir = (DefaultOutputDirectoryTextBox.Text ?? string.Empty).Trim();
        config.DefaultOutputDirectory = string.IsNullOrWhiteSpace(outputDir) ? null : outputDir;

        config.XAxisMode = (AxisRangePanel.XMinValue.HasValue && AxisRangePanel.XMaxValue.HasValue)
            ? "Manual" : "Auto";
        config.XAxisMinNm = AxisRangePanel.XMinValue ?? GraphFormattingConfig.DefaultXAxisMinNm;
        config.XAxisMaxNm = AxisRangePanel.XMaxValue ?? GraphFormattingConfig.DefaultXAxisMaxNm;
        config.YAxisMode = (AxisRangePanel.YMinValue.HasValue && AxisRangePanel.YMaxValue.HasValue)
            ? "Manual" : "Auto";
        config.YAxisMinPercent = AxisRangePanel.YMinValue ?? GraphFormattingConfig.DefaultYAxisMinPercent;
        config.YAxisMaxPercent = AxisRangePanel.YMaxValue ?? GraphFormattingConfig.DefaultYAxisMaxPercent;

        config.DefaultDistributionMode = GetComboBoxTag(DefaultDistributionComboBox)
            ?? GraphFormattingConfig.DefaultDistributionModeValue;
        config.DefaultRunIndex = TryParseInt(DefaultRunIndexTextBox.Text, out var idx) ? idx : 0;

        config.Normalize();
        return config;
    }

    private void LoadFormattingDefaults()
    {
        _formattingDefaults = FormattingDefaultsStore.Load<GraphFormattingConfig>(
            FormattingConfigPath,
            FormattingConfigJsonOptions,
            ShowError);
    }

    private void SaveFormattingDefaults()
    {
        FormattingDefaultsStore.Save(
            _formattingDefaults,
            FormattingConfigPath,
            FormattingConfigJsonOptions);
    }

    private GraphFormattingConfig BuildSessionFormatting()
    {
        var formatting = FormattingDefaultsStore.Clone(_formattingConfig, FormattingConfigJsonOptions);
        formatting.DefaultOutputDirectory = null;
        return formatting;
    }

    private async void BrowseDefaultOutputDirectoryButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        IStorageFolder? start = null;
        var current = (DefaultOutputDirectoryTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            try { start = await sp.TryGetFolderFromPathAsync(current); }
            catch { start = null; }
        }

        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "既定の出力フォルダを選択",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        DefaultOutputDirectoryTextBox.Text = path;
    }

    private async void ResetGraphSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        // v1.3 Batch I: 破壊的操作なので確認ダイアログを挟む。誤クリックで書式が
        // 全部飛ぶ事故を防ぐ。
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

        foreach (var item in _datasetItems)
            ApplyDefaultDatasetStyle(item);

        SyncStyleControlsFromActiveItem();
        _formattingConfig = CaptureFormattingConfigFromControls();
        UpdatePlotHostAspectRatio();
        RefreshPlot();

        // v1.3 Batch B: 瞬間 OK 系の Success フィードバックを Toast で軽く出す。
        Toast?.Show("既定値に戻しました", StatusSeverity.Success);
    }

    private void SaveDefaultFormattingButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _formattingDefaults = CaptureFormattingConfigFromControls();
            SaveFormattingDefaults();
            HideError();
            SetStatus($"書式の既定値を保存しました: {FormattingConfigPath}", StatusSeverity.Success);
            Toast?.Show("既定値を更新しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowError($"書式の既定値を保存できませんでした: {ex.Message}");
        }
    }

    private void ApplyDefaultDatasetStyle(DlsDatasetItem item)
    {
        item.Style.ColorHex = _formattingDefaults.DefaultLineColorHex;
        item.Style.LegendName = null;
        item.Style.LineWidth = _formattingDefaults.LineWidth;
        item.Style.MarkerSize = _formattingDefaults.MarkerSize;
    }

    private void ApplyFormattingConfigToControls(GraphFormattingConfig config)
    {
        config.Normalize();

        GraphFormatPanel.Apply(config);

        _suppressFormattingEvents = true;
        try
        {
            TitleVisibleCheckBox.IsChecked = config.ShowTitle;
            TitleBoldCheckBox.IsChecked = config.TitleBold;
            AxisLabelBoldCheckBox.IsChecked = config.AxisLabelBold;

            AxisRangePanel.SetXValues(
                config.XAxisMode == "Manual" ? config.XAxisMinNm : null,
                config.XAxisMode == "Manual" ? config.XAxisMaxNm : null);
            AxisRangePanel.SetYValues(
                config.YAxisMode == "Manual" ? config.YAxisMinPercent : null,
                config.YAxisMode == "Manual" ? config.YAxisMaxPercent : null);
            SelectComboBoxByTag(DefaultDistributionComboBox, config.DefaultDistributionMode);
            DefaultRunIndexTextBox.Text = config.DefaultRunIndex.ToString(CultureInfo.InvariantCulture);
            DefaultOutputDirectoryTextBox.Text = config.DefaultOutputDirectory ?? string.Empty;
        }
        finally { _suppressFormattingEvents = false; }
    }

    // ---------- Generic helpers ----------

    private static DistributionMode DistributionModeFromTag(string? tag) => tag switch
    {
        "Intensity" => DistributionMode.Intensity,
        "Volume" => DistributionMode.Volume,
        "Correlation" => DistributionMode.Correlation,
        "TemperatureRamp" => DistributionMode.TemperatureRamp,
        "ConcentrationSeries" => DistributionMode.ConcentrationSeries,
        "SizeDistributionInversion" => DistributionMode.SizeDistributionInversion,
        _ => DistributionMode.Number,
    };

    private static DataSeries? GetSeries(DlsDataset? dataset, DistributionMode mode)
    {
        if (dataset is null) return null;
        if (mode == DistributionMode.Correlation)
        {
            var corr = dataset.Correlation;
            return corr is null
                ? null
                : new DataSeries(corr.TimesMicroseconds, corr.Runs, corr.ActiveRunIndex);
        }
        var dist = mode switch
        {
            DistributionMode.Intensity => dataset.IntensityDistribution,
            DistributionMode.Volume => dataset.VolumeDistribution,
            _ => dataset.NumberDistribution,
        };
        return dist is null
            ? null
            : new DataSeries(dist.SizeBinsNm, dist.Runs, dist.ActiveRunIndex);
    }

    // 旧ネスト型 (DlsDatasetStyle / DlsDatasetMetadataState / DlsDatasetCumulantSettings /
    // DlsDatasetItem) は AnalysisWindow からも触る必要が出たため
    // src/LabPlot.DLS.Avalonia/DlsDatasetItem.cs に Top-level として切り出した。

    // ---------- IDlsAnalysisHost (consumed by AnalysisWindow) ----------

    public IReadOnlyList<DlsDatasetItem> DatasetItems => _datasetItems;
    public IReadOnlyList<DlsDataset> SelectedDatasets => _selectedDatasets;
    public int ActiveItemIndex => _activeItemIndex;
    public DistributionMode SelectedMode => _selectedMode;

    public event EventHandler? AnalysisDataChanged;
    public event EventHandler? ActiveItemChanged;

    private void RaiseAnalysisDataChanged() => AnalysisDataChanged?.Invoke(this, EventArgs.Empty);
    private void RaiseActiveItemChanged() => ActiveItemChanged?.Invoke(this, EventArgs.Empty);

    public void RequestPlotRefresh() => RefreshPlot();

    public void RequestAnalysisDataChanged() => RaiseAnalysisDataChanged();

    public void RequestShowAsGraph(DistributionMode mode)
    {
        _selectedMode = mode;
        SelectComboBoxByTag(DistributionTypeComboBox, mode.ToString());
        UpdateRunCombo();
        RefreshPlot();
    }

    private void OpenAnalysisWindowButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_analysisWindow is null)
        {
            _analysisWindow = new AnalysisWindow(this);
            _analysisWindow.Closed += (_, _) => _analysisWindow = null;
        }
        if (!_analysisWindow.IsVisible)
        {
            // macOS で Show(owner) は addChildWindow: で親に attach されるため、
            // 子 Window が独立して最小化できなくなる。Windows / Linux の挙動は維持しつつ
            // macOS のみ Owner なしで開く。MainWindow を閉じた時の連動 close は
            // OnClosing 側で明示的に行っているので Owner を外しても破綻しない。
            if (OperatingSystem.IsMacOS())
                _analysisWindow.Show();
            else
                _analysisWindow.Show(this);
        }
        _analysisWindow.Activate();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 直近セッションのウィンドウサイズ・位置を復元 (画面外フォールバックは Store 側で処理)。
        WindowStateStore.ApplyTo(this, RecentFilesAppKey);
        // macOS では "Ctrl+O" のような tooltip 表記を "Cmd+O" に置換 (Windows / Linux は noop)。
        KeyboardShortcuts.LocalizeTooltipsForMac(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // ウィンドウを閉じる直前に Maximized / Normal サイズと位置を保存。
        WindowStateStore.PersistFrom(this, RecentFilesAppKey);
        _analysisWindow?.Close();
        _missingFileWatcher?.Dispose();
        base.OnClosing(e);
    }

    private string GetGraphTitle(string defaultTitle)
    {
        var title = (TitleTextBox.Text ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(title) ? defaultTitle : title;
    }

    private static string GetGraphLabel(TextBox textBox, string defaultLabel)
    {
        var label = (textBox.Text ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(label) ? defaultLabel : label;
    }

    private void ShowError(string message)
    {
        ErrorBanner.Show(message);
        SetStatus(message, isError: true);
    }

    private void HideError() => ErrorBanner.Hide();

    private void SetStatus(string message, bool isError = false)
    {
        StatusBar?.SetStatus(message, isError ? StatusSeverity.Error : StatusSeverity.Info);
        if (!isError) ErrorBanner.Hide();
    }

    // v1.3 Batch A: 4 段階 severity を明示したい呼び出し向け。Success (保存完了)
    // や Warning (外挿警告等) は新 API を直接使う。
    private void SetStatus(string message, StatusSeverity severity)
    {
        StatusBar?.SetStatus(message, severity);
        if (severity != StatusSeverity.Error) ErrorBanner.Hide();
    }

    private sealed record DataSeries(
        IReadOnlyList<double> Xs,
        IReadOnlyList<IReadOnlyList<double>> Runs,
        int ActiveRunIndex)
    {
        public int RunCount => Runs.Count;
    }

    // GPC PR #12 と同パターン: PlotCurrentDataset / Refresh*Plot の冒頭で
    // _plot.Plot.Clear() を呼んでいたところを、追加したものだけ Remove する
    // 形に切り替える。Title / axes / legend など Scatter 以外の Plot state を
    // 不必要にリセットしない。ScottPlot 5.1.58 では Scatter.Data setter が
    // 公開されていないので、ここでは「pool に貯めたものを Remove して再 Add」
    // までしかできず、本当の意味のリサイクルは将来の課題 (ROADMAP §2-DLS)。
    private void ClearPlottablePool()
    {
        if (_plot is null) return;
        var plot = _plot.Plot;
        foreach (var plottable in _plottablePool)
        {
            plot.Remove(plottable);
        }
        _plottablePool.Clear();
    }
}

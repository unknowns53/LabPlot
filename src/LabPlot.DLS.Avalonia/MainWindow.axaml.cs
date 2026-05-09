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
public partial class MainWindow : Window
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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
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
    private bool _suppressMetadataControlEvents;
    private string? _currentWorkbookPath;

    public MainWindow()
    {
        InitializeComponent();
        LoadFormattingDefaults();
        _formattingConfig = CloneFormattingConfig(_formattingDefaults);
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

            ApplyFormattingConfigToControls(_formattingConfig);
            SyncStyleControlsFromActiveItem();
            SyncMetadataControlsFromActiveItem();
            SyncCumulantControlsFromActiveItem();
            _selectedMode = DistributionModeFromTag(_formattingConfig.DefaultDistributionMode);
            SelectComboBoxByTag(DistributionTypeComboBox, _formattingConfig.DefaultDistributionMode);
            _selectedRunIndex = Math.Max(0, _formattingConfig.DefaultRunIndex);

            // 初期化成功時点でスケルトンを消す。placeholder TextBlock の文言は
            // InitializeEmptyPlot で SetState(EmptyReady) に切り替わる。
            PlotPlaceholderSkeleton.IsVisible = false;
            InitializeEmptyPlot();
            UpdatePlotHostAspectRatio();
        }
        catch (Exception ex)
        {
            PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.InitFailed);
            ShowError($"グラフ表示の初期化に失敗しました: {ex.Message}");
        }
    }

    // WPF の InputBindings / RoutedUICommand 配列を OnKeyDown 1 メソッドに集約。
    // 修飾キー判定は Avalonia の KeyModifiers flags で行う。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && shift)
        {
            switch (e.Key)
            {
                case Key.S: _ = SaveSessionAsync(); e.Handled = true; return;
                case Key.O: _ = LoadSessionAsync(); e.Handled = true; return;
            }
        }
        else if (ctrl)
        {
            switch (e.Key)
            {
                case Key.O: _ = OpenWorkbookAsync(); e.Handled = true; return;
                case Key.S: _ = SaveGraphAsync(); e.Handled = true; return;
                case Key.E: _ = ExportAnalysisAsync(); e.Handled = true; return;
                case Key.R: AxisRangePanel.ResetToAuto(); e.Handled = true; return;
                case Key.G: GraphFormatPanel.TogglePlotGrid(); e.Handled = true; return;
                case Key.L: ToggleAllDatasets(); e.Handled = true; return;
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

        _plot.Plot.Clear();
        _plot.Plot.Title(GetGraphTitle(DefaultLabels.GetPlotTypeLabel(_selectedMode)));
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
        var dir = FormattingDefaultsStore.GetExistingDefaultOutputDirectory(_formattingDefaults);
        if (string.IsNullOrEmpty(dir)) return null;
        try { return await sp.TryGetFolderFromPathAsync(dir); }
        catch { return null; }
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

            _currentWorkbookPath = filePath;
            DatasetListBox.ItemsSource = null;
            DatasetListBox.ItemsSource = _datasetItems;
            UpdateDatasetListPlaceholder();
            DatasetCountText.Text = _datasets.Count == 0
                ? "粒径分布シートが見つかりませんでした"
                : $"{_datasets.Count} シート読み込み済み（{Path.GetFileName(filePath)}）";

            HideError();
            SetStatus(_datasets.Count == 0
                ? $"粒径分布シートが見つかりませんでした: {filePath}"
                : $"{_datasets.Count} シートを読み込みました: {filePath}");

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
                    SetStatus($"グラフをSVGで保存しました: {fileName} ({width:N0} x {height:N0})");
                }
                else
                {
                    GraphSaveHelpers.SaveGraphPng(_plot.Plot, fileName, width, height, GraphSaveHelpers.ExportDpi);
                    SetStatus($"グラフをPNGで保存しました: {fileName} ({width:N0} x {height:N0} px, {GraphSaveHelpers.ExportDpi} dpi)");
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
            SetStatus($"解析結果を保存しました: {fileName}");
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
            SetStatus($"解析条件を保存しました: {path}");
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
        var sessionDatasets = new List<DlsAnalysisSessionDataset>();
        for (int i = 0; i < _datasetItems.Count; i++)
        {
            var item = _datasetItems[i];
            var ds = item.Dataset;
            sessionDatasets.Add(new DlsAnalysisSessionDataset
            {
                SheetName = ds.SheetName,
                SourceFilePath = _currentWorkbookPath ?? string.Empty,
                Selected = _selectedDatasets.Contains(ds),
                Style = new AnalysisSessionStyle
                {
                    ColorHex = item.Style.ColorHex,
                    LegendName = item.Style.LegendName,
                    LineWidth = item.Style.LineWidth,
                    MarkerSize = item.Style.MarkerSize,
                },
                Metadata = new DlsAnalysisSessionMetadata
                {
                    TemperatureCelsius = item.Metadata.TemperatureCelsius,
                    Solvent = item.Metadata.Solvent,
                    ConcentrationMgPerMl = item.Metadata.ConcentrationMgPerMl,
                    RefractiveIndex = item.Metadata.RefractiveIndex,
                    ViscosityMpas = item.Metadata.ViscosityMpas,
                    WavelengthNm = item.Metadata.WavelengthNm,
                    ScatteringAngleDegrees = item.Metadata.ScatteringAngleDegrees,
                },
                CumulantSettings = new DlsAnalysisSessionCumulantSettings
                {
                    FitRangeMinMicroseconds = item.Cumulant.FitRangeMinMicroseconds,
                    FitRangeMaxMicroseconds = item.Cumulant.FitRangeMaxMicroseconds,
                },
            });
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

            item.Style.ColorHex = sessionDs.Style.ColorHex;
            item.Style.LegendName = sessionDs.Style.LegendName;
            item.Style.LineWidth = sessionDs.Style.LineWidth;
            item.Style.MarkerSize = sessionDs.Style.MarkerSize;

            item.Metadata.TemperatureCelsius = sessionDs.Metadata.TemperatureCelsius;
            item.Metadata.Solvent = sessionDs.Metadata.Solvent;
            item.Metadata.ConcentrationMgPerMl = sessionDs.Metadata.ConcentrationMgPerMl;
            item.Metadata.RefractiveIndex = sessionDs.Metadata.RefractiveIndex;
            item.Metadata.ViscosityMpas = sessionDs.Metadata.ViscosityMpas;
            item.Metadata.WavelengthNm = sessionDs.Metadata.WavelengthNm;
            item.Metadata.ScatteringAngleDegrees = sessionDs.Metadata.ScatteringAngleDegrees;

            item.Cumulant.FitRangeMinMicroseconds = sessionDs.CumulantSettings.FitRangeMinMicroseconds;
            item.Cumulant.FitRangeMaxMicroseconds = sessionDs.CumulantSettings.FitRangeMaxMicroseconds;
        }

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
            SyncMetadataControlsFromActiveItem();
            SyncCumulantControlsFromActiveItem();
            UpdateExportButtonState();
            InitializeEmptyPlot();
        }

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
        SyncMetadataControlsFromActiveItem();
        SyncCumulantControlsFromActiveItem();

        UpdateRunCombo();
        UpdateDistributionTypeAvailability();
        UpdateExportButtonState();
        RefreshPlot();
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

    // ---------- Measurement metadata ----------

    private void SyncMetadataControlsFromActiveItem()
    {
        bool hasActive = _activeItemIndex >= 0 && _activeItemIndex < _datasetItems.Count;
        MetadataTemperatureTextBox.IsEnabled = hasActive;
        MetadataConcentrationTextBox.IsEnabled = hasActive;
        MetadataSolventTextBox.IsEnabled = hasActive;
        MetadataRefractiveIndexTextBox.IsEnabled = hasActive;
        MetadataViscosityTextBox.IsEnabled = hasActive;

        if (!hasActive)
        {
            ActiveMetadataLabel.Text = "(選択中シート)";
            _suppressMetadataControlEvents = true;
            try
            {
                MetadataTemperatureTextBox.Text = string.Empty;
                MetadataConcentrationTextBox.Text = string.Empty;
                MetadataSolventTextBox.Text = string.Empty;
                MetadataRefractiveIndexTextBox.Text = string.Empty;
                MetadataViscosityTextBox.Text = string.Empty;
                MetadataWavelengthTextBox.Text = string.Empty;
                MetadataScatteringAngleTextBox.Text = string.Empty;
            }
            finally { _suppressMetadataControlEvents = false; }
            return;
        }

        MetadataWavelengthTextBox.IsEnabled = true;
        MetadataScatteringAngleTextBox.IsEnabled = true;

        var metadata = _datasetItems[_activeItemIndex].Metadata;
        ActiveMetadataLabel.Text = $"({_datasetItems[_activeItemIndex].SheetName})";

        _suppressMetadataControlEvents = true;
        try
        {
            MetadataTemperatureTextBox.Text = FormatNullableDouble(metadata.TemperatureCelsius);
            MetadataConcentrationTextBox.Text = FormatNullableDouble(metadata.ConcentrationMgPerMl);
            MetadataSolventTextBox.Text = metadata.Solvent ?? string.Empty;
            MetadataRefractiveIndexTextBox.Text = FormatNullableDouble(metadata.RefractiveIndex);
            MetadataViscosityTextBox.Text = FormatNullableDouble(metadata.ViscosityMpas);
            MetadataWavelengthTextBox.Text = FormatNullableDouble(metadata.WavelengthNm);
            MetadataScatteringAngleTextBox.Text = FormatNullableDouble(metadata.ScatteringAngleDegrees);
        }
        finally { _suppressMetadataControlEvents = false; }
    }

    private void SyncCumulantControlsFromActiveItem()
    {
        bool hasActive = _activeItemIndex >= 0 && _activeItemIndex < _datasetItems.Count;
        CumulantFitMinTextBox.IsEnabled = hasActive;
        CumulantFitMaxTextBox.IsEnabled = hasActive;

        if (!hasActive)
        {
            ActiveCumulantLabel.Text = "(選択中シート)";
            _suppressMetadataControlEvents = true;
            try
            {
                CumulantFitMinTextBox.Text = string.Empty;
                CumulantFitMaxTextBox.Text = string.Empty;
            }
            finally { _suppressMetadataControlEvents = false; }
            UpdateCumulantDisplay();
            return;
        }

        var item = _datasetItems[_activeItemIndex];
        ActiveCumulantLabel.Text = $"({item.SheetName})";

        _suppressMetadataControlEvents = true;
        try
        {
            CumulantFitMinTextBox.Text = FormatNullableDouble(item.Cumulant.FitRangeMinMicroseconds);
            CumulantFitMaxTextBox.Text = FormatNullableDouble(item.Cumulant.FitRangeMaxMicroseconds);
        }
        finally { _suppressMetadataControlEvents = false; }

        UpdateCumulantDisplay();
    }

    private void UpdateCumulantDisplay()
    {
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count)
        {
            ResetCumulantDisplay();
            return;
        }

        var item = _datasetItems[_activeItemIndex];
        var correlation = item.Dataset.Correlation;

        if (correlation is null)
        {
            ResetCumulantDisplay();
            ShowCumulantStatus("自己相関データがありません");
            return;
        }

        var outcome = CumulantAnalyzer.Analyze(
            correlation,
            item.Cumulant.FitRangeMinMicroseconds,
            item.Cumulant.FitRangeMaxMicroseconds);

        if (!outcome.Success || outcome.Result is null)
        {
            ResetCumulantDisplay();
            ShowCumulantStatus(outcome.FailureReason ?? "fit に失敗しました");
            return;
        }

        var result = outcome.Result;
        CumulantGammaText.Text = $"{FormatScientific(result.FirstCumulantPerMicrosecond)} μs⁻¹";
        CumulantPdiText.Text = result.PolydispersityIndex.ToString("0.000",
            CultureInfo.InvariantCulture);
        CumulantRSquaredText.Text = result.RSquared.ToString("0.0000",
            CultureInfo.InvariantCulture);
        CumulantRangeText.Text =
            $"{FormatDouble(result.AppliedRangeMinMicroseconds)} 〜 "
            + $"{FormatDouble(result.AppliedRangeMaxMicroseconds)} μs"
            + $" ({result.PointCount} 点)";

        var sizeOutcome = StokesEinstein.Compute(
            result.FirstCumulantPerMicrosecond,
            item.Metadata.TemperatureCelsius,
            item.Metadata.ViscosityMpas,
            item.Metadata.RefractiveIndex,
            item.Metadata.WavelengthNm,
            item.Metadata.ScatteringAngleDegrees);

        if (sizeOutcome.Success && sizeOutcome.HydrodynamicDiameterNm.HasValue)
        {
            CumulantZAverageText.Text =
                $"{sizeOutcome.HydrodynamicDiameterNm.Value.ToString("0.0", CultureInfo.InvariantCulture)} nm";
            HideCumulantStatus();
        }
        else
        {
            CumulantZAverageText.Text = "—";
            var missing = string.Join("・", sizeOutcome.MissingFields);
            ShowCumulantStatus(string.IsNullOrEmpty(missing)
                ? "粒径計算に必要なメタデータが不足しています"
                : $"{missing} が未入力で粒径計算できません");
        }
    }

    private void ResetCumulantDisplay()
    {
        CumulantZAverageText.Text = "—";
        CumulantPdiText.Text = "—";
        CumulantGammaText.Text = "—";
        CumulantRangeText.Text = "—";
        CumulantRSquaredText.Text = "—";
        HideCumulantStatus();
    }

    private void ShowCumulantStatus(string message)
    {
        CumulantStatusText.Text = message;
        CumulantStatusText.IsVisible = true;
    }

    private void HideCumulantStatus()
    {
        CumulantStatusText.Text = string.Empty;
        CumulantStatusText.IsVisible = false;
    }

    private static string FormatScientific(double value)
    {
        if (!double.IsFinite(value)) return "—";
        return value.ToString("0.###e+0", CultureInfo.InvariantCulture);
    }

    // Enter で TextBox からフォーカスを外して LostFocus 経路に乗せる。
    // WPF の Keyboard.ClearFocus + FocusManager.SetFocusedElement は
    // Avalonia には等価が無いので、Window 自身に Focus() してフォーカスを
    // TextBox から剥がす方針。
    private void MetadataTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Focus();
        e.Handled = true;
    }

    private void MetadataTemperatureTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => CommitNumericMetadata(MetadataTemperatureTextBox, NumericConstraint.AnyFinite,
            (item, value) => item.Metadata.TemperatureCelsius = value);

    private void MetadataConcentrationTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => CommitNumericMetadata(MetadataConcentrationTextBox, NumericConstraint.NonNegative,
            (item, value) => item.Metadata.ConcentrationMgPerMl = value);

    private void MetadataRefractiveIndexTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => CommitNumericMetadata(MetadataRefractiveIndexTextBox, NumericConstraint.Positive,
            (item, value) => item.Metadata.RefractiveIndex = value);

    private void MetadataViscosityTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => CommitNumericMetadata(MetadataViscosityTextBox, NumericConstraint.Positive,
            (item, value) => item.Metadata.ViscosityMpas = value);

    private void MetadataWavelengthTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => CommitNumericMetadata(MetadataWavelengthTextBox, NumericConstraint.Positive,
            (item, value) => item.Metadata.WavelengthNm = value);

    private void MetadataScatteringAngleTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => CommitNumericMetadata(MetadataScatteringAngleTextBox, NumericConstraint.Positive,
            (item, value) => item.Metadata.ScatteringAngleDegrees = value);

    private void CumulantFitRangeTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressMetadataControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var item = _datasetItems[_activeItemIndex];
        bool reverted = false;
        if (!TryCommitCumulantBound(CumulantFitMinTextBox,
                value => item.Cumulant.FitRangeMinMicroseconds = value))
            reverted = true;
        if (!TryCommitCumulantBound(CumulantFitMaxTextBox,
                value => item.Cumulant.FitRangeMaxMicroseconds = value))
            reverted = true;

        if (reverted) SyncCumulantControlsFromActiveItem();
        else UpdateCumulantDisplay();
    }

    private bool TryCommitCumulantBound(TextBox textBox, Action<double?> apply)
    {
        var raw = (textBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            apply(null);
            return true;
        }
        if (!TryParsePositiveDouble(raw, out var value)) return false;

        apply(value);
        _suppressMetadataControlEvents = true;
        try { textBox.Text = FormatDouble(value); }
        finally { _suppressMetadataControlEvents = false; }
        return true;
    }

    private void MetadataSolventTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressMetadataControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var solvent = (MetadataSolventTextBox.Text ?? string.Empty).Trim();
        _datasetItems[_activeItemIndex].Metadata.Solvent =
            string.IsNullOrWhiteSpace(solvent) ? null : solvent;
        UpdateCumulantDisplay();
    }

    private enum NumericConstraint { AnyFinite, NonNegative, Positive }

    private void CommitNumericMetadata(
        TextBox textBox,
        NumericConstraint constraint,
        Action<DlsDatasetItem, double?> apply)
    {
        if (!IsInitialized) return;
        if (_suppressMetadataControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var item = _datasetItems[_activeItemIndex];
        var raw = (textBox.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            apply(item, null);
            _suppressMetadataControlEvents = true;
            try { textBox.Text = string.Empty; }
            finally { _suppressMetadataControlEvents = false; }
            return;
        }

        bool ok = constraint switch
        {
            NumericConstraint.Positive => TryParsePositiveDouble(raw, out _),
            NumericConstraint.NonNegative => TryParseNonNegativeDouble(raw, out _),
            _ => TryParseDouble(raw, out _),
        };

        if (!ok)
        {
            SyncMetadataControlsFromActiveItem();
            return;
        }

        TryParseDouble(raw, out var value);
        apply(item, value);

        _suppressMetadataControlEvents = true;
        try { textBox.Text = FormatDouble(value); }
        finally { _suppressMetadataControlEvents = false; }

        UpdateCumulantDisplay();
    }

    private static string FormatNullableDouble(double? value)
        => value.HasValue ? FormatDouble(value.Value) : string.Empty;

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
        SyncMetadataControlsFromActiveItem();
        SyncCumulantControlsFromActiveItem();
        UpdateExportButtonState();
        InitializeEmptyPlot();
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

        if (_selectedDatasets.Count == 0)
        {
            InitializeEmptyPlot();
            return;
        }

        // データを描画するので placeholder を非表示にする。
        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);

        _plot.Plot.Clear();

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

        _plot.Plot.Clear();

        // Color refresh keeps the dataset list dots (sidebar) consistent
        // with the rest of the app even though the ramp plot itself does
        // not draw per-dataset series.
        for (int i = 0; i < _datasetItems.Count; i++)
            _datasetItems[i].ColorBrush = ResolveDatasetBrush(i);

        var (points, eligibleCount, missingTemp, missingFit) = BuildTemperatureRampPoints();
        var outcome = TemperatureRampAnalyzer.Analyze(points);
        UpdateTemperatureRampDisplay(eligibleCount, missingTemp, missingFit, outcome);

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

    private void UpdateTemperatureRampDisplay(int eligibleCount, int missingTemp, int missingFit, TemperatureRampOutcome outcome)
    {
        var totalSheets = _datasetItems.Count;
        TemperatureRampPointCountLabel.Text = totalSheets == 0
            ? "(0 点)"
            : $"({eligibleCount}/{totalSheets} 点)";

        if (!outcome.Success || outcome.Result is null)
        {
            ResetRampDisplay();
            var reason = outcome.FailureReason ?? "解析できません";
            var hints = new List<string>();
            if (missingTemp > 0) hints.Add($"温度未入力 {missingTemp} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            var detail = hints.Count > 0 ? $"（{string.Join(" / ", hints)}）" : string.Empty;
            ShowRampStatus($"{reason}{detail}");
            return;
        }

        var r = outcome.Result;
        RampTransitionTemperatureText.Text =
            $"{r.TransitionTemperatureCelsius.ToString("0.00", CultureInfo.InvariantCulture)} °C";
        RampTransitionWidthText.Text =
            $"{r.TransitionWidthCelsius.ToString("0.00", CultureInfo.InvariantCulture)} °C";
        RampLowPlateauText.Text =
            $"{r.LowPlateauNm.ToString("0.0", CultureInfo.InvariantCulture)} nm";
        RampHighPlateauText.Text =
            $"{r.HighPlateauNm.ToString("0.0", CultureInfo.InvariantCulture)} nm";
        RampRSquaredText.Text =
            r.RSquared.ToString("0.0000", CultureInfo.InvariantCulture);

        if (missingTemp > 0 || missingFit > 0)
        {
            var hints = new List<string>();
            if (missingTemp > 0) hints.Add($"温度未入力 {missingTemp} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            ShowRampStatus($"残り {string.Join(" / ", hints)} は除外しました");
        }
        else
        {
            HideRampStatus();
        }
    }

    private void ResetRampDisplay()
    {
        RampTransitionTemperatureText.Text = "—";
        RampTransitionWidthText.Text = "—";
        RampLowPlateauText.Text = "—";
        RampHighPlateauText.Text = "—";
        RampRSquaredText.Text = "—";
    }

    private void ShowRampStatus(string message)
    {
        RampStatusText.Text = message;
        RampStatusText.IsVisible = true;
    }

    private void HideRampStatus()
    {
        RampStatusText.Text = string.Empty;
        RampStatusText.IsVisible = false;
    }

    // ---------- Concentration series (D vs c linear fit across loaded sheets) ----------

    /// <summary>μm²/s display unit for the diffusion coefficient axis (D × 1e12).</summary>
    private const double DiffusionDisplayScale = 1e12;

    private void RefreshConcentrationSeriesPlot()
    {
        if (_plot is null) return;
        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);

        _plot.Plot.Clear();

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

        UpdateConcentrationSeriesDisplay(
            eligibleCount, missingConc, missingFit, multiTemperature, multiViscosity, outcome);

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

    private void UpdateConcentrationSeriesDisplay(
        int eligibleCount,
        int missingConcentration,
        int missingFit,
        bool multipleTemperatures,
        bool multipleViscosities,
        ConcentrationSeriesOutcome outcome)
    {
        var totalSheets = _datasetItems.Count;
        ConcentrationSeriesPointCountLabel.Text = totalSheets == 0
            ? "(0 点)"
            : $"({eligibleCount}/{totalSheets} 点)";

        if (!outcome.Success || outcome.Result is null)
        {
            ResetConcentrationDisplay();
            var reason = outcome.FailureReason ?? "解析できません";
            var hints = new List<string>();
            if (missingConcentration > 0) hints.Add($"濃度未入力 {missingConcentration} 件");
            if (missingFit > 0) hints.Add($"キュムラント失敗 {missingFit} 件");
            var detail = hints.Count > 0 ? $"（{string.Join(" / ", hints)}）" : string.Empty;
            ShowConcentrationStatus($"{reason}{detail}");
            return;
        }

        var r = outcome.Result;
        var d0Display = r.D0M2PerSecond * DiffusionDisplayScale;
        var d0SeDisplay = r.D0StandardErrorM2PerSecond * DiffusionDisplayScale;
        ConcentrationD0Text.Text = d0SeDisplay > 0
            ? $"{d0Display.ToString("0.00", CultureInfo.InvariantCulture)} ± {d0SeDisplay.ToString("0.00", CultureInfo.InvariantCulture)} μm²/s"
            : $"{d0Display.ToString("0.00", CultureInfo.InvariantCulture)} μm²/s";

        ConcentrationKDText.Text = r.KDStandardErrorMlPerGram > 0
            ? $"{r.KDmlPerGram.ToString("0.00", CultureInfo.InvariantCulture)} ± {r.KDStandardErrorMlPerGram.ToString("0.00", CultureInfo.InvariantCulture)} mL/g"
            : $"{r.KDmlPerGram.ToString("0.00", CultureInfo.InvariantCulture)} mL/g";

        ConcentrationDhText.Text =
            $"{r.HydrodynamicDiameterAtZeroConcentrationNm.ToString("0.0", CultureInfo.InvariantCulture)} nm";
        ConcentrationRSquaredText.Text =
            r.RSquared.ToString("0.0000", CultureInfo.InvariantCulture);
        ConcentrationReferenceText.Text =
            $"T = {r.ReferenceTemperatureCelsius.ToString("0.#", CultureInfo.InvariantCulture)} °C, η = {r.ReferenceViscosityMpas.ToString("0.000", CultureInfo.InvariantCulture)} mPa·s";

        var warnings = new List<string>();
        if (missingConcentration > 0) warnings.Add($"濃度未入力 {missingConcentration} 件");
        if (missingFit > 0) warnings.Add($"キュムラント失敗 {missingFit} 件");
        if (multipleTemperatures) warnings.Add("シート間で温度が異なります（中央値を使用）");
        if (multipleViscosities) warnings.Add("シート間で粘度が異なります（中央値を使用）");

        if (warnings.Count > 0)
            ShowConcentrationStatus(string.Join(" / ", warnings));
        else
            HideConcentrationStatus();
    }

    private void ResetConcentrationDisplay()
    {
        ConcentrationD0Text.Text = "—";
        ConcentrationKDText.Text = "—";
        ConcentrationDhText.Text = "—";
        ConcentrationRSquaredText.Text = "—";
        ConcentrationReferenceText.Text = "—";
    }

    private void ShowConcentrationStatus(string message)
    {
        ConcentrationStatusText.Text = message;
        ConcentrationStatusText.IsVisible = true;
    }

    private void HideConcentrationStatus()
    {
        ConcentrationStatusText.Text = string.Empty;
        ConcentrationStatusText.IsVisible = false;
    }

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

    private static GraphFormattingConfig CloneFormattingConfig(GraphFormattingConfig source)
    {
        var json = JsonSerializer.Serialize(source, FormattingConfigJsonOptions);
        var clone = JsonSerializer.Deserialize<GraphFormattingConfig>(json, FormattingConfigJsonOptions)
            ?? GraphFormattingConfig.CreateFactoryDefault();
        clone.Normalize();
        return clone;
    }

    private GraphFormattingConfig BuildSessionFormatting()
    {
        var formatting = CloneFormattingConfig(_formattingConfig);
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

    private void ResetGraphSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
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
    }

    private void SaveDefaultFormattingButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _formattingDefaults = CaptureFormattingConfigFromControls();
            SaveFormattingDefaults();
            HideError();
            SetStatus($"書式の既定値を保存しました: {FormattingConfigPath}");
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

    // DLS code-behind が ListBox の DataTemplate / 内部状態で参照する VM。
    // WPF 版は private nested クラスだったが、Avalonia の AXAML が
    // CompiledBinding に格上げできるよう internal に昇格して
    // x:DataType で参照可能にする。
    internal sealed class DlsDatasetStyle
    {
        public string? ColorHex { get; set; }
        public string? LegendName { get; set; }
        public double LineWidth { get; set; } = GraphFormattingConfigBase.DefaultLineWidth;
        public double MarkerSize { get; set; } = GraphFormattingConfigBase.DefaultMarkerSize;
    }

    internal sealed class DlsDatasetMetadataState
    {
        public const double DefaultWavelengthNm = 633.0;
        public const double DefaultScatteringAngleDegrees = 173.0;

        public double? TemperatureCelsius { get; set; }
        public string? Solvent { get; set; }
        public double? ConcentrationMgPerMl { get; set; }
        public double? RefractiveIndex { get; set; }
        public double? ViscosityMpas { get; set; }
        public double? WavelengthNm { get; set; } = DefaultWavelengthNm;
        public double? ScatteringAngleDegrees { get; set; } = DefaultScatteringAngleDegrees;
    }

    internal sealed class DlsDatasetCumulantSettings
    {
        public double? FitRangeMinMicroseconds { get; set; }
        public double? FitRangeMaxMicroseconds { get; set; }
    }

    internal sealed class DlsDatasetItem : INotifyPropertyChanged
    {
        public DlsDataset Dataset { get; }
        public DlsDatasetStyle Style { get; } = new();
        public DlsDatasetMetadataState Metadata { get; } = new();
        public DlsDatasetCumulantSettings Cumulant { get; } = new();
        public string SheetName => Dataset.SheetName;

        private SolidColorBrush _colorBrush = new(Colors.Gray);
        public SolidColorBrush ColorBrush
        {
            get => _colorBrush;
            set
            {
                if (_colorBrush.Color == value.Color) return;
                _colorBrush = value;
                OnPropertyChanged();
            }
        }

        public DlsDatasetItem(DlsDataset dataset)
        {
            Dataset = dataset;
            Metadata.TemperatureCelsius = dataset.Metadata.TemperatureCelsius;
            Metadata.Solvent = dataset.Metadata.Solvent;
            Metadata.ConcentrationMgPerMl = dataset.Metadata.ConcentrationMgPerMl;
            Metadata.RefractiveIndex = dataset.Metadata.RefractiveIndex;
            Metadata.ViscosityMpas = dataset.Metadata.ViscosityMpas;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
        if (StatusTextBlock is null) return;
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C))
            : new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
        if (!isError) ErrorBanner.Hide();
    }

    private sealed record DataSeries(
        IReadOnlyList<double> Xs,
        IReadOnlyList<IReadOnlyList<double>> Runs,
        int ActiveRunIndex)
    {
        public int RunCount => Runs.Count;
    }
}

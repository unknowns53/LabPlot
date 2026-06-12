using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
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
        public SeriesTransform Transform { get; set; } = SeriesTransform.Identity;
        public AnalysisSessionStyle Style { get; } = new();
    }

    // ItemTemplate の CompiledBinding DataType から参照されるため public。
    public sealed class TableEntryVm
    {
        public string DisplayName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public SolidColorBrush ColorBrush { get; init; } = new(Colors.Gray);
    }

    /// <summary>列マッピングセクションの Y 列チェック行。CheckBox の TwoWay
    /// CompiledBinding が IsVisible に書き戻し、Click ハンドラが SeriesState へ
    /// 反映する (再構築なしでチェック操作を受けるための薄い VM)。</summary>
    public sealed class SeriesRowVm
    {
        public string ColumnName { get; init; } = string.Empty;
        public bool IsVisible { get; set; }
        public bool UseRightAxis { get; set; }
        public SolidColorBrush ColorBrush { get; init; } = new(Colors.Gray);
        internal SeriesState? State { get; init; }
    }

    private readonly List<LoadedTable> _loadedTables = new();
    private readonly ObservableCollection<TableEntryVm> _tableEntries = new();
    private readonly DispatcherTimer _plotRefreshDebounceTimer = new() { Interval = PlotRefreshDebounceInterval };

    // RefreshPlot が描画した順の系列スタイル。エクスポート時のスケール再適用
    // (ApplyExistingSeriesStyles) で GetPlottables の並びと突き合わせる。
    private readonly List<(AnalysisSessionStyle Style, int AutoColorIndex)> _plottedSeriesStyles = new();

    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private AvaPlot? _plot;
    private LegendDragController? _legendDragController;
    private int _activeTableIndex = -1;
    private int _activeSeriesIndex = -1;
    private bool _suppressGraphAppearanceEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressTableListEvents;
    private bool _suppressSeriesComboEvents;
    private bool _suppressMappingEvents;
    private bool _currentLegendAutoShow;
    private bool _rightAxisInUse;
    private string _currentRightAxisDefaultLabel = "Y2";
    private int _clipboardTableCount;
    private double? _y2Min;
    private double? _y2Max;

    // X 列 ComboBox の表示順 → 実カラム index の対応表 (numeric 列のみ並ぶ)。
    private readonly List<int> _xComboColumnIndexes = new();
    private readonly ObservableCollection<SeriesRowVm> _seriesRows = new();

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

    public MainWindow()
    {
        InitializeComponent();
        LoadFormattingDefaults();
        _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
        ApplyFormattingConfigToControls(_formattingConfig);
        TableListBox.ItemsSource = _tableEntries;
        YColumnItemsControl.ItemsSource = _seriesRows;
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
            if (LegendNameTextBox.IsEnabled)
            {
                LegendNameTextBox.Focus();
                LegendNameTextBox.SelectAll();
            }

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

            UpdatePlotHostAspectRatio();
            PlotPlaceholderSkeleton.IsVisible = false;
            InitializeEmptyPlot();
        }
        catch (Exception ex)
        {
            PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.InitFailed);
            ShowError($"グラフ表示の初期化に失敗しました: {ex.Message}");
        }
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
            });
        }

        _loadedTables.Add(loaded);
        return true;
    }

    /// <summary>スタイル編集の初期選択: X 列を避けて最初の可視系列を返す。</summary>
    private static int GetDefaultSeriesIndex(LoadedTable loaded)
    {
        for (var i = 0; i < loaded.Series.Count; i++)
        {
            if (loaded.Series[i].IsVisible && loaded.Series[i].ColumnIndex != loaded.XColumnIndex)
            {
                return i;
            }
        }

        return loaded.Series.Count > 0 ? 0 : -1;
    }

    /// <summary>読み込み直後の共通 UI 更新: 最後のテーブルをアクティブ化して再描画。</summary>
    private void ActivateLastLoadedTable()
    {
        _activeTableIndex = _loadedTables.Count - 1;
        _activeSeriesIndex = GetDefaultSeriesIndex(_loadedTables[_activeTableIndex]);
        RefreshTableEntries();
        RefreshMappingPanel();
        RefreshSeriesCombo();
        SyncStyleControlsFromActiveSeries();
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

    // ---------- Column mapping panel ----------

    /// <summary>選択中テーブルの X 列 ComboBox と Y 列チェックリストを作り直す。</summary>
    private void RefreshMappingPanel()
    {
        _suppressMappingEvents = true;
        try
        {
            _seriesRows.Clear();
            _xComboColumnIndexes.Clear();

            if (ActiveTable is not { } loaded)
            {
                XColumnComboBox.ItemsSource = null;
                XColumnComboBox.IsEnabled = false;
                MappingTableLabel.Text = "(テーブル未選択)";
                MappingEmptyHint.IsVisible = true;
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

            for (var i = 0; i < loaded.Series.Count; i++)
            {
                var series = loaded.Series[i];
                if (series.ColumnIndex == loaded.XColumnIndex && loaded.Series.Count > 1)
                {
                    continue;
                }

                var autoIndex = GetSeriesAutoColorIndex(_activeTableIndex, i);
                var hex = series.Style.ColorHex ?? AutoLineColors[autoIndex % AutoLineColors.Length];
                _seriesRows.Add(new SeriesRowVm
                {
                    ColumnName = series.ColumnName,
                    IsVisible = series.IsVisible,
                    UseRightAxis = series.UseRightAxis,
                    ColorBrush = new SolidColorBrush(HexToAvaloniaColor(hex)),
                    State = series,
                });
            }

            MappingTableLabel.Text = $"({loaded.DisplayName})";
            MappingEmptyHint.IsVisible = _seriesRows.Count == 0;
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
        RefreshMappingPanel();
        RefreshSeriesCombo();
        RefreshPlot();
    }

    private void YColumnCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (_suppressMappingEvents) return;
        if (sender is not CheckBox { Tag: SeriesRowVm { State: { } state } } checkBox) return;

        state.IsVisible = checkBox.IsChecked == true;
        RefreshTableEntries();
        RefreshPlot();
    }

    private void YColumnRightAxisCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (_suppressMappingEvents) return;
        if (sender is not CheckBox { Tag: SeriesRowVm { State: { } state } } checkBox) return;

        state.UseRightAxis = checkBox.IsChecked == true;
        RefreshPlot();
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
                    ?? AutoLineColors[GetSeriesAutoColorIndex(i, 0) % AutoLineColors.Length];
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

    /// <summary>auto 色をテーブル境界をまたいだ通し番号で割り当てるための系列序数。</summary>
    private int GetSeriesAutoColorIndex(int tableIndex, int seriesIndexInTable)
    {
        var ordinal = 0;
        for (var i = 0; i < Math.Min(tableIndex, _loadedTables.Count); i++)
        {
            ordinal += _loadedTables[i].Series.Count;
        }

        return ordinal + seriesIndexInTable;
    }

    private void TableListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTableListEvents) return;

        var newIndex = TableListBox.SelectedIndex;
        if (newIndex < 0 || newIndex >= _loadedTables.Count) return;

        _activeTableIndex = newIndex;
        _activeSeriesIndex = GetDefaultSeriesIndex(_loadedTables[newIndex]);
        RefreshMappingPanel();
        RefreshSeriesCombo();
        SyncStyleControlsFromActiveSeries();
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
            _activeSeriesIndex = -1;
            MainTitleBar.Subtitle = "Tabular data viewer";
            Title = "Data Viewer";
        }
        else
        {
            _activeTableIndex = Math.Clamp(_activeTableIndex, 0, _loadedTables.Count - 1);
            _activeSeriesIndex = GetDefaultSeriesIndex(_loadedTables[_activeTableIndex]);
        }

        RefreshTableEntries();
        RefreshMappingPanel();
        RefreshSeriesCombo();
        SyncStyleControlsFromActiveSeries();
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
                ResetReorderState();
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
        var (targetItem, insertAbove) = ResolveDropTargetFromVisual(e);
        if (targetItem is null || ReferenceEquals(targetItem, _tableDragSourceContainer))
        {
            HideInsertionLine();
        }
        else
        {
            UpdateInsertionLine(targetItem, insertAbove);
        }

        e.Handled = true;
    }

    private void OnTableListBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isInternalReordering)
        {
            ResetReorderState();
            return;
        }

        var sourceIndex = _tableDragSourceIndex ?? -1;
        var (targetItem, insertAbove) = ResolveDropTargetFromVisual(e);

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

        newIndex = Math.Clamp(newIndex, 0, Math.Max(0, _tableEntries.Count - 1));
        HideInsertionLine();

        if (sourceIndex >= 0 && newIndex != sourceIndex)
        {
            MoveTable(sourceIndex, newIndex);
        }

        ResetReorderState();
        e.Handled = true;
    }

    private void OnTableListBoxPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        HideInsertionLine();
        ResetReorderState();
    }

    private void ResetReorderState()
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

    private (ListBoxItem? Item, bool InsertAbove) ResolveDropTargetFromVisual(PointerEventArgs e)
    {
        // PointerCapture 中は e.Source が常に capture 先になるため、Pointer 位置から
        // ListBox の hit-test を自前実行する (GPC と同じ理由)。
        var posInListBox = e.GetPosition(TableListBox);
        var hit = TableListBox.InputHitTest(posInListBox) as Visual;
        var item = hit is null ? null : FindAncestor<ListBoxItem>(hit);
        if (item is null) return (null, false);
        var pos = e.GetPosition(item);
        var insertAbove = pos.Y < item.Bounds.Height / 2;
        return (item, insertAbove);
    }

    private void UpdateInsertionLine(ListBoxItem item, bool insertAbove)
    {
        var transformPoint = item.TranslatePoint(new Point(0, 0), TableListBox);
        if (transformPoint is null)
        {
            HideInsertionLine();
            return;
        }

        var listBoxTopInGrid = TableListBox.Bounds.Top;
        var itemTopInGrid = listBoxTopInGrid + transformPoint.Value.Y;
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
        for (var tableIndex = 0; tableIndex < _loadedTables.Count; tableIndex++)
        {
            var loaded = _loadedTables[tableIndex];
            for (var seriesIndex = 0; seriesIndex < loaded.Series.Count; seriesIndex++)
            {
                var style = loaded.Series[seriesIndex].Style;
                if (string.IsNullOrEmpty(style.ColorHex))
                {
                    var autoIndex = GetSeriesAutoColorIndex(tableIndex, seriesIndex);
                    style.ColorHex = AutoLineColors[autoIndex % AutoLineColors.Length];
                }
            }
        }

        var table = _loadedTables[oldIndex];
        _loadedTables.RemoveAt(oldIndex);
        _loadedTables.Insert(newIndex, table);
        _activeTableIndex = newIndex;

        RefreshTableEntries();
        RefreshMappingPanel();
        RefreshSeriesCombo();
        SyncStyleControlsFromActiveSeries();
        RefreshPlot();
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

    // ---------- Series style ----------

    private LoadedTable? ActiveTable =>
        _activeTableIndex >= 0 && _activeTableIndex < _loadedTables.Count
            ? _loadedTables[_activeTableIndex]
            : null;

    private SeriesState? ActiveSeries =>
        ActiveTable is { } table && _activeSeriesIndex >= 0 && _activeSeriesIndex < table.Series.Count
            ? table.Series[_activeSeriesIndex]
            : null;

    private void RefreshSeriesCombo()
    {
        _suppressSeriesComboEvents = true;
        try
        {
            var series = ActiveTable?.Series ?? new List<SeriesState>();
            SeriesComboBox.ItemsSource = series.Select(static state => state.ColumnName).ToArray();
            SeriesComboBox.IsEnabled = series.Count > 0;
            SeriesComboBox.SelectedIndex = series.Count > 0
                ? Math.Clamp(_activeSeriesIndex, 0, series.Count - 1)
                : -1;
        }
        finally
        {
            _suppressSeriesComboEvents = false;
        }
    }

    private void SeriesComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSeriesComboEvents) return;

        var newIndex = SeriesComboBox.SelectedIndex;
        if (ActiveTable is not { } table || newIndex < 0 || newIndex >= table.Series.Count) return;

        _activeSeriesIndex = newIndex;
        SyncStyleControlsFromActiveSeries();
    }

    private void SyncStyleControlsFromActiveSeries()
    {
        _suppressStyleControlEvents = true;
        try
        {
            if (ActiveSeries is not { } series)
            {
                LineColorPicker.DefaultHex = AutoLineColors[0];
                LineColorPicker.SetHexValue(null);
                LegendNameTextBox.Text = string.Empty;
                LegendNameTextBox.IsEnabled = false;
                LineWidthTextBox.Text = _formattingConfig.FormatLineWidth();
                MarkerSizeTextBox.Text = _formattingConfig.FormatMarkerSize();
                NormalizeCheckBox.IsChecked = false;
                YOffsetTextBox.Text = "0";
                SmoothingWindowTextBox.Text = "0";
                ActiveSeriesLabel.Text = "(系列未選択)";
                return;
            }

            var autoIndex = GetSeriesAutoColorIndex(_activeTableIndex, _activeSeriesIndex);
            LineColorPicker.DefaultHex = AutoLineColors[autoIndex % AutoLineColors.Length];
            LineColorPicker.SetHexValue(series.Style.ColorHex);
            LegendNameTextBox.Text = series.Style.LegendName ?? string.Empty;
            LegendNameTextBox.IsEnabled = true;
            LineWidthTextBox.Text = series.Style.LineWidth.ToString("0.##", CultureInfo.InvariantCulture);
            MarkerSizeTextBox.Text = series.Style.MarkerSize.ToString("0.##", CultureInfo.InvariantCulture);
            NormalizeCheckBox.IsChecked = series.Transform.Normalize;
            YOffsetTextBox.Text = series.Transform.YOffset.ToString("0.######", CultureInfo.InvariantCulture);
            SmoothingWindowTextBox.Text = series.Transform.SmoothingWindow.ToString(CultureInfo.InvariantCulture);
            ActiveSeriesLabel.Text = $"({series.ColumnName})";
        }
        finally
        {
            _suppressStyleControlEvents = false;
        }
    }

    private bool ApplySeriesStyleEdit(Action<AnalysisSessionStyle> mutate)
    {
        if (ActiveSeries is not { } series) return false;
        mutate(series.Style);
        return true;
    }

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (_suppressStyleControlEvents) return;
        if (!ApplySeriesStyleEdit(style => style.ColorHex = LineColorPicker.HexValue)) return;

        RefreshTableEntries();
        SchedulePlotRefresh();
    }

    private void LegendNameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;

        DatasetStyleCommit.CommitLegendName(LegendNameTextBox, value =>
            ApplySeriesStyleEdit(style => style.LegendName = value));
        SchedulePlotRefresh();
    }

    private void StyleNumberTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents) return;

        var committed = sender == LineWidthTextBox
            ? DatasetStyleCommit.TryCommitPositiveDouble(LineWidthTextBox, value =>
                ApplySeriesStyleEdit(style => style.LineWidth = value))
            : DatasetStyleCommit.TryCommitNonNegativeDouble(MarkerSizeTextBox, value =>
                ApplySeriesStyleEdit(style => style.MarkerSize = value));
        if (committed)
        {
            SchedulePlotRefresh();
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

        var xRange = new AxisDataRange();
        var yRange = new AxisDataRange();
        var y2Range = new AxisDataRange();
        var plottedCount = 0;
        var hasCustomLegendName = false;
        string? firstSeriesName = null;
        string? firstRightSeriesName = null;
        _rightAxisInUse = false;

        for (var tableIndex = 0; tableIndex < _loadedTables.Count; tableIndex++)
        {
            var loaded = _loadedTables[tableIndex];
            var xColumn = loaded.Table.Columns[loaded.XColumnIndex];
            for (var seriesIndex = 0; seriesIndex < loaded.Series.Count; seriesIndex++)
            {
                var series = loaded.Series[seriesIndex];
                if (!series.IsVisible) continue;

                // X に割り当てた列は Y からは描かない。数値列がそれ 1 本しか
                // ないテーブルだけは行番号を X にして値の推移を見せる。
                var isXColumn = series.ColumnIndex == loaded.XColumnIndex;
                if (isXColumn && loaded.Series.Count > 1) continue;

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

                var scatter = _plot.Plot.Add.Scatter(xs, ys);
                scatter.LegendText = GetSeriesLegendText(loaded, series);
                var autoIndex = GetSeriesAutoColorIndex(tableIndex, seriesIndex);
                ApplySeriesStyle(scatter, series.Style, autoIndex);
                _plottedSeriesStyles.Add((series.Style, autoIndex));

                xRange.Include(xs);
                if (series.UseRightAxis)
                {
                    scatter.Axes.YAxis = _plot.Plot.Axes.Right;
                    y2Range.Include(ys);
                    _rightAxisInUse = true;
                    firstRightSeriesName ??= series.ColumnName;
                }
                else
                {
                    yRange.Include(ys);
                    firstSeriesName ??= series.ColumnName;
                }

                plottedCount++;
                hasCustomLegendName |= !string.IsNullOrWhiteSpace(series.Style.LegendName);
            }
        }

        // log 軸は decade ticks (NumericManual)、linear は automatic に戻す。
        _plot.Plot.Axes.Bottom.TickGenerator = xLog
            ? LogAxisHelper.CreateDecadeTicks(xRange.Min, xRange.Max)
            : new ScottPlot.TickGenerators.NumericAutomatic();
        _plot.Plot.Axes.Left.TickGenerator = yLog
            ? LogAxisHelper.CreateDecadeTicks(yRange.Min, yRange.Max)
            : new ScottPlot.TickGenerators.NumericAutomatic();
        _plot.Plot.Axes.Right.TickGenerator = _rightAxisInUse && y2Log
            ? LogAxisHelper.CreateDecadeTicks(y2Range.Min, y2Range.Max)
            : new ScottPlot.TickGenerators.NumericAutomatic();

        _currentLegendAutoShow = plottedCount > 1 || hasCustomLegendName;
        ApplyLegend(_plot.Plot, CaptureFormattingConfigFromControls(), autoShow: _currentLegendAutoShow);

        var activeTable = ActiveTable ?? _loadedTables[0];
        var defaultTitle = _loadedTables.Count == 1
            ? activeTable.DisplayName
            : $"{_loadedTables.Count} tables";
        var defaultXLabel = activeTable.Series.Count == 1
            && activeTable.Series[0].ColumnIndex == activeTable.XColumnIndex
            ? "Index"
            : activeTable.Table.Columns[activeTable.XColumnIndex].Name;
        _currentRightAxisDefaultLabel = firstRightSeriesName ?? "Y2";
        _plot.Plot.Title(GetGraphTitle(defaultTitle));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, defaultXLabel));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, firstSeriesName ?? "Value"));
        _plot.Plot.Axes.AutoScale();
        ApplyAxisLimits(xRange, yRange, y2Range, xLog, yLog, y2Log);
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
    {
        if (!string.IsNullOrWhiteSpace(series.Style.LegendName))
        {
            return series.Style.LegendName!.Trim();
        }

        return _loadedTables.Count == 1
            ? series.ColumnName
            : $"{table.DisplayName}: {series.ColumnName}";
    }

    private void ApplySeriesStyle(
        ScottPlot.Plottables.Scatter scatter,
        AnalysisSessionStyle style,
        int autoColorIndex,
        float scale = 1f)
    {
        scatter.LineWidth = (float)style.LineWidth * scale;
        scatter.MarkerSize = (float)style.MarkerSize * scale;
        var hex = style.ColorHex ?? AutoLineColors[autoColorIndex % AutoLineColors.Length];
        scatter.Color = ScottPlot.Color.FromHex(new[] { hex }).First();
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
        SchedulePlotRefresh();
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
        var scatters = _plot.Plot
            .GetPlottables()
            .OfType<ScottPlot.Plottables.Scatter>()
            .ToArray();
        for (var i = 0; i < scatters.Length && i < _plottedSeriesStyles.Count; i++)
        {
            var (style, autoIndex) = _plottedSeriesStyles[i];
            ApplySeriesStyle(scatters[i], style, autoIndex, scale);
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

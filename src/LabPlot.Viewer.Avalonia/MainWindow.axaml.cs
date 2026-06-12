using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
        public AnalysisSessionStyle Style { get; } = new();
    }

    // ItemTemplate の CompiledBinding DataType から参照されるため public。
    public sealed class TableEntryVm
    {
        public string DisplayName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public SolidColorBrush ColorBrush { get; init; } = new(Colors.Gray);
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
    private bool _currentLegendAutoShow;

    public MainWindow()
    {
        InitializeComponent();
        LoadFormattingDefaults();
        _formattingConfig = FormattingDefaultsStore.Clone(_formattingDefaults, FormattingConfigJsonOptions);
        ApplyFormattingConfigToControls(_formattingConfig);
        TableListBox.ItemsSource = _tableEntries;
        _plotRefreshDebounceTimer.Tick += PlotRefreshDebounceTimer_Tick;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowStateStore.ApplyTo(this, WindowStateAppKey);
        KeyboardShortcuts.LocalizeTooltipsForMac(this);

        Dispatcher.UIThread.Post(InitializePlotControl, DispatcherPriority.Background);
        SetStatus("CSV / TSV / xlsx の表形式データを開いてください。", StatusSeverity.Info);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        WindowStateStore.PersistFrom(this, WindowStateAppKey);
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var cmd = e.HasCommandModifier();
        if (cmd)
        {
            switch (e.Key)
            {
                case Key.O: OpenFileButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.S: SaveGraphButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.R: AxisRangePanel.ResetToAuto(); e.Handled = true; return;
                case Key.G: GraphFormatPanel.TogglePlotGrid(); e.Handled = true; return;
            }
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
            foreach (var tableSet in tableSets)
            {
                foreach (var table in tableSet.Tables)
                {
                    if (TryAddLoadedTable(table))
                    {
                        addedTables++;
                    }
                }
            }

            if (addedTables == 0)
            {
                SetStatus("プロットできる数値列が見つかりませんでした。", true);
                return;
            }

            _activeTableIndex = _loadedTables.Count - 1;
            _activeSeriesIndex = _loadedTables[_activeTableIndex].Series.Count > 0 ? 0 : -1;
            RefreshTableEntries();
            RefreshSeriesCombo();
            SyncStyleControlsFromActiveSeries();
            RefreshPlot();

            var rowCount = tableSets.SelectMany(static set => set.Tables).Sum(static table => table.RowCount);
            SetStatus(addedTables == 1
                ? $"{rowCount:N0} 行のデータを読み込みました。"
                : $"{addedTables} テーブル / {rowCount:N0} 行のデータを読み込みました。", false);

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
    /// テーブルは false を返してスキップ (xlsx の説明シートなど)。
    /// </summary>
    private bool TryAddLoadedTable(ViewerTable table)
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
        var displayName = (fileName, table.SheetName) switch
        {
            (null or "", _) => $"テーブル {_loadedTables.Count + 1}",
            (_, null or "") => fileName!,
            _ => $"{fileName} [{table.SheetName}]",
        };

        var loaded = new LoadedTable
        {
            Table = table,
            DisplayName = displayName,
            XColumnIndex = mapping.XColumnIndex,
        };

        var autoEnabled = mapping.YColumnIndexes.ToHashSet();
        for (var col = 0; col < table.Columns.Count; col++)
        {
            if (!table.Columns[col].IsNumeric || (col == mapping.XColumnIndex && !autoEnabled.Contains(col)))
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
        _activeSeriesIndex = _loadedTables[newIndex].Series.Count > 0 ? 0 : -1;
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
            _activeSeriesIndex = _loadedTables[_activeTableIndex].Series.Count > 0 ? 0 : -1;
        }

        RefreshTableEntries();
        RefreshSeriesCombo();
        SyncStyleControlsFromActiveSeries();
        RefreshPlot();
        SetStatus("テーブルを削除しました。", StatusSeverity.Info);
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

        var xRange = new AxisDataRange();
        var yRange = new AxisDataRange();
        var plottedCount = 0;
        var hasCustomLegendName = false;
        string? firstSeriesName = null;

        for (var tableIndex = 0; tableIndex < _loadedTables.Count; tableIndex++)
        {
            var loaded = _loadedTables[tableIndex];
            var xColumn = loaded.Table.Columns[loaded.XColumnIndex];
            for (var seriesIndex = 0; seriesIndex < loaded.Series.Count; seriesIndex++)
            {
                var series = loaded.Series[seriesIndex];
                if (!series.IsVisible) continue;

                var yColumn = loaded.Table.Columns[series.ColumnIndex];
                var (xs, ys) = ExtractFinitePairs(xColumn.Values, yColumn.Values);
                if (xs.Length == 0) continue;

                var scatter = _plot.Plot.Add.Scatter(xs, ys);
                scatter.LegendText = GetSeriesLegendText(loaded, series);
                var autoIndex = GetSeriesAutoColorIndex(tableIndex, seriesIndex);
                ApplySeriesStyle(scatter, series.Style, autoIndex);
                _plottedSeriesStyles.Add((series.Style, autoIndex));

                xRange.Include(xs);
                yRange.Include(ys);
                plottedCount++;
                hasCustomLegendName |= !string.IsNullOrWhiteSpace(series.Style.LegendName);
                firstSeriesName ??= series.ColumnName;
            }
        }

        _currentLegendAutoShow = plottedCount > 1 || hasCustomLegendName;
        ApplyLegend(_plot.Plot, CaptureFormattingConfigFromControls(), autoShow: _currentLegendAutoShow);

        var activeTable = ActiveTable ?? _loadedTables[0];
        var defaultTitle = _loadedTables.Count == 1
            ? activeTable.DisplayName
            : $"{_loadedTables.Count} tables";
        _plot.Plot.Title(GetGraphTitle(defaultTitle));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, activeTable.Table.Columns[activeTable.XColumnIndex].Name));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, plottedCount == 1 ? firstSeriesName ?? "Value" : "Value"));
        _plot.Plot.Axes.AutoScale();
        ApplyAxisLimits(xRange, yRange);
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

    private void ApplyAxisLimits(AxisDataRange xRange, AxisDataRange yRange)
    {
        if (_plot is null) return;

        var xMin = AxisRangePanel.XMinValue;
        var xMax = AxisRangePanel.XMaxValue;
        var yMin = AxisRangePanel.YMinValue;
        var yMax = AxisRangePanel.YMaxValue;

        if ((xMin.HasValue || xMax.HasValue)
            && TryGetRequestedRange(xRange, xMin, xMax, "X", out var left, out var right))
        {
            _plot.Plot.Axes.SetLimitsX(left, right);
        }

        if ((yMin.HasValue || yMax.HasValue)
            && TryGetRequestedRange(yRange, yMin, yMax, "Y", out var bottom, out var top))
        {
            _plot.Plot.Axes.SetLimitsY(bottom, top);
        }
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
        ApplyAll(_plot.Plot, CaptureFormattingConfigFromControls(), scale);
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

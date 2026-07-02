using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataViewer.Core;
using LabPlot.Core;
using LabPlot.Core.Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.Viewer.Avalonia;

// セッション (.gvjson) / データ出力 / MRU の partial。プロット・マッピング系は
// MainWindow.axaml.cs 側が持つ (GPC の MainWindow.Plot.cs 分割と同じ方針)。
public partial class MainWindow
{
    private const string RecentFilesAppKey = "viewer";
    private const string SessionExtension = "gvjson";

    private readonly AnalysisSessionStore<ViewerAnalysisSession> _sessionStore = new();
    private bool _suppressRecentFilesEvents;
    private string? _lastLoadedFilePath;

    // ---------- 最近開いたファイル (MRU) ----------

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
        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i], _lastLoadedFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
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
        _ = ImportDataFilesAsync(new[] { path });
    }

    // 履歴だけをクリアする。読み込み済みテーブルにはクリップボード由来も
    // 混ざるため、GPC と違いプロットや内部状態には触れない。
    private async void ClearRecentFilesMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmDialog.ShowAsync(
            this,
            title: "履歴をクリアしますか?",
            message: "最近開いたファイルの履歴を消去します。読み込み済みのテーブルとグラフはそのまま残ります。",
            confirmLabel: "クリア",
            isDestructive: true);
        if (!confirmed) return;

        RecentFilesStore.Clear(RecentFilesAppKey);
        _lastLoadedFilePath = null;
        RefreshRecentFilesUi();
        SetStatus("最近開いたファイルの履歴をクリアしました。", StatusSeverity.Info);
    }

    private void RegisterRecentFiles(IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 0) return;
        foreach (var fileName in fileNames.Reverse())
        {
            RecentFilesStore.Add(RecentFilesAppKey, fileName);
        }

        _lastLoadedFilePath = fileNames[0];
        RefreshRecentFilesUi();
    }

    // ---------- セッション (.gvjson) ----------

    private ViewerAnalysisSession BuildAnalysisSession()
    {
        var session = new ViewerAnalysisSession
        {
            Overlay = true,
            ActiveDatasetIndex = _activeTableIndex,
            Labels = new AnalysisSessionLabels
            {
                Title = TitleTextBox.Text,
                XLabel = XLabelTextBox.Text,
                YLabel = YLabelTextBox.Text,
            },
            Axes = new ViewerSessionAxes
            {
                XMin = AxisRangePanel.XMinValue,
                XMax = AxisRangePanel.XMaxValue,
                YMin = AxisRangePanel.YMinValue,
                YMax = AxisRangePanel.YMaxValue,
                XLogScale = XLogCheckBox.IsChecked == true,
                YLogScale = YLogCheckBox.IsChecked == true,
                Y2LogScale = Y2LogCheckBox.IsChecked == true,
                Y2Min = _y2Min,
                Y2Max = _y2Max,
                Y2Label = Y2LabelTextBox.Text,
            },
            Formatting = CaptureFormattingConfigFromControls(),
        };

        foreach (var loaded in _loadedTables)
        {
            var dataset = new ViewerSessionDataset
            {
                SourceFilePath = loaded.Table.SourceFilePath ?? string.Empty,
                SheetName = loaded.Table.SheetName,
                XColumnIndex = loaded.XColumnIndex,
                EmbeddedTable = loaded.Table.SourceFilePath is null
                    ? ViewerEmbeddedTable.FromTable(loaded.Table)
                    : null,
            };

            foreach (var series in loaded.Series)
            {
                dataset.Series.Add(new ViewerSessionSeries
                {
                    ColumnIndex = series.ColumnIndex,
                    ColumnName = series.ColumnName,
                    DisplayOrder = series.DisplayOrder,
                    IsVisible = series.IsVisible,
                    AxisSide = series.UseRightAxis ? "Right" : "Left",
                    ChartType = series.ChartType.ToToken(),
                    Normalize = series.Transform.Normalize,
                    YOffset = series.Transform.YOffset,
                    SmoothingWindow = series.Transform.SmoothingWindow,
                    Style = new AnalysisSessionStyle
                    {
                        ColorHex = series.Style.ColorHex,
                        LegendName = series.Style.LegendName,
                        LineWidth = series.Style.LineWidth,
                        MarkerSize = series.Style.MarkerSize,
                    },
                });
            }

            session.Datasets.Add(dataset);
        }

        return session;
    }

    private async void SaveSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_loadedTables.Count == 0)
        {
            ShowError("保存する表示条件がありません。");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "表示条件を保存",
            SuggestedFileName = $"viewer_session.{SessionExtension}",
            DefaultExtension = SessionExtension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Data Viewer 表示条件") { Patterns = new[] { $"*.{SessionExtension}" } },
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            _sessionStore.Save(BuildAnalysisSession(), path);
            SetStatus($"表示条件を保存しました: {path}", StatusSeverity.Success);
            Toast?.Show("表示条件を保存しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"表示条件の保存に失敗しました: {ex.Message}");
        }
    }

    private async void LoadSessionButton_Click(object? sender, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "表示条件を読み込み",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Data Viewer 表示条件") { Patterns = new[] { $"*.{SessionExtension}", "*.json" } },
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
            await ApplyAnalysisSessionAsync(session);
            SetStatus($"表示条件を読み込みました: {Path.GetFileName(path)}", StatusSeverity.Success);
            Toast?.Show("表示条件を読み込みました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            ShowError($"表示条件の読み込みに失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// セッションからテーブル群と表示状態を復元する。ファイル由来はパスから
    /// 再読込 (欠損は Warning でスキップ)、クリップボード由来は埋め込みデータ
    /// から復元する。列はまず index、ズレていれば列名で再照合する。
    /// </summary>
    private async Task ApplyAnalysisSessionAsync(ViewerAnalysisSession session)
    {
        BusyOverlay.Show("表示条件を読み込み中…");
        try
        {
            var newTables = new List<LoadedTable>();
            var skipped = new List<string>();

            // 復元前のカウンタ残値 (直前のアプリ状態由来) は保存済み最大値より
            // 小さいことがあり、そのままだと未マッチ列の採番が既存系列の途中へ
            // 割り込む。先に保存済み DisplayOrder の最大値+1 へ立て直す。
            _nextSeriesDisplayOrder = session.Datasets
                .SelectMany(static dataset => dataset.Series)
                .Select(static series => series.DisplayOrder)
                .DefaultIfEmpty(-1)
                .Max() + 1;

            foreach (var dataset in session.Datasets)
            {
                var loaded = await RestoreLoadedTableAsync(dataset, skipped);
                if (loaded is not null)
                {
                    newTables.Add(loaded);
                }
            }

            if (newTables.Count == 0)
            {
                ShowError("表示条件のテーブルを 1 件も復元できませんでした。");
                return;
            }

            _loadedTables.Clear();
            _loadedTables.AddRange(newTables);

            ApplySessionPresentation(session);

            _activeTableIndex = Math.Clamp(
                session.ActiveDatasetIndex,
                0,
                _loadedTables.Count - 1);
            RefreshTableEntries();
            RefreshXColumnPanel();
            RefreshSeriesList();
            RefreshPlot();

            if (skipped.Count > 0)
            {
                SetStatus($"一部のテーブルを復元できませんでした: {string.Join(" / ", skipped)}", StatusSeverity.Warning);
                Toast?.Show($"{skipped.Count} 件のテーブルをスキップしました", StatusSeverity.Warning);
            }

            // 復元中に割り当てた DisplayOrder (保存値・末尾採番の混在) を踏まえ、
            // 次回の新規ロード・貼り付けが必ず末尾へ付くようカウンタを立て直す。
            _nextSeriesDisplayOrder = _loadedTables
                .SelectMany(static loaded => loaded.Series)
                .Select(static series => series.DisplayOrder)
                .DefaultIfEmpty(-1)
                .Max() + 1;
        }
        finally
        {
            BusyOverlay.Hide();
        }
    }

    private async Task<LoadedTable?> RestoreLoadedTableAsync(
        ViewerSessionDataset dataset,
        List<string> skipped)
    {
        ViewerTable? table = null;
        string displayName;

        if (dataset.EmbeddedTable is not null)
        {
            table = dataset.EmbeddedTable.ToTable();
            displayName = $"クリップボード {++_clipboardTableCount}";
        }
        else if (!string.IsNullOrWhiteSpace(dataset.SourceFilePath))
        {
            if (!File.Exists(dataset.SourceFilePath))
            {
                skipped.Add(Path.GetFileName(dataset.SourceFilePath));
                return null;
            }

            try
            {
                var set = await Task.Run(() => ReadTableSet(dataset.SourceFilePath));
                table = set.Tables.FirstOrDefault(t =>
                        string.Equals(t.SheetName, dataset.SheetName, StringComparison.OrdinalIgnoreCase))
                    ?? set.Tables[0];
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                skipped.Add($"{Path.GetFileName(dataset.SourceFilePath)} ({ex.Message})");
                return null;
            }

            var fileName = Path.GetFileName(dataset.SourceFilePath);
            displayName = string.IsNullOrEmpty(table.SheetName)
                ? fileName
                : $"{fileName} [{table.SheetName}]";
        }
        else
        {
            return null;
        }

        var numericIndexes = new List<int>();
        for (var col = 0; col < table.Columns.Count; col++)
        {
            if (table.Columns[col].IsNumeric)
            {
                numericIndexes.Add(col);
            }
        }

        if (numericIndexes.Count == 0)
        {
            skipped.Add(displayName);
            return null;
        }

        var loaded = new LoadedTable
        {
            Table = table,
            DisplayName = displayName,
            XColumnIndex = numericIndexes.Contains(dataset.XColumnIndex)
                ? dataset.XColumnIndex
                : numericIndexes[0],
        };

        // まず全数値列を非表示の系列として用意し、保存済みの系列状態を
        // index → 名前の順で照合して上書きする (列の挿入・削除に耐える)。
        foreach (var col in numericIndexes)
        {
            loaded.Series.Add(new SeriesState
            {
                ColumnIndex = col,
                ColumnName = table.Columns[col].Name,
                IsVisible = false,
            });
        }

        var matchedSeries = new HashSet<SeriesState>();
        foreach (var saved in dataset.Series)
        {
            var target = loaded.Series.FirstOrDefault(s =>
                    s.ColumnIndex == saved.ColumnIndex
                    && (string.IsNullOrEmpty(saved.ColumnName) || s.ColumnName == saved.ColumnName))
                ?? loaded.Series.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(saved.ColumnName) && s.ColumnName == saved.ColumnName);
            if (target is null) continue;

            matchedSeries.Add(target);
            target.IsVisible = saved.IsVisible;
            target.UseRightAxis = string.Equals(saved.AxisSide, "Right", StringComparison.OrdinalIgnoreCase);
            target.ChartType = ViewerChartTypes.Parse(saved.ChartType);
            target.DisplayOrder = saved.DisplayOrder;
            target.Transform = new SeriesTransform
            {
                Normalize = saved.Normalize,
                YOffset = double.IsFinite(saved.YOffset) ? saved.YOffset : 0,
                SmoothingWindow = Math.Clamp(saved.SmoothingWindow, 0, 9999),
            };
            target.Style.ColorHex = saved.Style?.ColorHex;
            target.Style.LegendName = saved.Style?.LegendName;
            if (saved.Style is not null)
            {
                target.Style.LineWidth = saved.Style.LineWidth;
                target.Style.MarkerSize = saved.Style.MarkerSize;
            }
        }

        // セッションに保存されていない実列 (テーブル更新等で増えた新規列) は
        // フラット表示順の末尾に付くよう、その場で採番カウンタを進めて割り当てる。
        // 復元完了後 (ApplyAnalysisSessionAsync 末尾) にカウンタ自体を再計算するため、
        // ここでの値はこの読み込み内で「末尾になる」ことだけ保証すれば十分。
        foreach (var series in loaded.Series)
        {
            if (!matchedSeries.Contains(series))
            {
                series.DisplayOrder = _nextSeriesDisplayOrder++;
            }
        }

        return loaded;
    }

    /// <summary>ラベル・軸設定・書式をセッションからコントロールへ流し込む。</summary>
    private void ApplySessionPresentation(ViewerAnalysisSession session)
    {
        _suppressGraphAppearanceEvents = true;
        try
        {
            TitleTextBox.Text = session.Labels.Title ?? string.Empty;
            XLabelTextBox.Text = session.Labels.XLabel ?? string.Empty;
            YLabelTextBox.Text = session.Labels.YLabel ?? string.Empty;
            Y2LabelTextBox.Text = session.Axes.Y2Label ?? string.Empty;

            XLogCheckBox.IsChecked = session.Axes.XLogScale;
            YLogCheckBox.IsChecked = session.Axes.YLogScale;
            Y2LogCheckBox.IsChecked = session.Axes.Y2LogScale;

            AxisRangePanel.ResetToAuto();
            if (session.Axes.XMin.HasValue && session.Axes.XMax.HasValue)
            {
                AxisRangePanel.SetXValues(session.Axes.XMin.Value, session.Axes.XMax.Value);
            }

            if (session.Axes.YMin.HasValue && session.Axes.YMax.HasValue)
            {
                AxisRangePanel.SetYValues(session.Axes.YMin.Value, session.Axes.YMax.Value);
            }

            _y2Min = session.Axes.Y2Min;
            _y2Max = session.Axes.Y2Max;
            Y2MinTextBox.Text = FormatOptionalDouble(_y2Min);
            Y2MaxTextBox.Text = FormatOptionalDouble(_y2Max);
        }
        finally
        {
            _suppressGraphAppearanceEvents = false;
        }

        if (session.Formatting is { } formatting)
        {
            _formattingConfig = formatting;
            ApplyFormattingConfigToControls(formatting);
            UpdatePlotHostAspectRatio();
        }
    }

    private static string FormatOptionalDouble(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
    }

    // ---------- データ出力 (CSV / xlsx) ----------

    /// <summary>
    /// 表示中の系列を「プロットされたままのデータ単位」で書き出す。変換
    /// (正規化・オフセット・平滑化) は適用済み、log は表示変換なので含めない。
    /// </summary>
    private AnalysisExport BuildAnalysisExport()
    {
        var entries = new List<AnalysisExportEntry>();
        // 出力順 = 表示順。フラット表示順で列挙し、X 列参照は各行の Table 側から辿る。
        foreach (var (loaded, series) in EnumerateSeriesInDisplayOrder())
        {
            if (!series.IsVisible) continue;
            var isXColumn = series.ColumnIndex == loaded.XColumnIndex;
            if (isXColumn && loaded.Series.Count > 1) continue;

            var xColumn = loaded.Table.Columns[loaded.XColumnIndex];
            var yColumn = loaded.Table.Columns[series.ColumnIndex];
            var xValues = isXColumn
                ? Enumerable.Range(1, yColumn.Values.Length).Select(static i => (double)i).ToArray()
                : xColumn.Values;
            var yValues = SeriesTransformer.Apply(yColumn.Values, series.Transform);

            var points = new List<ViewerDataPoint>(yValues.Length);
            var count = Math.Min(xValues.Length, yValues.Length);
            for (var i = 0; i < count; i++)
            {
                if (double.IsFinite(xValues[i]) && double.IsFinite(yValues[i]))
                {
                    points.Add(new ViewerDataPoint(xValues[i], yValues[i]));
                }
            }

            if (points.Count == 0) continue;

            entries.Add(new ViewerAnalysisExportEntry
            {
                DisplayName = GetSeriesLegendText(loaded, series),
                SourceFilePath = loaded.Table.SourceFilePath,
                XLabel = isXColumn ? "Index" : xColumn.Name,
                YLabel = series.ColumnName,
                Points = points,
            });
        }

        return new AnalysisExport
        {
            Entries = entries,
            GeneratorName = "Data Viewer",
        };
    }

    private async void ExportDataButton_Click(object? sender, RoutedEventArgs e)
    {
        var export = BuildAnalysisExport();
        if (export.Entries.Count == 0)
        {
            ShowError("出力する系列がありません。");
            return;
        }

        var sp = StorageProvider;
        if (sp is null) return;

        var defaultName = ActiveTable is { } table
            ? Path.GetFileNameWithoutExtension(table.Table.SourceFilePath) ?? "data_viewer"
            : "data_viewer";
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "データを出力",
            SuggestedFileName = $"{defaultName}_export.csv",
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
                new FilePickerFileType("Excelブック") { Patterns = new[] { "*.xlsx" } },
            },
            SuggestedStartLocation = await GetDefaultStartLocationAsync(sp),
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            IAnalysisExporter exporter = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? new XlsxAnalysisExporter()
                : new CsvAnalysisExporter();
            exporter.Export(export, path);
            SetStatus($"データを出力しました: {path} ({export.Entries.Count} 系列)", StatusSeverity.Success);
            Toast?.Show("データを出力しました", StatusSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"データ出力に失敗しました: {ex.Message}");
        }
    }
}

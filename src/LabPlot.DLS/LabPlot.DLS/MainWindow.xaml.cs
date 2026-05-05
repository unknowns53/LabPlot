using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DlsAnalyzer.Core;
using LabPlot.Core;
using LabPlot.Core.Wpf.Helpers;
using Microsoft.Win32;
using ScottPlot.WPF;
using static LabPlot.Core.PlotAppearance;
using static LabPlot.Core.Wpf.FormatHelpers;

namespace LabPlot.DLS;

public partial class MainWindow : Window
{
    // Stable per-dataset overlay palette. Index is the dataset's position in
    // _datasets, so a sheet keeps its colour even as the user toggles the
    // selection on/off in the sidebar.
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
    // Parallel to _datasets: VM that bridges the ListBox (color preview +
    // sheet name) and per-sheet style overrides (color / legend name /
    // line width / marker size). Constructed fresh on every file load so
    // a new file resets all per-sheet state, while sheet selection within
    // a loaded file preserves it (per Batch U3 design).
    private readonly List<DlsDatasetItem> _datasetItems = new();
    private GraphFormattingConfig _formattingConfig = GraphFormattingConfig.CreateFactoryDefault();
    private GraphFormattingConfig _formattingDefaults = GraphFormattingConfig.CreateFactoryDefault();
    private WpfPlot? _plot;
    private DistributionMode _selectedMode = DistributionMode.Number;
    private int _selectedRunIndex;
    // Index into _datasetItems / _datasets that the per-dataset style
    // panel currently edits (last-clicked sheet in a multi-select). -1
    // when nothing is selected, in which case the panel is disabled.
    private int _activeItemIndex = -1;
    private bool _suppressRunComboEvents;
    private bool _suppressFormattingEvents;
    private bool _suppressStyleControlEvents;
    private bool _suppressMetadataControlEvents;

    public MainWindow()
    {
        InitializeComponent();
        LoadFormattingDefaults();
        // Seed the active config with the persisted defaults so the very
        // first plot picks up font / frame / background / line / output
        // path settings the user previously saved.
        _formattingConfig = CloneFormattingConfig(_formattingDefaults);
        RegisterShortcuts();
        Loaded += OnLoaded;
    }

    private void RegisterShortcuts()
    {
        AddShortcut(Key.O, ModifierKeys.Control,
            () => OpenButton_Click(this, new RoutedEventArgs()));
        AddShortcut(Key.S, ModifierKeys.Control,
            () => SaveGraphButton_Click(this, new RoutedEventArgs()));
        AddShortcut(Key.E, ModifierKeys.Control,
            () => ExportButton_Click(this, new RoutedEventArgs()));
        AddShortcut(Key.R, ModifierKeys.Control,
            () => AxisRangePanel.ResetToAuto());
        AddShortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift,
            () => SaveSessionButton_Click(this, new RoutedEventArgs()));
        AddShortcut(Key.O, ModifierKeys.Control | ModifierKeys.Shift,
            () => LoadSessionButton_Click(this, new RoutedEventArgs()));
        AddShortcut(Key.G, ModifierKeys.Control, () => GraphFormatPanel.TogglePlotGrid());
        // Ctrl+L mirrors the GPC / Spectrum Overlay toggle. DLS uses a
        // multi-select ListBox instead of an Overlay checkbox, so the
        // semantically-equivalent action is "select all sheets ⇄ clear
        // selection" — i.e. overlay every loaded sheet vs. show none.
        AddShortcut(Key.L, ModifierKeys.Control, ToggleAllDatasets);
        AddShortcut(Key.F2, ModifierKeys.None, FocusLegendNameTextBox);
    }

    private void ToggleAllDatasets()
    {
        if (DatasetListBox is null || DatasetListBox.Items.Count == 0)
        {
            return;
        }

        if (DatasetListBox.SelectedItems.Count == DatasetListBox.Items.Count)
        {
            DatasetListBox.UnselectAll();
        }
        else
        {
            DatasetListBox.SelectAll();
        }
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

    private void AddShortcut(Key key, ModifierKeys modifiers, Action handler)
    {
        var command = new RoutedUICommand();
        InputBindings.Add(new KeyBinding(command, key, modifiers));
        CommandBindings.Add(new CommandBinding(command, (_, e) =>
        {
            handler();
            e.Handled = true;
        }));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _plot = new WpfPlot();
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_plot);

            // Push the factory-default formatting config into the controls
            // and align the active distribution / run with it. Once session
            // persistence ships in Batch 6 a saved config will replace the
            // factory default before this runs.
            ApplyFormattingConfigToControls(_formattingConfig);
            // Per-dataset style controls start disabled until the user
            // selects a sheet (no file loaded yet → _activeItemIndex = -1).
            SyncStyleControlsFromActiveItem();
            SyncMetadataControlsFromActiveItem();
            SyncCumulantControlsFromActiveItem();
            _selectedMode = DistributionModeFromTag(_formattingConfig.DefaultDistributionMode);
            SelectComboBoxByTag(DistributionTypeComboBox, _formattingConfig.DefaultDistributionMode);
            _selectedRunIndex = Math.Max(0, _formattingConfig.DefaultRunIndex);

            InitializeEmptyPlot();
            // Apply the persisted aspect-ratio default *after* the plot is
            // hosted, so the very first layout pass renders the preview at
            // the right shape instead of waiting for a SizeChanged.
            UpdatePlotHostAspectRatio();
        }
        catch (Exception ex)
        {
            ShowError($"グラフ表示の初期化に失敗しました: {ex.Message}");
        }
    }

    private void InitializeEmptyPlot()
    {
        if (_plot is null) return;
        _plot.Plot.Clear();
        _plot.Plot.Title(GetGraphTitle(PlotTypeLabel(_selectedMode)));
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultXLabel(_selectedMode)));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, ModeLabel(_selectedMode)));
        ApplyLogXTicksForMode(_selectedMode);
        // Pick a sensible default viewport per mode. Correlation g₂-1
        // sits in [0, 1.05] over 0.5–10000 μs typically; particle size
        // 0.3–10000 nm with 0–30% spans the common Zetasizer output.
        if (_selectedMode == DistributionMode.Correlation)
            _plot.Plot.Axes.SetLimits(Math.Log10(0.5), Math.Log10(10000), 0, 1.05);
        else
            _plot.Plot.Axes.SetLimits(Math.Log10(0.3), Math.Log10(10000), 0, 30);
        ApplyPlotAppearance();
        ApplyLegend(0);
        _plot.Refresh();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDatasets.Count == 0)
        {
            ShowError("出力可能なデータがありません。");
            return;
        }

        var defaultName = $"{Path.GetFileNameWithoutExtension(GetCurrentWorkbookHint())}_dls.xlsx";
        var dialog = new SaveFileDialog
        {
            Title = "解析結果を保存",
            Filter = "Excel ファイル (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            FileName = string.IsNullOrWhiteSpace(defaultName) ? "dls_export.xlsx" : defaultName,
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var data = BuildAnalysisExport();
            if (data.Entries.Count == 0)
            {
                ShowError("出力可能なデータがありません。");
                return;
            }

            var format = GetExportFormat(dialog.FileName, dialog.FilterIndex);
            var fileName = EnsureExportExtension(dialog.FileName, format);
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

    private void SaveGraphButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plot is null || _selectedDatasets.Count == 0)
        {
            ShowError("出力可能なデータがありません。");
            return;
        }

        var defaultName = $"{Path.GetFileNameWithoutExtension(GetCurrentWorkbookHint())}_dls";
        var dialog = new SaveFileDialog
        {
            Title = "グラフを保存",
            Filter = "PNG画像 (*.png)|*.png|SVGベクター画像 (*.svg)|*.svg",
            FileName = $"{defaultName}.png",
            DefaultExt = ".png",
            AddExtension = true,
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var saveFormat = GraphSaveHelpers.GetGraphSaveFormat(dialog.FileName, dialog.FilterIndex);
            var fileName = GraphSaveHelpers.EnsureGraphSaveFileExtension(dialog.FileName, saveFormat);
            // Resolution / aspect-ratio is decided by GraphFormatPanel — when
            // the user picked Auto we get the 3600x2160 landscape default
            // (matches GPC / Spectrum), otherwise the panel-selected ratio
            // drives width / height.
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

    private AnalysisExport BuildAnalysisExport()
    {
        var entries = new List<DlsAnalysisExportEntry>();
        var modeName = _selectedMode.ToString();
        var xLabel = DefaultXLabel(_selectedMode);
        var yLabel = ModeLabel(_selectedMode);

        foreach (var dataset in _selectedDatasets)
        {
            var datasetIdx = _datasets.IndexOf(dataset);
            var item = (datasetIdx >= 0 && datasetIdx < _datasetItems.Count)
                ? _datasetItems[datasetIdx]
                : null;

            var series = GetSeries(dataset, _selectedMode);
            var (xs, ys) = ResolveSeriesPoints(series);

            // Cumulant + Stokes-Einstein read from the correlation
            // function regardless of which mode is currently displayed,
            // so the exported summary is complete even when the user
            // was looking at a particle-size mode.
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

    private static ExportFormat GetExportFormat(string filePath, int filterIndex)
    {
        var ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase)) return ExportFormat.Csv;
        if (string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase)) return ExportFormat.Xlsx;
        return filterIndex == 2 ? ExportFormat.Csv : ExportFormat.Xlsx;
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
        // Session save: at least one workbook must be loaded; selection
        // is allowed to be empty (user might want to save an empty
        // overlay state on purpose).
        SaveSessionButton.IsEnabled = _datasetItems.Count > 0
            && !string.IsNullOrEmpty(_currentWorkbookPath);
    }

    private void SaveSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_datasetItems.Count == 0 || string.IsNullOrEmpty(_currentWorkbookPath))
        {
            ShowError("保存する状態がありません。");
            return;
        }

        var defaultName = $"{Path.GetFileNameWithoutExtension(_currentWorkbookPath)}_session.dlsjson";
        var dialog = new SaveFileDialog
        {
            Title = "解析条件を保存",
            Filter = "DLS 解析条件 (*.dlsjson)|*.dlsjson|JSON (*.json)|*.json",
            FileName = defaultName,
            DefaultExt = ".dlsjson",
            AddExtension = true,
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var session = BuildSession();
            new AnalysisSessionStore<DlsAnalysisSession>().Save(session, dialog.FileName);
            HideError();
            SetStatus($"解析条件を保存しました: {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"保存に失敗しました: {ex.Message}");
        }
    }

    private void LoadSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "解析条件を読み込み",
            Filter = "DLS 解析条件 (*.dlsjson;*.json)|*.dlsjson;*.json|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };
        ApplyDefaultOutputDirectoryToDialog(dialog);
        if (dialog.ShowDialog(this) != true) return;

        DlsAnalysisSession session;
        try
        {
            session = new AnalysisSessionStore<DlsAnalysisSession>().Load(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or FileNotFoundException)
        {
            ShowError($"読み込みに失敗しました: {ex.Message}");
            return;
        }

        var warnings = new List<string>();
        ApplySession(session, warnings);

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
                Title = TitleTextBox.Text,
                XLabel = XLabelTextBox.Text,
                YLabel = YLabelTextBox.Text,
            },
            SelectedDistributionMode = _selectedMode.ToString(),
            SelectedRunIndex = _selectedRunIndex,
            ActiveDatasetIndex = _activeItemIndex,
            Overlay = _selectedDatasets.Count > 1,
        };
    }

    private void ApplySession(DlsAnalysisSession session, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(session.WorkbookPath))
        {
            warnings.Add("xlsx ファイルパスが空です");
            return;
        }
        if (!File.Exists(session.WorkbookPath))
        {
            warnings.Add($"xlsx ファイルが見つかりません ({session.WorkbookPath})");
            return;
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
            DatasetCountText.Text =
                $"{_datasets.Count} シート読み込み済み（{Path.GetFileName(session.WorkbookPath)}）";
        }
        catch (Exception ex)
        {
            warnings.Add($"xlsx 再読み込み失敗: {ex.Message}");
            return;
        }

        // Restore per-sheet state by SheetName match. Sheets that were
        // saved but no longer exist in the workbook surface as warnings.
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

        // Restore mode + run before plot refresh so labels / axes match
        // what the user saved.
        _selectedMode = DistributionModeFromTag(session.SelectedDistributionMode);
        SelectComboBoxByTag(DistributionTypeComboBox, session.SelectedDistributionMode);
        _selectedRunIndex = Math.Max(0, session.SelectedRunIndex);

        if (session.Formatting is not null)
        {
            session.Formatting.Normalize();
            // 環境設定はセッションファイルではなくユーザーごとの formatting_config に
            // 属するので、ローカルの defaults を上書きしない。
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

        // Restore selection. Setting SelectedItems triggers the
        // SelectionChanged handler which rebuilds _selectedDatasets,
        // syncs the side panels, and refreshes the plot.
        DatasetListBox.SelectedItems.Clear();
        foreach (var sessionDs in session.Datasets.Where(d => d.Selected))
        {
            var item = _datasetItems.FirstOrDefault(it =>
                string.Equals(it.SheetName, sessionDs.SheetName, StringComparison.Ordinal));
            if (item is not null) DatasetListBox.SelectedItems.Add(item);
        }

        // If no sheet was selected on save (Selected = false everywhere),
        // SelectionChanged did not fire; sync the empty state by hand.
        if (_selectedDatasets.Count == 0)
        {
            _activeItemIndex = -1;
            SyncStyleControlsFromActiveItem();
            SyncMetadataControlsFromActiveItem();
            SyncCumulantControlsFromActiveItem();
            UpdateExportButtonState();
            InitializeEmptyPlot();
        }
    }

    // Best-effort filename hint for the SaveFileDialog default. Empty
    // when no file has been opened (defaults fall back to "dls_export").
    private string? _currentWorkbookPath;

    private string GetCurrentWorkbookHint() => _currentWorkbookPath ?? "dls";

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Zetasizer xlsx を開く",
            Filter = "Excel ファイル (*.xlsx)|*.xlsx|すべてのファイル (*.*)|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var loaded = _reader.Read(dialog.FileName);
            _datasets.Clear();
            foreach (var ds in loaded) _datasets.Add(ds);

            // Rebuild VM list so a fresh file resets every per-sheet style
            // override. Sheet-to-sheet selection within the same file
            // preserves state because we never recreate the VMs there.
            _datasetItems.Clear();
            foreach (var ds in _datasets) _datasetItems.Add(new DlsDatasetItem(ds));

            _currentWorkbookPath = dialog.FileName;
            DatasetListBox.ItemsSource = null;
            DatasetListBox.ItemsSource = _datasetItems;
            DatasetCountText.Text = _datasets.Count == 0
                ? "粒径分布シートが見つかりませんでした"
                : $"{_datasets.Count} シート読み込み済み（{Path.GetFileName(dialog.FileName)}）";

            HideError();
            SetStatus(_datasets.Count == 0
                ? $"粒径分布シートが見つかりませんでした: {dialog.FileName}"
                : $"{_datasets.Count} シートを読み込みました: {dialog.FileName}");

            if (_datasets.Count > 0)
                DatasetListBox.SelectedIndex = 0;
            else
                ClearActiveDatasets();
        }
        catch (Exception ex)
        {
            ShowError($"読み込みに失敗しました: {ex.Message}");
        }
    }

    private void DatasetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        // Snapshot the selection in dataset order (not click order) so the
        // overlay palette stays predictable as the user toggles items.
        _selectedDatasets.Clear();
        foreach (var item in _datasetItems)
        {
            if (DatasetListBox.SelectedItems.Contains(item))
                _selectedDatasets.Add(item.Dataset);
        }

        // Active item drives the per-dataset style panel: prefer the most
        // recently added selection (last click in a multi-select), fall
        // back to the last item still selected when the user only removed
        // items, and -1 when nothing is selected.
        DlsDatasetItem? activeItem = null;
        foreach (var added in e.AddedItems)
        {
            if (added is DlsDatasetItem item) activeItem = item;
        }
        if (activeItem is null && DatasetListBox.SelectedItems.Count > 0)
        {
            activeItem = DatasetListBox.SelectedItems[DatasetListBox.SelectedItems.Count - 1] as DlsDatasetItem;
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

    // Pushes the active sheet's per-dataset style values into the panel
    // controls. Suppresses change events so writing the values back does
    // not retrigger RefreshPlot — that path is owned by the per-control
    // *_Changed handlers instead.
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
            // Match the GPC pattern: DefaultHex tracks the active palette
            // index so the "Auto" preset preview reflects the actual color
            // ScottPlot will render for this sheet.
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

    private void LegendNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressStyleControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var legendName = LegendNameTextBox.Text.Trim();
        _datasetItems[_activeItemIndex].Style.LegendName =
            string.IsNullOrWhiteSpace(legendName) ? null : legendName;
        RefreshPlot();
    }

    private void LineWidthTextBox_TextChanged(object sender, TextChangedEventArgs e)
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

    private void MarkerSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
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

    // Pushes the active sheet's measurement metadata into the panel
    // controls. Mirrors SyncStyleControlsFromActiveItem but for the
    // "測定条件 (選択中シート)" section. Suppresses change events so
    // writing values back does not retrigger the per-control LostFocus
    // commit path.
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
            finally
            {
                _suppressMetadataControlEvents = false;
            }
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
        finally
        {
            _suppressMetadataControlEvents = false;
        }
    }

    // Pushes the active sheet's cumulant fit settings into the panel
    // textboxes and recomputes the analysis display. Suppresses change
    // events while writing so the LostFocus commit path is not
    // retriggered.
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
            finally
            {
                _suppressMetadataControlEvents = false;
            }
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
        finally
        {
            _suppressMetadataControlEvents = false;
        }

        UpdateCumulantDisplay();
    }

    // Tier-2 display logic. Runs the cumulant fit on the active sheet
    // and pushes the four numeric fields + Stokes-Einstein diameter
    // into the panel TextBlocks. Status text shows:
    //   • no active sheet → all dashes
    //   • no Correlation data → "自己相関データがありません"
    //   • fit failure → reason from CumulantOutcome.FailureReason
    //   • fit OK / metadata incomplete → numeric fields filled, Z-average
    //     shows "—" with reason "X が未入力で計算不可"
    //   • fit OK / metadata complete → all fields filled
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
            System.Globalization.CultureInfo.InvariantCulture);
        CumulantRSquaredText.Text = result.RSquared.ToString("0.0000",
            System.Globalization.CultureInfo.InvariantCulture);
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
                $"{sizeOutcome.HydrodynamicDiameterNm.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} nm";
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
        CumulantStatusText.Visibility = Visibility.Visible;
    }

    private void HideCumulantStatus()
    {
        CumulantStatusText.Text = string.Empty;
        CumulantStatusText.Visibility = Visibility.Collapsed;
    }

    // Compact scientific format used for Γ which spans a wide range.
    // 0.005 → "5.00e-3", 0.002345 → "2.35e-3".
    private static string FormatScientific(double value)
    {
        if (!double.IsFinite(value)) return "—";
        return value.ToString("0.###e+0", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Shared Enter-to-commit for all metadata textboxes. The textboxes
    // commit on LostFocus, so Enter just bounces focus to the window to
    // fire that path. Moving focus to a sibling control would scroll the
    // sidebar — bouncing to the window keeps the layout stable.
    private void MetadataTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, this);
        e.Handled = true;
    }

    private void MetadataTemperatureTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Temperature accepts negatives (rare DLS sub-zero protocols).
        CommitNumericMetadata(MetadataTemperatureTextBox, NumericConstraint.AnyFinite,
            (item, value) => item.Metadata.TemperatureCelsius = value);
    }

    private void MetadataConcentrationTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Concentration of zero is meaningful (pure solvent baseline run).
        CommitNumericMetadata(MetadataConcentrationTextBox, NumericConstraint.NonNegative,
            (item, value) => item.Metadata.ConcentrationMgPerMl = value);
    }

    private void MetadataRefractiveIndexTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Refractive index n > 0; air ~1.0, water ~1.33, organic solvents up to ~1.6.
        CommitNumericMetadata(MetadataRefractiveIndexTextBox, NumericConstraint.Positive,
            (item, value) => item.Metadata.RefractiveIndex = value);
    }

    private void MetadataViscosityTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Dynamic viscosity > 0; water ~0.89 mPa·s at 25°C.
        CommitNumericMetadata(MetadataViscosityTextBox, NumericConstraint.Positive,
            (item, value) => item.Metadata.ViscosityMpas = value);
    }

    private void MetadataWavelengthTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Laser wavelength > 0; Zetasizer red 633 nm by default.
        CommitNumericMetadata(MetadataWavelengthTextBox, NumericConstraint.Positive,
            (item, value) => item.Metadata.WavelengthNm = value);
    }

    private void MetadataScatteringAngleTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Detector angle in (0, 360); Zetasizer backscatter 173° by default.
        CommitNumericMetadata(MetadataScatteringAngleTextBox, NumericConstraint.Positive,
            (item, value) => item.Metadata.ScatteringAngleDegrees = value);
    }

    private void CumulantFitRangeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressMetadataControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var item = _datasetItems[_activeItemIndex];

        // Empty → auto on that side. Non-positive / non-finite input is
        // rejected by reverting the textbox to the last committed value
        // (SyncCumulantControlsFromActiveItem rewrites both textboxes
        // from item.Cumulant). We commit only the valid sides.
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

    // True means the textbox commits cleanly (empty → null, or a valid
    // positive number → that number). False means rejection — caller
    // re-syncs from the model so the textbox snaps back.
    private bool TryCommitCumulantBound(TextBox textBox, Action<double?> apply)
    {
        var raw = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            apply(null);
            return true;
        }
        if (!TryParsePositiveDouble(raw, out var value))
            return false;

        apply(value);
        _suppressMetadataControlEvents = true;
        try { textBox.Text = FormatDouble(value); }
        finally { _suppressMetadataControlEvents = false; }
        return true;
    }

    private void MetadataSolventTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressMetadataControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var solvent = MetadataSolventTextBox.Text.Trim();
        _datasetItems[_activeItemIndex].Metadata.Solvent =
            string.IsNullOrWhiteSpace(solvent) ? null : solvent;
        // Solvent itself does not feed Stokes-Einstein (η / n do that
        // job), but keeping the recompute call here avoids a stale
        // status banner if the user types into Solvent after fixing
        // numeric fields.
        UpdateCumulantDisplay();
    }

    // Constraint check passed to CommitNumericMetadata. `null` means any
    // finite double is accepted; otherwise the constraint rejects values
    // outside its domain (e.g. NonNegative rejects negative numbers).
    private enum NumericConstraint
    {
        AnyFinite,
        NonNegative,
        Positive,
    }

    // Shared parse + commit flow for numeric metadata. Empty input clears
    // the value (back to null). Invalid or out-of-range input reverts the
    // textbox to the last committed value. Stokes-Einstein calculations
    // (Batch 5) read from item.Metadata, so Batch 4a does not refresh
    // the plot on commit.
    private void CommitNumericMetadata(
        TextBox textBox,
        NumericConstraint constraint,
        Action<DlsDatasetItem, double?> apply)
    {
        if (!IsInitialized) return;
        if (_suppressMetadataControlEvents) return;
        if (_activeItemIndex < 0 || _activeItemIndex >= _datasetItems.Count) return;

        var item = _datasetItems[_activeItemIndex];
        var raw = textBox.Text.Trim();

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
            // Revert to the last committed value so the textbox does not
            // hold input the model cannot represent.
            SyncMetadataControlsFromActiveItem();
            return;
        }

        // We just verified the input parses; one extra TryParseDouble call
        // unifies the value extraction across all three constraints.
        TryParseDouble(raw, out var value);
        apply(item, value);

        // Normalize the visible text (e.g. "1.20" → "1.2").
        _suppressMetadataControlEvents = true;
        try { textBox.Text = FormatDouble(value); }
        finally { _suppressMetadataControlEvents = false; }

        // Any numeric metadata change can move the Stokes-Einstein
        // result, so refresh the cumulant panel after every commit.
        UpdateCumulantDisplay();
    }

    private static string FormatNullableDouble(double? value)
        => value.HasValue ? FormatDouble(value.Value) : string.Empty;

    private void DistributionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged fires once during InitializeComponent because
        // ComboBoxItem.IsSelected="True" is applied while RunComboBox has
        // not been parsed yet. Skip until the XAML tree is fully built.
        if (!IsInitialized) return;
        if (DistributionTypeComboBox.SelectedItem is not ComboBoxItem item) return;
        _selectedMode = DistributionModeFromTag(item.Tag as string);
        UpdateRunCombo();
        RefreshPlot();
    }

    private void RunComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressRunComboEvents) return;
        _selectedRunIndex = Math.Max(0, RunComboBox.SelectedIndex);
        RefreshPlot();
    }

    private void FormatTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    private void FormatCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_suppressFormattingEvents) return;
        _formattingConfig = CaptureFormattingConfigFromControls();
        RefreshPlot();
    }

    // Title / X / Y label TextBoxes do not feed into _formattingConfig: the
    // values are read directly by GetGraphTitle / GetGraphLabel at plot
    // time (matching the GPC / Spectrum convention).
    private void GraphLabelTextBox_TextChanged(object sender, TextChangedEventArgs e)
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
        // Aspect ratio is part of the shared formatting config (Capture
        // reads AspectRatio from the panel), so refresh it before
        // resizing the host so saved sessions / defaults round-trip.
        if (!_suppressFormattingEvents)
        {
            _formattingConfig = CaptureFormattingConfigFromControls();
        }
        UpdatePlotHostAspectRatio();
    }

    private void PlotContainerBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlotHostAspectRatio();
    }

    /// <summary>
    /// Resize PlotHost to the GraphFormatPanel-selected aspect ratio so
    /// the on-screen preview matches the exported image. Auto leaves the
    /// host stretched to fill PlotContainerBorder; an explicit ratio
    /// shrinks the host to a centered fixed-aspect rectangle, mirroring
    /// the GPC / Spectrum behaviour.
    /// </summary>
    private void UpdatePlotHostAspectRatio()
        => PlotHostAspectRatio.Apply(PlotHost, PlotContainerBorder, GraphFormatPanel.AspectRatioValue);

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
        // Enable a mode if at least one selected dataset has data for it;
        // datasets lacking that mode are silently skipped during overlay
        // draw. Correlation participates in the same availability check
        // because GetSeries returns null when dataset.Correlation is null.
        for (int i = 0; i < DistributionTypeComboBox.Items.Count; i++)
        {
            if (DistributionTypeComboBox.Items[i] is not ComboBoxItem item) continue;
            var mode = DistributionModeFromTag(item.Tag as string);
            item.IsEnabled = _selectedDatasets.Count == 0
                || _selectedDatasets.Any(ds => GetSeries(ds, mode) is not null);
        }
    }

    private void UpdateRunCombo()
    {
        // Run picker only makes sense for a single-dataset selection. With
        // multiple datasets each one keeps its own ActiveRunIndex (default 0)
        // since runs are not aligned across measurements.
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
        finally
        {
            _suppressRunComboEvents = false;
        }
    }

    private void RefreshPlot()
    {
        if (_plot is null) return;

        if (_selectedDatasets.Count == 0)
        {
            InitializeEmptyPlot();
            return;
        }

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

            // Both modes share log10 X spacing: particle size in nm and
            // correlation delay in μs. Negative / zero raw values can
            // appear in noisy correlation tails, so clamp to a small
            // positive epsilon before taking the log.
            var xs = new double[n];
            var ys = new double[n];
            for (int p = 0; p < n; p++)
            {
                xs[p] = Math.Log10(Math.Max(rawXs[p], 1e-6));
                ys[p] = run[p];
            }

            // Per-sheet style takes precedence over the global default.
            // Independence from _formattingConfig.LineWidth / MarkerSize
            // matches the GPC convention (the "line style" panel edits
            // per-sheet values; the global default only seeds new sheets).
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

        // Sync the ListBox color swatches with whatever the plot just
        // rendered. Done unconditionally so unselected rows still reflect
        // their assigned palette colour.
        for (int i = 0; i < _datasetItems.Count; i++)
        {
            _datasetItems[i].ColorBrush = ResolveDatasetBrush(i);
        }

        if (seriesCount == 0)
        {
            // All selected datasets lack the chosen mode. Render an empty
            // labelled plot so the user notices the mode mismatch.
            _plot.Plot.Title(GetGraphTitle($"{ModeLabel(_selectedMode)} データなし"));
            _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultXLabel(_selectedMode)));
            _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, ModeLabel(_selectedMode)));
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
        _plot.Plot.XLabel(GetGraphLabel(XLabelTextBox, DefaultXLabel(_selectedMode)));
        _plot.Plot.YLabel(GetGraphLabel(YLabelTextBox, ModeLabel(_selectedMode)));
        ApplyLogXTicksForMode(_selectedMode);
        _plot.Plot.Axes.AutoScale();
        ApplyPlotAppearance();
        ApplyLegend(seriesCount);
        _plot.Refresh();
    }

    private string BuildTitle()
    {
        if (_selectedDatasets.Count == 1)
        {
            var dataset = _selectedDatasets[0];
            var series = GetSeries(dataset, _selectedMode);
            var runLabel = series is { RunCount: > 1 }
                ? $", Run {Math.Clamp(_selectedRunIndex, 0, series.RunCount - 1) + 1}"
                : string.Empty;
            return $"{dataset.SheetName} ({ModeLabel(_selectedMode)}{runLabel})";
        }

        return $"{PlotTypeLabel(_selectedMode)} ({ModeLabel(_selectedMode)}, {_selectedDatasets.Count} datasets)";
    }

    private void ApplyDatasetColor(ScottPlot.Plottables.Scatter scatter, DlsDataset dataset)
    {
        // Three-tier fallback (per-sheet override → global default → palette).
        // The per-sheet override comes from the per-dataset style panel and
        // takes precedence so the user can paint individual sheets without
        // changing the global default. Auto keeps the stable palette indexed
        // off _datasets so each sheet's swatch is consistent across renders.
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

    // Mirrors ApplyDatasetColor's resolution but returns the hex / Media
    // brush directly so DatasetListBox row swatches stay in sync with the
    // plot. Called from RefreshPlot for every loaded sheet (selected or
    // not) so unselected rows still show their assigned palette colour.
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
        return new SolidColorBrush(HexToMediaColor(hex));
    }

    // Decade range covered by the log10-spaced bottom-axis tick generator.
    // Particle-size distributions span 0.1 nm – 10000 nm (Zetasizer's
    // 70-bin built-in grid). Correlation Time (μs) can run from ≪1 μs
    // out to ~10^7 μs (10 s) for very slow decays, so its tick range is
    // wider; ScottPlot only renders the ticks that fall inside the
    // visible axis window so leaving extras defined is harmless.
    private const int SizeAxisMinExponent = -1;
    private const int SizeAxisMaxExponent = 4;
    private const int CorrelationAxisMinExponent = -1;
    private const int CorrelationAxisMaxExponent = 7;

    private void ApplyLogXTicks(int minExponent, int maxExponent)
    {
        if (_plot is null) return;

        // Render the X axis in log10 space with major ticks at 10^k and
        // minor ticks at 2..9 × 10^k per decade (matches the GPC
        // molecular-weight axis approach).
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

    // Mode-aware shortcut so RefreshPlot / InitializeEmptyPlot do not
    // need to remember the per-mode exponent constants.
    private void ApplyLogXTicksForMode(DistributionMode mode)
    {
        if (mode == DistributionMode.Correlation)
            ApplyLogXTicks(CorrelationAxisMinExponent, CorrelationAxisMaxExponent);
        else
            ApplyLogXTicks(SizeAxisMinExponent, SizeAxisMaxExponent);
    }

    /// <summary>
    /// Scale up font / line / marker / frame metrics ahead of a high-DPI
    /// PNG / SVG export so 3600x2160 / 300 dpi output keeps a usable
    /// font:plot ratio (matches GPC / Spectrum behaviour). Pair with a
    /// follow-up call passing scale=1f and a Refresh() to restore the
    /// on-screen preview.
    /// </summary>
    private void ApplyExportStyleScale(float scale)
    {
        if (_plot is null) return;
        ApplyPlotAppearance(scale);
        ApplyExistingSeriesStyles(scale);
    }

    private void ApplyExistingSeriesStyles(float scale)
    {
        if (_plot is null) return;

        // Match scatters back to datasets by mirroring the iteration in
        // RefreshPlot: empty / null series are skipped there, so they
        // don't consume a scatter slot here either.
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

        // Manual axis range overrides only apply to the particle-size
        // modes: the panel labels its fields in nm / % which do not match
        // Correlation Time (μs) / g₂-1. Falling back to AutoScale for
        // Correlation keeps the user from hitting confused units. The
        // panel itself stays editable so a value the user typed for
        // particle-size mode is preserved when they switch back.
        if (_selectedMode == DistributionMode.Correlation) return;

        // X is in log10(d.nm) space so we translate the configured nm
        // endpoints through Log10 once before applying.
        if (_formattingConfig.XAxisMode == "Manual")
        {
            var xMinLog = Math.Log10(Math.Max(_formattingConfig.XAxisMinNm, 1e-6));
            var xMaxLog = Math.Log10(Math.Max(_formattingConfig.XAxisMaxNm, 1e-6));
            plot.Axes.SetLimitsX(xMinLog, xMaxLog);
        }
        if (_formattingConfig.YAxisMode == "Manual")
        {
            plot.Axes.SetLimitsY(_formattingConfig.YAxisMinPercent, _formattingConfig.YAxisMaxPercent);
        }
    }

    private void ApplyLegend(int seriesCount)
    {
        if (_plot is null) return;

        // Auto-show: 2+ overlaid datasets OR any selected sheet has a
        // custom legend name (so a single rename still surfaces). Matches
        // the GPC / Spectrum ShouldShowLegend rule. seriesCount == 0
        // forces autoShow false so an empty plot never shows an empty
        // legend frame in Auto mode.
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

    private GraphFormattingConfig CaptureFormattingConfigFromControls()
    {
        var config = new GraphFormattingConfig();
        // Pull all GraphFormattingConfigBase properties (font / ticks / frame /
        // background / aspect ratio / legend) from the shared panel, then layer
        // DLS-specific properties on top.
        GraphFormatPanel.Capture(config);

        // Title / axis label visibility lives in the standalone "グラフラベル"
        // section, not in GraphFormatPanel.
        config.ShowTitle = TitleVisibleCheckBox.IsChecked == true;
        config.TitleBold = TitleBoldCheckBox.IsChecked == true;
        config.AxisLabelBold = AxisLabelBoldCheckBox.IsChecked == true;

        // Per-sheet line style controls live in their own panel and mutate
        // _datasetItems[i].Style directly, so capture preserves whatever
        // default seeded the file load (factory defaults at first; future
        // Phase 4 Batch 6 session loads will replace these via
        // _formattingConfig assignment).
        config.DefaultLineColorHex = _formattingConfig.DefaultLineColorHex;
        config.LineWidth = _formattingConfig.LineWidth;
        config.MarkerSize = _formattingConfig.MarkerSize;

        // The default output directory now lives in the 環境設定 expander
        // as an editable text box (with a 参照 picker), so reflect what
        // the user typed instead of the previous in-memory value.
        var outputDir = DefaultOutputDirectoryTextBox.Text?.Trim();
        config.DefaultOutputDirectory = string.IsNullOrWhiteSpace(outputDir) ? null : outputDir;

        // Axis range: empty textboxes mean "Auto" (let ScottPlot auto-scale).
        // Both endpoints must be filled for the axis to flip into "Manual".
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

    /// <summary>
    /// セッション保存用の formatting を構築する。環境設定（出力フォルダ）はユーザー
    /// ごとの formatting_config.json 側で管理するため、ここでは null クリアする。
    /// </summary>
    private GraphFormattingConfig BuildSessionFormatting()
    {
        var formatting = CloneFormattingConfig(_formattingConfig);
        formatting.DefaultOutputDirectory = null;
        return formatting;
    }

    private string? GetDefaultOutputDirectoryIfExists()
        => FormattingDefaultsStore.GetExistingDefaultOutputDirectory(_formattingDefaults);

    private void ApplyDefaultOutputDirectoryToDialog(Microsoft.Win32.FileDialog dialog)
        => FormattingDefaultsStore.ApplyDefaultOutputDirectoryToDialog(dialog, _formattingDefaults);

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

    private void ResetGraphSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        TitleTextBox.Clear();
        XLabelTextBox.Clear();
        YLabelTextBox.Clear();
        AxisRangePanel.SetXValues(null, null);
        AxisRangePanel.SetYValues(null, null);
        ApplyFormattingConfigToControls(_formattingDefaults);

        // Per-sheet styles are seeded from the saved defaults too, so a
        // user who reset gets the same colors / line width as a fresh
        // launch instead of hanging on to ad-hoc overrides.
        foreach (var item in _datasetItems)
        {
            ApplyDefaultDatasetStyle(item);
        }

        SyncStyleControlsFromActiveItem();
        _formattingConfig = CaptureFormattingConfigFromControls();
        UpdatePlotHostAspectRatio();
        RefreshPlot();
    }

    private void SaveDefaultFormattingButton_Click(object sender, RoutedEventArgs e)
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

        // GraphFormatPanel suppresses its own change events while writing.
        GraphFormatPanel.Apply(config);

        _suppressFormattingEvents = true;
        try
        {
            TitleVisibleCheckBox.IsChecked = config.ShowTitle;
            TitleBoldCheckBox.IsChecked = config.TitleBold;
            AxisLabelBoldCheckBox.IsChecked = config.AxisLabelBold;
            // Per-sheet line style controls are driven by
            // SyncStyleControlsFromActiveItem (selection change), not by
            // the global formatting config — that decoupling is what lets
            // each sheet keep its own state.

            // "Manual" mode: write the saved values back into the panel.
            // "Auto" mode: leave the textboxes empty so the plot auto-scales.
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
        finally
        {
            _suppressFormattingEvents = false;
        }
    }

    // ---------- Generic helpers ----------

    private static DistributionMode DistributionModeFromTag(string? tag) => tag switch
    {
        "Intensity" => DistributionMode.Intensity,
        "Volume" => DistributionMode.Volume,
        "Correlation" => DistributionMode.Correlation,
        _ => DistributionMode.Number,
    };

    // Unified data accessor: returns null if the dataset has no data for
    // the requested mode (e.g. Correlation requested but the sheet only
    // carried Number distribution).
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

    private static string ModeLabel(DistributionMode mode) => mode switch
    {
        DistributionMode.Intensity => "Intensity (%)",
        DistributionMode.Volume => "Volume (%)",
        DistributionMode.Correlation => "g₂-1",
        _ => "Number (%)",
    };

    // Title prefix when more than one dataset is overlaid (or no dataset
    // is selected at all). Mirrors the ScottPlot label convention used
    // by GPC / Spectrum.
    private static string PlotTypeLabel(DistributionMode mode) => mode switch
    {
        DistributionMode.Correlation => "Correlation Function",
        _ => "Particle Size Distribution",
    };

    // Default X-axis label when the user has not typed an override into
    // XLabelTextBox. The size axis stays in nm (logarithmic), the
    // correlation axis is delay time in microseconds (also logarithmic).
    private static string DefaultXLabel(DistributionMode mode) => mode switch
    {
        DistributionMode.Correlation => "Time (μs)",
        _ => "Size (d.nm)",
    };

    // Per-sheet style overrides. ColorHex / LegendName are nullable so the
    // empty case falls back to the auto palette / SheetName. LineWidth /
    // MarkerSize default to the shared GraphFormattingConfigBase values so
    // a freshly loaded file paints with the global defaults until the
    // user touches the per-sheet panel.
    private sealed class DlsDatasetStyle
    {
        public string? ColorHex { get; set; }
        public string? LegendName { get; set; }
        public double LineWidth { get; set; } = GraphFormattingConfigBase.DefaultLineWidth;
        public double MarkerSize { get; set; } = GraphFormattingConfigBase.DefaultMarkerSize;
    }

    // Mutable metadata state edited from the "測定条件 (選択中シート)" panel.
    // Mirrors DlsDatasetMetadata (which is an immutable record on the
    // DlsAnalyzer.Core side) so the UI can write back without rebuilding
    // the dataset. Stokes-Einstein calculations (Batch 5) and session
    // save (Batch 6) read from here, not from Dataset.Metadata, because
    // the user enters these values after the file is loaded.
    //
    // WavelengthNm and ScatteringAngleDegrees default to the Zetasizer
    // standard optics (633 nm red laser, 173° backscatter) but stay
    // editable so the same UI handles other instruments.
    private sealed class DlsDatasetMetadataState
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

    // Per-sheet cumulant fit settings. Both null = auto-detect range
    // (drop noise tail at g₂-1 < threshold). Either / both filled →
    // honour as the τ window for the next analysis.
    private sealed class DlsDatasetCumulantSettings
    {
        public double? FitRangeMinMicroseconds { get; set; }
        public double? FitRangeMaxMicroseconds { get; set; }
    }

    // ListBox row VM. Holds the underlying DlsDataset, the per-sheet
    // style, the per-sheet measurement metadata, and a notifying
    // ColorBrush so the color swatch updates as soon as RefreshPlot()
    // recomputes the palette / overrides.
    private sealed class DlsDatasetItem : INotifyPropertyChanged
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
            // Seed the editable metadata from whatever the reader produced.
            // Zetasizer xlsx never embeds these fields, so in practice all
            // values start out null and the user fills them in by hand.
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

    // GPC / Spectrum convention: empty TextBox falls back to the dataset-
    // derived default. Matches GetGraphTitle / GetGraphLabel in the other
    // two apps so the GPC-basis label panel works identically here.
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

    private void ShowError(string message)
    {
        ErrorBanner.Show(message);
        SetStatus(message, isError: true);
    }

    private void HideError()
    {
        ErrorBanner.Hide();
    }

    // Bottom status bar matches the GPC / Spectrum convention. Errors get
    // a red foreground; informational messages stay in slate to keep the
    // banner-style ErrorBanner as the dominant signal for hard failures.
    // A non-error status also implicitly clears any leftover banner so
    // success / progress messages cancel out the previous failure
    // without the caller having to remember HideError().
    private void SetStatus(string message, bool isError = false)
    {
        if (StatusTextBlock is null)
        {
            return;
        }
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C))
            : new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
        if (!isError)
        {
            ErrorBanner.Hide();
        }
    }

    private enum DistributionMode
    {
        Number,
        Intensity,
        Volume,
        // Intensity autocorrelation function g₂-1 vs delay time (μs).
        // Reads from DlsDataset.Correlation rather than the three particle-
        // size distributions; treated as a fourth mode of the same
        // DistributionTypeComboBox so overlay / run-switch / per-sheet
        // styling all work uniformly.
        Correlation,
    }

    // Unified view over the per-mode data access. ParticleSize* modes pull
    // from one of the three ParticleSizeDistribution slots; Correlation
    // pulls from CorrelationFunction. Wrapping both in DataSeries means
    // every consumer (UpdateRunCombo / RefreshPlot / availability check)
    // can share a single null check + run/x access path.
    private sealed record DataSeries(
        IReadOnlyList<double> Xs,
        IReadOnlyList<IReadOnlyList<double>> Runs,
        int ActiveRunIndex)
    {
        public int RunCount => Runs.Count;
    }
}

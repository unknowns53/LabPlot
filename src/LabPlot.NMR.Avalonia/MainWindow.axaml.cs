using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LabPlot.Core;
using LabPlot.Core.Avalonia;
using LabPlot.Core.Avalonia.Controls;
using LabPlot.Core.Avalonia.Helpers;
using NMRAnalyzer.Core;
using ScottPlot.Avalonia;

namespace LabPlot.NMR.Avalonia;

public partial class MainWindow : Window, IPortalFileOpener
{
    // Window-state persistence key: %APPDATA%/LabPlot/window-nmr.json.
    private const string AppKey = "nmr";

    // Round-robin palette for datasets without an explicit colour (matches
    // the other modules' AutoLineColors).
    private static readonly string[] AutoLineColors =
    {
        "#2563EB", "#DC2626", "#16A34A", "#EA580C",
        "#7C3AED", "#0891B2", "#4B5563",
    };

    private readonly JdfReader _reader = new();
    private readonly List<NmrDataset> _loadedDatasets = new();
    private readonly List<DatasetStyle> _datasetStyles = new();
    private readonly ObservableCollection<DatasetEntryVm> _datasetEntries = new();

    private AvaPlot? _plot;
    private int _activeIndex = -1;
    private bool _suppressDatasetListEvents;
    private bool _suppressStyleControlEvents;

    public MainWindow()
    {
        // Avalonia.Generators emits InitializeComponent + the x:Name fields, so
        // no manual definition here (a hand-written one re-nulls the generated
        // fields and NREs — the trap the other modules note).
        InitializeComponent();
        DatasetListBox.ItemsSource = _datasetEntries;
        Opened += OnOpenedInitializePlot;

        PlotContainerBorder.AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        PlotContainerBorder.AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowStateStore.ApplyTo(this, AppKey);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        WindowStateStore.PersistFrom(this, AppKey);
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            KeyboardShortcutsWindow.ShowFor(this, AppKind.Nmr);
            e.Handled = true;
            return;
        }

        if (e.HasCommandModifier() && e.Key == Key.O)
        {
            _ = OpenFileAsync();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // Loaded-equivalent: build the ScottPlot control once the visual tree is up.
    private void OnOpenedInitializePlot(object? sender, EventArgs e)
    {
        _plot = new AvaPlot();
        PlotHost.Children.Clear();
        PlotHost.Children.Add(_plot);
        InitializeEmptyPlot();
    }

    private void InitializeEmptyPlot()
    {
        if (_plot is null)
        {
            return;
        }

        _plot.Plot.Clear();
        _plot.Plot.XLabel("Chemical shift (ppm)");
        _plot.Plot.YLabel("Intensity");
        _plot.Refresh();
    }

    // ---------------------------------------------------------------- file open

    private void OpenButton_Click(object? sender, RoutedEventArgs e) => _ = OpenFileAsync();

    private async Task OpenFileAsync()
    {
        var sp = StorageProvider;
        if (sp is null)
        {
            return;
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "JEOL .jdf を開く",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JEOL NMR データ") { Patterns = new[] { "*.jdf" } },
                FilePickerFileTypes.All,
            },
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToArray();
        if (paths.Length > 0)
        {
            LoadFiles(paths);
        }
    }

    private void LoadFiles(IReadOnlyList<string> paths)
    {
        var loaded = 0;
        string? lastError = null;
        foreach (var path in paths)
        {
            try
            {
                AddLoadedDataset(_reader.Read(path));
                loaded++;
            }
            catch (Exception ex)
            {
                lastError = $"{Path.GetFileName(path)}: {ex.Message}";
            }
        }

        PlotDatasets();

        if (lastError is not null)
        {
            Toast?.Show($"読み込みに失敗しました ({lastError})", StatusSeverity.Error, 5000);
        }

        UpdateStatus(loaded > 0 ? null : "読み込みに失敗しました。");
    }

    private void UpdateStatus(string? overrideMessage)
    {
        if (overrideMessage is not null)
        {
            StatusText.Text = overrideMessage;
            return;
        }

        if (_loadedDatasets.Count == 0)
        {
            StatusText.Text = "JEOL .jdf ファイルを開いてください。";
            return;
        }

        if (_loadedDatasets.Count == 1)
        {
            var d = _loadedDatasets[0];
            var name = d.Title is { Length: > 0 } title ? title : NameOf(d);
            StatusText.Text = $"{name}  ({d.RealValues.Count} 点)";
            return;
        }

        StatusText.Text = $"{_loadedDatasets.Count} 件のスペクトル";
    }

    // ------------------------------------------------------------- dataset list

    private void AddLoadedDataset(NmrDataset dataset)
    {
        var overlay = OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0;
        if (!overlay)
        {
            _loadedDatasets.Clear();
            _datasetStyles.Clear();
        }

        _loadedDatasets.Add(dataset);
        _datasetStyles.Add(new DatasetStyle());
        _activeIndex = _loadedDatasets.Count - 1;

        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();
    }

    private void RemoveDatasetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DatasetEntryVm vm })
        {
            return;
        }

        var index = _datasetEntries.IndexOf(vm);
        if (index < 0 || index >= _loadedDatasets.Count)
        {
            return;
        }

        _loadedDatasets.RemoveAt(index);
        if (index < _datasetStyles.Count)
        {
            _datasetStyles.RemoveAt(index);
        }

        if (_loadedDatasets.Count == 0)
        {
            _activeIndex = -1;
            RefreshDatasetEntries();
            SyncStyleControlsFromActiveDataset();
            InitializeEmptyPlot();
            UpdateStatus(null);
            return;
        }

        _activeIndex = Math.Clamp(_activeIndex >= index ? _activeIndex - 1 : _activeIndex, 0, _loadedDatasets.Count - 1);
        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();
        PlotDatasets();
        UpdateStatus(null);
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
                    : NameOf(dataset, i);

                _datasetEntries.Add(new DatasetEntryVm
                {
                    DisplayName = displayName,
                    FullPath = dataset.SourceFilePath ?? string.Empty,
                    ColorBrush = new SolidColorBrush(Color.Parse(hex)),
                });
            }

            DatasetListPlaceholder.IsVisible = _datasetEntries.Count == 0;
            DatasetListBox.SelectedIndex = _activeIndex >= 0 && _activeIndex < _datasetEntries.Count ? _activeIndex : -1;
        }
        finally
        {
            _suppressDatasetListEvents = false;
        }
    }

    private void DatasetListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDatasetListEvents)
        {
            return;
        }

        var index = DatasetListBox.SelectedIndex;
        if (index < 0 || index >= _loadedDatasets.Count)
        {
            return;
        }

        _activeIndex = index;
        SyncStyleControlsFromActiveDataset();
        PlotDatasets();
    }

    private void OverlayCheckBox_Changed(object? sender, RoutedEventArgs e) => PlotDatasets();

    // ------------------------------------------------------------- style editing

    private void SyncStyleControlsFromActiveDataset()
    {
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            ActiveDatasetLabel.Text = "(選択中データセット)";
            return;
        }

        var dataset = _loadedDatasets[_activeIndex];
        var style = _datasetStyles[_activeIndex];
        ActiveDatasetLabel.Text = $"({NameOf(dataset, _activeIndex)})";

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

    private void LineColorPicker_ColorChanged(object? sender, EventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }

        ApplyDatasetStyle(style => style.ColorHex = LineColorPicker.HexValue);
        RefreshDatasetEntries();
        PlotDatasets();
    }

    private void LegendNameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }

        DatasetStyleCommit.CommitLegendName(LegendNameTextBox, value => ApplyDatasetStyle(style => style.LegendName = value));
        RefreshDatasetEntries();
        PlotDatasets();
    }

    private void LineWidthTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }

        if (DatasetStyleCommit.TryCommitPositiveDouble(LineWidthTextBox, value => ApplyDatasetStyle(style => style.LineWidth = value)))
        {
            PlotDatasets();
        }
    }

    private void MarkerSizeTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressStyleControlEvents)
        {
            return;
        }

        if (DatasetStyleCommit.TryCommitNonNegativeDouble(MarkerSizeTextBox, value => ApplyDatasetStyle(style => style.MarkerSize = value)))
        {
            PlotDatasets();
        }
    }

    private void ApplyDatasetStyle(Action<DatasetStyle> mutate)
    {
        if (_activeIndex < 0 || _activeIndex >= _datasetStyles.Count)
        {
            return;
        }

        mutate(_datasetStyles[_activeIndex]);
    }

    // ------------------------------------------------------------------- plotting

    private void PlotDatasets()
    {
        if (_plot is null)
        {
            return;
        }

        var entries = GetDatasetsToPlotWithIndices();
        _plot.Plot.Clear();
        if (entries.Length == 0)
        {
            InitializeEmptyPlot();
            return;
        }

        var xMin = double.PositiveInfinity;
        var xMax = double.NegativeInfinity;
        foreach (var (dataset, index) in entries)
        {
            var scatter = _plot.Plot.Add.Scatter(dataset.XValues, dataset.YValues);
            scatter.LegendText = GetSeriesLegendText(dataset, index);
            ApplySeriesStyle(scatter, index);

            xMin = Math.Min(xMin, Math.Min(dataset.XValues[0], dataset.XValues[^1]));
            xMax = Math.Max(xMax, Math.Max(dataset.XValues[0], dataset.XValues[^1]));
        }

        _plot.Plot.XLabel("Chemical shift (ppm)");
        _plot.Plot.YLabel("Intensity");
        _plot.Plot.Axes.AutoScale();

        // ppm axes are displayed descending — high ppm on the left. Flip X
        // while keeping the auto-scaled margins.
        if (double.IsFinite(xMin) && double.IsFinite(xMax) && xMax > xMin)
        {
            var limits = _plot.Plot.Axes.GetLimits();
            var high = Math.Max(limits.Left, limits.Right);
            var low = Math.Min(limits.Left, limits.Right);
            _plot.Plot.Axes.SetLimitsX(high, low);
        }

        _plot.Plot.Legend.IsVisible = ShouldShowLegend(entries.Select(entry => entry.Index));
        _plot.Refresh();
    }

    private (NmrDataset Dataset, int Index)[] GetDatasetsToPlotWithIndices()
    {
        if (OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0)
        {
            var result = new (NmrDataset, int)[_loadedDatasets.Count];
            for (var i = 0; i < _loadedDatasets.Count; i++)
            {
                result[i] = (_loadedDatasets[i], i);
            }

            return result;
        }

        if (_activeIndex < 0 || _activeIndex >= _loadedDatasets.Count)
        {
            return Array.Empty<(NmrDataset, int)>();
        }

        return new[] { (_loadedDatasets[_activeIndex], _activeIndex) };
    }

    private void ApplySeriesStyle(ScottPlot.Plottables.Scatter scatter, int index)
    {
        var style = index >= 0 && index < _datasetStyles.Count ? _datasetStyles[index] : null;
        var hex = style?.ColorHex ?? AutoLineColors[Math.Max(0, index) % AutoLineColors.Length];
        scatter.Color = ScottPlot.Color.FromHex(hex);
        scatter.LineWidth = (float)(style?.LineWidth ?? GraphFormattingConfigBase.DefaultLineWidth);
        scatter.MarkerSize = (float)(style?.MarkerSize ?? GraphFormattingConfigBase.DefaultMarkerSize);
    }

    private bool ShouldShowLegend(IEnumerable<int> datasetIndices)
    {
        var indices = datasetIndices.ToArray();
        return indices.Length > 1 || indices.Any(HasCustomLegendName);
    }

    private bool HasCustomLegendName(int datasetIndex) =>
        datasetIndex >= 0 && datasetIndex < _datasetStyles.Count
        && !string.IsNullOrWhiteSpace(_datasetStyles[datasetIndex].LegendName);

    private string GetSeriesLegendText(NmrDataset dataset, int datasetIndex)
    {
        if (datasetIndex >= 0 && datasetIndex < _datasetStyles.Count
            && !string.IsNullOrWhiteSpace(_datasetStyles[datasetIndex].LegendName))
        {
            return _datasetStyles[datasetIndex].LegendName!.Trim();
        }

        return NameOf(dataset, datasetIndex);
    }

    private static string NameOf(NmrDataset dataset, int index = 0)
    {
        if (dataset.Title is { Length: > 0 } title)
        {
            return title;
        }

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? $"dataset {index + 1}" : fileName;
    }

    // ------------------------------------------------------------- drag and drop

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer is not null && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer is null || !e.DataTransfer.Contains(DataFormat.File))
        {
            return;
        }

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToArray();
        if (paths is { Length: > 0 })
        {
            e.Handled = true;
            LoadFiles(paths);
        }
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0)
        {
            return;
        }

        await this.WhenLoadedAsync();
        LoadFiles(filePaths);
    }

    // --------------------------------------------------------------------- vm

    /// <summary>Display model for one row in the dataset list.</summary>
    public sealed class DatasetEntryVm
    {
        public string DisplayName { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;

        public IBrush ColorBrush { get; init; } = Brushes.Gray;
    }
}

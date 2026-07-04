using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly AnalysisSessionStore<NmrAnalysisSession> _sessionStore = new();
    private readonly List<NmrDataset> _loadedDatasets = new();
    private readonly List<DatasetStyle> _datasetStyles = new();
    private readonly ObservableCollection<DatasetEntryVm> _datasetEntries = new();

    // Analysis state, all evaluated against the active dataset.
    private readonly List<NmrPeakResult> _peaks = new();
    private readonly ObservableCollection<PeakRowVm> _peakEntries = new();
    private readonly List<NmrIntegrationRegion> _regions = new();
    private readonly ObservableCollection<IntegrationRowVm> _regionRows = new();
    private int _referenceRegionIndex;

    // Cumulative chemical-shift referencing applied to every dataset.
    private double _referenceShiftPpm;

    private AvaPlot? _plot;
    private int _activeIndex = -1;
    private bool _suppressDatasetListEvents;
    private bool _suppressStyleControlEvents;

    private NmrDataset? ActiveDataset =>
        _activeIndex >= 0 && _activeIndex < _loadedDatasets.Count ? _loadedDatasets[_activeIndex] : null;

    public MainWindow()
    {
        // Avalonia.Generators emits InitializeComponent + the x:Name fields, so
        // no manual definition here (a hand-written one re-nulls the generated
        // fields and NREs — the trap the other modules note).
        InitializeComponent();
        DatasetListBox.ItemsSource = _datasetEntries;
        PeakList.ItemsSource = _peakEntries;
        IntegrationGrid.ItemsSource = _regionRows;
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

        if (e.HasCommandModifier())
        {
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            switch (e.Key)
            {
                case Key.O when shift: _ = LoadSessionAsync(); e.Handled = true; return;
                case Key.O: _ = OpenFileAsync(); e.Handled = true; return;
                case Key.S when shift: _ = SaveSessionAsync(); e.Handled = true; return;
                case Key.S: _ = SaveImageAsync(); e.Handled = true; return;
                case Key.E: _ = ExportCsvAsync(); e.Handled = true; return;
            }
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

        ClearPeaks();
        RecomputeIntegration();
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
            ClearPeaks();
            RecomputeIntegration();
            RefreshDatasetEntries();
            SyncStyleControlsFromActiveDataset();
            InitializeEmptyPlot();
            UpdateStatus(null);
            return;
        }

        _activeIndex = Math.Clamp(_activeIndex >= index ? _activeIndex - 1 : _activeIndex, 0, _loadedDatasets.Count - 1);
        ClearPeaks();
        RecomputeIntegration();
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
        ClearPeaks();
        RecomputeIntegration();
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
            var scatter = _plot.Plot.Add.Scatter(dataset.XValues, TransformedY(dataset, index));
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

        DrawIntegrationRegions();
        DrawPeakMarkers();

        _plot.Plot.Legend.IsVisible = ShouldShowLegend(entries.Select(entry => entry.Index));
        _plot.Refresh();
    }

    private void DrawIntegrationRegions()
    {
        if (_plot is null || _regions.Count == 0)
        {
            return;
        }

        var limits = _plot.Plot.Axes.GetLimits();
        var span = limits.Top - limits.Bottom;
        var pad = span > 0 ? span * 10.0 : 1.0;
        var color = ScottPlot.Color.FromHex("#94A3B8");

        foreach (var region in _regions)
        {
            var rect = _plot.Plot.Add.Rectangle(region.PpmMin, region.PpmMax, limits.Bottom - pad, limits.Top + pad);
            rect.FillStyle.Color = color.WithAlpha((byte)40);
            rect.LineStyle.Color = color;
            rect.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
            rect.LineStyle.Width = 1;
            rect.LegendText = string.Empty;
        }
    }

    private void DrawPeakMarkers()
    {
        if (_plot is null || _peaks.Count == 0)
        {
            return;
        }

        var limits = _plot.Plot.Axes.GetLimits();
        var labelOffset = (limits.Top - limits.Bottom) * 0.04;
        var color = ScottPlot.Color.FromHex("#DC2626");

        foreach (var peak in _peaks)
        {
            var markerY = TransformYScalar(peak.Intensity, _activeIndex);
            var marker = _plot.Plot.Add.Marker(peak.Ppm, markerY);
            marker.MarkerStyle.Shape = ScottPlot.MarkerShape.OpenTriangleDown;
            marker.MarkerStyle.Size = 8;
            marker.MarkerStyle.LineColor = color;
            marker.MarkerStyle.LineWidth = 1.5f;
            marker.MarkerStyle.FillColor = ScottPlot.Colors.White;
            marker.LegendText = string.Empty;

            var text = _plot.Plot.Add.Text(FormatPpm(peak.Ppm), peak.Ppm, markerY + labelOffset);
            text.LabelFontColor = color;
            text.LabelFontSize = 10;
            text.LabelAlignment = ScottPlot.Alignment.LowerCenter;
        }
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

    private double[] TransformedY(NmrDataset dataset, int index)
    {
        var style = index >= 0 && index < _datasetStyles.Count ? _datasetStyles[index] : null;
        if (style is null || (style.YScale == 1.0 && style.YOffset == 0.0))
        {
            return dataset.YValues;
        }

        var ys = dataset.YValues;
        var result = new double[ys.Length];
        for (var i = 0; i < ys.Length; i++)
        {
            result[i] = ys[i] * style.YScale + style.YOffset;
        }

        return result;
    }

    private double TransformYScalar(double y, int index)
    {
        var style = index >= 0 && index < _datasetStyles.Count ? _datasetStyles[index] : null;
        return style is null ? y : y * style.YScale + style.YOffset;
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

    // ----------------------------------------------------------- peak detection

    private void DetectPeaksButton_Click(object? sender, RoutedEventArgs e)
    {
        var dataset = ActiveDataset;
        if (dataset is null)
        {
            Toast?.Show("先にスペクトルを開いてください。", StatusSeverity.Warning, 3000);
            return;
        }

        var config = new NmrPeakFinderConfig
        {
            MinimumIntensity = ParseDouble(PeakMinIntensityTextBox.Text, 0.0),
            MinimumProminence = ParseDouble(PeakProminenceTextBox.Text, 0.0),
            MaxPeaks = (int)Math.Max(0, ParseDouble(PeakMaxCountTextBox.Text, 20)),
        };

        _peaks.Clear();
        _peaks.AddRange(NmrPeakDetector.Find(dataset, config));
        RefreshPeakList();
        PlotDatasets();
    }

    private void ClearPeaksButton_Click(object? sender, RoutedEventArgs e)
    {
        ClearPeaks();
        PlotDatasets();
    }

    private void ClearPeaks()
    {
        _peaks.Clear();
        RefreshPeakList();
    }

    private void RefreshPeakList()
    {
        _peakEntries.Clear();
        foreach (var peak in _peaks.OrderByDescending(p => p.Ppm))
        {
            _peakEntries.Add(new PeakRowVm
            {
                PpmText = $"{FormatPpm(peak.Ppm)} ppm",
                IntensityText = peak.Intensity.ToString("0.###", CultureInfo.InvariantCulture),
            });
        }
    }

    // --------------------------------------------------------------- integration

    private void AddRegionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryParseDouble(RegionMinTextBox.Text, out var a) || !TryParseDouble(RegionMaxTextBox.Text, out var b) || a == b)
        {
            Toast?.Show("開始・終了 ppm を入力してください。", StatusSeverity.Warning, 3000);
            return;
        }

        _regions.Add(new NmrIntegrationRegion
        {
            Label = $"R{_regions.Count + 1}",
            PpmMin = Math.Min(a, b),
            PpmMax = Math.Max(a, b),
            Baseline = RegionLinearBaselineCheckBox.IsChecked == true ? NmrBaselineMode.Linear : NmrBaselineMode.None,
        });

        RecomputeIntegration();
        PlotDatasets();
    }

    private void RemoveRegionButton_Click(object? sender, RoutedEventArgs e)
    {
        var index = IntegrationGrid.SelectedIndex;
        if (index < 0 || index >= _regions.Count)
        {
            return;
        }

        _regions.RemoveAt(index);
        if (_referenceRegionIndex >= _regions.Count)
        {
            _referenceRegionIndex = 0;
        }

        RecomputeIntegration();
        PlotDatasets();
    }

    private void IntegrationGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = IntegrationGrid.SelectedIndex;
        if (index < 0 || index >= _regions.Count || index == _referenceRegionIndex)
        {
            return;
        }

        _referenceRegionIndex = index;
        RecomputeIntegration();
    }

    private void RecomputeIntegration()
    {
        // Rebuild the row collection only when the region count changed (keeps
        // the DataGrid selection / reference row stable across area updates).
        if (_regionRows.Count != _regions.Count)
        {
            _regionRows.Clear();
            foreach (var region in _regions)
            {
                _regionRows.Add(new IntegrationRowVm
                {
                    RangeText = $"{region.PpmMax.ToString("0.##", CultureInfo.InvariantCulture)}–" +
                                $"{region.PpmMin.ToString("0.##", CultureInfo.InvariantCulture)}",
                });
            }
        }

        var dataset = ActiveDataset;
        if (dataset is null || _regions.Count == 0)
        {
            foreach (var row in _regionRows)
            {
                row.AreaText = "—";
                row.RatioText = "—";
            }

            return;
        }

        if (_referenceRegionIndex < 0 || _referenceRegionIndex >= _regions.Count)
        {
            _referenceRegionIndex = 0;
        }

        var results = _regions.Select(region => NmrIntegrator.Integrate(dataset, region)).ToList();
        results = NmrIntegrator.NormalizeToReference(results, _referenceRegionIndex).ToList();

        for (var i = 0; i < _regionRows.Count && i < results.Count; i++)
        {
            _regionRows[i].AreaText = double.IsFinite(results[i].Area)
                ? results[i].Area.ToString("0.###", CultureInfo.InvariantCulture)
                : "—";
            _regionRows[i].RatioText = double.IsFinite(results[i].Ratio)
                ? results[i].Ratio.ToString("0.##", CultureInfo.InvariantCulture)
                : "—";
        }
    }

    // Format a ppm value, collapsing a "-0.00" (negative zero from parabolic
    // interpolation near the reference) to "0.00".
    private static string FormatPpm(double ppm)
    {
        if (Math.Abs(ppm) < 0.005)
        {
            ppm = 0.0;
        }

        return ppm.ToString("0.00", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------ referencing / display

    private void ApplyReferenceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_loadedDatasets.Count == 0)
        {
            return;
        }

        if (!TryParseDouble(ReferenceObservedTextBox.Text, out var observed))
        {
            Toast?.Show("基準ピークの現在 ppm を入力してください。", StatusSeverity.Warning, 3000);
            return;
        }

        var target = ParseDouble(ReferenceTargetTextBox.Text, 0.0);
        ShiftAllDatasets(ChemicalShiftReferencer.ComputeShift(observed, target));
    }

    private void ResetReferenceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_referenceShiftPpm != 0.0)
        {
            ShiftAllDatasets(-_referenceShiftPpm);
        }
    }

    private void ShiftAllDatasets(double delta)
    {
        if (delta == 0.0)
        {
            return;
        }

        for (var i = 0; i < _loadedDatasets.Count; i++)
        {
            _loadedDatasets[i] = ChemicalShiftReferencer.ApplyShift(_loadedDatasets[i], delta);
        }

        _referenceShiftPpm += delta;
        ClearPeaks();
        RecomputeIntegration();
        RefreshDatasetEntries();
        PlotDatasets();
    }

    private void NormalizeButton_Click(object? sender, RoutedEventArgs e)
    {
        for (var i = 0; i < _loadedDatasets.Count; i++)
        {
            var max = _loadedDatasets[i].YValues.Select(Math.Abs).DefaultIfEmpty(0.0).Max();
            _datasetStyles[i].YScale = max > 0 ? 1.0 / max : 1.0;
            _datasetStyles[i].YOffset = 0.0;
        }

        PlotDatasets();
    }

    private void StackButton_Click(object? sender, RoutedEventArgs e)
    {
        // Normalize each spectrum to unit height, then offset by a constant
        // step so the traces stack instead of overlapping.
        for (var i = 0; i < _loadedDatasets.Count; i++)
        {
            var max = _loadedDatasets[i].YValues.Select(Math.Abs).DefaultIfEmpty(0.0).Max();
            _datasetStyles[i].YScale = max > 0 ? 1.0 / max : 1.0;
            _datasetStyles[i].YOffset = i * 1.1;
        }

        PlotDatasets();
    }

    private void ResetDisplayButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var style in _datasetStyles)
        {
            style.YScale = 1.0;
            style.YOffset = 0.0;
        }

        PlotDatasets();
    }

    private static double ParseDouble(string? text, double fallback) =>
        TryParseDouble(text, out var value) ? value : fallback;

    private static bool TryParseDouble(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    // ------------------------------------------------------------ export / session

    private void SaveImageButton_Click(object? sender, RoutedEventArgs e) => _ = SaveImageAsync();

    private void ExportCsvButton_Click(object? sender, RoutedEventArgs e) => _ = ExportCsvAsync();

    private void SaveSessionButton_Click(object? sender, RoutedEventArgs e) => _ = SaveSessionAsync();

    private void LoadSessionButton_Click(object? sender, RoutedEventArgs e) => _ = LoadSessionAsync();

    private async Task SaveImageAsync()
    {
        if (_plot is null || _loadedDatasets.Count == 0)
        {
            Toast?.Show("先にスペクトルを開いてください。", StatusSeverity.Warning, 3000);
            return;
        }

        var sp = StorageProvider;
        if (sp is null)
        {
            return;
        }

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "グラフを保存",
            SuggestedFileName = "nmr",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG 画像") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("SVG 画像") { Patterns = new[] { "*.svg" } },
            },
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var format = GraphSaveHelpers.GetGraphSaveFormat(path);
        var fileName = GraphSaveHelpers.EnsureGraphSaveFileExtension(path, format);
        var (width, height) = GraphSaveHelpers.GetExportImageSize(null);
        if (format == GraphSaveFormat.Svg)
        {
            GraphSaveHelpers.SaveGraphSvg(_plot.Plot, fileName, width, height);
        }
        else
        {
            GraphSaveHelpers.SaveGraphPng(_plot.Plot, fileName, width, height, GraphSaveHelpers.ExportDpi);
        }

        Toast?.Show($"画像を保存しました: {Path.GetFileName(fileName)}", StatusSeverity.Success, 4000);
    }

    private async Task ExportCsvAsync()
    {
        if (_loadedDatasets.Count == 0)
        {
            Toast?.Show("先にスペクトルを開いてください。", StatusSeverity.Warning, 3000);
            return;
        }

        var sp = StorageProvider;
        if (sp is null)
        {
            return;
        }

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "CSV を出力",
            SuggestedFileName = "nmr",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var entries = _loadedDatasets.Select(dataset => new NmrAnalysisExportEntry
        {
            DisplayName = NameOf(dataset),
            SourceFilePath = dataset.SourceFilePath,
            XLabel = "ppm",
            YLabel = "Intensity",
            Points = dataset.XValues
                .Zip(dataset.YValues, (ppm, intensity) => new NmrDataPoint(ppm, intensity))
                .ToArray(),
        }).ToArray();

        new CsvNmrAnalysisExporter().Export(
            new AnalysisExport { GeneratorName = "NMR Analyzer", Entries = entries }, path);

        // Write the integration summary alongside, if any regions exist.
        if (_regions.Count > 0 && ActiveDataset is { } active)
        {
            var results = _regions.Select(region => NmrIntegrator.Integrate(active, region)).ToList();
            results = NmrIntegrator.NormalizeToReference(results, _referenceRegionIndex).ToList();
            var integrationPath = Path.Combine(
                Path.GetDirectoryName(path) ?? string.Empty,
                Path.GetFileNameWithoutExtension(path) + "_integration.csv");
            CsvNmrAnalysisExporter.WriteIntegrationTable(results, integrationPath);
        }

        Toast?.Show($"CSV を出力しました: {Path.GetFileName(path)}", StatusSeverity.Success, 4000);
    }

    private async Task SaveSessionAsync()
    {
        if (_loadedDatasets.Count == 0)
        {
            Toast?.Show("先にスペクトルを開いてください。", StatusSeverity.Warning, 3000);
            return;
        }

        var sp = StorageProvider;
        if (sp is null)
        {
            return;
        }

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "解析条件を保存",
            SuggestedFileName = "nmr",
            FileTypeChoices = new[] { new FilePickerFileType("NMR セッション") { Patterns = new[] { "*.nmrjson" } } },
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var session = NmrSessionMapper.ToSession(
            _loadedDatasets, _datasetStyles, _regions,
            OverlayCheckBox.IsChecked == true, _activeIndex, _referenceShiftPpm);
        _sessionStore.Save(session, path);
        Toast?.Show($"セッションを保存しました: {Path.GetFileName(path)}", StatusSeverity.Success, 4000);
    }

    private async Task LoadSessionAsync()
    {
        var sp = StorageProvider;
        if (sp is null)
        {
            return;
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "解析条件を読み込み",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("NMR セッション") { Patterns = new[] { "*.nmrjson" } } },
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        NmrAnalysisSession session;
        try
        {
            session = _sessionStore.Load(path);
        }
        catch (Exception ex)
        {
            Toast?.Show($"セッションの読み込みに失敗しました: {ex.Message}", StatusSeverity.Error, 5000);
            return;
        }

        _loadedDatasets.Clear();
        _datasetStyles.Clear();
        var missing = 0;
        foreach (var entry in session.Datasets)
        {
            try
            {
                var dataset = _reader.Read(entry.SourceFilePath);
                if (session.ReferenceShiftPpm != 0.0)
                {
                    dataset = ChemicalShiftReferencer.ApplyShift(dataset, session.ReferenceShiftPpm);
                }

                _loadedDatasets.Add(dataset);
                _datasetStyles.Add(NmrSessionMapper.ToStyle(entry.Style));
            }
            catch
            {
                missing++;
            }
        }

        _referenceShiftPpm = session.ReferenceShiftPpm;
        _regions.Clear();
        _regions.AddRange(session.IntegrationRegions);
        _referenceRegionIndex = 0;
        _activeIndex = _loadedDatasets.Count == 0
            ? -1
            : Math.Clamp(session.ActiveDatasetIndex, 0, _loadedDatasets.Count - 1);

        ClearPeaks();
        RefreshDatasetEntries();
        SyncStyleControlsFromActiveDataset();
        OverlayCheckBox.IsChecked = session.Overlay;
        RecomputeIntegration();
        PlotDatasets();
        UpdateStatus(null);

        if (missing > 0)
        {
            Toast?.Show($"{missing} 件のファイルが見つかりませんでした。", StatusSeverity.Warning, 5000);
        }
    }

    // --------------------------------------------------------------------- vm

    /// <summary>Display model for one row in the dataset list.</summary>
    public sealed class DatasetEntryVm
    {
        public string DisplayName { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;

        public IBrush ColorBrush { get; init; } = Brushes.Gray;
    }

    /// <summary>Display model for one detected peak.</summary>
    public sealed class PeakRowVm
    {
        public string PpmText { get; init; } = string.Empty;

        public string IntensityText { get; init; } = string.Empty;
    }

    /// <summary>
    /// Display model for one integration region. Area / ratio change in place
    /// when the reference row or active dataset changes, so this notifies to
    /// keep the DataGrid selection stable across recomputes.
    /// </summary>
    public sealed class IntegrationRowVm : INotifyPropertyChanged
    {
        private string _areaText = "—";
        private string _ratioText = "—";

        public string RangeText { get; init; } = string.Empty;

        public string AreaText
        {
            get => _areaText;
            set => Set(ref _areaText, value, nameof(AreaText));
        }

        public string RatioText
        {
            get => _ratioText;
            set => Set(ref _ratioText, value, nameof(RatioText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref string field, string value, string propertyName)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

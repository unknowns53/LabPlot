using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GpcAnalyzer.Core;
using LabPlot.Core;
using LabPlot.Core.Avalonia.Helpers;
using ScottPlot.Avalonia;
using static LabPlot.Core.PlotAppearance;

namespace LabPlot.GPC.Avalonia;

// Plot rendering / axis / legend / style partial of MainWindow.
// Split from MainWindow.axaml.cs (which still owns importers, calibration,
// statistics text, dataset list UI, etc.) so the chart plumbing has a
// dedicated file. State (_chromatogramPlot, _datasetStyles, _activeIndex,
// _molecularWeightCache, _plotSeriesCache, etc.) lives on MainWindow and
// is shared via the partial declaration.
public partial class MainWindow
{
    private void InitializePlotControl()
    {
        try
        {
            _chromatogramPlot = new AvaPlot();
            _chromatogramPlot.PointerReleased += ChromatogramPlot_PointerInteractionFinished;
            _chromatogramPlot.PointerWheelChanged += ChromatogramPlot_PointerInteractionFinished;
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_chromatogramPlot);

            // Phase 7 Batch 6 step 3: WPF 同等の凡例ドラッグ移動を有効化。
            _legendDragController = new LegendDragController(
                _chromatogramPlot,
                () => _formattingConfig.LegendPosition,
                () => (_formattingConfig.LegendOffsetX, _formattingConfig.LegendOffsetY),
                OnLegendDragCommit);
            _legendDragController.Attach();

            // パン / ホイールズーム操作中だけ AA を切って描画を軽くする。
            _plotFastModeController = new PlotFastModeController(
                _chromatogramPlot,
                () => _scatterPool);
            _plotFastModeController.Attach();

            PlotContextMenu.Apply(_chromatogramPlot, () => SaveGraphButton_Click(this, new RoutedEventArgs()));

            UpdatePlotHostAspectRatio();
            // 初期化成功時点でスケルトンを消す。placeholder TextBlock の文言は
            // InitializeEmptyPlot で SetState(EmptyReady) に切り替わる。
            PlotPlaceholderSkeleton.IsVisible = false;
            InitializeEmptyPlot();

            if (_currentDataset is not null)
            {
                PlotCurrentDataset();
                SetGraphActionsEnabled(true);
            }
        }
        catch (Exception ex)
        {
            PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.InitFailed);
            ShowError($"グラフ表示の初期化に失敗しました: {ex.Message}");
        }
    }

    private void InitializeEmptyPlot()
    {
        if (_chromatogramPlot is null) return;

        // データ無しの状態 — placeholder を「ファイルを読み込むと…」に切り替え。
        // 起動時 (InitializePlotControl 直後) と全データセット削除時の両方から呼ばれる。
        PlotPlaceholder.SetState(PlotPlaceholderTextBlock, PlotPlaceholder.State.EmptyReady);

        // 全データセット削除パス (RemoveDatasetButton_Click の Count==0 ブランチ) から呼ばれる時、
        // ScottPlot.Plot に残っている Scatter / Line 要素を明示的に消さないと「空状態のラベルだけ
        // 書き換えて、過去のデータ曲線が残ったまま」というゴースト描画になる。DLS 版の
        // InitializeEmptyPlot() は最初から Plot.Clear() を呼んでいるので同じ規約に揃える。
        _chromatogramPlot.Plot.Clear();
        _scatterPool.Clear();

        _chromatogramPlot.Plot.Title(DefaultLabels.PlaceholderTitle);
        _chromatogramPlot.Plot.XLabel(DefaultLabels.PlaceholderXLabel);
        _chromatogramPlot.Plot.YLabel(DefaultLabels.PlaceholderYLabel);
        _chromatogramPlot.Plot.Axes.NumericTicksBottom();
        ApplyPlotAppearance();
        UpdateStatisticsText(null);
        _chromatogramPlot.Refresh();
    }

    private void SchedulePlotCurrentDataset()
    {
        _plotRefreshDebounceTimer.Stop();
        _plotRefreshDebounceTimer.Start();
    }

    private void PlotRefreshDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _plotRefreshDebounceTimer.Stop();
        PlotCurrentDataset();
    }

    private void PlotCurrentDataset()
    {
        _plotRefreshDebounceTimer.Stop();

        if (_currentDataset is null)
        {
            SetGraphActionsEnabled(false);
            UpdateStatisticsText((MolecularWeightStatistics?)null);
            return;
        }

        // データを描画するので placeholder を非表示にする。
        PlotPlaceholder.Hide(PlotPlaceholderTextBlock);

        var activeDataset = GetSelectedDetectorDataset(_currentDataset);
        var plotEntries = GetDatasetsToPlotWithIndices();
        if (MolecularWeightCheckBox.IsChecked == true)
        {
            if (_selectedCalibrationCurve is null)
            {
                ShowError("分子量表示には較正曲線、溶媒、検出器の選択が必要です。");
                SetGraphActionsEnabled(_chromatogramPlot is not null);
                return;
            }

            try
            {
                var yMode = GetSelectedMolecularWeightYMode();
                var convertedEntries = plotEntries
                    .Select(entry => (
                        Dataset: GetMolecularWeightDataset(entry.Dataset, _selectedCalibrationCurve, yMode),
                        Index: entry.Index))
                    .ToArray();
                var activeConverted = convertedEntries
                    .Where(entry => entry.Index == _activeIndex)
                    .Select(entry => entry.Dataset)
                    .FirstOrDefault()
                    ?? GetMolecularWeightDataset(activeDataset, _selectedCalibrationCurve, yMode);
                PlotMolecularWeightDatasets(convertedEntries, activeConverted);
            }
            catch (InvalidDataException ex)
            {
                ShowError($"分子量表示に失敗しました: {ex.Message}");
                return;
            }
        }
        else
        {
            PlotRetentionTimeDatasets(plotEntries, activeDataset);
        }

        SetGraphActionsEnabled(_chromatogramPlot is not null);
    }

    private MolecularWeightDataset GetMolecularWeightDataset(
        GpcDataset dataset,
        CalibrationCurve curve,
        MolecularWeightYMode yMode)
    {
        var key = new MolecularWeightCacheKey(
            dataset.Points,
            dataset.SourceFilePath,
            dataset.YLabel,
            dataset.MolecularWeightStatistics,
            curve,
            yMode,
            MolecularWeightConverter.DefaultMinMolecularWeight,
            MolecularWeightConverter.DefaultMaxMolecularWeight);

        if (_molecularWeightCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var converted = _molecularWeightConverter.Convert(dataset, curve, yMode);
        _molecularWeightCache[key] = converted;
        return converted;
    }

    private void ClearComputedDataCaches()
    {
        _molecularWeightCache.Clear();
        _plotSeriesCache.Clear();
        _currentPlotUsesDownsampledData = false;
    }

    private int GetDisplayPointLimit(int seriesCount, long totalPointCount)
    {
        if (_forceFullResolutionPlot
            || seriesCount < OverlayDownsampleMinSeriesCount
            || totalPointCount <= OverlayDownsampleMinTotalPoints)
        {
            return int.MaxValue;
        }

        var perSeriesBudget = OverlayDisplayPointBudget / Math.Max(1, seriesCount);
        return Math.Clamp(
            perSeriesBudget,
            MinOverlayDisplayPointsPerSeries,
            MaxOverlayDisplayPointsPerSeries);
    }

    private PlotSeriesData GetPlotSeriesData(double[] xs, double[] ys, int maxPointCount)
    {
        var pointCount = Math.Min(xs.Length, ys.Length);
        var normalizedMaxPointCount = maxPointCount == int.MaxValue
            ? int.MaxValue
            : Math.Max(2, maxPointCount);
        var key = new PlotSeriesCacheKey(xs, ys, normalizedMaxPointCount);
        if (_plotSeriesCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var xRange = CreateDataRange(xs, pointCount);
        var yRange = CreateDataRange(ys, pointCount);
        var isDownsampled = pointCount > normalizedMaxPointCount;
        var series = isDownsampled
            ? CreateDownsampledPlotSeries(xs, ys, pointCount, normalizedMaxPointCount, xRange, yRange)
            : new PlotSeriesData
            {
                XValues = xs,
                YValues = ys,
                XRange = xRange,
                YRange = yRange,
                IsDownsampled = false,
            };

        _plotSeriesCache[key] = series;
        return series;
    }

    private static AxisDataRange CreateDataRange(IReadOnlyList<double> values, int count)
    {
        var range = new AxisDataRange();
        var valueCount = Math.Min(values.Count, count);
        for (var i = 0; i < valueCount; i++)
        {
            range.Include(values[i]);
        }

        return range;
    }

    private static PlotSeriesData CreateDownsampledPlotSeries(
        double[] xs,
        double[] ys,
        int sourcePointCount,
        int maxPointCount,
        AxisDataRange xRange,
        AxisDataRange yRange)
    {
        var targetBucketCount = Math.Max(1, (maxPointCount - 2) / 2);
        var bucketSize = Math.Max(1, (int)Math.Ceiling((sourcePointCount - 2) / (double)targetBucketCount));
        var downsampledXs = new List<double>(maxPointCount + 2) { xs[0] };
        var downsampledYs = new List<double>(maxPointCount + 2) { ys[0] };

        for (var start = 1; start < sourcePointCount - 1; start += bucketSize)
        {
            var end = Math.Min(sourcePointCount - 1, start + bucketSize);
            var minIndex = start;
            var maxIndex = start;
            var minY = double.PositiveInfinity;
            var maxY = double.NegativeInfinity;

            for (var i = start; i < end; i++)
            {
                var y = ys[i];
                if (!double.IsFinite(y)) continue;
                if (y < minY) { minY = y; minIndex = i; }
                if (y > maxY) { maxY = y; maxIndex = i; }
            }

            if (!double.IsFinite(minY) || !double.IsFinite(maxY))
            {
                AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, start);
                continue;
            }

            if (minIndex <= maxIndex)
            {
                AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, minIndex);
                if (maxIndex != minIndex)
                {
                    AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, maxIndex);
                }
            }
            else
            {
                AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, maxIndex);
                AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, minIndex);
            }
        }

        AddDownsampledPoint(downsampledXs, downsampledYs, xs, ys, sourcePointCount - 1);

        return new PlotSeriesData
        {
            XValues = downsampledXs.ToArray(),
            YValues = downsampledYs.ToArray(),
            XRange = xRange,
            YRange = yRange,
            IsDownsampled = true,
        };
    }

    private static void AddDownsampledPoint(
        List<double> downsampledXs,
        List<double> downsampledYs,
        IReadOnlyList<double> xs,
        IReadOnlyList<double> ys,
        int index)
    {
        if (downsampledXs.Count > 0
            && downsampledXs[^1].Equals(xs[index])
            && downsampledYs[^1].Equals(ys[index]))
        {
            return;
        }

        downsampledXs.Add(xs[index]);
        downsampledYs.Add(ys[index]);
    }

    private (GpcDataset Dataset, int Index)[] GetDatasetsToPlotWithIndices()
    {
        if (OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0)
        {
            var result = new (GpcDataset, int)[_loadedDatasets.Count];
            for (var i = 0; i < _loadedDatasets.Count; i++)
            {
                result[i] = (GetSelectedDetectorDataset(_loadedDatasets[i]), i);
            }
            return result;
        }

        if (_activeIndex < 0 || _activeIndex >= _loadedDatasets.Count)
        {
            return Array.Empty<(GpcDataset, int)>();
        }

        return new[] { (GetSelectedDetectorDataset(_loadedDatasets[_activeIndex]), _activeIndex) };
    }

    private GpcDataset GetSelectedDetectorDataset(GpcDataset dataset)
    {
        if (DetectorComboBox.SelectedItem is string detector
            && dataset.TryGetDetectorDataset(detector, out _))
        {
            return dataset.WithDetector(detector);
        }

        return dataset;
    }

    private static long GetRetentionTimePointCount(IReadOnlyList<(GpcDataset Dataset, int Index)> entries)
    {
        var pointCount = 0L;
        for (var i = 0; i < entries.Count; i++)
        {
            pointCount += entries[i].Dataset.XValues.LongLength;
        }

        return pointCount;
    }

    private static long GetMolecularWeightPointCount(IReadOnlyList<(MolecularWeightDataset Dataset, int Index)> entries)
    {
        var pointCount = 0L;
        for (var i = 0; i < entries.Count; i++)
        {
            pointCount += entries[i].Dataset.LogMolecularWeightValues.LongLength;
        }

        return pointCount;
    }

    private void PlotRetentionTimeDatasets(IReadOnlyList<(GpcDataset Dataset, int Index)> entries, GpcDataset activeDataset)
    {
        if (_chromatogramPlot is null)
        {
            ShowError("グラフ表示を初期化中です。少し待ってからもう一度お試しください。");
            return;
        }

        ClearScatterPool();
        _chromatogramPlot.Plot.Axes.NumericTicksBottom();

        var displayPointLimit = GetDisplayPointLimit(entries.Count, GetRetentionTimePointCount(entries));
        _currentPlotUsesDownsampledData = false;
        var xRange = new AxisDataRange();
        var yRange = new AxisDataRange();
        for (var i = 0; i < entries.Count; i++)
        {
            var (dataset, datasetIndex) = entries[i];
            var series = GetPlotSeriesData(dataset.XValues, dataset.YValues, displayPointLimit);
            xRange.Include(series.XRange);
            yRange.Include(series.YRange);
            _currentPlotUsesDownsampledData |= series.IsDownsampled;

            var signal = _chromatogramPlot.Plot.Add.Scatter(series.XValues, series.YValues);
            _scatterPool.Add(signal);
            signal.LegendText = GetSeriesLegendText(dataset, "Signal", datasetIndex);
            ApplySeriesStyle(signal, datasetIndex);
        }

        _currentLegendAutoShow = ShouldShowLegend(entries.Select(entry => entry.Index));
        ApplyLegend(_chromatogramPlot.Plot, CaptureFormattingConfigFromControls(),
            autoShow: _currentLegendAutoShow);

        _chromatogramPlot.Plot.Title(GetGraphTitle(Path.GetFileName(activeDataset.SourceFilePath) ?? DefaultLabels.ChromatogramFallbackTitle));
        _chromatogramPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, activeDataset.XLabel));
        _chromatogramPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, activeDataset.YLabel));
        _chromatogramPlot.Plot.Axes.AutoScale();
        if (!ApplyAxisLimits(xRange, yRange, false))
        {
            _chromatogramPlot.Refresh();
            return;
        }

        UpdateStatisticsForDatasets(entries
            .Select(en => (
                Label: GetStatsLabel(en.Dataset.SourceFilePath, en.Index),
                Stats: ApplyStoredSelectedPeak(en.Dataset.MolecularWeightStatistics, en.Index)))
            .ToList());
        ApplyPlotAppearance();
        _chromatogramPlot.Refresh();
    }

    private void PlotMolecularWeightDatasets(
        IReadOnlyList<(MolecularWeightDataset Dataset, int Index)> entries,
        MolecularWeightDataset activeDataset)
    {
        if (_chromatogramPlot is null)
        {
            ShowError("グラフ表示を初期化中です。少し待ってからもう一度お試しください。");
            return;
        }

        ClearScatterPool();
        SetMolecularWeightLogTicks();

        var displayPointLimit = GetDisplayPointLimit(entries.Count, GetMolecularWeightPointCount(entries));
        _currentPlotUsesDownsampledData = false;
        var xRange = new AxisDataRange();
        var yRange = new AxisDataRange();
        for (var i = 0; i < entries.Count; i++)
        {
            var (dataset, datasetIndex) = entries[i];
            var series = GetPlotSeriesData(dataset.LogMolecularWeightValues, dataset.SignalValues, displayPointLimit);
            xRange.Include(series.XRange);
            yRange.Include(series.YRange);
            _currentPlotUsesDownsampledData |= series.IsDownsampled;

            var signal = _chromatogramPlot.Plot.Add.Scatter(series.XValues, series.YValues);
            _scatterPool.Add(signal);
            signal.LegendText = GetSeriesLegendText(dataset, $"{dataset.Solvent}/{dataset.Detector}", datasetIndex);
            ApplySeriesStyle(signal, datasetIndex);
        }

        _currentLegendAutoShow = ShouldShowLegend(entries.Select(entry => entry.Index));
        ApplyLegend(_chromatogramPlot.Plot, CaptureFormattingConfigFromControls(),
            autoShow: _currentLegendAutoShow);

        _chromatogramPlot.Plot.Title(GetGraphTitle(Path.GetFileName(activeDataset.SourceFilePath) ?? DefaultLabels.ChromatogramFallbackTitle));
        _chromatogramPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, string.Format(DefaultLabels.LogScaleXLabelFormat, activeDataset.XLabel)));
        _chromatogramPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, activeDataset.YLabel));
        _chromatogramPlot.Plot.Axes.AutoScale();
        if (!ApplyAxisLimits(xRange, yRange, true))
        {
            _chromatogramPlot.Refresh();
            return;
        }

        UpdateStatisticsForDatasets(entries
            .Select(en => (
                Label: GetStatsLabel(en.Dataset.SourceFilePath, en.Index),
                Stats: ApplyStoredSelectedPeak(en.Dataset.Statistics, en.Index)))
            .ToList());
        ApplyPlotAppearance();
        _chromatogramPlot.Refresh();
    }

    private void ApplyPlotAppearance(float scale = 1f)
    {
        if (_chromatogramPlot is null) return;
        var plot = _chromatogramPlot.Plot;
        var config = CaptureFormattingConfigFromControls();
        ApplyAll(plot, config, scale);
    }

    private void ApplySeriesStyle(ScottPlot.Plottables.Scatter signal, int datasetIndex, float scale = 1f)
    {
        if (datasetIndex >= 0 && datasetIndex < _datasetStyles.Count)
        {
            var style = _datasetStyles[datasetIndex];
            signal.LineWidth = (float)style.LineWidth * scale;
            signal.MarkerSize = (float)style.MarkerSize * scale;
            var hex = style.ColorHex ?? AutoLineColors[datasetIndex % AutoLineColors.Length];
            signal.Color = ScottPlot.Color.FromHex(new[] { hex }).First();
            return;
        }

        signal.LineWidth = (float)GraphFormattingConfig.DefaultLineWidth * scale;
        signal.MarkerSize = (float)GraphFormattingConfig.DefaultMarkerSize * scale;
        var fallback = AutoLineColors[Math.Max(0, datasetIndex) % AutoLineColors.Length];
        signal.Color = ScottPlot.Color.FromHex(new[] { fallback }).First();
    }

    private void ApplyExportStyleScale(float scale)
    {
        if (_chromatogramPlot is null) return;
        ApplyPlotAppearance(scale);
        ApplyExistingSeriesStyles(scale);
    }

    private void ApplyExistingSeriesStyles(float scale)
    {
        if (_chromatogramPlot is null) return;

        var entries = GetDatasetsToPlotWithIndices();
        var scatters = _chromatogramPlot.Plot
            .GetPlottables()
            .OfType<ScottPlot.Plottables.Scatter>()
            .ToArray();

        for (var i = 0; i < scatters.Length; i++)
        {
            var datasetIndex = i < entries.Length ? entries[i].Index : i;
            ApplySeriesStyle(scatters[i], datasetIndex, scale);
        }
    }

    private static float GetExportStyleScale()
    {
        return GraphSaveHelpers.ExportDpi / GraphSaveHelpers.DisplayDpi;
    }

    private static string GetStatsLabel(string? sourceFilePath, int index)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFilePath);
        return string.IsNullOrWhiteSpace(name) ? $"dataset {index + 1}" : name;
    }

    private bool ShouldShowLegend(IEnumerable<int> datasetIndices)
    {
        var indices = datasetIndices.ToArray();
        return indices.Length > 1 || indices.Any(HasCustomLegendName);
    }

    private bool HasCustomLegendName(int datasetIndex)
    {
        return datasetIndex >= 0
            && datasetIndex < _datasetStyles.Count
            && !string.IsNullOrWhiteSpace(_datasetStyles[datasetIndex].LegendName);
    }

    private string? GetCustomLegendName(int datasetIndex)
    {
        if (!HasCustomLegendName(datasetIndex)) return null;
        return _datasetStyles[datasetIndex].LegendName!.Trim();
    }

    private string GetSeriesLegendText(GpcDataset dataset, string fallback, int datasetIndex)
    {
        var customName = GetCustomLegendName(datasetIndex);
        if (customName is not null) return customName;

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        var detector = string.IsNullOrWhiteSpace(dataset.Detector) ? string.Empty : $" / Detector {dataset.Detector}";
        return string.IsNullOrWhiteSpace(fileName) ? $"{fallback}{detector}" : $"{fileName}{detector}";
    }

    private string GetSeriesLegendText(MolecularWeightDataset dataset, string fallback, int datasetIndex)
    {
        var customName = GetCustomLegendName(datasetIndex);
        if (customName is not null) return customName;

        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? fallback : $"{fileName} / {fallback}";
    }

    private bool ApplyAxisLimits(AxisDataRange xRange, AxisDataRange yRange, bool xIsMolecularWeight)
    {
        if (_chromatogramPlot is null) return false;

        var xMin = AxisRangePanel.XMinValue;
        var xMax = AxisRangePanel.XMaxValue;
        var yMin = AxisRangePanel.YMinValue;
        var yMax = AxisRangePanel.YMaxValue;

        if (xIsMolecularWeight
            && (!TryConvertMolecularWeightLimit(ref xMin, "X Min")
                || !TryConvertMolecularWeightLimit(ref xMax, "X Max")))
        {
            return false;
        }

        if (xMin.HasValue || xMax.HasValue)
        {
            if (!TryGetRequestedRange(xRange, xMin, xMax, "X", out var left, out var right))
            {
                return false;
            }

            _chromatogramPlot.Plot.Axes.SetLimitsX(left, right);
        }

        if (yMin.HasValue || yMax.HasValue)
        {
            if (!TryGetRequestedRange(yRange, yMin, yMax, "Y", out var bottom, out var top))
            {
                return false;
            }

            _chromatogramPlot.Plot.Axes.SetLimitsY(bottom, top);
        }

        return true;
    }

    private bool TryConvertMolecularWeightLimit(ref double? value, string label)
    {
        if (!value.HasValue) return true;
        if (value.Value <= 0)
        {
            SetStatus($"{label} must be positive in molecular-weight view.", true);
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
            SetStatus($"{axisName} axis range could not be determined.", true);
            return false;
        }

        if (min >= max)
        {
            SetStatus($"{axisName} Min must be smaller than {axisName} Max.", true);
            return false;
        }

        return true;
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

    /// <summary>
    /// Removes every Scatter currently held in <see cref="_scatterPool"/>
    /// from the chromatogram plot without disturbing axes / title / legend
    /// state, then clears the pool ready to be repopulated by the next
    /// rendering pass. Replaces the broader <c>Plot.Clear()</c> call so
    /// non-plottable plot state (titles, axis ticks, legend orientation)
    /// is preserved between refreshes.
    /// </summary>
    /// <remarks>
    /// ScottPlot 5.1.58 does not expose a setter on <c>Scatter.Data</c> or
    /// the underlying <c>ScatterSourceDoubleArray.Xs / Ys</c> auto-properties,
    /// so true in-place data swap is not currently possible — each refresh
    /// still allocates a fresh Scatter per dataset. The pool gives us
    /// precise lifecycle tracking and lets us avoid the wider state-reset
    /// that <c>Plot.Clear()</c> implies; data-swap recycling will become
    /// available if ScottPlot adds a public mutation surface later.
    /// </remarks>
    private void ClearScatterPool()
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        var plot = _chromatogramPlot.Plot;
        foreach (var scatter in _scatterPool)
        {
            plot.Remove(scatter);
        }
        _scatterPool.Clear();
    }
}

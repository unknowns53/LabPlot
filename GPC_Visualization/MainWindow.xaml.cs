using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using GpcAnalyzer.Core;
using Microsoft.Win32;
using ScottPlot.WPF;

namespace GPC_Visualization;

public partial class MainWindow : Window
{
    private readonly IGpcDataReader _reader = new CsvGpcDataReader();
    private readonly StandardCurveFileReader _standardCurveReader = new();
    private readonly MolecularWeightConverter _molecularWeightConverter = new();
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

    private readonly List<GpcDataset> _loadedDatasets = new();
    private GpcDataset? _currentDataset;
    private CalibrationCurveSet? _calibrationCurveSet;
    private CalibrationCurve? _selectedCalibrationCurve;
    private WpfPlot? _chromatogramPlot;
    private bool _updatingCalibrationSelection;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(InitializePlotControl, DispatcherPriority.ApplicationIdle);
    }

    private void OpenCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "GPCデータを開く",
            Filter = "GPCデータ (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|CSV (*.csv)|*.csv|テキスト (*.txt;*.tsv)|*.txt;*.tsv|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var dataset = _reader.Read(dialog.FileName);
            _currentDataset = dataset;
            if (OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0)
            {
                _loadedDatasets.Add(dataset);
            }
            else
            {
                _loadedDatasets.Clear();
                _loadedDatasets.Add(dataset);
            }

            FilePathTextBlock.Text = _loadedDatasets.Count > 1
                ? $"{_loadedDatasets.Count} files (latest: {dialog.FileName})"
                : dialog.FileName;
            if (_calibrationCurveSet is not null)
            {
                PopulateSolventComboBox();
            }
            else
            {
                UpdateMolecularWeightAvailability();
            }

            PlotCurrentDataset();
            SetStatus($"{dataset.Points.Count:N0} 点のデータを読み込みました。", false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            _currentDataset = null;
            _loadedDatasets.Clear();
            SaveGraphButton.IsEnabled = false;
            UpdateStatisticsText(null);
            SetStatus($"読み込みに失敗しました: {ex.Message}", true);
        }
    }

    private void OpenCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "較正曲線JSONを開く",
            Filter = "JSON (*.json)|*.json|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _calibrationCurveSet = _standardCurveReader.Read(dialog.FileName);
            CalibrationPathTextBlock.Text = $"較正曲線: {dialog.FileName}";
            PopulateSolventComboBox();
            UpdateMolecularWeightAvailability();
            PlotCurrentDataset();
            SetStatus("較正曲線を読み込みました。", false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException)
        {
            _calibrationCurveSet = null;
            _selectedCalibrationCurve = null;
            CalibrationPathTextBlock.Text = "較正曲線: 未選択";
            SolventComboBox.ItemsSource = null;
            SolventComboBox.IsEnabled = false;
            DetectorComboBox.ItemsSource = null;
            DetectorComboBox.IsEnabled = false;
            UpdateMolecularWeightAvailability();
            PlotCurrentDataset();
            SetStatus($"較正曲線の読み込みに失敗しました: {ex.Message}", true);
        }
    }

    private void SolventComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingCalibrationSelection)
        {
            return;
        }

        PopulateDetectorComboBox();
    }

    private void DetectorComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingCalibrationSelection)
        {
            return;
        }

        SelectCalibrationCurve();
    }

    private void MolecularWeightCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_currentDataset is null)
        {
            return;
        }

        MolecularWeightYModeComboBox.IsEnabled = MolecularWeightCheckBox.IsChecked == true
            && _selectedCalibrationCurve is not null;
        PlotCurrentDataset();

        if (MolecularWeightCheckBox.IsChecked == true && _selectedCalibrationCurve is not null)
        {
            var yModeLabel = GetSelectedMolecularWeightYMode() == MolecularWeightYMode.DifferentialWeightFraction
                ? "dw/dlogM"
                : "Signal";
            SetStatus($"分子量表示に切り替えました: {_selectedCalibrationCurve.Solvent}/{_selectedCalibrationCurve.Detector}, Y={yModeLabel}", false);
        }
        else
        {
            SetStatus("保持時間表示に切り替えました。", false);
        }
    }

    private void MolecularWeightYModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentDataset is not null && MolecularWeightCheckBox.IsChecked == true)
        {
            PlotCurrentDataset();
        }
    }

    private void OverlayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_currentDataset is not null)
        {
            PlotCurrentDataset();
        }
    }

    private void ApplyGraphSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        PlotCurrentDataset();
    }

    private void ResetGraphSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        TitleTextBox.Clear();
        XLabelTextBox.Clear();
        YLabelTextBox.Clear();
        XMinTextBox.Clear();
        XMaxTextBox.Clear();
        YMinTextBox.Clear();
        YMaxTextBox.Clear();
        LineColorComboBox.SelectedIndex = 0;
        LineWidthTextBox.Text = "1.5";
        MarkerSizeTextBox.Text = "0";
        PlotCurrentDataset();
    }

    private void SaveGraphButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDataset is null || _chromatogramPlot is null)
        {
            SetStatus("保存するグラフがありません。", true);
            return;
        }

        var defaultName = Path.GetFileNameWithoutExtension(_currentDataset.SourceFilePath) ?? "gpc_chromatogram";
        var dialog = new SaveFileDialog
        {
            Title = "グラフをPNGで保存",
            Filter = "PNG画像 (*.png)|*.png",
            FileName = $"{defaultName}.png",
            DefaultExt = ".png",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _chromatogramPlot.Plot.SavePng(dialog.FileName, 1200, 720);
            SetStatus($"グラフを保存しました: {dialog.FileName}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"保存に失敗しました: {ex.Message}", true);
        }
    }

    private void InitializePlotControl()
    {
        try
        {
            _chromatogramPlot = new WpfPlot();
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_chromatogramPlot);
            InitializeEmptyPlot();

            if (_currentDataset is not null)
            {
                PlotCurrentDataset();
                SaveGraphButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            PlotPlaceholderTextBlock.Text = "グラフ表示の初期化に失敗しました。";
            SetStatus($"グラフ表示の初期化に失敗しました: {ex.Message}", true);
        }
    }

    private void InitializeEmptyPlot()
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        _chromatogramPlot.Plot.Title("GPC chromatogram");
        _chromatogramPlot.Plot.XLabel("Time");
        _chromatogramPlot.Plot.YLabel("Signal");
        _chromatogramPlot.Plot.Axes.NumericTicksBottom();
        UpdateStatisticsText(null);
        _chromatogramPlot.Refresh();
    }

    private void PlotCurrentDataset()
    {
        if (_currentDataset is null)
        {
            SaveGraphButton.IsEnabled = false;
            UpdateStatisticsText(null);
            return;
        }

        var currentDataset = GetSelectedDetectorDataset(_currentDataset);
        var plotDatasets = GetDatasetsToPlot();
        if (MolecularWeightCheckBox.IsChecked == true)
        {
            if (_selectedCalibrationCurve is null)
            {
                SetStatus("分子量表示には較正曲線、溶媒、検出器の選択が必要です。", true);
                SaveGraphButton.IsEnabled = _chromatogramPlot is not null;
                return;
            }

            try
            {
                var convertedDatasets = plotDatasets
                    .Select(dataset => _molecularWeightConverter.Convert(
                        dataset,
                        _selectedCalibrationCurve,
                        GetSelectedMolecularWeightYMode()))
                    .ToArray();
                var currentConverted = _molecularWeightConverter.Convert(
                    currentDataset,
                    _selectedCalibrationCurve,
                    GetSelectedMolecularWeightYMode());
                PlotMolecularWeightDatasets(convertedDatasets, currentConverted);
            }
            catch (InvalidDataException ex)
            {
                SetStatus($"分子量表示に失敗しました: {ex.Message}", true);
                return;
            }
        }
        else
        {
            PlotRetentionTimeDatasets(plotDatasets, currentDataset);
        }

        SaveGraphButton.IsEnabled = _chromatogramPlot is not null;
    }

    private GpcDataset[] GetDatasetsToPlot()
    {
        var sourceDatasets = OverlayCheckBox.IsChecked == true && _loadedDatasets.Count > 0
            ? _loadedDatasets
            : _currentDataset is null
                ? []
                : [_currentDataset];

        return sourceDatasets
            .Select(GetSelectedDetectorDataset)
            .ToArray();
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

    private void PlotRetentionTimeDatasets(IReadOnlyList<GpcDataset> datasets, GpcDataset currentDataset)
    {
        if (_chromatogramPlot is null)
        {
            SetStatus("グラフ表示を初期化中です。少し待ってからもう一度お試しください。", true);
            return;
        }

        _chromatogramPlot.Plot.Clear();
        _chromatogramPlot.Plot.Axes.NumericTicksBottom();

        var allXs = new List<double>();
        var allYs = new List<double>();
        for (var i = 0; i < datasets.Count; i++)
        {
            var dataset = datasets[i];
            var xs = dataset.Points.Select(point => point.X).ToArray();
            var ys = dataset.Points.Select(point => point.Y).ToArray();
            allXs.AddRange(xs);
            allYs.AddRange(ys);

            var signal = _chromatogramPlot.Plot.Add.Scatter(xs, ys);
            signal.LegendText = GetSeriesLegendText(dataset, "Signal");
            ApplySeriesStyle(signal, i);
        }

        if (datasets.Count > 1)
        {
            _chromatogramPlot.Plot.ShowLegend();
        }
        else
        {
            _chromatogramPlot.Plot.HideLegend();
        }

        _chromatogramPlot.Plot.Title(GetGraphTitle(Path.GetFileName(currentDataset.SourceFilePath) ?? "GPC chromatogram"));
        _chromatogramPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, currentDataset.XLabel));
        _chromatogramPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, currentDataset.YLabel));
        _chromatogramPlot.Plot.Axes.AutoScale();
        if (!ApplyAxisLimits(allXs.ToArray(), allYs.ToArray(), false))
        {
            _chromatogramPlot.Refresh();
            return;
        }

        UpdateStatisticsText(currentDataset.MolecularWeightStatistics);
        _chromatogramPlot.Refresh();
    }

    private void PlotMolecularWeightDatasets(
        IReadOnlyList<MolecularWeightDataset> datasets,
        MolecularWeightDataset currentDataset)
    {
        if (_chromatogramPlot is null)
        {
            SetStatus("グラフ表示を初期化中です。少し待ってからもう一度お試しください。", true);
            return;
        }

        _chromatogramPlot.Plot.Clear();
        SetMolecularWeightLogTicks();

        var allXs = new List<double>();
        var allYs = new List<double>();
        for (var i = 0; i < datasets.Count; i++)
        {
            var dataset = datasets[i];
            var xs = dataset.Points.Select(point => Math.Log10(point.MolecularWeight)).ToArray();
            var ys = dataset.Points.Select(point => point.Signal).ToArray();
            allXs.AddRange(xs);
            allYs.AddRange(ys);

            var signal = _chromatogramPlot.Plot.Add.Scatter(xs, ys);
            signal.LegendText = GetSeriesLegendText(dataset, $"{dataset.Solvent}/{dataset.Detector}");
            ApplySeriesStyle(signal, i);
        }

        if (datasets.Count > 1)
        {
            _chromatogramPlot.Plot.ShowLegend();
        }
        else
        {
            _chromatogramPlot.Plot.HideLegend();
        }

        _chromatogramPlot.Plot.Title(GetGraphTitle(Path.GetFileName(currentDataset.SourceFilePath) ?? "GPC chromatogram"));
        _chromatogramPlot.Plot.XLabel(GetGraphLabel(XLabelTextBox, $"{currentDataset.XLabel} (log scale)"));
        _chromatogramPlot.Plot.YLabel(GetGraphLabel(YLabelTextBox, currentDataset.YLabel));
        _chromatogramPlot.Plot.Axes.AutoScale();
        if (!ApplyAxisLimits(allXs.ToArray(), allYs.ToArray(), true))
        {
            _chromatogramPlot.Refresh();
            return;
        }

        UpdateStatisticsText(currentDataset.Statistics);
        _chromatogramPlot.Refresh();
    }

    private void ApplySeriesStyle(ScottPlot.Plottables.Scatter signal, int seriesIndex)
    {
        signal.LineWidth = (float)GetRequestedLineWidth();
        signal.MarkerSize = (float)GetRequestedMarkerSize();
        signal.Color = GetRequestedLineColor(seriesIndex);
    }

    private double GetRequestedLineWidth()
    {
        return TryParsePositiveDouble(LineWidthTextBox.Text, out var lineWidth)
            ? lineWidth
            : 1.5;
    }

    private double GetRequestedMarkerSize()
    {
        return TryParseNonNegativeDouble(MarkerSizeTextBox.Text, out var markerSize)
            ? markerSize
            : 0;
    }

    private ScottPlot.Color GetRequestedLineColor(int seriesIndex)
    {
        if (LineColorComboBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && !tag.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return ScottPlot.Color.FromHex(new[] { tag }).First();
        }

        return ScottPlot.Color.FromHex(new[] { AutoLineColors[seriesIndex % AutoLineColors.Length] }).First();
    }

    private static bool TryParsePositiveDouble(string text, out double value)
    {
        return TryParseDouble(text, out value) && value > 0;
    }

    private static bool TryParseNonNegativeDouble(string text, out double value)
    {
        return TryParseDouble(text, out value) && value >= 0;
    }

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
    }

    private static string GetSeriesLegendText(GpcDataset dataset, string fallback)
    {
        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        var detector = string.IsNullOrWhiteSpace(dataset.Detector) ? string.Empty : $" / Detector {dataset.Detector}";
        return string.IsNullOrWhiteSpace(fileName) ? $"{fallback}{detector}" : $"{fileName}{detector}";
    }

    private static string GetSeriesLegendText(MolecularWeightDataset dataset, string fallback)
    {
        var fileName = Path.GetFileNameWithoutExtension(dataset.SourceFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? fallback : $"{fileName} / {fallback}";
    }

    private bool ApplyAxisLimits(double[] xs, double[] ys, bool xIsMolecularWeight)
    {
        if (_chromatogramPlot is null)
        {
            return false;
        }

        if (!TryReadOptionalDouble(XMinTextBox, "X Min", out var xMin)
            || !TryReadOptionalDouble(XMaxTextBox, "X Max", out var xMax)
            || !TryReadOptionalDouble(YMinTextBox, "Y Min", out var yMin)
            || !TryReadOptionalDouble(YMaxTextBox, "Y Max", out var yMax))
        {
            return false;
        }

        if (xIsMolecularWeight
            && (!TryConvertMolecularWeightLimit(ref xMin, "X Min")
                || !TryConvertMolecularWeightLimit(ref xMax, "X Max")))
        {
            return false;
        }

        var finiteXs = xs.Where(double.IsFinite).ToArray();
        var finiteYs = ys.Where(double.IsFinite).ToArray();

        if (xMin.HasValue || xMax.HasValue)
        {
            if (!TryGetRequestedRange(finiteXs, xMin, xMax, "X", out var left, out var right))
            {
                return false;
            }

            _chromatogramPlot.Plot.Axes.SetLimitsX(left, right);
        }

        if (yMin.HasValue || yMax.HasValue)
        {
            if (!TryGetRequestedRange(finiteYs, yMin, yMax, "Y", out var bottom, out var top))
            {
                return false;
            }

            _chromatogramPlot.Plot.Axes.SetLimitsY(bottom, top);
        }

        return true;
    }

    private bool TryConvertMolecularWeightLimit(ref double? value, string label)
    {
        if (!value.HasValue)
        {
            return true;
        }

        if (value.Value <= 0)
        {
            SetStatus($"{label} must be positive in molecular-weight view.", true);
            return false;
        }

        value = Math.Log10(value.Value);
        return true;
    }

    private bool TryGetRequestedRange(
        IReadOnlyList<double> dataValues,
        double? requestedMin,
        double? requestedMax,
        string axisName,
        out double min,
        out double max)
    {
        min = requestedMin ?? (dataValues.Count > 0 ? dataValues.Min() : double.NaN);
        max = requestedMax ?? (dataValues.Count > 0 ? dataValues.Max() : double.NaN);

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

    private bool TryReadOptionalDouble(TextBox textBox, string label, out double? value)
    {
        value = null;
        var text = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var parsed)
            || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
        {
            value = parsed;
            return true;
        }

        SetStatus($"{label} must be a number.", true);
        return false;
    }

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

    private void UpdateStatisticsText(MolecularWeightStatistics? statistics)
    {
        if (statistics is null || !statistics.HasAnyValue)
        {
            StatisticsTextBlock.Text = "Mn: -   Mw: -   PDI: -";
            return;
        }

        if (statistics.Peaks.Count > 0)
        {
            var peakSummaries = statistics.Peaks
                .Take(3)
                .Select(FormatPeakStatistic);
            StatisticsTextBlock.Text = "Peaks by %: " + string.Join("   ", peakSummaries);
            return;
        }

        var source = statistics.Source == MolecularWeightStatisticsSource.DataFile ? "file" : "calc";
        StatisticsTextBlock.Text = $"Mn: {FormatStatistic(statistics.Mn)}   Mw: {FormatStatistic(statistics.Mw)}   PDI: {FormatStatistic(statistics.Pdi)} ({source})";
    }

    private static string FormatPeakStatistic(MolecularWeightPeak peak)
    {
        return $"#{peak.PeakId} {FormatStatistic(peak.Percent)}% Mn {FormatStatistic(peak.Mn)} Mw {FormatStatistic(peak.Mw)} PDI {FormatStatistic(peak.Pdi)}";
    }

    private static string FormatStatistic(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return "-";
        }

        var absoluteValue = Math.Abs(value.Value);
        if (absoluteValue <= double.Epsilon)
        {
            return "0";
        }

        if (absoluteValue is >= 0.01 and < 10000)
        {
            return value.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return value.Value.ToString("0.###E+0", CultureInfo.InvariantCulture);
    }

    private void SetMolecularWeightLogTicks()
    {
        if (_chromatogramPlot is null)
        {
            return;
        }

        var minExponent = (int)Math.Log10(MolecularWeightConverter.DefaultMinMolecularWeight);
        var maxExponent = (int)Math.Log10(MolecularWeightConverter.DefaultMaxMolecularWeight);
        var tickPositions = Enumerable
            .Range(minExponent, maxExponent - minExponent + 1)
            .Select(exponent => (double)exponent)
            .ToArray();
        var tickLabels = tickPositions
            .Select(exponent => $"10{ToSuperscript((int)exponent)}")
            .ToArray();

        _chromatogramPlot.Plot.Axes.Bottom.SetTicks(tickPositions, tickLabels);
    }

    private static string ToSuperscript(int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return string.Concat(text.Select(character => character switch
        {
            '-' => '⁻',
            '0' => '⁰',
            '1' => '¹',
            '2' => '²',
            '3' => '³',
            '4' => '⁴',
            '5' => '⁵',
            '6' => '⁶',
            '7' => '⁷',
            '8' => '⁸',
            '9' => '⁹',
            _ => character,
        }));
    }

    private void PopulateSolventComboBox()
    {
        if (_calibrationCurveSet is null)
        {
            return;
        }

        _updatingCalibrationSelection = true;
        try
        {
            SolventComboBox.ItemsSource = _calibrationCurveSet.Solvents;
            SolventComboBox.IsEnabled = _calibrationCurveSet.Solvents.Count > 0;
            SolventComboBox.SelectedItem = GuessPreferredSolvent(_calibrationCurveSet.Solvents)
                ?? _calibrationCurveSet.Solvents.FirstOrDefault();
        }
        finally
        {
            _updatingCalibrationSelection = false;
        }

        PopulateDetectorComboBox();
    }

    private void PopulateDetectorComboBox()
    {
        if (_calibrationCurveSet is null || SolventComboBox.SelectedItem is not string solvent)
        {
            _selectedCalibrationCurve = null;
            UpdateMolecularWeightAvailability();
            return;
        }

        var detectors = _calibrationCurveSet.GetDetectors(solvent);

        _updatingCalibrationSelection = true;
        try
        {
            DetectorComboBox.ItemsSource = detectors;
            DetectorComboBox.IsEnabled = detectors.Count > 0;
            DetectorComboBox.SelectedItem = detectors.FirstOrDefault(detector => detector.Equals("A", StringComparison.OrdinalIgnoreCase))
                ?? detectors.FirstOrDefault();
        }
        finally
        {
            _updatingCalibrationSelection = false;
        }

        SelectCalibrationCurve();
    }

    private void SelectCalibrationCurve()
    {
        _selectedCalibrationCurve = null;

        if (_calibrationCurveSet is not null
            && SolventComboBox.SelectedItem is string solvent
            && DetectorComboBox.SelectedItem is string detector)
        {
            _selectedCalibrationCurve = _calibrationCurveSet.GetCurve(solvent, detector);
            if (_currentDataset is not null && _currentDataset.TryGetDetectorDataset(detector, out _))
            {
                _currentDataset = _currentDataset.WithDetector(detector);
            }
        }

        UpdateMolecularWeightAvailability();

        if (_currentDataset is not null)
        {
            PlotCurrentDataset();
        }
    }

    private void UpdateMolecularWeightAvailability()
    {
        var canConvert = _currentDataset is not null && _selectedCalibrationCurve is not null;
        MolecularWeightCheckBox.IsEnabled = canConvert;
        MolecularWeightYModeComboBox.IsEnabled = canConvert && MolecularWeightCheckBox.IsChecked == true;

        if (!canConvert)
        {
            MolecularWeightCheckBox.IsChecked = false;
            MolecularWeightYModeComboBox.SelectedIndex = 0;
        }
    }

    private MolecularWeightYMode GetSelectedMolecularWeightYMode()
    {
        if (MolecularWeightYModeComboBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && tag.Equals("DwdLogM", StringComparison.OrdinalIgnoreCase))
        {
            return MolecularWeightYMode.DifferentialWeightFraction;
        }

        return MolecularWeightYMode.Signal;
    }

    private string? GuessPreferredSolvent(IReadOnlyList<string> solvents)
    {
        var sourceFilePath = _currentDataset?.SourceFilePath ?? string.Empty;
        return solvents.FirstOrDefault(solvent => sourceFilePath.Contains(solvent, StringComparison.OrdinalIgnoreCase))
            ?? solvents.FirstOrDefault(solvent => solvent.Equals("DMF", StringComparison.OrdinalIgnoreCase));
    }

    private void SetStatus(string message, bool isError)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
            : new SolidColorBrush(Color.FromRgb(71, 85, 105));
    }
}

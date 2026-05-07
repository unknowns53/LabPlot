using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScottPlot;
using SpectrumAnalyzer.Core;

namespace Spectrum_Visualization;

public partial class CalibrationCurveWindow : Window
{
    /// <summary>
    /// One dataset offered to the calibration editor. Carries the dataset
    /// itself plus a stable key (Title or SourceFilePath) that survives
    /// being saved and reloaded.
    /// </summary>
    public sealed class CalibrationDatasetInput
    {
        public required string DatasetKey { get; init; }

        public required string DisplayName { get; init; }

        public required SpectrumDataset Dataset { get; init; }
    }

    private readonly ObservableCollection<RowVm> _rowVms = new();
    private readonly IReadOnlyList<IntegrationRegion> _availableRegions;
    private readonly Dictionary<string, IntegrationRegion> _regionByLabel;
    private readonly string? _defaultOutputDirectory;

    private CalibrationCurveConfig _config;
    private CalibrationResult _lastResult = CalibrationResult.Empty(
        CalibrationQuantificationMode.SingleWavelength,
        CalibrationFitMode.ForceOrigin,
        1.0);

    private bool _suppressEvents;

    /// <summary>
    /// The configuration the parent window should pick up after the dialog
    /// is dismissed. Reflects every edit made in the editor regardless of
    /// whether the user closed via OK or Cancel — the parent is expected
    /// to keep it only when the dialog returned <c>true</c>.
    /// </summary>
    public CalibrationCurveConfig ResultConfig => _config;

    public CalibrationCurveWindow(
        CalibrationCurveConfig? sourceConfig,
        IReadOnlyList<CalibrationDatasetInput> datasets,
        IReadOnlyList<IntegrationRegion> availableRegions,
        string? defaultOutputDirectory = null)
    {
        InitializeComponent();

        _availableRegions = availableRegions;
        _regionByLabel = availableRegions
            .Where(r => !string.IsNullOrWhiteSpace(r.Label))
            .GroupBy(r => r.Label, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _defaultOutputDirectory = defaultOutputDirectory;

        _config = CloneOrDefault(sourceConfig);

        SamplesDataGrid.ItemsSource = _rowVms;

        BuildRowsFromDatasets(datasets);
        InitializeSettingsControls();
        Recalculate();
    }

    private static CalibrationCurveConfig CloneOrDefault(CalibrationCurveConfig? source)
    {
        if (source is null)
        {
            return new CalibrationCurveConfig();
        }

        return new CalibrationCurveConfig
        {
            Mode = source.Mode,
            WavelengthNm = source.WavelengthNm,
            IntegrationRegionLabel = source.IntegrationRegionLabel,
            PathLengthCm = source.PathLengthCm,
            FitMode = source.FitMode,
            ConcentrationUnit = source.ConcentrationUnit,
            MolarMass = source.MolarMass,
            Samples = source.Samples
                .Select(s => new CalibrationSample
                {
                    DatasetKey = s.DatasetKey,
                    ConcentrationInUnit = s.ConcentrationInUnit,
                    IsExcluded = s.IsExcluded,
                })
                .ToList<CalibrationSample>(),
        };
    }

    private void BuildRowsFromDatasets(IReadOnlyList<CalibrationDatasetInput> datasets)
    {
        // Pull saved per-sample state from the config so reopening the
        // editor remembers the user's previous concentrations and exclusions.
        var savedByKey = _config.Samples.ToDictionary(s => s.DatasetKey, StringComparer.Ordinal);

        foreach (var ds in datasets)
        {
            savedByKey.TryGetValue(ds.DatasetKey, out var saved);
            var vm = new RowVm(ds.DatasetKey, ds.DisplayName, ds.Dataset)
            {
                ConcentrationInUnit = saved?.ConcentrationInUnit,
                IsExcluded = saved?.IsExcluded ?? false,
            };
            vm.PropertyChanged += RowVm_PropertyChanged;
            _rowVms.Add(vm);
        }
    }

    private void InitializeSettingsControls()
    {
        _suppressEvents = true;
        try
        {
            SelectComboBoxByTag(ModeComboBox, _config.Mode.ToString());
            SelectComboBoxByTag(FitModeComboBox, _config.FitMode.ToString());
            SelectComboBoxByTag(UnitComboBox, _config.ConcentrationUnit.ToString());

            WavelengthTextBox.Text = _config.WavelengthNm.ToString("0.###", CultureInfo.InvariantCulture);
            PathLengthTextBox.Text = _config.PathLengthCm.ToString("0.###", CultureInfo.InvariantCulture);
            MolarMassTextBox.Text = _config.MolarMass is { } mw
                ? mw.ToString("0.###", CultureInfo.InvariantCulture)
                : string.Empty;

            BuildRegionComboBox();
            ApplyModeVisibility();
            ApplyUnitVisibility();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void BuildRegionComboBox()
    {
        RegionComboBox.Items.Clear();

        if (_availableRegions.Count == 0)
        {
            // Surface a clear hint when no regions are defined; the combo
            // box stays empty and the user gets pushed back to single-
            // wavelength mode.
            RegionComboBox.IsEnabled = false;
            return;
        }

        RegionComboBox.IsEnabled = true;
        foreach (var region in _availableRegions)
        {
            RegionComboBox.Items.Add(new ComboBoxItem
            {
                Content = region.Label,
                Tag = region.Label,
            });
        }

        if (!string.IsNullOrWhiteSpace(_config.IntegrationRegionLabel)
            && SelectComboBoxByTag(RegionComboBox, _config.IntegrationRegionLabel))
        {
            return;
        }

        if (RegionComboBox.Items.Count > 0)
        {
            RegionComboBox.SelectedIndex = 0;
            _config.IntegrationRegionLabel = (RegionComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        }
    }

    private static bool SelectComboBoxByTag(ComboBox combo, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    private void ApplyModeVisibility()
    {
        var isSingleWavelength = _config.Mode == CalibrationQuantificationMode.SingleWavelength;
        WavelengthLabel.Text = isSingleWavelength ? "波長 [nm]" : "積分領域";
        WavelengthTextBox.Visibility = isSingleWavelength ? Visibility.Visible : Visibility.Collapsed;
        RegionComboBox.Visibility = isSingleWavelength ? Visibility.Collapsed : Visibility.Visible;

        if (!isSingleWavelength && _availableRegions.Count == 0)
        {
            StatusTextBlock.Text = "積分領域が定義されていません。書式パネルの『積分』で領域を追加してください。";
        }
        else if (StatusTextBlock.Text.StartsWith("積分領域が定義"))
        {
            StatusTextBlock.Text = string.Empty;
        }
    }

    private void ApplyUnitVisibility()
    {
        var unit = _config.ConcentrationUnit;
        var requiresMolarMass = CalibrationUnitConverter.RequiresMolarMass(unit);
        MolarMassPanel.Visibility = requiresMolarMass ? Visibility.Visible : Visibility.Collapsed;

        UnitHelpTextBlock.Text = requiresMolarMass
            ? $"濃度は {CalibrationUnitConverter.GetSymbol(unit)} で入力します。mol/L へ換算するため、上の分子量も入力してください。"
            : $"濃度は {CalibrationUnitConverter.GetSymbol(unit)} で入力します。空欄の行はフィットから自動的に除外されます。";
    }

    // ----- Event handlers ---------------------------------------------------

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var tag = (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _config.Mode = string.Equals(tag, "IntegrationArea", StringComparison.Ordinal)
            ? CalibrationQuantificationMode.IntegrationArea
            : CalibrationQuantificationMode.SingleWavelength;
        ApplyModeVisibility();
        Recalculate();
    }

    private void FitModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var tag = (FitModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _config.FitMode = string.Equals(tag, "WithIntercept", StringComparison.Ordinal)
            ? CalibrationFitMode.WithIntercept
            : CalibrationFitMode.ForceOrigin;
        Recalculate();
    }

    private void UnitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var tag = (UnitComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (Enum.TryParse<CalibrationConcentrationUnit>(tag, out var unit))
        {
            _config.ConcentrationUnit = unit;
            ApplyUnitVisibility();
            Recalculate();
        }
    }

    private void RegionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        _config.IntegrationRegionLabel = (RegionComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        Recalculate();
    }

    private void WavelengthTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (TryParseDouble(WavelengthTextBox.Text, out var value) && value > 0)
        {
            _config.WavelengthNm = value;
        }

        Recalculate();
    }

    private void PathLengthTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (TryParseDouble(PathLengthTextBox.Text, out var value) && value > 0)
        {
            _config.PathLengthCm = value;
        }

        Recalculate();
    }

    private void MolarMassTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var text = MolarMassTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _config.MolarMass = null;
        }
        else if (TryParseDouble(text, out var value) && value > 0)
        {
            _config.MolarMass = value;
        }

        Recalculate();
    }

    private void RowVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (e.PropertyName is nameof(RowVm.ConcentrationInUnit) or nameof(RowVm.IsExcluded))
        {
            Recalculate();
        }
    }

    private void SamplesDataGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // The two-way binding fires after the commit, but we trigger an
        // explicit recompute on cell-edit so the predicted / residual
        // columns refresh as soon as the user tabs out of the cell —
        // otherwise the bound row stays stale until the next focus change.
        Dispatcher.BeginInvoke(new Action(Recalculate));
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        FlushConfigFromEditors();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        FlushConfigFromEditors();
        Recalculate();

        if (!_lastResult.HasFit)
        {
            StatusTextBlock.Text = "フィットがまだ確定していないため出力できません（濃度を 2 件以上入力してください）。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "検量線結果を保存",
            Filter = "Excelブック (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            FileName = "calibration_curve",
        };

        if (!string.IsNullOrWhiteSpace(_defaultOutputDirectory) && Directory.Exists(_defaultOutputDirectory))
        {
            dialog.InitialDirectory = _defaultOutputDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var rows = _rowVms.Zip(_lastResult.Points, (vm, point) => new CalibrationExportRow
        {
            DatasetName = vm.DisplayName,
            ConcentrationInUnit = vm.ConcentrationInUnit,
            ConcentrationMolar = point.ConcentrationMolar,
            Signal = point.Signal,
            Predicted = point.Predicted,
            Residual = point.Residual,
            IsExcluded = point.IsExcluded,
        }).ToArray();

        var export = new CalibrationExport
        {
            Config = _config,
            Result = _lastResult,
            Rows = rows,
        };

        try
        {
            var ext = Path.GetExtension(dialog.FileName);
            if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase))
            {
                export.WriteCsv(dialog.FileName);
            }
            else
            {
                export.WriteXlsx(dialog.FileName);
            }

            StatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("SuccessForegroundBrush");
            StatusTextBlock.Text = $"保存しました: {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("ErrorForegroundBrush");
            StatusTextBlock.Text = $"保存に失敗しました: {ex.Message}";
        }
    }

    // ----- Core recalculation ----------------------------------------------

    private void FlushConfigFromEditors()
    {
        // Make sure the latest free-text edits and the per-row state are
        // reflected on the config object before it leaves the dialog.
        if (TryParseDouble(WavelengthTextBox.Text, out var wl) && wl > 0)
        {
            _config.WavelengthNm = wl;
        }

        if (TryParseDouble(PathLengthTextBox.Text, out var l) && l > 0)
        {
            _config.PathLengthCm = l;
        }

        var molarMassText = MolarMassTextBox.Text;
        if (string.IsNullOrWhiteSpace(molarMassText))
        {
            _config.MolarMass = null;
        }
        else if (TryParseDouble(molarMassText, out var mw) && mw > 0)
        {
            _config.MolarMass = mw;
        }

        // Replace the persisted samples list with the current grid state —
        // includes rows whose concentration has been cleared so the
        // exclusion is remembered next session.
        _config.Samples = _rowVms.Select(vm => new CalibrationSample
        {
            DatasetKey = vm.DatasetKey,
            ConcentrationInUnit = vm.ConcentrationInUnit,
            IsExcluded = vm.IsExcluded,
        }).ToList<CalibrationSample>();
    }

    private void Recalculate()
    {
        if (_suppressEvents) return;

        FlushConfigFromEditors();

        var inputs = new List<CalibrationFitInput>(_rowVms.Count);
        foreach (var vm in _rowVms)
        {
            var concentrationMolar = vm.ConcentrationInUnit is { } c
                ? CalibrationUnitConverter.ToMolar(c, _config.ConcentrationUnit, _config.MolarMass)
                : null;
            var signal = SpectrumQuantifier.Quantify(vm.Dataset, _config, _availableRegions);

            inputs.Add(new CalibrationFitInput
            {
                DatasetKey = vm.DatasetKey,
                DisplayName = vm.DisplayName,
                ConcentrationMolar = concentrationMolar,
                Signal = signal,
                IsExcluded = vm.IsExcluded,
            });
        }

        _lastResult = CalibrationFitter.Fit(
            inputs,
            _config.FitMode,
            _config.Mode,
            _config.PathLengthCm);

        // Update each row's display fields. We zip strictly by index so
        // the ordering matches what the fitter saw.
        for (var i = 0; i < _rowVms.Count; i++)
        {
            var vm = _rowVms[i];
            var input = inputs[i];
            var point = _lastResult.Points[i];

            vm.SetDerivedFields(
                concentrationMolar: input.ConcentrationMolar,
                signal: input.Signal,
                predicted: point.Predicted,
                residual: point.Residual);
        }

        DrawCalibrationPlot();
        UpdateResultSummary();
    }

    private void DrawCalibrationPlot()
    {
        var plot = CalibrationPlotHost.Plot;
        plot.Clear();

        var xs = new List<double>();
        var ys = new List<double>();
        var excludedXs = new List<double>();
        var excludedYs = new List<double>();

        foreach (var point in _lastResult.Points)
        {
            if (!point.HasSignal) continue;
            if (point.IsExcluded)
            {
                excludedXs.Add(point.ConcentrationMolar);
                excludedYs.Add(point.Signal);
            }
            else
            {
                xs.Add(point.ConcentrationMolar);
                ys.Add(point.Signal);
            }
        }

        if (xs.Count > 0)
        {
            var scatter = plot.Add.ScatterPoints(xs.ToArray(), ys.ToArray());
            scatter.Color = ScottPlot.Colors.Blue;
            scatter.MarkerSize = 8;
        }

        if (excludedXs.Count > 0)
        {
            var excluded = plot.Add.ScatterPoints(excludedXs.ToArray(), excludedYs.ToArray());
            excluded.Color = ScottPlot.Colors.Gray.WithAlpha(0.6);
            excluded.MarkerSize = 8;
        }

        if (_lastResult.HasFit)
        {
            var allXs = xs.Concat(excludedXs).ToArray();
            if (allXs.Length > 0)
            {
                var xMin = allXs.Min();
                var xMax = allXs.Max();
                if (xMin == xMax) xMax = xMin + 1e-12;
                var pad = (xMax - xMin) * 0.05;
                var x1 = Math.Max(0, xMin - pad);
                var x2 = xMax + pad;
                var y1 = _lastResult.Slope * x1 + _lastResult.Intercept;
                var y2 = _lastResult.Slope * x2 + _lastResult.Intercept;

                var line = plot.Add.Line(x1, y1, x2, y2);
                line.LineWidth = 2;
                line.Color = ScottPlot.Colors.Crimson;
            }
        }

        plot.XLabel("Concentration / M");
        plot.YLabel(SpectrumQuantifier.GetSignalLabel(_config));
        plot.Axes.AutoScale();
        CalibrationPlotHost.Refresh();
    }

    private void UpdateResultSummary()
    {
        if (!_lastResult.HasFit)
        {
            ResultSummaryTextBlock.Text =
                "フィット未確定（濃度・信号が揃った行が 2 件未満）";
            return;
        }

        var lines = new List<string>
        {
            $"slope = {FormatScientific(_lastResult.Slope)}",
        };

        if (_lastResult.FitMode == CalibrationFitMode.WithIntercept)
        {
            lines.Add($"intercept = {FormatScientific(_lastResult.Intercept)}");
        }

        if (_lastResult.QuantificationMode == CalibrationQuantificationMode.SingleWavelength)
        {
            lines.Add($"ε = {FormatScientific(_lastResult.EpsilonPerCmPerMolar)} M⁻¹·cm⁻¹  (l = {_lastResult.PathLengthCm:0.###} cm)");
        }

        var rSquaredText = double.IsFinite(_lastResult.RSquared)
            ? _lastResult.RSquared.ToString("0.0000", CultureInfo.InvariantCulture)
            : "—";
        lines.Add($"R² = {rSquaredText}    N = {_lastResult.N}");

        ResultSummaryTextBlock.Text = string.Join("\n", lines);

        StatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("WarningForegroundBrush");
        StatusTextBlock.Text = string.Empty;
    }

    private static string FormatScientific(double value)
    {
        if (!double.IsFinite(value)) return "—";
        var abs = Math.Abs(value);
        if (abs == 0) return "0";
        if (abs >= 1e4 || abs < 1e-3)
        {
            return value.ToString("0.000E+0", CultureInfo.InvariantCulture);
        }

        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        value = double.NaN;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ----- Row view-model ---------------------------------------------------

    private sealed class RowVm : INotifyPropertyChanged
    {
        public RowVm(string datasetKey, string displayName, SpectrumDataset dataset)
        {
            DatasetKey = datasetKey;
            DisplayName = displayName;
            Dataset = dataset;
        }

        public string DatasetKey { get; }

        public string DisplayName { get; }

        public SpectrumDataset Dataset { get; }

        private double? _concentrationInUnit;
        public double? ConcentrationInUnit
        {
            get => _concentrationInUnit;
            set
            {
                if (Nullable.Equals(_concentrationInUnit, value)) return;
                _concentrationInUnit = value;
                Raise(nameof(ConcentrationInUnit));
                Raise(nameof(ConcentrationText));
            }
        }

        public string ConcentrationText
        {
            get => _concentrationInUnit is { } v
                ? v.ToString("G6", CultureInfo.InvariantCulture)
                : string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    ConcentrationInUnit = null;
                    return;
                }

                if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && double.IsFinite(parsed))
                {
                    ConcentrationInUnit = parsed;
                }
                else
                {
                    // Reject the bad input but fire a notification so the
                    // editor reverts to the last good value.
                    Raise(nameof(ConcentrationText));
                }
            }
        }

        private bool _isExcluded;
        public bool IsExcluded
        {
            get => _isExcluded;
            set
            {
                if (_isExcluded == value) return;
                _isExcluded = value;
                Raise(nameof(IsExcluded));
            }
        }

        // Derived fields written by the parent after each fit.
        private double? _concentrationMolar;
        public string ConcentrationMolarText =>
            _concentrationMolar is { } c && double.IsFinite(c)
                ? c.ToString("0.###E+0", CultureInfo.InvariantCulture)
                : string.Empty;

        private double _signal = double.NaN;
        public string SignalText =>
            double.IsFinite(_signal)
                ? _signal.ToString("0.####", CultureInfo.InvariantCulture)
                : string.Empty;

        private double _predicted = double.NaN;
        public string PredictedText =>
            double.IsFinite(_predicted)
                ? _predicted.ToString("0.####", CultureInfo.InvariantCulture)
                : string.Empty;

        private double _residual = double.NaN;
        public string ResidualText =>
            double.IsFinite(_residual)
                ? _residual.ToString("+0.####;-0.####;0", CultureInfo.InvariantCulture)
                : string.Empty;

        public void SetDerivedFields(double? concentrationMolar, double signal, double predicted, double residual)
        {
            _concentrationMolar = concentrationMolar;
            _signal = signal;
            _predicted = predicted;
            _residual = residual;
            Raise(nameof(ConcentrationMolarText));
            Raise(nameof(SignalText));
            Raise(nameof(PredictedText));
            Raise(nameof(ResidualText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

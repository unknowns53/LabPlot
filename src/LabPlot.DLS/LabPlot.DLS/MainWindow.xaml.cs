using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DlsAnalyzer.Core;
using Microsoft.Win32;
using ScottPlot.WPF;

namespace LabPlot.DLS;

public partial class MainWindow : Window
{
    private readonly ZetasizerXlsxReader _reader = new();
    private readonly List<DlsDataset> _datasets = new();
    private WpfPlot? _plot;
    private DlsDataset? _selectedDataset;
    private DistributionMode _selectedMode = DistributionMode.Number;
    private int _selectedRunIndex;
    private bool _suppressRunComboEvents;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _plot = new WpfPlot();
            PlotHost.Children.Clear();
            PlotHost.Children.Add(_plot);
            InitializeEmptyPlot();
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
        _plot.Plot.Title("Particle Size Distribution");
        _plot.Plot.XLabel("Size (d.nm)");
        _plot.Plot.YLabel("Number (%)");
        ApplyLogXTicks();
        _plot.Plot.Axes.SetLimits(Math.Log10(0.3), Math.Log10(10000), 0, 30);
        _plot.Refresh();
    }

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

            DatasetListBox.ItemsSource = null;
            DatasetListBox.ItemsSource = _datasets;
            DatasetCountText.Text = _datasets.Count == 0
                ? "粒径分布シートが見つかりませんでした"
                : $"{_datasets.Count} シート読み込み済み（{Path.GetFileName(dialog.FileName)}）";

            HideError();

            if (_datasets.Count > 0)
                DatasetListBox.SelectedIndex = 0;
            else
                ClearActiveDataset();
        }
        catch (Exception ex)
        {
            ShowError($"読み込みに失敗しました: {ex.Message}");
        }
    }

    private void DatasetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDataset = DatasetListBox.SelectedItem as DlsDataset;
        UpdateRunCombo();
        UpdateDistributionTypeAvailability();
        RefreshPlot();
    }

    private void DistributionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DistributionTypeComboBox.SelectedItem is not ComboBoxItem item) return;
        _selectedMode = (item.Tag as string) switch
        {
            "Intensity" => DistributionMode.Intensity,
            "Volume" => DistributionMode.Volume,
            _ => DistributionMode.Number,
        };
        UpdateRunCombo();
        RefreshPlot();
    }

    private void RunComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRunComboEvents) return;
        _selectedRunIndex = Math.Max(0, RunComboBox.SelectedIndex);
        RefreshPlot();
    }

    private void ClearActiveDataset()
    {
        _selectedDataset = null;
        InitializeEmptyPlot();
    }

    private void UpdateDistributionTypeAvailability()
    {
        // Disable distribution kinds that are not present on the active dataset.
        for (int i = 0; i < DistributionTypeComboBox.Items.Count; i++)
        {
            if (DistributionTypeComboBox.Items[i] is not ComboBoxItem item) continue;
            var mode = (item.Tag as string) switch
            {
                "Intensity" => DistributionMode.Intensity,
                "Volume" => DistributionMode.Volume,
                _ => DistributionMode.Number,
            };
            item.IsEnabled = GetDistribution(_selectedDataset, mode) is not null;
        }
    }

    private void UpdateRunCombo()
    {
        var distribution = GetDistribution(_selectedDataset, _selectedMode);
        _suppressRunComboEvents = true;
        try
        {
            RunComboBox.Items.Clear();
            if (distribution is null || distribution.RunCount == 0)
            {
                RunComboBox.IsEnabled = false;
                _selectedRunIndex = 0;
                return;
            }

            for (int i = 0; i < distribution.RunCount; i++)
                RunComboBox.Items.Add(new ComboBoxItem { Content = $"Run {i + 1}" });

            RunComboBox.IsEnabled = distribution.RunCount > 1;
            _selectedRunIndex = Math.Clamp(_selectedRunIndex, 0, distribution.RunCount - 1);
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

        if (_selectedDataset is null)
        {
            InitializeEmptyPlot();
            return;
        }

        var distribution = GetDistribution(_selectedDataset, _selectedMode);
        if (distribution is null || distribution.RunCount == 0)
        {
            _plot.Plot.Clear();
            _plot.Plot.Title($"{_selectedDataset.SheetName} ({ModeLabel(_selectedMode)})");
            _plot.Plot.XLabel("Size (d.nm)");
            _plot.Plot.YLabel(ModeLabel(_selectedMode));
            ApplyLogXTicks();
            _plot.Refresh();
            return;
        }

        var runIndex = Math.Clamp(_selectedRunIndex, 0, distribution.RunCount - 1);
        var run = distribution.Runs[runIndex];
        var sizes = distribution.SizeBinsNm;
        var n = Math.Min(run.Count, sizes.Count);

        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = Math.Log10(Math.Max(sizes[i], 1e-6));
            ys[i] = run[i];
        }

        _plot.Plot.Clear();
        var scatter = _plot.Plot.Add.Scatter(xs, ys);
        scatter.LineWidth = 2;
        scatter.MarkerSize = 0;
        _plot.Plot.Title($"{_selectedDataset.SheetName} ({ModeLabel(_selectedMode)}, Run {runIndex + 1})");
        _plot.Plot.XLabel("Size (d.nm)");
        _plot.Plot.YLabel(ModeLabel(_selectedMode));
        ApplyLogXTicks();
        _plot.Plot.Axes.AutoScale();
        _plot.Refresh();
    }

    private void ApplyLogXTicks()
    {
        if (_plot is null) return;

        // Render the X axis as log10(d.nm) with major ticks at 0.1, 1, 10,
        // 100, 1000, 10000 nm and minor ticks at the 2x..9x positions per
        // decade (matches the GPC molecular-weight axis approach).
        var generator = new ScottPlot.TickGenerators.NumericManual();
        for (int exponent = -1; exponent <= 4; exponent++)
        {
            var label = exponent switch
            {
                < 0 => (Math.Pow(10, exponent)).ToString("0.#", CultureInfo.InvariantCulture),
                _ => Math.Pow(10, exponent).ToString("0", CultureInfo.InvariantCulture),
            };
            generator.AddMajor(exponent, label);
            if (exponent < 4)
            {
                for (int multiplier = 2; multiplier <= 9; multiplier++)
                    generator.AddMinor(exponent + Math.Log10(multiplier));
            }
        }
        _plot.Plot.Axes.Bottom.TickGenerator = generator;
    }

    private static ParticleSizeDistribution? GetDistribution(DlsDataset? dataset, DistributionMode mode) => mode switch
    {
        DistributionMode.Number => dataset?.NumberDistribution,
        DistributionMode.Intensity => dataset?.IntensityDistribution,
        DistributionMode.Volume => dataset?.VolumeDistribution,
        _ => null,
    };

    private static string ModeLabel(DistributionMode mode) => mode switch
    {
        DistributionMode.Intensity => "Intensity (%)",
        DistributionMode.Volume => "Volume (%)",
        _ => "Number (%)",
    };

    private void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private enum DistributionMode
    {
        Number,
        Intensity,
        Volume,
    }
}

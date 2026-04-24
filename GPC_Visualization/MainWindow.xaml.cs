using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using GpcAnalyzer.Core;
using Microsoft.Win32;
using ScottPlot.WPF;

namespace GPC_Visualization;

public partial class MainWindow : Window
{
    private readonly IGpcDataReader _reader = new CsvGpcDataReader();
    private GpcDataset? _currentDataset;
    private WpfPlot? _chromatogramPlot;

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

            FilePathTextBlock.Text = dialog.FileName;
            PlotDataset(dataset);
            SaveGraphButton.IsEnabled = _chromatogramPlot is not null;
            SetStatus($"{dataset.Points.Count:N0} 点のデータを読み込みました。", false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            _currentDataset = null;
            SaveGraphButton.IsEnabled = false;
            SetStatus($"読み込みに失敗しました: {ex.Message}", true);
        }
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
                PlotDataset(_currentDataset);
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
        _chromatogramPlot.Refresh();
    }

    private void PlotDataset(GpcDataset dataset)
    {
        if (_chromatogramPlot is null)
        {
            SetStatus("グラフ表示を初期化中です。少し待ってからもう一度お試しください。", true);
            return;
        }

        var xs = dataset.Points.Select(point => point.X).ToArray();
        var ys = dataset.Points.Select(point => point.Y).ToArray();

        _chromatogramPlot.Plot.Clear();
        var signal = _chromatogramPlot.Plot.Add.Scatter(xs, ys);
        signal.LegendText = "Signal";
        signal.LineWidth = 1.5f;
        signal.MarkerSize = 0;

        _chromatogramPlot.Plot.Title(Path.GetFileName(dataset.SourceFilePath) ?? "GPC chromatogram");
        _chromatogramPlot.Plot.XLabel(dataset.XLabel);
        _chromatogramPlot.Plot.YLabel(dataset.YLabel);
        _chromatogramPlot.Plot.Axes.AutoScale();
        _chromatogramPlot.Refresh();
    }

    private void SetStatus(string message, bool isError)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
            : new SolidColorBrush(Color.FromRgb(71, 85, 105));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    private readonly JdfReader _reader = new();
    private AvaPlot? _plot;
    private NmrDataset? _dataset;

    public MainWindow()
    {
        // Avalonia.Generators emits InitializeComponent + the x:Name fields, so
        // no manual definition here (a hand-written one re-nulls the generated
        // fields and NREs — the Phase 7 Batch 6 trap the other modules note).
        InitializeComponent();
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

        _plot.Plot.XLabel("Chemical shift (ppm)");
        _plot.Plot.YLabel("Intensity");
        _plot.Refresh();
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
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JEOL NMR データ") { Patterns = new[] { "*.jdf" } },
                FilePickerFileTypes.All,
            },
        });

        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            LoadFile(path);
        }
    }

    private void LoadFile(string path)
    {
        try
        {
            _dataset = _reader.Read(path);
            PlotDataset(_dataset);
            StatusText.Text = _dataset.Title is { Length: > 0 } title
                ? $"{title}  ({_dataset.RealValues.Count} 点)"
                : $"{System.IO.Path.GetFileName(path)}  ({_dataset.RealValues.Count} 点)";
        }
        catch (Exception ex)
        {
            Toast?.Show($"読み込みに失敗しました: {ex.Message}", StatusSeverity.Error, 5000);
            StatusText.Text = "読み込みに失敗しました。";
        }
    }

    private void PlotDataset(NmrDataset dataset)
    {
        if (_plot is null)
        {
            return;
        }

        _plot.Plot.Clear();
        var scatter = _plot.Plot.Add.Scatter(dataset.XValues, dataset.YValues);
        scatter.MarkerSize = 0; // line only
        _plot.Plot.XLabel("Chemical shift (ppm)");
        _plot.Plot.YLabel("Intensity");
        _plot.Plot.Axes.AutoScale();

        // ppm axes are displayed descending — high ppm on the left. Flip X
        // while keeping the auto-scaled margins.
        var limits = _plot.Plot.Axes.GetLimits();
        var high = Math.Max(limits.Left, limits.Right);
        var low = Math.Min(limits.Left, limits.Right);
        _plot.Plot.Axes.SetLimitsX(high, low);

        _plot.Refresh();
    }

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

        var path = e.DataTransfer.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            e.Handled = true;
            LoadFile(path);
        }
    }

    public async Task OpenFilesAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths is null || filePaths.Count == 0)
        {
            return;
        }

        await this.WhenLoadedAsync();
        LoadFile(filePaths[0]);
    }
}

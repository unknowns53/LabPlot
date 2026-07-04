using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LabPlot.Core.Avalonia.Helpers;
using LabPlot.NMR.Avalonia;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// NMR (LabPlot.NMR.Avalonia) 用スクリーンショットシナリオ。実機の .jdf は測定者名など
/// 個人情報をヘッダーに含みうるため、ここでは合成の 1D 処理済み .jdf をプロセス内で
/// 組み立てて開く（デモ用の固定バイナリはリポジトリに置かない）。
/// </summary>
internal static class NmrScenarios
{
    public static ScreenshotScenario[] All { get; } =
    {
        new("nmr/00-startup.png", CaptureStartupAsync),
        new("nmr/10-data-loaded.png", CaptureDataLoadedAsync),
        new("nmr/20-overlay.png", CaptureOverlayAsync),
        new("nmr/30-analysis.png", CaptureAnalysisAsync),
        new("nmr/40-stack.png", CaptureStackAsync),
    };

    private static async Task CaptureStartupAsync(ShotContext ctx)
    {
        var window = CreateWindow();
        await ctx.CaptureAsync(window, "nmr/00-startup.png");
    }

    private static async Task CaptureDataLoadedAsync(ShotContext ctx)
    {
        var window = CreateWindow();
        await ctx.ShowAsync(window);

        var jdf = WriteSyntheticJdf(0.0);
        await ((IPortalFileOpener)window).OpenFilesAsync(new[] { jdf });
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "nmr/10-data-loaded.png");
    }

    private static async Task CaptureOverlayAsync(ShotContext ctx)
    {
        var window = CreateWindow();
        await ctx.ShowAsync(window);

        var overlay = window.FindControl<CheckBox>("OverlayCheckBox")
            ?? throw new InvalidOperationException("OverlayCheckBox が見つからない。");
        overlay.IsChecked = true;
        await ShotContext.SettleAsync();

        // Two spectra with a small shift so the overlay is visibly two traces.
        var a = WriteSyntheticJdf(0.0);
        var b = WriteSyntheticJdf(0.15);
        await ((IPortalFileOpener)window).OpenFilesAsync(new[] { a, b });
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "nmr/20-overlay.png");
    }

    private static async Task CaptureAnalysisAsync(ShotContext ctx)
    {
        var window = CreateWindow();
        await ctx.ShowAsync(window);

        var jdf = WriteSyntheticJdf(0.0);
        await ((IPortalFileOpener)window).OpenFilesAsync(new[] { jdf });
        await ShotContext.SettleAsync();

        // Detect peaks.
        Click(window, "DetectPeaksButton");
        await ShotContext.SettleAsync();

        // Add an integration region around the 1.25 ppm peak.
        SetText(window, "RegionMinTextBox", "1.0");
        SetText(window, "RegionMaxTextBox", "1.5");
        Click(window, "AddRegionButton");
        await ShotContext.SettleAsync();

        // And a reference region around the CDCl3 peak at 7.26 ppm.
        SetText(window, "RegionMinTextBox", "7.0");
        SetText(window, "RegionMaxTextBox", "7.5");
        Click(window, "AddRegionButton");
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "nmr/30-analysis.png");
    }

    private static async Task CaptureStackAsync(ShotContext ctx)
    {
        var window = CreateWindow();
        await ctx.ShowAsync(window);

        var overlay = window.FindControl<CheckBox>("OverlayCheckBox")
            ?? throw new InvalidOperationException("OverlayCheckBox が見つからない。");
        overlay.IsChecked = true;
        await ShotContext.SettleAsync();

        var a = WriteSyntheticJdf(0.0);
        var b = WriteSyntheticJdf(0.15);
        var c = WriteSyntheticJdf(0.30);
        await ((IPortalFileOpener)window).OpenFilesAsync(new[] { a, b, c });
        await ShotContext.SettleAsync();

        Click(window, "StackButton");
        await ShotContext.SettleAsync();

        await ctx.CaptureAsync(window, "nmr/40-stack.png");
    }

    private static void Click(MainWindow window, string name)
    {
        var button = window.FindControl<Button>(name)
            ?? throw new InvalidOperationException($"{name} が見つからない。");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void SetText(MainWindow window, string name, string value)
    {
        var box = window.FindControl<TextBox>(name)
            ?? throw new InvalidOperationException($"{name} が見つからない。");
        box.Text = value;
    }

    private static MainWindow CreateWindow()
    {
        var window = new MainWindow();
        IsolationHelper.UseFreshAppData("nmr");
        return window;
    }

    /// <summary>
    /// Build a synthetic 1D processed .jdf (float64, complex, big-endian body)
    /// with a few Gaussian peaks resembling a ¹H spectrum, shifted by
    /// <paramref name="shiftPpm"/>, and write it to a temp file. Field offsets
    /// mirror NMRAnalyzer.Core.JdfReader.
    /// </summary>
    private static string WriteSyntheticJdf(double shiftPpm)
    {
        const int n = 2048;
        const double axisStart = 12.0;  // high ppm (left)
        const double axisStop = -1.0;   // low ppm (right)

        var real = new double[n];
        for (var i = 0; i < n; i++)
        {
            var ppm = axisStart + (axisStop - axisStart) * i / (n - 1);
            real[i] =
                Gaussian(ppm, 7.26 + shiftPpm, 0.03, 1.0) +   // CDCl3 residual
                Gaussian(ppm, 3.65 + shiftPpm, 0.03, 0.8) +
                Gaussian(ppm, 1.25 + shiftPpm, 0.03, 1.2) +
                Gaussian(ppm, 0.00, 0.02, 0.15);              // TMS
        }

        var header = new byte[1360];
        header[8] = 0;            // endian: big-endian body
        header[14] = 0b0000_0001; // info: dataType=float64, dataFormat=one_d
        header[24] = 3;           // data_axis_type[0] = complex
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(176), n);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(208), 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(240), (uint)(n - 1));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(272), (ulong)BitConverter.DoubleToInt64Bits(axisStart));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(336), (ulong)BitConverter.DoubleToInt64Bits(axisStop));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1284), 1360);
        System.Text.Encoding.ASCII.GetBytes("Synthetic 1H NMR").CopyTo(header, 48);

        // Body: real section then imaginary section (all zeros here).
        var body = new byte[n * 2 * 8];
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(
                body.AsSpan(i * 8), (ulong)BitConverter.DoubleToInt64Bits(real[i]));
        }

        var path = Path.Combine(Path.GetTempPath(), $"nmr-synthetic-{Guid.NewGuid():N}.jdf");
        using var stream = File.Create(path);
        stream.Write(header);
        stream.Write(body);
        return path;
    }

    private static double Gaussian(double x, double center, double width, double height) =>
        height * Math.Exp(-Math.Pow(x - center, 2) / (2 * width * width));
}

using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// 1 枚のスクリーンショット撮影に必要な共通処理をまとめたヘルパー。
/// Window を Show し、レンダリングが安定するまで dispatcher job / render tick を
/// 複数回回してから <c>CaptureRenderedFrame</c> (Avalonia.Headless) で PNG 保存する。
/// 出力先は <c>&lt;repoRoot&gt;/artifacts/screenshots/&lt;relativePath&gt;</c>。
/// </summary>
internal sealed class ShotContext
{
    public string RepoRoot { get; }

    private readonly string _outputRoot;

    public ShotContext(string repoRoot, string outputRoot)
    {
        RepoRoot = repoRoot;
        _outputRoot = outputRoot;
    }

    /// <summary>
    /// Window を Show して撮影する。ScottPlot (AvaPlot) 等の非同期描画パスが完了する前に
    /// フレームを取ると空白 / 未確定状態が写ることがあったため、
    /// Dispatcher.UIThread.RunJobs() + AvaloniaHeadlessPlatform.ForceRenderTimerTick() +
    /// 短い Task.Delay の組を複数回繰り返して安定させてから CaptureRenderedFrame する。
    /// </summary>
    public async Task CaptureAsync(Window window, string relativePath)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        await SettleAsync();

        var outputPath = Path.Combine(_outputRoot, relativePath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException(
                $"CaptureRenderedFrame が null を返した (window={window.GetType().Name}, output={relativePath})。" +
                "UseHeadlessDrawing=false で AppBuilder をセットアップしているか確認する。");
        frame.Save(outputPath);

        Console.WriteLine($"[LabPlot.Screenshots]   -> {outputPath}");
    }

    /// <summary>
    /// レイアウト・描画パスが落ち着くまで dispatcher job / render tick / 短い delay の
    /// 組を複数回回す。CaptureAsync から呼ぶ他、ComboBox の Popup を開いた直後など
    /// 「撮影までに 1 拍待ちたい」場面から単独でも呼べるように公開している。
    /// </summary>
    public static async Task SettleAsync(int iterations = 6)
    {
        for (var i = 0; i < iterations; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(50);
        }
    }
}

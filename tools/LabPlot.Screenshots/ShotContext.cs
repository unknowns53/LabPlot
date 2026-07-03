using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using SkiaSharp;

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
    /// Window を Show してレンダリングが安定するまで待つ。撮影せずに続けて
    /// OpenFilesAsync 等の操作を行いたいシナリオ (IPortalFileOpener.OpenFilesAsync は
    /// Window.Loaded を待つため、先に Show しておく必要がある) から使う。
    /// </summary>
    public async Task ShowAsync(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        await SettleAsync();
    }

    /// <summary>
    /// Window を Show して撮影する。ScottPlot (AvaPlot) 等の非同期描画パスが完了する前に
    /// フレームを取ると空白 / 未確定状態が写ることがあったため、
    /// Dispatcher.UIThread.RunJobs() + AvaloniaHeadlessPlatform.ForceRenderTimerTick() +
    /// 短い Task.Delay の組を複数回繰り返して安定させてから CaptureRenderedFrame する。
    /// </summary>
    public async Task CaptureAsync(Window window, string relativePath)
    {
        await ShowAsync(window);

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
    /// Window 全体をレンダリングしたうえで、<paramref name="cropRectInWindow"/> (Window 座標系,
    /// DIP 単位) の矩形だけを切り出して PNG 保存する。座標のハードコードを避けるため、
    /// 呼び出し側は対象コントロールの <c>Bounds</c> を <c>TranslatePoint</c> で Window 座標へ
    /// 変換してから渡す想定 (portal/10-titlebar.png, gpc/40-export.png で使用)。
    /// フレームは Avalonia の Bitmap として得られる (DIP と物理ピクセルが一致するとは限らない)
    /// ため、一旦 PNG にエンコードして SkiaSharp でデコードし直し、実ピクセルサイズとの比率
    /// から切り出し矩形をスケールしてから <see cref="SKBitmap.ExtractSubset"/> で切り出す。
    /// </summary>
    public async Task CaptureCroppedAsync(Window window, Rect cropRectInWindow, string relativePath)
    {
        await ShowAsync(window);

        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException(
                $"CaptureRenderedFrame が null を返した (window={window.GetType().Name}, output={relativePath})。");

        using var pngStream = new MemoryStream();
        frame.Save(pngStream);
        pngStream.Position = 0;

        using var full = SKBitmap.Decode(pngStream)
            ?? throw new InvalidOperationException($"SKBitmap.Decode に失敗した (output={relativePath})。");

        var scaleX = full.Width / window.ClientSize.Width;
        var scaleY = full.Height / window.ClientSize.Height;

        var pixelRect = new SKRectI(
            (int)Math.Round(cropRectInWindow.X * scaleX),
            (int)Math.Round(cropRectInWindow.Y * scaleY),
            (int)Math.Round((cropRectInWindow.X + cropRectInWindow.Width) * scaleX),
            (int)Math.Round((cropRectInWindow.Y + cropRectInWindow.Height) * scaleY));
        pixelRect = SKRectI.Intersect(pixelRect, new SKRectI(0, 0, full.Width, full.Height));

        if (pixelRect.Width <= 0 || pixelRect.Height <= 0)
        {
            throw new InvalidOperationException(
                $"クロップ範囲がフレーム範囲外になった (output={relativePath}, requested={cropRectInWindow}, frame={full.Width}x{full.Height})。");
        }

        using var cropped = new SKBitmap(pixelRect.Width, pixelRect.Height);
        if (!full.ExtractSubset(cropped, pixelRect))
        {
            throw new InvalidOperationException($"SKBitmap.ExtractSubset に失敗した (output={relativePath})。");
        }

        var outputPath = Path.Combine(_outputRoot, relativePath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var image = SKImage.FromBitmap(cropped);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var fileStream = File.Create(outputPath))
        {
            data.SaveTo(fileStream);
        }

        Console.WriteLine($"[LabPlot.Screenshots]   -> {outputPath} (cropped)");
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

    /// <summary>
    /// <paramref name="target"/> の下端が <paramref name="scrollViewer"/> の可視領域から
    /// はみ出ている場合だけ、はみ出た分 + <paramref name="bottomMargin"/> をスクロールして
    /// 見えるようにする。折り畳み Expander を展開した直後は展開前のレイアウトで計測しても
    /// ズレるため、呼び出し前に <see cref="SettleAsync"/> を挟んでおくこと。
    /// </summary>
    public static async Task ScrollIntoViewAsync(ScrollViewer scrollViewer, Control target, double bottomMargin = 12)
    {
        await SettleAsync();

        var topLeft = target.TranslatePoint(new Point(0, 0), scrollViewer) ?? new Point(0, 0);
        var bottomY = topLeft.Y + target.Bounds.Height;
        var viewportHeight = scrollViewer.Viewport.Height;

        if (bottomY > viewportHeight)
        {
            var delta = bottomY - viewportHeight + bottomMargin;
            var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var newOffsetY = Math.Min(maxOffsetY, scrollViewer.Offset.Y + delta);
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, newOffsetY);
        }

        await SettleAsync();
    }
}

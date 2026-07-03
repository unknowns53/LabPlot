using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using LabPlot.Shell.Avalonia;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// ユーザーガイド用スクリーンショットを Avalonia.Headless でオフスクリーン生成するハーネスの
/// エントリポイント。実デスクトップを占有せず、利用者本人の AppData も汚さない
/// (LABPLOT_APPDATA_OVERRIDE で fresh な一時ディレクトリへ隔離する)。
///
/// <para>
/// Main はあえて同期メソッドにしている。Avalonia.Headless には既定のメインループが無いため、
/// もし <c>async Task&lt;int&gt; Main</c> にすると CLR が生成する同期ラッパー
/// (<c>GetAwaiter().GetResult()</c>) が UI スレッドをブロックしたまま
/// <c>await Task.Delay(...)</c> 等の継続 (Avalonia の SynchronizationContext 経由で
/// Dispatcher キューに積まれる) を待つことになり、そのキューを誰も汲み出せずデッドロックする
/// (実機で確認済み: portal/00-launcher.png の撮影開始直後で無限ハング)。
/// <see cref="PumpUntilCompleted"/> で Dispatcher.UIThread.RunJobs() を回しながら
/// シナリオの Task を手動駆動することでこれを避ける。
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var repoRoot = ResolveRepoRoot();
        var outputRoot = Path.Combine(repoRoot, "artifacts", "screenshots");

        // AppBuilder 構築より前に隔離用の AppData override を設定する。App.axaml.cs や
        // 各モジュール MainWindow の formatting_config.json / MRU / window 状態などが
        // すべてこの一時ディレクトリ配下に書かれ、実プロファイルには一切触れない。
        var isolatedAppData = Path.Combine(
            Path.GetTempPath(),
            "LabPlotScreenshots",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedAppData);
        Environment.SetEnvironmentVariable("LABPLOT_APPDATA_OVERRIDE", isolatedAppData);

        BuildAvaloniaApp().SetupWithClassicDesktopLifetime(args);

        var context = new ShotContext(repoRoot, outputRoot);
        var runTask = RunScenariosAsync(context);
        PumpUntilCompleted(runTask);
        runTask.GetAwaiter().GetResult(); // 完了済みなので即座に返る。例外があればここで再送出。

        Console.WriteLine($"[LabPlot.Screenshots] done. isolated AppData: {isolatedAppData}");
        return 0;
    }

    private static async Task RunScenariosAsync(ShotContext context)
    {
        foreach (var scenario in Scenarios.All)
        {
            Console.WriteLine($"[LabPlot.Screenshots] {scenario.RelativePath} ...");
            await scenario.RunAsync(context);
        }
    }

    /// <summary>
    /// 渡された Task が完了するまで Dispatcher.UIThread.RunJobs() を手動で回すポンプ。
    /// このメソッドを呼んでいるスレッドが Avalonia の UI スレッド (Setup() を呼んだスレッド)
    /// である前提。
    /// </summary>
    private static void PumpUntilCompleted(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>AppContext.BaseDirectory から上方向に LabPlot.slnx を探してリポジトリルートを解決する。</summary>
    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LabPlot.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"LabPlot.slnx が見つからない (起点: {AppContext.BaseDirectory})。リポジトリルートを解決できない。");
    }
}

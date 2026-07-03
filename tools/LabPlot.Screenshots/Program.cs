using System;
using System.IO;
using System.Linq;
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
        var onlyPrefix = ParseOnlyFilter(args, out var avaloniaArgs);

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

        BuildAvaloniaApp().SetupWithClassicDesktopLifetime(avaloniaArgs);

        var context = new ShotContext(repoRoot, outputRoot);
        var runTask = RunScenariosAsync(context, onlyPrefix);
        PumpUntilCompleted(runTask);
        runTask.GetAwaiter().GetResult(); // 完了済みなので即座に返る。例外があればここで再送出。

        Console.WriteLine($"[LabPlot.Screenshots] done. isolated AppData: {isolatedAppData}");
        return 0;
    }

    /// <summary>
    /// <c>--only &lt;prefix&gt;</c> (または <c>--only=&lt;prefix&gt;</c>) を args から取り出す。
    /// 例: <c>--only gpc/</c> は relativePath が "gpc/" で始まるシナリオだけを実行する
    /// (バッチの反復のたびに 10 枚全部を焼き直さずに済むようにするための開発用フィルタ)。
    /// マッチした引数は Avalonia 側の SetupWithClassicDesktopLifetime に渡さないよう取り除く。
    /// </summary>
    private static string? ParseOnlyFilter(string[] args, out string[] remainingArgs)
    {
        string? only = null;
        var remaining = new System.Collections.Generic.List<string>(args.Length);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--only" && i + 1 < args.Length)
            {
                only = args[i + 1];
                i++; // 値トークンもスキップする
                continue;
            }

            if (args[i].StartsWith("--only=", StringComparison.Ordinal))
            {
                only = args[i]["--only=".Length..];
                continue;
            }

            remaining.Add(args[i]);
        }

        remainingArgs = remaining.ToArray();
        return only;
    }

    private static async Task RunScenariosAsync(ShotContext context, string? onlyPrefix)
    {
        var scenarios = Scenarios.All;
        if (!string.IsNullOrEmpty(onlyPrefix))
        {
            scenarios = scenarios
                .Where(s => s.RelativePath.StartsWith(onlyPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (scenarios.Count == 0)
            {
                Console.WriteLine($"[LabPlot.Screenshots] --only \"{onlyPrefix}\" に一致するシナリオが無い。");
            }
        }

        foreach (var scenario in scenarios)
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

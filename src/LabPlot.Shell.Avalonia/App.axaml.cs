using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.Shell.Avalonia;

/// <summary>
/// Avalonia 版 App。WPF 版 LabPlot.Shell.App と同じ構成を踏襲する：
/// <list type="bullet">
///   <item>未捕捉例外を 3 経路 (UI スレッド / AppDomain / TaskScheduler) で拾う</item>
///   <item>例外時はログを LocalApplicationData/LabPlot/Logs/shell-error.log に追記</item>
///   <item>OnFrameworkInitializationCompleted で <see cref="PortalWindow"/> を起動</item>
/// </list>
/// WPF の DispatcherUnhandledException に対応するのは Avalonia の
/// <see cref="Dispatcher.UnhandledException"/>。MessageBox は Avalonia 純正には無いので
/// 簡易ダイアログ Window で代替する (発生時のみ作成するので IL リンク負荷も無し)。
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Phase 7 Batch 6 step 2: 全 Window に対して文字描画を WPF 同等に揃える。
            // Window.WindowOpenedEvent (ルーテッドイベント) のクラスハンドラを 1 つ仕込めば、
            // PortalWindow / 各 MainWindow / 子 Dialog すべての Opened タイミングで
            // RenderOptions.SetTextRenderingMode = SubpixelAntialias と
            // RenderOptions.SetEdgeMode = Antialias が走る。
            Window.WindowOpenedEvent.AddClassHandler<Window>(OnAnyWindowOpened);

            // Portal を × で閉じた瞬間に GPC / DLS / Spectrum / 子ダイアログをまとめて
            // 終了させる。デフォルトの OnLastWindowClose だと子ウィンドウの閉じ忘れで
            // dotnet プロセスが裏に残留しやすかったので Portal=MainWindow を起点に統一する。
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new PortalWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnAnyWindowOpened(Window window, RoutedEventArgs e)
    {
        WindowAppearance.ApplyDefaults(window);
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatalException(e.Exception);
        e.Handled = true;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportFatalException(exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportFatalException(e.Exception);
        e.SetObserved();
    }

    private static void ReportFatalException(Exception exception)
    {
        var logPath = WriteExceptionLog(exception);
        // Avalonia には標準 MessageBox が無いので、ログを残しつつ Debug 出力に流す。
        // GUI 通知は Phase 7 後半で簡易 Dialog Window を整備する予定。
        System.Diagnostics.Debug.WriteLine($"[LabPlot.Avalonia] Fatal: {exception.Message} (log: {logPath})");
    }

    private static string WriteExceptionLog(Exception exception)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LabPlot",
            "Logs");
        Directory.CreateDirectory(logDirectory);

        var logPath = Path.Combine(logDirectory, "shell-avalonia-error.log");
        var text = new StringBuilder()
            .AppendLine($"[{DateTimeOffset.Now:O}]")
            .AppendLine(exception.ToString())
            .AppendLine()
            .ToString();

        File.AppendAllText(logPath, text);
        return logPath;
    }
}

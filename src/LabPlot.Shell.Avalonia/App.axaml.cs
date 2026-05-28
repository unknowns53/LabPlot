using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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

            // `dotnet run` 由来 (= .app バンドルでない) で起動した場合、macOS の Dock には
            // .NET ホストの汎用アイコンが出てしまう。NSApp.setApplicationIconImage: を呼んで
            // app-icon.png に差し替える (配布 .app バンドルでは Info.plist + .icns が
            // 機能するので本処理は冪等な no-op 相当)。Windows / Linux は ガードで即抜ける。
            MacAppIcon.TrySetDockIcon(new Uri("avares://LabPlot.Core.Avalonia/Assets/app-icon.png"));
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

    // ---------- macOS アプリメニュー (App.axaml の NativeMenu からハンドリング) ----------

    /// <summary>App メニュー ▸ About LabPlot のハンドラ。バージョン / リポジトリを示す簡易ダイアログを出す。</summary>
    private async void OnAbout_Click(object? sender, EventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "(dev build)";
        await ShowMacAppMenuDialogAsync(
            title: "About LabPlot",
            heading: "LabPlot",
            body: $"v{version}\n\nGPC / UV-Vis / DLS 計測データの解析・可視化ツール。\nhttps://github.com/unknowns53/LabPlot");
    }

    /// <summary>
    /// App メニュー ▸ Preferences... のハンドラ。LabPlot は専用の Preferences Window を持たず
    /// 書式設定や既定パスは各モジュールの軸範囲 / グラフ書式パネルに直接ぶら下がっているため、
    /// 利用者にその旨を伝えるダイアログだけ出す。専用 Window を立てる場合は別タスクで切る。
    /// </summary>
    private async void OnPreferences_Click(object? sender, EventArgs e)
    {
        await ShowMacAppMenuDialogAsync(
            title: "Preferences",
            heading: "Preferences",
            body: "LabPlot は専用の設定 Window を持たない。\n書式 / 既定の出力フォルダ / 凡例フォントなどは各モジュール (GPC / UV-Vis / DLS) の「軸範囲」「グラフ書式」パネルに直接ぶら下がっている。");
    }

    /// <summary>
    /// 320×220 程度の borderless 情報ダイアログを 1 つ生成する。PortalWindow.ShowComingSoonAsync と
    /// 同じ系列。Owner として現在の MainWindow が取れればそちらに ShowDialog、無理なら non-modal Show。
    /// </summary>
    private static async Task ShowMacAppMenuDialogAsync(string title, string heading, string body)
    {
        var dialog = new Window
        {
            Title = $"LabPlot — {title}",
            Width = 380,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            // v1.3.5: アプリ全体の Window 背景は CommonTokens.MainBgSurfaceBrush に一元化。
            //         FindResource が null を返した場合は #F7F8FA に fallback。
            Background = (Current?.FindResource("MainBgSurfaceBrush") as IBrush)
                ?? new SolidColorBrush(Color.Parse("#F7F8FA")),
            FontFamily = new FontFamily("Segoe UI, Yu Gothic UI, Meiryo UI, sans-serif"),
            FontSize = 13,
            UseLayoutRounding = true,
            SystemDecorations = SystemDecorations.BorderOnly,
        };

        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 88,
            Padding = new Thickness(16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
            IsCancel = true,
        };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = heading,
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                },
                new TextBlock
                {
                    Text = body,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.Parse("#475569")),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                },
                okButton,
            },
        };

        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } owner && owner.IsVisible)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
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

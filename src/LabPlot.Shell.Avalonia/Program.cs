using Avalonia;

namespace LabPlot.Shell.Avalonia;

/// <summary>
/// Avalonia 版エントリポイント。WPF 版 LabPlot.Shell が App.xaml の StartupUri で
/// PortalWindow を起動するのと同じ役割を、Avalonia の AppBuilder + ClassicDesktopLifetime
/// パターンで担う。<see cref="App.OnFrameworkInitializationCompleted"/> 側で
/// <see cref="PortalWindow"/> を MainWindow に割り当てる。
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

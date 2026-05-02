using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GPC_Visualization;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatalException(e.Exception);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
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
        MessageBox.Show(
            $"アプリの起動または処理中にエラーが発生しました。\n\n{exception.Message}\n\nログ: {logPath}",
            "GPC Analyzer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteExceptionLog(Exception exception)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GPC_Visualization",
            "Logs");
        Directory.CreateDirectory(logDirectory);

        var logPath = Path.Combine(logDirectory, "startup-error.log");
        var text = new StringBuilder()
            .AppendLine($"[{DateTimeOffset.Now:O}]")
            .AppendLine(exception.ToString())
            .AppendLine()
            .ToString();

        File.AppendAllText(logPath, text);
        return logPath;
    }
}

using System.Windows;

namespace LabPlot.Shell;

public partial class PortalWindow : Window
{
    public PortalWindow()
    {
        InitializeComponent();
    }

    private void OpenGpc_Click(object sender, RoutedEventArgs e) => OpenSingleton<global::GPC_Visualization.MainWindow>();

    private void OpenSpectrum_Click(object sender, RoutedEventArgs e) => OpenSingleton<global::Spectrum_Visualization.MainWindow>();

    private void OpenDls_Click(object sender, RoutedEventArgs e) => OpenSingleton<DLS.MainWindow>();

    // 同モジュールが既に開いていればフォーカスのみ。複数同時オープンは「同じ
    // モジュールを 2 つ並べたい」運用が想定しづらいので最初は単一インスタンス。
    private static void OpenSingleton<TWindow>() where TWindow : Window, new()
    {
        if (TryActivateExistingWindow<TWindow>())
        {
            return;
        }

        var window = new TWindow();
        window.Show();
    }

    private static bool TryActivateExistingWindow<TWindow>() where TWindow : Window
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is TWindow existing)
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return true;
            }
        }

        return false;
    }
}

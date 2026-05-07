using System.Windows;

namespace LabPlot.Shell;

public partial class PortalWindow : Window
{
    public PortalWindow()
    {
        InitializeComponent();
    }

    private void OpenGpc_Click(object sender, RoutedEventArgs e) => ShowComingSoon("GPC");

    private void OpenSpectrum_Click(object sender, RoutedEventArgs e) => ShowComingSoon("UV-Vis");

    private void OpenDls_Click(object sender, RoutedEventArgs e) => ShowComingSoon("DLS");

    // Batch 0 ではカードはまだ各アプリと接続していない (各アプリが WinExe のままで
    // ライブラリ化されていないため)。Batch 1 以降で順次 new MainWindow().Show() に
    // 置き換える。
    private void ShowComingSoon(string moduleName)
    {
        MessageBox.Show(
            this,
            $"{moduleName} モジュールは Batch 1 以降で接続予定です。",
            "LabPlot",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}

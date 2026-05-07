using System.Windows;
using System.Windows.Controls;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// Banner overlay used by the 3 apps to surface "soft warning" signals
/// (degraded data, partial calibration, fallback path used) on top of
/// the plot area. API mirrors <see cref="ErrorBanner"/>.
/// </summary>
public partial class WarningBanner : UserControl
{
    public WarningBanner()
    {
        InitializeComponent();
    }

    public void Show(string message)
    {
        MessageTextBlock.Text = message;
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
    }
}

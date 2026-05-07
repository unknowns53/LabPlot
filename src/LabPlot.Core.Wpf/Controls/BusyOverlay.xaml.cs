using System.Windows;
using System.Windows.Controls;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// Modal-feel overlay for long-running synchronous work (CSV parse,
/// .dat scan, calibration fit). Half-transparent white scrim + rotating
/// blue arc + message label, sized to cover whatever container it's
/// dropped into. API mirrors <see cref="ErrorBanner"/>: <see cref="Show"/>
/// to display a message, <see cref="Hide"/> to dismiss.
/// </summary>
public partial class BusyOverlay : UserControl
{
    public BusyOverlay()
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

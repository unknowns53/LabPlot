using System.Windows;
using System.Windows.Controls;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// Banner overlay used by the 3 apps to surface "soft success" signals
/// (file saved, settings reset, defaults written) on top of the plot
/// area. API mirrors <see cref="ErrorBanner"/> so callers can swap one
/// for the other when promoting a status message.
/// </summary>
public partial class SuccessBanner : UserControl
{
    public SuccessBanner()
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

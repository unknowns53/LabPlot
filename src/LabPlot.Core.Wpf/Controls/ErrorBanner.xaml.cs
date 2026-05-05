using System.Windows;
using System.Windows.Controls;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// Banner overlay used by the 3 apps to surface "hard failure" signals
/// (file open / save errors, plot init failures) on top of the plot
/// area. Soft validation errors stay in the status bar — only call
/// <see cref="Show"/> for failures that should grab attention.
/// </summary>
public partial class ErrorBanner : UserControl
{
    public ErrorBanner()
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

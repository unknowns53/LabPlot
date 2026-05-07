using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// Banner overlay used by the Avalonia apps to surface "hard failure"
/// signals (file open / save errors, plot init failures) on top of the
/// plot area. Mirrors LabPlot.Core.Wpf.Controls.ErrorBanner so callers
/// can use the same Show / Hide API on either backend.
/// </summary>
public partial class ErrorBanner : UserControl
{
    private TextBlock? _messageTextBlock;

    public ErrorBanner()
    {
        InitializeComponent();
        _messageTextBlock = this.FindControl<TextBlock>("MessageTextBlock");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void Show(string message)
    {
        if (_messageTextBlock is not null)
        {
            _messageTextBlock.Text = message;
        }
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
    }
}

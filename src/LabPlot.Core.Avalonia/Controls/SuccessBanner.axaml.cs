using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// Banner overlay used by the Avalonia apps to surface "soft success"
/// signals (file saved, settings reset, defaults written) on top of the
/// plot area. Mirrors LabPlot.Core.Wpf.Controls.SuccessBanner so callers
/// can swap one for the other when promoting a status message.
/// </summary>
public partial class SuccessBanner : UserControl
{
    private TextBlock? _messageTextBlock;

    public SuccessBanner()
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

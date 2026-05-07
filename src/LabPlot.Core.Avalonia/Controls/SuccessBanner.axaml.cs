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

    // Avalonia.Generators が partial class に InitializeComponent + x:Name フィールド代入を
    // 自動生成するので手動定義しない。

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

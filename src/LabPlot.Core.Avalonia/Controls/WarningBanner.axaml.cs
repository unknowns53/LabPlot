using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// Banner overlay used by the Avalonia apps to surface "soft warning"
/// signals (degraded data, partial calibration, fallback path used) on
/// top of the plot area. Mirrors LabPlot.Core.Wpf.Controls.WarningBanner.
/// </summary>
public partial class WarningBanner : UserControl
{
    private TextBlock? _messageTextBlock;

    public WarningBanner()
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

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// Modal-feel overlay for long-running synchronous work (CSV parse,
/// .dat scan, calibration fit). Half-transparent white scrim + rotating
/// blue arc + message label, sized to cover whatever container it's
/// dropped into. Mirrors LabPlot.Core.Wpf.Controls.BusyOverlay.
/// </summary>
public partial class BusyOverlay : UserControl
{
    private TextBlock? _messageTextBlock;

    public BusyOverlay()
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

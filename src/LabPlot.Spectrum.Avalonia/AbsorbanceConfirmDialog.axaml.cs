using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace LabPlot.Spectrum.Avalonia;

/// <summary>
/// 「Y 軸を Absorbance に切り替えますか？」確認ダイアログ。WPF 版
/// <c>Spectrum_Visualization.AbsorbanceConfirmDialog</c> と同 API surface。
/// MainWindow が積分領域追加時 (Transmittance データに対して Absorbance ベースの
/// 積分が必要な場面) に表示し、ユーザーの選択を <see cref="Choice"/> で返す。
/// SwitchAndAdd / AddWithoutSwitch / Cancel の 3 択。WPF 版と異なり、Avalonia の
/// Window.DragMove は <c>BeginMoveDrag(PointerPressedEventArgs)</c> を直接呼ぶ
/// 形式なので、PointerPressed ハンドラから渡す。
/// </summary>
public partial class AbsorbanceConfirmDialog : Window
{
    public enum DialogChoice { SwitchAndAdd, AddWithoutSwitch, Cancel }

    public DialogChoice Choice { get; private set; } = DialogChoice.Cancel;

    public AbsorbanceConfirmDialog()
    {
        InitializeComponent();
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
    }

    // Avalonia.Generators が partial class に InitializeComponent + x:Name フィールド代入を
    // 自動生成するので手動定義しない。

    private void YesButton_Click(object? sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.SwitchAndAdd;
        Close(true);
    }

    private void NoButton_Click(object? sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.AddWithoutSwitch;
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.Cancel;
        Close(false);
    }
}

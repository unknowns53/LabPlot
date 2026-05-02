using System.Windows;
using System.Windows.Input;

namespace Spectrum_Visualization;

public partial class AbsorbanceConfirmDialog : Window
{
    public enum DialogChoice { SwitchAndAdd, AddWithoutSwitch, Cancel }

    public DialogChoice Choice { get; private set; } = DialogChoice.Cancel;

    public AbsorbanceConfirmDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.SwitchAndAdd;
        DialogResult = true;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.AddWithoutSwitch;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = DialogChoice.Cancel;
        DialogResult = false;
        Close();
    }
}

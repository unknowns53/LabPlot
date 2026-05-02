using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// Reusable axis-range editor with X / Y min and max textboxes plus an
/// "auto" reset button. Empty textboxes mean "let the plot auto-scale";
/// filled textboxes mean "use these values as the fixed window".
/// Apps wire <see cref="AxisRangeCommitted"/> to refresh the plot on
/// Enter / focus loss / auto reset.
/// </summary>
public partial class AxisRangePanel : UserControl
{
    public static readonly DependencyProperty XAxisLabelProperty =
        DependencyProperty.Register(nameof(XAxisLabel), typeof(string), typeof(AxisRangePanel),
            new PropertyMetadata("X"));

    public static readonly DependencyProperty YAxisLabelProperty =
        DependencyProperty.Register(nameof(YAxisLabel), typeof(string), typeof(AxisRangePanel),
            new PropertyMetadata("Y"));

    public static readonly DependencyProperty NumberFormatProperty =
        DependencyProperty.Register(nameof(NumberFormat), typeof(string), typeof(AxisRangePanel),
            new PropertyMetadata("G"));

    private bool _suppressCommit;

    public AxisRangePanel()
    {
        InitializeComponent();
    }

    public string XAxisLabel
    {
        get => (string)GetValue(XAxisLabelProperty);
        set => SetValue(XAxisLabelProperty, value);
    }

    public string YAxisLabel
    {
        get => (string)GetValue(YAxisLabelProperty);
        set => SetValue(YAxisLabelProperty, value);
    }

    /// <summary>
    /// .NET numeric format string used when <see cref="SetXValues"/> /
    /// <see cref="SetYValues"/> write back into the textboxes (e.g. after a
    /// pan/zoom sync). Defaults to "G"; apps that want shorter display can
    /// pass "G6" etc. Parsing always uses invariant culture, so the round
    /// trip is lossy but stable.
    /// </summary>
    public string NumberFormat
    {
        get => (string)GetValue(NumberFormatProperty);
        set => SetValue(NumberFormatProperty, value);
    }

    /// <summary>
    /// Fired when the user commits a new axis range (Enter, focus loss, or
    /// the auto reset button). Suppressed while <see cref="SetXValues"/> /
    /// <see cref="SetYValues"/> apply external state.
    /// </summary>
    public event EventHandler? AxisRangeCommitted;

    public double? XMinValue => TryParse(XMinTextBox.Text);
    public double? XMaxValue => TryParse(XMaxTextBox.Text);
    public double? YMinValue => TryParse(YMinTextBox.Text);
    public double? YMaxValue => TryParse(YMaxTextBox.Text);

    public void SetXValues(double? min, double? max)
    {
        _suppressCommit = true;
        try
        {
            XMinTextBox.Text = Format(min, NumberFormat);
            XMaxTextBox.Text = Format(max, NumberFormat);
        }
        finally
        {
            _suppressCommit = false;
        }
    }

    public void SetYValues(double? min, double? max)
    {
        _suppressCommit = true;
        try
        {
            YMinTextBox.Text = Format(min, NumberFormat);
            YMaxTextBox.Text = Format(max, NumberFormat);
        }
        finally
        {
            _suppressCommit = false;
        }
    }

    public void ResetToAuto()
    {
        _suppressCommit = true;
        try
        {
            XMinTextBox.Clear();
            XMaxTextBox.Clear();
            YMinTextBox.Clear();
            YMaxTextBox.Clear();
        }
        finally
        {
            _suppressCommit = false;
        }
        RaiseCommitted();
    }

    private void AxisTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
        {
            return;
        }

        e.Handled = true;
        RaiseCommitted();
    }

    private void AxisTextBox_LostFocus(object sender, RoutedEventArgs e)
        => RaiseCommitted();

    private void AutoRangeButton_Click(object sender, RoutedEventArgs e)
        => ResetToAuto();

    private void RaiseCommitted()
    {
        if (_suppressCommit)
        {
            return;
        }
        AxisRangeCommitted?.Invoke(this, EventArgs.Empty);
    }

    private static double? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value))
        {
            return value;
        }
        return null;
    }

    private static string Format(double? value, string format)
        => value.HasValue
            ? value.Value.ToString(string.IsNullOrEmpty(format) ? "G" : format, CultureInfo.InvariantCulture)
            : string.Empty;
}

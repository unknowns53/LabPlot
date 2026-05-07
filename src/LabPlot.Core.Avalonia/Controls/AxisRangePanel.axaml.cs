using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// Reusable Avalonia axis-range editor with X / Y min and max textboxes
/// plus an "auto" reset button. Empty textboxes mean "let the plot
/// auto-scale"; filled textboxes mean "use these values as the fixed
/// window". Apps wire <see cref="AxisRangeCommitted"/> to refresh the
/// plot on Enter / focus loss / auto reset. Mirrors
/// <see cref="LabPlot.Core.Wpf.Controls.AxisRangePanel"/> so callers can
/// use the same API on either backend.
/// </summary>
public partial class AxisRangePanel : UserControl
{
    public static readonly StyledProperty<string> XAxisLabelProperty =
        AvaloniaProperty.Register<AxisRangePanel, string>(nameof(XAxisLabel), "X");

    public static readonly StyledProperty<string> YAxisLabelProperty =
        AvaloniaProperty.Register<AxisRangePanel, string>(nameof(YAxisLabel), "Y");

    public static readonly StyledProperty<string> NumberFormatProperty =
        AvaloniaProperty.Register<AxisRangePanel, string>(nameof(NumberFormat), "G");

    private bool _suppressCommit;

    private TextBox? _xMinTextBox;
    private TextBox? _xMaxTextBox;
    private TextBox? _yMinTextBox;
    private TextBox? _yMaxTextBox;

    public AxisRangePanel()
    {
        InitializeComponent();
        _xMinTextBox = this.FindControl<TextBox>("XMinTextBox");
        _xMaxTextBox = this.FindControl<TextBox>("XMaxTextBox");
        _yMinTextBox = this.FindControl<TextBox>("YMinTextBox");
        _yMaxTextBox = this.FindControl<TextBox>("YMaxTextBox");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public string XAxisLabel
    {
        get => GetValue(XAxisLabelProperty);
        set => SetValue(XAxisLabelProperty, value);
    }

    public string YAxisLabel
    {
        get => GetValue(YAxisLabelProperty);
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
        get => GetValue(NumberFormatProperty);
        set => SetValue(NumberFormatProperty, value);
    }

    /// <summary>
    /// Fired when the user commits a new axis range (Enter, focus loss, or
    /// the auto reset button). Suppressed while <see cref="SetXValues"/> /
    /// <see cref="SetYValues"/> apply external state.
    /// </summary>
    public event EventHandler? AxisRangeCommitted;

    public double? XMinValue => TryParse(_xMinTextBox?.Text);
    public double? XMaxValue => TryParse(_xMaxTextBox?.Text);
    public double? YMinValue => TryParse(_yMinTextBox?.Text);
    public double? YMaxValue => TryParse(_yMaxTextBox?.Text);

    public void SetXValues(double? min, double? max)
    {
        _suppressCommit = true;
        try
        {
            if (_xMinTextBox is not null) _xMinTextBox.Text = Format(min, NumberFormat);
            if (_xMaxTextBox is not null) _xMaxTextBox.Text = Format(max, NumberFormat);
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
            if (_yMinTextBox is not null) _yMinTextBox.Text = Format(min, NumberFormat);
            if (_yMaxTextBox is not null) _yMaxTextBox.Text = Format(max, NumberFormat);
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
            // Avalonia の TextBox には WPF と同名の Clear() メソッドが無いので
            // Text プロパティを空文字に戻す。null では SelectionStart 等が
            // 例外を起こすケースがあるため string.Empty を入れる。
            if (_xMinTextBox is not null) _xMinTextBox.Text = string.Empty;
            if (_xMaxTextBox is not null) _xMaxTextBox.Text = string.Empty;
            if (_yMinTextBox is not null) _yMinTextBox.Text = string.Empty;
            if (_yMaxTextBox is not null) _yMaxTextBox.Text = string.Empty;
        }
        finally
        {
            _suppressCommit = false;
        }
        RaiseCommitted();
    }

    private void AxisTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
        {
            return;
        }

        e.Handled = true;
        RaiseCommitted();
    }

    private void AxisTextBox_LostFocus(object? sender, RoutedEventArgs e)
        => RaiseCommitted();

    private void AutoRangeButton_Click(object? sender, RoutedEventArgs e)
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

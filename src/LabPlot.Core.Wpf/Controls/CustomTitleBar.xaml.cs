using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LabPlot.Core.Wpf.Controls;

/// <summary>
/// Custom WPF window title bar replacing the default OS chrome. Hosts the
/// app branding (badge + name + subtitle) on the left and the minimize /
/// maximize-or-restore / close buttons on the right. The control discovers
/// its parent <see cref="Window"/> on Loaded and routes button clicks
/// through <see cref="SystemCommands"/> so Aero Snap and the system menu
/// keep working. The maximize / restore glyph is swapped automatically
/// when the parent's <see cref="Window.WindowState"/> changes.
/// </summary>
public partial class CustomTitleBar : UserControl
{
    public static readonly DependencyProperty AppNameProperty =
        DependencyProperty.Register(
            nameof(AppName),
            typeof(string),
            typeof(CustomTitleBar),
            new PropertyMetadata(string.Empty, OnAppNameChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(
            nameof(Subtitle),
            typeof(string),
            typeof(CustomTitleBar),
            new PropertyMetadata(string.Empty, OnSubtitleChanged));

    public static readonly DependencyProperty AppIconDataProperty =
        DependencyProperty.Register(
            nameof(AppIconData),
            typeof(Geometry),
            typeof(CustomTitleBar),
            new PropertyMetadata(null, OnAppIconDataChanged));

    private Window? _parentWindow;

    public CustomTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string AppName
    {
        get => (string)GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public Geometry? AppIconData
    {
        get => (Geometry?)GetValue(AppIconDataProperty);
        set => SetValue(AppIconDataProperty, value);
    }

    private static void OnAppNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CustomTitleBar bar)
            bar.AppNameTextBlock.Text = (string?)e.NewValue ?? string.Empty;
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CustomTitleBar bar) return;
        var text = (string?)e.NewValue ?? string.Empty;
        bar.SubtitleTextBlock.Text = text;
        bar.SubtitleTextBlock.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void OnAppIconDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CustomTitleBar bar)
            bar.AppIconPath.Data = e.NewValue as Geometry;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _parentWindow = Window.GetWindow(this);
        if (_parentWindow is null) return;

        _parentWindow.StateChanged += OnParentStateChanged;
        SyncMaxRestoreGlyph();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_parentWindow is null) return;

        _parentWindow.StateChanged -= OnParentStateChanged;
        _parentWindow = null;
    }

    private void OnParentStateChanged(object? sender, EventArgs e) => SyncMaxRestoreGlyph();

    private void SyncMaxRestoreGlyph()
    {
        if (_parentWindow is null) return;

        var isMaximized = _parentWindow.WindowState == WindowState.Maximized;
        var resourceKey = isMaximized ? "ChromeRestoreIcon" : "ChromeMaximizeIcon";

        if (TryFindResource(resourceKey) is Geometry geom)
            MaxRestoreButton.Tag = geom;

        MaxRestoreButton.ToolTip = isMaximized ? "ウィンドウ サイズに戻す" : "最大化";
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parentWindow is not null)
            SystemCommands.MinimizeWindow(_parentWindow);
    }

    private void MaxRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parentWindow is null) return;

        if (_parentWindow.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(_parentWindow);
        else
            SystemCommands.MaximizeWindow(_parentWindow);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parentWindow is not null)
            SystemCommands.CloseWindow(_parentWindow);
    }
}

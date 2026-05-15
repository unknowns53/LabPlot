using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// 画面下中央に短時間表示される浮遊トースト。<see cref="Show(string, StatusSeverity, int)"/>
/// を呼ぶと指定 ms 後に自動で消える (Opacity アニメ + IsVisible 折り畳み)。
/// 連続して Show された場合は古いタイマーをキャンセルして新しいメッセージで上書きする。
/// </summary>
public partial class ToastHost : UserControl
{
    private static readonly Color SuccessIconColor = Color.FromRgb(0x86, 0xEF, 0xAC);
    private static readonly Color InfoIconColor = Color.FromRgb(0x93, 0xC5, 0xFD);
    private static readonly Color WarningIconColor = Color.FromRgb(0xFC, 0xD3, 0x4D);
    private static readonly Color ErrorIconColor = Color.FromRgb(0xFC, 0xA5, 0xA5);

    private static readonly Geometry CheckIcon = Geometry.Parse("M 2,7 L 6,11 L 12,3");
    private static readonly Geometry WarningIcon = Geometry.Parse("M 7,1 L 13,12 L 1,12 Z M 6,5 L 8,5 L 8,9 L 6,9 Z M 6,10 L 8,10 L 8,11.5 L 6,11.5 Z");
    private static readonly Geometry ErrorIcon = Geometry.Parse("M 3,3 L 11,11 M 11,3 L 3,11");
    private static readonly Geometry InfoIcon = Geometry.Parse("M 7,2 A 5,5 0 1 0 7.001,2 Z M 7,5 L 7,5.5 M 6.25,7 L 7,7 L 7,11 L 7.75,11");

    private global::Avalonia.Controls.Shapes.Path? _iconPath;
    private TextBlock? _messageTextBlock;
    private Border? _toastBorder;
    private DispatcherTimer? _hideTimer;

    public ToastHost()
    {
        InitializeComponent();
        _iconPath = this.FindControl<global::Avalonia.Controls.Shapes.Path>("IconPath");
        _messageTextBlock = this.FindControl<TextBlock>("MessageTextBlock");
        _toastBorder = this.FindControl<Border>("ToastBorder");
    }

    /// <summary>
    /// トーストを表示する。<paramref name="severity"/> に応じてアイコンと色が切り替わる。
    /// 既存のトーストが表示中なら hide タイマーをキャンセルして新しいメッセージで上書きする。
    /// </summary>
    /// <param name="message">表示メッセージ。空文字なら何もしない。</param>
    /// <param name="severity">既定は Success。</param>
    /// <param name="durationMs">表示時間 (ms)。既定 2000。フェードイン 180 ms / フェードアウト 250 ms は別途加算される。</param>
    public void Show(string message, StatusSeverity severity = StatusSeverity.Success, int durationMs = 2000)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (_messageTextBlock is null || _iconPath is null) return;

        _hideTimer?.Stop();
        _hideTimer = null;

        _messageTextBlock.Text = message;
        ApplyIcon(severity);

        IsVisible = true;
        // Opacity Transition は XAML 側で Border に設定済み。UserControl 側を 1 に上げると
        // 中の Border が fade-in。
        Opacity = 1;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        _hideTimer.Tick += OnHideTimerTick;
        _hideTimer.Start();
    }

    /// <summary>すぐに非表示にする (アニメーション抜き)。</summary>
    public void Hide()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
        Opacity = 0;
        IsVisible = false;
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        _hideTimer = null;
        Opacity = 0;
        // フェードアウト後に hit-test も切る。Transition Duration 0.18s + 余裕の 0.1 = 0.28s 後に折り畳む。
        DispatcherTimer.RunOnce(() => IsVisible = false, TimeSpan.FromMilliseconds(280));
    }

    private void ApplyIcon(StatusSeverity severity)
    {
        if (_iconPath is null) return;
        var (geometry, color, fill) = severity switch
        {
            StatusSeverity.Warning => (WarningIcon, WarningIconColor, true),
            StatusSeverity.Error => (ErrorIcon, ErrorIconColor, false),
            StatusSeverity.Info => (InfoIcon, InfoIconColor, false),
            _ => (CheckIcon, SuccessIconColor, false),
        };
        _iconPath.Data = geometry;
        var brush = new SolidColorBrush(color);
        if (fill)
        {
            _iconPath.Fill = brush;
            _iconPath.Stroke = null;
            _iconPath.StrokeThickness = 0;
        }
        else
        {
            _iconPath.Fill = null;
            _iconPath.Stroke = brush;
            _iconPath.StrokeThickness = severity == StatusSeverity.Success ? 2.0 : 1.6;
        }
    }
}

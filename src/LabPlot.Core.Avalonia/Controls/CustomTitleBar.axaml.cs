using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

// Avalonia の Path は System.IO.Path とクラス名衝突するので、
// implicit using の System.IO に隠されないよう alias 経由で固定する。
using Path = Avalonia.Controls.Shapes.Path;

namespace LabPlot.Core.Avalonia.Controls;

/// <summary>
/// Custom Avalonia window title bar, mirroring
/// <see cref="LabPlot.Core.Wpf.Controls.CustomTitleBar"/>. Hosts the app
/// branding (badge + name + subtitle) on the left and the minimize /
/// maximize-or-restore / close buttons on the right. The control discovers
/// its parent <see cref="Window"/> on attach and routes button clicks
/// through the Window's <see cref="Window.WindowState"/> /
/// <see cref="Window.Close"/> APIs (Avalonia has no SystemCommands
/// equivalent). The maximize / restore glyph is swapped automatically when
/// the parent's WindowState changes.
/// </summary>
public partial class CustomTitleBar : UserControl
{
    public static readonly StyledProperty<string> AppNameProperty =
        AvaloniaProperty.Register<CustomTitleBar, string>(nameof(AppName), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<CustomTitleBar, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<Geometry?> AppIconDataProperty =
        AvaloniaProperty.Register<CustomTitleBar, Geometry?>(nameof(AppIconData));

    private Window? _parentWindow;
    private TextBlock? _appNameTextBlock;
    private TextBlock? _subtitleTextBlock;
    private Path? _appIconPath;
    private Button? _maxRestoreButton;
    private IDisposable? _windowStateSubscription;

    public CustomTitleBar()
    {
        InitializeComponent();
        _appNameTextBlock = this.FindControl<TextBlock>("AppNameTextBlock");
        _subtitleTextBlock = this.FindControl<TextBlock>("SubtitleTextBlock");
        _appIconPath = this.FindControl<Path>("AppIconPath");
        _maxRestoreButton = this.FindControl<Button>("MaxRestoreButton");
    }

    // Avalonia.Generators が partial class に InitializeComponent + x:Name フィールド代入を
    // 自動生成するので手動定義しない。

    public string AppName
    {
        get => GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public Geometry? AppIconData
    {
        get => GetValue(AppIconDataProperty);
        set => SetValue(AppIconDataProperty, value);
    }

    /// <summary>
    /// Avalonia 版の StyledProperty 変更通知は WPF の DependencyProperty の
    /// PropertyChangedCallback ではなく、OnPropertyChanged をオーバーライド
    /// して Property を比較する形で受け取る。AppName / Subtitle / AppIconData
    /// の 3 本に集約することで OnXxxChanged static コールバックを増やさない。
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AppNameProperty && _appNameTextBlock is not null)
        {
            _appNameTextBlock.Text = change.GetNewValue<string>() ?? string.Empty;
        }
        else if (change.Property == SubtitleProperty && _subtitleTextBlock is not null)
        {
            var text = change.GetNewValue<string>() ?? string.Empty;
            _subtitleTextBlock.Text = text;
            _subtitleTextBlock.IsVisible = !string.IsNullOrEmpty(text);
        }
        else if (change.Property == AppIconDataProperty && _appIconPath is not null)
        {
            _appIconPath.Data = change.GetNewValue<Geometry?>();
        }
    }

    /// <summary>
    /// Avalonia の Visual ツリー attach タイミングで親 Window を解決する。
    /// WPF 版は Loaded イベントだったが、Avalonia は OnAttachedToVisualTree /
    /// OnDetachedFromVisualTree が同等の役割を担う。WindowState 変更は
    /// Window.GetObservable(Window.WindowStateProperty) を Subscribe して
    /// 検知する (WPF の Window.StateChanged イベントは Avalonia には無い)。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (_parentWindow is null) return;

        _windowStateSubscription = _parentWindow
            .GetObservable(Window.WindowStateProperty)
            .Subscribe(new AnonymousObserver<WindowState>(_ => SyncMaxRestoreGlyph()));
        SyncMaxRestoreGlyph();

        // 親 Window が CanResize=False (固定サイズ運用、例: PortalWindow) の場合、
        // 標準 OS タイトルバーと同じく Maximize ボタンを完全に隠す。隠さずに残すと
        // 押下時に WindowState=Maximized が通る一方、Avalonia 11.3 + macOS では
        // Bounds が画面いっぱいに広がったまま declared Width/Height が更新され、
        // Normal に戻したあとも「巨大な Normal」状態に固定されて見た目「最大化が
        // 戻らない」現象になる (WindowStateStore.PersistFrom が Maximized 時に
        // Bounds を Normal サイズとして書き出すのも組み合わさる)。
        if (_maxRestoreButton is not null)
            _maxRestoreButton.IsVisible = _parentWindow.CanResize;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _windowStateSubscription?.Dispose();
        _windowStateSubscription = null;
        _parentWindow = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void SyncMaxRestoreGlyph()
    {
        if (_parentWindow is null || _maxRestoreButton is null) return;

        var isMaximized = _parentWindow.WindowState == WindowState.Maximized;
        var resourceKey = isMaximized ? "ChromeRestoreIcon" : "ChromeMaximizeIcon";

        if (this.TryFindResource(resourceKey, out var resource) && resource is Geometry geom)
            _maxRestoreButton.Tag = geom;

        ToolTip.SetTip(_maxRestoreButton, isMaximized ? "ウィンドウ サイズに戻す" : "最大化");
    }

    /// <summary>
    /// 中央ドラッグ領域の PointerPressed。WPF の WindowChrome.CaptionHeight
    /// の代替として、ダブルクリックで最大化トグル、シングルクリックで
    /// Window.BeginMoveDrag を呼んでドラッグ移動を開始する。
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_parentWindow is null) return;

        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed) return;

        // CanResize=False のウィンドウではダブルクリック最大化も無効。Maximize
        // ボタン側 (OnAttachedToVisualTree で隠している) と挙動を揃える。
        if (e.ClickCount == 2 && _parentWindow.CanResize)
        {
            _parentWindow.WindowState = _parentWindow.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        _parentWindow.BeginMoveDrag(e);
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_parentWindow is not null)
            _parentWindow.WindowState = WindowState.Minimized;
    }

    private void MaxRestoreButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_parentWindow is null) return;
        // CanResize=False のウィンドウでは Maximize ボタンを非表示にしているが、
        // テスト等で直接 Click を呼ばれた場合のガード。
        if (!_parentWindow.CanResize) return;

        _parentWindow.WindowState = _parentWindow.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        _parentWindow?.Close();
    }

    /// <summary>
    /// Avalonia の IObservable を Subscribe する際の最小限のオブザーバ実装。
    /// System.Reactive を依存追加せずに済ませるための small helper で、
    /// WindowState 変更通知のように OnNext しか使わないユースケース向け。
    /// </summary>
    private sealed class AnonymousObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        public AnonymousObserver(Action<T> onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => _onNext(value);
    }
}

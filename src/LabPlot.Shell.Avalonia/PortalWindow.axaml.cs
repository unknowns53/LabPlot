using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.Shell.Avalonia;

/// <summary>
/// Avalonia 版ポータルウィンドウ。WPF 版 <c>LabPlot.Shell.PortalWindow</c> と同じレイアウト
/// (CustomTitleBar + 2x2 カード グリッド) を Avalonia で再現する。WPF の
/// <c>System.Windows.Shell.WindowChrome</c> 相当は Window の
/// <see cref="Window.ExtendClientAreaToDecorationsHintProperty"/> と
/// <see cref="Window.ExtendClientAreaChromeHintsProperty"/> で代替している。
///
/// <para>
/// Phase 7 Batch 5a 時点で DLS / GPC / Spectrum すべての Avalonia 版を実起動に差し替え済み。
/// 残るプレースホルダ枠は Phase 8 以降の追加モジュール用。
/// </para>
/// </summary>
public partial class PortalWindow : Window
{
    private const string WindowStateAppKey = "portal";

    private Border? _chromeRoot;
    private IDisposable? _windowStateSubscription;

    public PortalWindow()
    {
        InitializeComponent();
        _chromeRoot = this.FindControl<Border>("ChromeRoot");
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 直近セッションのウィンドウサイズ・位置を復元する。Portal もリサイズ可能なので
        // 利用者の作業環境 (ウルトラワイド / サブモニタ) を毎起動で破壊しないように。
        WindowStateStore.ApplyTo(this, WindowStateAppKey);
        // macOS では "Ctrl+1" のような tooltip 表記を "Cmd+1" に置換 (Windows / Linux は noop)。
        KeyboardShortcuts.LocalizeTooltipsForMac(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        WindowStateStore.PersistFrom(this, WindowStateAppKey);
        base.OnClosing(e);
    }

    // Avalonia.Generators が partial class に InitializeComponent + x:Name フィールド代入を
    // 自動生成するので手動定義しない。

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyChromeRootTag(WindowState);
        _windowStateSubscription = this.GetObservable(WindowStateProperty).Subscribe(
            new AnonymousObserver<WindowState>(ApplyChromeRootTag));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _windowStateSubscription?.Dispose();
        _windowStateSubscription = null;
        base.OnDetachedFromVisualTree(e);
    }

    // WindowChromeRootBorderStyle の Selector ^[Tag=Maximized] を発火させるため、
    // WindowState を Tag に文字列で反映する。CustomTitleBar 側でも同型の購読を
    // 行っているが、こちらは Border の見た目 (7 px Margin の補正) 用。
    private void ApplyChromeRootTag(WindowState state)
    {
        if (_chromeRoot is null) return;
        _chromeRoot.Tag = state == WindowState.Maximized ? "Maximized" : "Normal";
    }

    private void OpenGpc_Click(object? sender, RoutedEventArgs e)
        => OpenSingleton<LabPlot.GPC.Avalonia.MainWindow>();

    private void OpenSpectrum_Click(object? sender, RoutedEventArgs e)
        => OpenSingleton<LabPlot.Spectrum.Avalonia.MainWindow>();

    private void OpenDls_Click(object? sender, RoutedEventArgs e)
        => OpenSingleton<LabPlot.DLS.Avalonia.MainWindow>();

    // v1.3 Batch G: Portal にもキーボードショートカットを入れる。
    // Ctrl/Cmd+1 = GPC、Ctrl/Cmd+2 = UV-Vis、Ctrl/Cmd+3 = DLS、F1 = ショートカット一覧、Esc = 終了。
    // 修飾キーは KeyboardShortcuts.HasCommandModifier 経由で OS 別に出し分ける (macOS = Cmd)。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var cmd = e.HasCommandModifier();
        if (cmd)
        {
            switch (e.Key)
            {
                case Key.D1: OpenSingleton<LabPlot.GPC.Avalonia.MainWindow>(); e.Handled = true; return;
                case Key.D2: OpenSingleton<LabPlot.Spectrum.Avalonia.MainWindow>(); e.Handled = true; return;
                case Key.D3: OpenSingleton<LabPlot.DLS.Avalonia.MainWindow>(); e.Handled = true; return;
            }
        }
        if (e.Key == Key.F1)
        {
            global::LabPlot.Core.Avalonia.KeyboardShortcutsWindow.ShowFor(this, global::LabPlot.Core.Avalonia.AppKind.Portal);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Avalonia には標準 MessageBox が無いので、軽量な情報ダイアログを Window で代替する。
    /// 320×170 の小窓に Title / Description / OK ボタンを並べ、ShowDialog で同期的に閉じる。
    /// Batch 3-5 で実モジュールに差し替える際にはこのメソッドごと OpenSingletonAsync に置換する。
    /// </summary>
    private async Task ShowComingSoonAsync(string moduleName, string description)
    {
        var dialog = new Window
        {
            Title = $"LabPlot — {moduleName}",
            Width = 360,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            FontFamily = new FontFamily("Segoe UI, Yu Gothic UI, Meiryo UI, sans-serif"),
            FontSize = 13,
            UseLayoutRounding = true,
            SystemDecorations = SystemDecorations.BorderOnly,
        };

        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 88,
            Padding = new Thickness(16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
            IsCancel = true,
        };
        okButton.Click += (_, _) => dialog.Close();

        var stack = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"{moduleName} （移植中）",
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                },
                new TextBlock
                {
                    Text = description,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.Parse("#475569")),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                },
                okButton,
            },
        };

        dialog.Content = stack;
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Batch 3 以降で利用予定のシングルトン起動ヘルパー。同モジュールが既に開いていれば
    /// アクティブ化、未起動なら新規 Window を生成する。WPF 版 <c>OpenSingleton&lt;T&gt;</c>
    /// と同じセマンティクス。
    /// </summary>
    private static void OpenSingleton<TWindow>() where TWindow : Window, new()
    {
        if (TryActivateExistingWindow<TWindow>())
        {
            return;
        }

        var window = new TWindow();
        window.Show();
    }

    private static bool TryActivateExistingWindow<TWindow>() where TWindow : Window
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return false;

        foreach (var window in desktop.Windows)
        {
            if (window is TWindow existing)
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// IObservable&lt;T&gt;.Subscribe(IObserver&lt;T&gt;) 用の最小実装。Avalonia には
    /// AnonymousObserver 公開クラスが無いので毎回これを定義している
    /// (CustomTitleBar.axaml.cs にも同じ pattern が入っている)。
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

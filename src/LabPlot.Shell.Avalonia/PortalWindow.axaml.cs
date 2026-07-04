using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LabPlot.Core.Avalonia.Controls;
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
///
/// <para>
/// v1.3.5 (PR C): Portal を "ただのモジュール launcher" から "ファイル workflow の起点"
/// に拡張する。3 つの新機能:
/// <list type="bullet">
///   <item><b>Esc で Portal close</b>: ShutdownMode=OnMainWindowClose と組み合わせて
///     キー 1 つで終了できる。子モジュール (GPC/Spectrum/DLS) には伝播させない
///     (解析中の誤操作で消えるリスクを避けるため)。</item>
///   <item><b>カードごとのファイル drop 受付</b>: 各モジュールカードが drop ターゲット
///     になっており、「どこに drop したか」が「どのモジュールで開くか」と一致する。
///     `.txt` / `.csv` のように複数モジュールが対応する拡張子で振り分けが曖昧になる
///     問題を避けるため、Window 全体 drop は廃止し、利用者がカード位置で意図を明示
///     する設計にしている。</item>
///   <item><b>最近開いたファイル一覧</b>: 3 モジュールが既に書き出している
///     <see cref="RecentFilesStore"/> JSON を集約し、最終更新日時で sort して表示。
///     クリックで該当モジュール起動 + ファイル open。</item>
/// </list>
/// </para>
/// </summary>
public partial class PortalWindow : Window
{
    private const string WindowStateAppKey = "portal";

    /// <summary>
    /// 各モジュールが受け入れるファイル拡張子。カード drop 時に「対応外なら Toast で
    /// 案内して open を起動しない」誤起動防止チェックに使う。GPC / Spectrum は
    /// `.csv` / `.txt` を共有し、GPC は `.tsv` も、DLS は `.xlsx` 専用。
    /// </summary>
    private static readonly IReadOnlyDictionary<PortalModuleKind, IReadOnlySet<string>> SupportedExtensions =
        new Dictionary<PortalModuleKind, IReadOnlySet<string>>
        {
            [PortalModuleKind.Gpc] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".txt" },
            [PortalModuleKind.Spectrum] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv", ".txt" },
            [PortalModuleKind.Dls] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xlsx" },
            [PortalModuleKind.Viewer] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv", ".tsv", ".txt", ".xlsx" },
            [PortalModuleKind.Nmr] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jdf" },
        };

    private Border? _chromeRoot;
    private IDisposable? _windowStateSubscription;
    private ItemsControl? _recentFilesList;
    private StackPanel? _emptyRecentPanel;
    private ToastHost? _toast;
    private Button? _gpcCard;
    private Button? _spectrumCard;
    private Button? _dlsCard;
    private Button? _viewerCard;
    private Button? _nmrCard;

    public PortalWindow()
    {
        InitializeComponent();
        _chromeRoot = this.FindControl<Border>("ChromeRoot");
        _recentFilesList = this.FindControl<ItemsControl>("RecentFilesList");
        _emptyRecentPanel = this.FindControl<StackPanel>("EmptyRecentPanel");
        _toast = this.FindControl<ToastHost>("PortalToast");
        _gpcCard = this.FindControl<Button>("GpcCard");
        _spectrumCard = this.FindControl<Button>("SpectrumCard");
        _dlsCard = this.FindControl<Button>("DlsCard");
        _viewerCard = this.FindControl<Button>("ViewerCard");
        _nmrCard = this.FindControl<Button>("NmrCard");

        // 各カードを drop ターゲットに登録。Card.Tag に PortalModuleKind を仕込み、
        // 1 つの共通 OnCardDrop からどのモジュールで開くか取り出す。Window 全体ハンドラは
        // 置かない (拡張子だけだと `.txt` 等で振り分けが曖昧になるのを避けるため)。
        AttachCardDropHandlers(_gpcCard, PortalModuleKind.Gpc);
        AttachCardDropHandlers(_spectrumCard, PortalModuleKind.Spectrum);
        AttachCardDropHandlers(_dlsCard, PortalModuleKind.Dls);
        AttachCardDropHandlers(_viewerCard, PortalModuleKind.Viewer);
        AttachCardDropHandlers(_nmrCard, PortalModuleKind.Nmr);
    }

    private void AttachCardDropHandlers(Button? card, PortalModuleKind kind)
    {
        if (card is null) return;
        card.Tag = kind;
        card.AddHandler(DragDrop.DragOverEvent, OnCardDragOver);
        card.AddHandler(DragDrop.DropEvent, OnCardDrop);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 直近セッションのウィンドウサイズ・位置を復元する。Portal もリサイズ可能なので
        // 利用者の作業環境 (ウルトラワイド / サブモニタ) を毎起動で破壊しないように。
        WindowStateStore.ApplyTo(this, WindowStateAppKey);
        // macOS では "Ctrl+1" のような tooltip 表記を "Cmd+1" に置換 (Windows / Linux は noop)。
        KeyboardShortcuts.LocalizeTooltipsForMac(this);
        // 最近開いたファイル一覧を 3 モジュール分集約して表示。
        RefreshRecentFiles();
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

    private void OpenViewer_Click(object? sender, RoutedEventArgs e)
        => OpenSingleton<LabPlot.Viewer.Avalonia.MainWindow>();

    private void OpenNmr_Click(object? sender, RoutedEventArgs e)
        => OpenSingleton<LabPlot.NMR.Avalonia.MainWindow>();

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
                case Key.D4: OpenSingleton<LabPlot.Viewer.Avalonia.MainWindow>(); e.Handled = true; return;
                case Key.D5: OpenSingleton<LabPlot.NMR.Avalonia.MainWindow>(); e.Handled = true; return;
            }
        }
        if (e.Key == Key.F1)
        {
            global::LabPlot.Core.Avalonia.KeyboardShortcutsWindow.ShowFor(this, global::LabPlot.Core.Avalonia.AppKind.Portal);
            e.Handled = true;
            return;
        }
        // v1.3.5 PR C: Portal は Esc で閉じる。ShutdownMode=OnMainWindowClose なので
        // Portal close = アプリ全体終了。子モジュール側に Esc close を伝播させない設計に
        // した (解析中の誤操作で消えるリスクを避ける)。
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // ============================================================
    // ファイル drop 受付 (各モジュールカード)
    // ============================================================

    /// <summary>
    /// Drag 中の cursor effect を Copy に固定し、Drop を受け付ける合図を OS 側に返す。
    /// File 以外 (テキスト drop 等) は None で拒否する。3 カード共通ハンドラ。
    /// </summary>
    private void OnCardDragOver(object? sender, DragEventArgs e)
    {
        // Avalonia 11.3 新 API: e.DataTransfer + DataFormat.File。DLS の既存ハンドラと
        // 揃えて、Drop 直前まで cursor effect を Copy で固定する。
        if (e.DataTransfer is not null && e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 各モジュールカードへの drop 共通ハンドラ。sender.Tag から
    /// <see cref="PortalModuleKind"/> を取り出し、そのモジュールが受け付ける拡張子だけを
    /// フィルタして open する。「カード = 利用者の意思表示」なので、ここで拡張子を
    /// 推測することはしない。対応外拡張子は Toast で案内するだけで誤起動させない。
    /// </summary>
    private async void OnCardDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button card || card.Tag is not PortalModuleKind kind) return;
        if (e.DataTransfer is null || !e.DataTransfer.Contains(DataFormat.File)) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0) return;

        if (!SupportedExtensions.TryGetValue(kind, out var allowedExts))
            return;

        var acceptedPaths = paths
            .Where(p => allowedExts.Contains(Path.GetExtension(p)))
            .ToArray();

        if (acceptedPaths.Length == 0)
        {
            var label = ModuleLabel(kind);
            var hint = string.Join(" / ", allowedExts);
            ShowToast($"{label} は {hint} のみ対応しています。", StatusSeverity.Warning, 2800);
            return;
        }

        if (acceptedPaths.Length < paths.Length)
        {
            // 対応外拡張子が混ざっていた場合、受理した件数だけを open する旨を Toast で通知。
            var skipped = paths.Length - acceptedPaths.Length;
            ShowToast($"対応形式以外の {skipped} 件はスキップしました。",
                StatusSeverity.Info, 2500);
        }

        await OpenModuleWithFilesAsync(kind, acceptedPaths);
    }

    // ============================================================
    // 最近開いたファイル一覧
    // ============================================================

    /// <summary>
    /// 3 モジュールの <see cref="RecentFilesStore"/> JSON を集約し、最終更新日時で
    /// 降順 sort して最大 8 件を ItemsControl に流し込む。空なら placeholder を出す。
    /// OnOpened からのみ呼ぶ (Portal 表示中の追加更新は不要)。
    /// </summary>
    private void RefreshRecentFiles()
    {
        if (_recentFilesList is null || _emptyRecentPanel is null) return;

        var entries = new List<PortalRecentFileEntry>();
        AppendRecent(entries, PortalModuleKind.Gpc, "gpc");
        AppendRecent(entries, PortalModuleKind.Spectrum, "spectrum");
        AppendRecent(entries, PortalModuleKind.Dls, "dls");
        AppendRecent(entries, PortalModuleKind.Viewer, "viewer");

        // 最終更新日時 (File.GetLastWriteTimeUtc) で降順 sort。
        // 同時刻 (秒精度で衝突) はモジュール順 (GPC → Spectrum → DLS) を保つ。
        var sorted = entries
            .OrderByDescending(e => e.LastWriteUtc)
            .Take(8)
            .ToArray();

        _recentFilesList.ItemsSource = sorted;
        _emptyRecentPanel.IsVisible = sorted.Length == 0;
    }

    private static void AppendRecent(List<PortalRecentFileEntry> destination, PortalModuleKind kind, string appKey)
    {
        var paths = RecentFilesStore.Load(appKey);
        foreach (var path in paths)
        {
            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(path); }
            catch { lastWrite = DateTime.MinValue; }
            destination.Add(new PortalRecentFileEntry(
                FilePath: path,
                DisplayName: Path.GetFileName(path),
                ModuleKind: kind,
                ModuleLabel: ModuleLabel(kind),
                IconData: ModuleIconData(kind),
                LastWriteUtc: lastWrite,
                SecondaryLine: $"{ModuleLabel(kind)} · {FormatRelativeTime(lastWrite)}"));
        }
    }

    /// <summary>
    /// "5 分前 / 2 時間前 / 昨日 / 3 日前 / 2 週間前 / 2026/05/01" のような人間向け表示。
    /// MinValue (= ファイル statt 失敗) は "不明" で済ませる。
    /// </summary>
    private static string FormatRelativeTime(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "更新日時不明";
        var diff = DateTime.UtcNow - utc;
        if (diff.TotalMinutes < 1) return "たった今";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 時間前";
        if (diff.TotalDays < 2) return "昨日";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} 日前";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} 週間前";
        return utc.ToLocalTime().ToString("yyyy/MM/dd");
    }

    /// <summary>
    /// 最近開いたファイル行のクリック。Tag に bind されている RecentFileEntry を
    /// 取り出し、該当モジュールを起動して open する。
    /// </summary>
    private async void RecentFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not PortalRecentFileEntry entry) return;
        if (!File.Exists(entry.FilePath))
        {
            ShowToast("ファイルが見つかりません。削除または移動された可能性があります。",
                StatusSeverity.Warning, 2800);
            return;
        }
        await OpenModuleWithFilesAsync(entry.ModuleKind, new[] { entry.FilePath });
    }

    // ============================================================
    // モジュール起動 + ファイル open の共通エントリ
    // ============================================================

    /// <summary>
    /// 指定 <see cref="ModuleKind"/> の MainWindow を起動 (既存ならアクティブ化) し、
    /// <see cref="IPortalFileOpener.OpenFilesAsync"/> でファイルを開かせる。
    /// </summary>
    private static Task OpenModuleWithFilesAsync(PortalModuleKind kind, IReadOnlyList<string> filePaths) => kind switch
    {
        PortalModuleKind.Gpc => OpenSingletonWithFilesAsync<LabPlot.GPC.Avalonia.MainWindow>(filePaths),
        PortalModuleKind.Spectrum => OpenSingletonWithFilesAsync<LabPlot.Spectrum.Avalonia.MainWindow>(filePaths),
        PortalModuleKind.Dls => OpenSingletonWithFilesAsync<LabPlot.DLS.Avalonia.MainWindow>(filePaths),
        PortalModuleKind.Viewer => OpenSingletonWithFilesAsync<LabPlot.Viewer.Avalonia.MainWindow>(filePaths),
        PortalModuleKind.Nmr => OpenSingletonWithFilesAsync<LabPlot.NMR.Avalonia.MainWindow>(filePaths),
        _ => Task.CompletedTask,
    };

    private static string ModuleLabel(PortalModuleKind kind) => kind switch
    {
        PortalModuleKind.Gpc => "GPC",
        PortalModuleKind.Spectrum => "UV-Vis",
        PortalModuleKind.Dls => "DLS",
        PortalModuleKind.Viewer => "Viewer",
        PortalModuleKind.Nmr => "NMR",
        _ => string.Empty,
    };

    /// <summary>各モジュールのカードバッジと揃った Path data (StrokeLineCap=Round 前提)。</summary>
    private static string ModuleIconData(PortalModuleKind kind) => kind switch
    {
        PortalModuleKind.Gpc => "M 2,16 L 6,16 L 8,11 L 10,3 L 12,11 L 14,16 L 18,16",
        PortalModuleKind.Spectrum => "M 2,10 Q 4.25,3 6.5,10 Q 8.75,17 11,10 Q 13.25,3 15.5,10 Q 17,14 18,10",
        PortalModuleKind.Dls => "M 2,16 C 5,16 6.5,4 10,4 C 13.5,4 15,16 18,16",
        PortalModuleKind.Viewer => "M 3,3 L 3,17 L 17,17 M 6,13 L 9,8 L 12,11 L 16,5",
        PortalModuleKind.Nmr => "M 2,16 L 6,16 L 7,4 L 8,16 L 11,16 L 12,9 L 13,16 L 18,16",
        _ => string.Empty,
    };

    private void ShowToast(string message, StatusSeverity severity, int durationMs)
    {
        if (_toast is null) return;
        _toast.IsVisible = true;
        _toast.Show(message, severity, durationMs);
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
            // v1.3.5: アプリ全体の Window 背景は CommonTokens.MainBgSurfaceBrush に一元化。
            //         FindResource が null を返した場合は #F7F8FA に fallback。
            Background = (Application.Current?.FindResource("MainBgSurfaceBrush") as IBrush)
                ?? new SolidColorBrush(Color.Parse("#F7F8FA")),
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
        if (TryActivateExistingWindow<TWindow>(out _))
        {
            return;
        }

        var window = new TWindow();
        window.Show();
    }

    /// <summary>
    /// シングルトン起動 + 起動した Window に対して <see cref="IPortalFileOpener.OpenFilesAsync"/>
    /// を呼ぶ。既存 Window がある場合もそれにファイル open を依頼する。
    /// </summary>
    private static async Task OpenSingletonWithFilesAsync<TWindow>(IReadOnlyList<string> filePaths)
        where TWindow : Window, new()
    {
        TWindow window;
        if (TryActivateExistingWindow<TWindow>(out var existing) && existing is not null)
        {
            window = existing;
        }
        else
        {
            window = new TWindow();
            window.Show();
        }

        if (window is IPortalFileOpener opener)
        {
            await opener.OpenFilesAsync(filePaths);
        }
    }

    private static bool TryActivateExistingWindow<TWindow>(out TWindow? existing) where TWindow : Window
    {
        existing = null;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return false;

        foreach (var window in desktop.Windows)
        {
            if (window is TWindow match)
            {
                if (match.WindowState == WindowState.Minimized)
                    match.WindowState = WindowState.Normal;
                match.Activate();
                existing = match;
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

/// <summary>
/// Portal の右側に並べる最近開いたファイル 1 行ぶんの表示用 record。
/// Avalonia の compiled bindings が DataTemplate からアクセスするため、PortalWindow の
/// 外側で <c>public</c> 公開する必要がある (同一アセンブリでも nested private 型は
/// XAML 側で解決できない)。
/// </summary>
public sealed record PortalRecentFileEntry(
    string FilePath,
    string DisplayName,
    PortalModuleKind ModuleKind,
    string ModuleLabel,
    string IconData,
    DateTime LastWriteUtc,
    string SecondaryLine);

/// <summary>モジュール種別の弁別子。拡張子マップとリストの両方で使う。</summary>
public enum PortalModuleKind
{
    Gpc,
    Spectrum,
    Dls,
    Viewer,
    Nmr,
}

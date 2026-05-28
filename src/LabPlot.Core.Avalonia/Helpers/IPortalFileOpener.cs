using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// 外部 (PortalWindow からのファイル drop / 最近開いたファイル クリック など) から
/// MainWindow に「このファイルを開いて」と依頼するための契約。Portal は具体的な
/// モジュール (GPC/Spectrum/DLS) の Reader API を知らずに、open 動作だけ発火できる。
///
/// <para>
/// 実装側 (各 MainWindow) は Window が表示完了 (= Loaded) するまで内部で待ってから
/// 既存の ImportXxxAsync を呼ぶ。Avalonia の Window.Show() は同期的に
/// InitializeComponent → AttachedToVisualTree → Loaded の順で発火するが、Show 直後
/// に Import*  を呼ぶと BusyOverlay 等の Visual Tree 依存処理が間に合わないことが
/// あるため、明示的に Loaded を待つ。
/// </para>
/// </summary>
public interface IPortalFileOpener
{
    /// <summary>
    /// 指定パスのファイル群を当該モジュールで開く。複数渡された場合、各モジュールは
    /// 自分の都合 (DLS は単一 xlsx しか扱えない等) で head のみ採用するなどしてよい。
    /// </summary>
    Task OpenFilesAsync(IReadOnlyList<string> filePaths);
}

/// <summary>
/// <see cref="Window.Loaded"/> を待つだけの極小ヘルパー。3 モジュールの OpenFilesAsync
/// で同じパターンを 3 重複して書かないために共通化する。
/// </summary>
public static class WindowLoadExtensions
{
    /// <summary>
    /// Window が既に Loaded 済みなら即座に完了する Task を、未 Loaded ならイベント発火
    /// まで待機する Task を返す。Loaded ハンドラは 1 度だけ呼ばれて即 detach する。
    /// </summary>
    public static Task WhenLoadedAsync(this Window window)
    {
        if (window.IsLoaded) return Task.CompletedTask;
        var tcs = new TaskCompletionSource<bool>();
        EventHandler<RoutedEventArgs>? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null) window.Loaded -= handler;
            tcs.TrySetResult(true);
        };
        window.Loaded += handler;
        return tcs.Task;
    }
}

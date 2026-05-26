using System;
using System.IO;
using Avalonia.Threading;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// 読み込み中のファイルがエクスプローラ / Finder / シェル削除で消えた瞬間を検知し、
/// UI スレッドでコールバックを発火する FileSystemWatcher ラッパ。MRU 履歴クリアと
/// 並んで「プロットが残り続ける」問題への対策として GPC / Spectrum / DLS の各
/// MainWindow から共通で使う。
///
/// Watch(path) で対象ファイルを切り替える。前の Watch は自動で停止 / Dispose される。
/// Watch(null) で監視解除のみ。OnClosing 等で必ず Dispose すること。
/// </summary>
public sealed class MissingFileWatcher : IDisposable
{
    private readonly Action _onMissing;
    private FileSystemWatcher? _watcher;
    private string? _watchedPath;

    public MissingFileWatcher(Action onMissing)
    {
        _onMissing = onMissing;
    }

    /// <summary>監視対象ファイルを差し替える。null / 存在しないパスを渡すと監視解除のみ。</summary>
    public void Watch(string? filePath)
    {
        Stop();
        if (string.IsNullOrEmpty(filePath)) return;
        if (!File.Exists(filePath)) return;

        var dir = Path.GetDirectoryName(filePath);
        var name = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

        _watchedPath = filePath;
        _watcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Deleted += OnTargetGone;
        _watcher.Renamed += OnTargetRenamed;
    }

    public void Stop()
    {
        if (_watcher is null) { _watchedPath = null; return; }
        _watcher.EnableRaisingEvents = false;
        _watcher.Deleted -= OnTargetGone;
        _watcher.Renamed -= OnTargetRenamed;
        _watcher.Dispose();
        _watcher = null;
        _watchedPath = null;
    }

    public void Dispose() => Stop();

    private void OnTargetGone(object? sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.FullPath, _watchedPath, StringComparison.OrdinalIgnoreCase)) return;
        FireOnUiThread();
    }

    private void OnTargetRenamed(object? sender, RenamedEventArgs e)
    {
        // 旧パス側が消えた扱いと等価。リネーム先までは追跡しない (ユーザの「同名で別場所」意図と区別不能)。
        if (!string.Equals(e.OldFullPath, _watchedPath, StringComparison.OrdinalIgnoreCase)) return;
        FireOnUiThread();
    }

    private void FireOnUiThread()
    {
        // FileSystemWatcher のイベントは IO 監視スレッドで発火するので、UI 操作は
        // Dispatcher 経由に倒さないと Avalonia 11 の cross-thread 検証で落ちる。
        Dispatcher.UIThread.Post(_onMissing);
    }
}

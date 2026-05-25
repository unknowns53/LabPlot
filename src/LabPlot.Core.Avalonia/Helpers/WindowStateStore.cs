using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// Avalonia の Window のサイズ・位置・Maximized 状態を <c>%APPDATA%/LabPlot/window-{appKey}.json</c>
/// に保存する best-effort ストア。<see cref="RecentFilesStore"/> と同様、破損や I/O 失敗時は
/// 黙って null / 既定値起動にフォールバックして UI を止めない。
///
/// 利用パターン:
///   protected override void OnOpened(EventArgs e) { base.OnOpened(e); ApplyPersistedWindowState(); }
///   protected override void OnClosing(WindowClosingEventArgs e) { PersistCurrentWindowState(); base.OnClosing(e); }
/// </summary>
public sealed record WindowStateRecord(
    double Width,
    double Height,
    double? X,
    double? Y,
    bool Maximized);

public static class WindowStateStore
{
    private static string DirectoryPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "LabPlot");
        }
    }

    private static string FilePathFor(string appKey)
        => Path.Combine(DirectoryPath, $"window-{appKey}.json");

    public static WindowStateRecord? Load(string appKey)
    {
        var path = FilePathFor(appKey);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WindowStateRecord>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string appKey, WindowStateRecord record)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = FilePathFor(appKey);
            File.WriteAllText(path, JsonSerializer.Serialize(record));
        }
        catch
        {
            // ignore — Window 状態の保存失敗で本体機能を止めない。
        }
    }

    /// <summary>
    /// 与えた矩形が、現在接続されているスクリーンのどれかと最低 1 ピクセルでも重なるか判定する。
    /// 完全に画面外なら false (マルチモニタ切断後の「画面外復元」を抑止する用途)。
    /// </summary>
    public static bool IsRectVisibleOnAnyScreen(Window window, double x, double y, double width, double height)
    {
        var screens = window.Screens;
        if (screens is null) return true; // Screens API 未提供の環境はそのまま採用する
        foreach (var screen in screens.All)
        {
            var bounds = screen.Bounds; // PixelRect (DIP ではなく px だが概算で十分)
            var sxRight = bounds.X + bounds.Width;
            var syBottom = bounds.Y + bounds.Height;
            var wxRight = x + width;
            var wyBottom = y + height;
            if (x < sxRight && wxRight > bounds.X && y < syBottom && wyBottom > bounds.Y)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 保存済みの状態 (あれば) を Window に適用する。OnOpened から呼ぶ。
    /// 画面外の位置や負のサイズは無視して既定の起動レイアウトにフォールバックする。
    /// </summary>
    public static void ApplyTo(Window window, string appKey)
    {
        var rec = Load(appKey);
        if (rec is null) return;
        if (!(rec.Width > 0) || !(rec.Height > 0)) return;

        var w = Math.Max(window.MinWidth > 0 ? window.MinWidth : 1, rec.Width);
        var h = Math.Max(window.MinHeight > 0 ? window.MinHeight : 1, rec.Height);

        if (rec.X is double x && rec.Y is double y
            && IsRectVisibleOnAnyScreen(window, x, y, w, h))
        {
            window.Position = new PixelPoint((int)x, (int)y);
            // 明示的に位置を当てた以降は OS の StartupLocation 補正で上書きされないように。
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }
        window.Width = w;
        window.Height = h;
        if (rec.Maximized) window.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// 現在の Window 状態を保存する。OnClosing から呼ぶ。
    /// Maximized 時は次回 Restore 用に Bounds (= Normal 想定の現サイズ) を保存。
    /// Minimized は記録せず Normal として保存する。
    /// </summary>
    public static void PersistFrom(Window window, string appKey)
    {
        var isMaximized = window.WindowState == WindowState.Maximized;
        var isNormal = window.WindowState == WindowState.Normal;

        double width = isNormal ? window.Width : window.Bounds.Width;
        double height = isNormal ? window.Height : window.Bounds.Height;
        if (!(width > 0)) width = window.Width;
        if (!(height > 0)) height = window.Height;

        double? x = isNormal ? window.Position.X : null;
        double? y = isNormal ? window.Position.Y : null;

        var rec = new WindowStateRecord(width, height, x, y, isMaximized);
        Save(appKey, rec);
    }
}

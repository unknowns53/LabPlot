using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using LabPlot.Core.Avalonia.Controls;

namespace LabPlot.Tools.Screenshots;

/// <summary>
/// Portal (LabPlot.Shell.Avalonia) 用のスクリーンショットシナリオ。
/// PortalWindow は Program.Main の SetupWithClassicDesktopLifetime 時点で
/// 既に desktop.MainWindow として構築済み (isolated AppData は Program.cs の
/// 初期セットアップで一度だけ設定されるので、両シナリオとも MRU 空の状態を共有する)。
/// </summary>
internal static class PortalScenarios
{
    public static ScreenshotScenario[] All { get; } =
    {
        new("portal/00-launcher.png", CaptureLauncherAsync),
        new("portal/10-titlebar.png", CaptureTitleBarAsync),
    };

    private static async Task CaptureLauncherAsync(ShotContext ctx)
    {
        var portal = GetPortalWindow();
        await ctx.CaptureAsync(portal, "portal/00-launcher.png");
    }

    /// <summary>
    /// 自前タイトルバー (CustomTitleBar) 部分のクロップ。座標は
    /// TranslatePoint で Window 座標へ変換した実測値から組み立て、下端には
    /// タイトルバー本体と同じ高さぶんのマージンを足して、カードグリッドの上端が
    /// わずかに覗く構図にする (docs/user-guide/images/portal/10-titlebar.png 相当)。
    /// </summary>
    private static async Task CaptureTitleBarAsync(ShotContext ctx)
    {
        var portal = GetPortalWindow();
        await ctx.ShowAsync(portal);

        var titleBar = portal.FindControl<CustomTitleBar>("MainTitleBar")
            ?? throw new InvalidOperationException("MainTitleBar (CustomTitleBar) が見つからない。");
        var topLeft = titleBar.TranslatePoint(new Point(0, 0), portal) ?? new Point(0, 0);
        var height = titleBar.Bounds.Height;
        var rect = new Rect(
            topLeft.X,
            topLeft.Y,
            Math.Max(0, portal.ClientSize.Width - topLeft.X),
            height * 2);

        await ctx.CaptureCroppedAsync(portal, rect, "portal/10-titlebar.png");
    }

    private static Window GetPortalWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } portal)
        {
            throw new InvalidOperationException(
                "desktop.MainWindow (PortalWindow) が見つからない。SetupWithClassicDesktopLifetime が先に走っているか確認する。");
        }

        return portal;
    }
}

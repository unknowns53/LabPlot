using System;
using ScottPlot;
using ScottPlot.Avalonia;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// ScottPlot 5 の <see cref="AvaPlot"/> が右クリックで出す既定メニュー (英語 2 項目
/// 「Save Image」「Autoscale」) を、日本語 2 項目に差し替える。<c>AvaPlot.Menu</c>
/// (<see cref="IPlotMenu"/>) は <c>Clear()</c> + <c>Add(label, action)</c> しか公開して
/// おらず、フォントや枠線などの見た目までは変更できないため、項目の日本語化と
/// 拡充だけに留める。
/// </summary>
public static class PlotContextMenu
{
    /// <summary>
    /// 右クリックメニューを日本語 2 項目に差し替える。
    /// </summary>
    /// <param name="avaPlot">対象の <see cref="AvaPlot"/>。</param>
    /// <param name="saveImage">
    /// 「画像を保存...」から呼ぶコールバック。各モジュール既存の PNG/SVG 保存フローが
    /// あればそれを渡す。<see langword="null"/> の場合は ScottPlot 既定の保存ダイアログ
    /// (<see cref="AvaPlotMenu.OpenSaveImageDialog"/>) にフォールバックする。
    /// </param>
    public static void Apply(AvaPlot avaPlot, Action? saveImage = null)
    {
        ArgumentNullException.ThrowIfNull(avaPlot);

        var menu = avaPlot.Menu;
        if (menu is null) return;

        menu.Clear();

        if (saveImage is not null)
        {
            menu.Add("画像を保存...", _ => saveImage());
        }
        else if (menu is AvaPlotMenu defaultMenu)
        {
            menu.Add("画像を保存...", plot => defaultMenu.OpenSaveImageDialog(plot));
        }

        menu.Add("自動範囲に戻す", plot =>
        {
            plot.Axes.AutoScale();
            avaPlot.Refresh();
        });
    }
}

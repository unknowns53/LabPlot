using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// プライマリ修飾キー (Windows / Linux = Ctrl、macOS = Cmd) を OS 別に出し分けるための
/// 共通ヘルパ。各 MainWindow / PortalWindow の OnKeyDown ハンドラと F1 cheat-sheet 表示の
/// 両方からこれを参照することで、Avalonia の `KeyModifiers.Control` 直判定が散らばらないよう
/// 寄せている。
///
/// macOS では Cmd キーは <see cref="KeyModifiers.Meta"/>、Windows / Linux では Ctrl は
/// <see cref="KeyModifiers.Control"/>。本ヘルパは IsMacOS のときだけ Meta を見るので、
/// macOS ユーザが Cmd+O を押すと従来 "Ctrl+O" として実装されていたコードがそのまま発火する。
/// Mac 上で誤って Ctrl+O を押した場合は反応しない (macOS の慣習に従う)。
/// </summary>
public static class KeyboardShortcuts
{
    /// <summary>OS 別の "command modifier" が押されているか。macOS は Cmd、それ以外は Ctrl。</summary>
    public static bool HasCommandModifier(this KeyEventArgs e)
        => OperatingSystem.IsMacOS()
            ? e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            : e.KeyModifiers.HasFlag(KeyModifiers.Control);

    /// <summary>UI 表示用の修飾キー名。macOS は "Cmd"、それ以外は "Ctrl"。</summary>
    public static string CommandModifierLabel
        => OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

    /// <summary>
    /// XAML 内に "Ctrl+O" のようにハードコードされている ToolTip.Tip 文字列を macOS でだけ
    /// "Cmd+O" に置換する。各 Window の Loaded ハンドラから一度だけ呼ぶ。Windows / Linux では
    /// 何もしないので noop。logical tree 全体を 1 度走査するだけなので体感コストはほぼゼロ。
    /// </summary>
    public static void LocalizeTooltipsForMac(Visual root)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (root is not ILogical logicalRoot) return;

        foreach (var descendant in logicalRoot.GetLogicalDescendants().OfType<Control>())
            RewriteTooltip(descendant);

        // ルート自身の ToolTip も忘れず置換。
        if (root is Control rootControl) RewriteTooltip(rootControl);
    }

    private static void RewriteTooltip(Control control)
    {
        if (ToolTip.GetTip(control) is not string tip) return;
        if (!tip.Contains("Ctrl", StringComparison.Ordinal)) return;
        ToolTip.SetTip(control, tip.Replace("Ctrl", "Cmd", StringComparison.Ordinal));
    }
}

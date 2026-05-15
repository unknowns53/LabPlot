using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LabPlot.Core.Avalonia.Helpers;

namespace LabPlot.Core.Avalonia;

/// <summary>
/// LabPlot 3 アプリ + 検量線エディタで共通の F1 ヘルプ。アプリ種別 (<see cref="AppKind"/>) を
/// 与えるとそのアプリのショートカット一覧をデータ化して縦に並べる。表示文字列は
/// MainWindow / CalibrationCurveWindow の OnKeyDown / InputBindings をそのまま反映している。
/// </summary>
public sealed partial class KeyboardShortcutsWindow : Window
{
    public KeyboardShortcutsWindow()
    {
        InitializeComponent();
        WindowAppearance.ApplyDefaults(this);
    }

    /// <summary>呼び出し元の WindowKind を指定して開く。Owner 配下で Modal 風に振る舞う。</summary>
    public static void ShowFor(Window owner, AppKind app)
    {
        var window = new KeyboardShortcutsWindow();
        var groups = BuildShortcutGroups(app);
        var list = window.FindControl<ItemsControl>("ShortcutGroups");
        if (list is not null) list.ItemsSource = groups;
        var title = window.FindControl<Controls.CustomTitleBar>("ShortcutTitleBar");
        if (title is not null) title.Subtitle = SubtitleFor(app);
        window.ShowDialog(owner);
    }

    private static string SubtitleFor(AppKind app) => app switch
    {
        AppKind.Dls => "DLS Analyzer",
        AppKind.Gpc => "GPC Analyzer",
        AppKind.Spectrum => "UV-Vis Analyzer",
        AppKind.Calibration => "Beer-Lambert 検量線エディタ",
        _ => string.Empty,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    // ---------- データソース ----------

    private static IReadOnlyList<ShortcutGroup> BuildShortcutGroups(AppKind app) => app switch
    {
        AppKind.Dls => new[]
        {
            new ShortcutGroup("ファイル / 解析", new[]
            {
                new ShortcutEntry("Ctrl + O", "DLS xlsx を開く"),
                new ShortcutEntry("Ctrl + S", "グラフを PNG / SVG で保存"),
                new ShortcutEntry("Ctrl + E", "解析結果を CSV / xlsx で出力"),
                new ShortcutEntry("Ctrl + Shift + S", "解析条件を保存 (.dlsjson)"),
                new ShortcutEntry("Ctrl + Shift + O", "解析条件を読み込み (.dlsjson)"),
            }),
            new ShortcutGroup("表示", new[]
            {
                new ShortcutEntry("Ctrl + L", "全シート選択 / 全解除"),
                new ShortcutEntry("Ctrl + R", "軸範囲を自動に戻す"),
                new ShortcutEntry("Ctrl + G", "プロットグリッドの表示切替"),
                new ShortcutEntry("F2", "凡例名 TextBox にフォーカス"),
                new ShortcutEntry("F1", "このヘルプ"),
            }),
            new ShortcutGroup("プロット操作", new[]
            {
                new ShortcutEntry("左ドラッグ", "パン"),
                new ShortcutEntry("右ドラッグ", "範囲ズーム"),
                new ShortcutEntry("ホイール", "拡大 / 縮小"),
                new ShortcutEntry("Ctrl + ホイール", "横方向のみ拡縮"),
                new ShortcutEntry("Shift + ホイール", "縦方向のみ拡縮"),
                new ShortcutEntry("ダブルクリック", "ベンチマーク表示"),
            }),
        },
        AppKind.Gpc => new[]
        {
            new ShortcutGroup("ファイル / 解析", new[]
            {
                new ShortcutEntry("Ctrl + O", "CSV / LabSolutions TXT を開く"),
                new ShortcutEntry("Ctrl + S", "グラフを PNG / SVG で保存"),
                new ShortcutEntry("Ctrl + E", "解析結果を CSV / xlsx で出力"),
                new ShortcutEntry("Ctrl + Shift + S", "解析条件を保存"),
                new ShortcutEntry("Ctrl + Shift + O", "解析条件を読み込み"),
            }),
            new ShortcutGroup("表示", new[]
            {
                new ShortcutEntry("Ctrl + L", "重ね描きの切替"),
                new ShortcutEntry("Ctrl + R", "軸範囲を自動に戻す"),
                new ShortcutEntry("Ctrl + G", "プロットグリッドの表示切替"),
                new ShortcutEntry("Ctrl + 1", "溶媒の選択を循環"),
                new ShortcutEntry("Ctrl + 2", "検出器の選択を循環"),
                new ShortcutEntry("Ctrl + 3", "分子量表示の切替"),
                new ShortcutEntry("Ctrl + 4", "MW Y モードを循環"),
                new ShortcutEntry("F2", "凡例名 TextBox にフォーカス"),
                new ShortcutEntry("F1", "このヘルプ"),
            }),
            new ShortcutGroup("プロット操作", new[]
            {
                new ShortcutEntry("左ドラッグ", "パン"),
                new ShortcutEntry("右ドラッグ", "範囲ズーム"),
                new ShortcutEntry("ホイール", "拡大 / 縮小"),
                new ShortcutEntry("Ctrl + ホイール", "横方向のみ拡縮"),
                new ShortcutEntry("Shift + ホイール", "縦方向のみ拡縮"),
                new ShortcutEntry("ダブルクリック", "ベンチマーク表示"),
            }),
        },
        AppKind.Spectrum => new[]
        {
            new ShortcutGroup("ファイル / 解析", new[]
            {
                new ShortcutEntry("Ctrl + O", "JASCO TXT を開く"),
                new ShortcutEntry("Ctrl + S", "グラフを PNG / SVG で保存"),
                new ShortcutEntry("Ctrl + E", "解析結果を CSV / xlsx で出力"),
                new ShortcutEntry("Ctrl + Shift + S", "解析条件を保存"),
                new ShortcutEntry("Ctrl + Shift + O", "解析条件を読み込み"),
            }),
            new ShortcutGroup("表示", new[]
            {
                new ShortcutEntry("Ctrl + L", "重ね描きの切替"),
                new ShortcutEntry("Ctrl + R", "軸範囲を自動に戻す"),
                new ShortcutEntry("Ctrl + G", "プロットグリッドの表示切替"),
                new ShortcutEntry("F2", "凡例名 TextBox にフォーカス"),
                new ShortcutEntry("F1", "このヘルプ"),
            }),
            new ShortcutGroup("ピーク / 領域操作", new[]
            {
                new ShortcutEntry("ESC", "ドラッグモード / 手動ピーク追加モードを解除"),
            }),
            new ShortcutGroup("プロット操作", new[]
            {
                new ShortcutEntry("左ドラッグ", "パン"),
                new ShortcutEntry("右ドラッグ", "範囲ズーム"),
                new ShortcutEntry("ホイール", "拡大 / 縮小"),
                new ShortcutEntry("Ctrl + ホイール", "横方向のみ拡縮"),
                new ShortcutEntry("Shift + ホイール", "縦方向のみ拡縮"),
                new ShortcutEntry("ダブルクリック", "ベンチマーク表示"),
            }),
        },
        AppKind.Calibration => new[]
        {
            new ShortcutGroup("検量線エディタ", new[]
            {
                new ShortcutEntry("Enter", "編集中のセルを確定"),
                new ShortcutEntry("ESC", "ダイアログをキャンセル"),
                new ShortcutEntry("F1", "このヘルプ"),
            }),
        },
        _ => System.Array.Empty<ShortcutGroup>(),
    };

    public sealed record ShortcutEntry(string KeyCombo, string Description);
    public sealed record ShortcutGroup(string GroupName, IReadOnlyList<ShortcutEntry> Entries);
}

/// <summary>F1 で開くショートカット一覧の対象アプリ。</summary>
public enum AppKind
{
    Dls,
    Gpc,
    Spectrum,
    Calibration,
}

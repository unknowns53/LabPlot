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
        AppKind.Portal => "Analysis portal",
        AppKind.Viewer => "Data Viewer",
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

    private static IReadOnlyList<ShortcutGroup> BuildShortcutGroups(AppKind app)
    {
        // OS 別の修飾キー名 (macOS なら "Cmd"、それ以外は "Ctrl") を 1 度だけ拾って共通の
        // 補間文字列に流す。各エントリで KeyboardShortcuts.CommandModifierLabel を直接書くより
        // 読みやすく、Mac / Windows / Linux で同じ entry list が動的に切り替わる。
        var mod = KeyboardShortcuts.CommandModifierLabel;
        return app switch
        {
        AppKind.Dls => new[]
        {
            new ShortcutGroup("ファイル / 解析", new[]
            {
                new ShortcutEntry($"{mod} + O", "DLS xlsx を開く"),
                new ShortcutEntry($"{mod} + S", "グラフを PNG / SVG で保存"),
                new ShortcutEntry($"{mod} + E", "解析結果を CSV / xlsx で出力"),
                new ShortcutEntry($"{mod} + Shift + S", "解析条件を保存 (.dlsjson)"),
                new ShortcutEntry($"{mod} + Shift + O", "解析条件を読み込み (.dlsjson)"),
            }),
            new ShortcutGroup("表示", new[]
            {
                new ShortcutEntry($"{mod} + A", "全シート選択 / 全解除"),
                new ShortcutEntry($"{mod} + R", "軸範囲を自動に戻す"),
                new ShortcutEntry($"{mod} + G", "プロットグリッドの表示切替"),
                new ShortcutEntry("F2", "凡例名 TextBox にフォーカス"),
                new ShortcutEntry("F1", "このヘルプ"),
            }),
            new ShortcutGroup("プロット操作", new[]
            {
                new ShortcutEntry("左ドラッグ", "パン"),
                new ShortcutEntry("右ドラッグ", "範囲ズーム"),
                new ShortcutEntry("ホイール", "拡大 / 縮小"),
                new ShortcutEntry($"{mod} + ホイール", "横方向のみ拡縮"),
                new ShortcutEntry("Shift + ホイール", "縦方向のみ拡縮"),
                new ShortcutEntry("ダブルクリック", "ベンチマーク表示"),
            }),
        },
        AppKind.Gpc => new[]
        {
            new ShortcutGroup("ファイル / 解析", new[]
            {
                new ShortcutEntry($"{mod} + O", "CSV / LabSolutions TXT を開く"),
                new ShortcutEntry($"{mod} + S", "グラフを PNG / SVG で保存"),
                new ShortcutEntry($"{mod} + E", "解析結果を CSV / xlsx で出力"),
                new ShortcutEntry($"{mod} + Shift + S", "解析条件を保存"),
                new ShortcutEntry($"{mod} + Shift + O", "解析条件を読み込み"),
            }),
            new ShortcutGroup("表示", new[]
            {
                new ShortcutEntry($"{mod} + L", "重ね描きの切替"),
                new ShortcutEntry($"{mod} + R", "軸範囲を自動に戻す"),
                new ShortcutEntry($"{mod} + G", "プロットグリッドの表示切替"),
                new ShortcutEntry($"{mod} + 1", "溶媒の選択を循環"),
                new ShortcutEntry($"{mod} + 2", "検出器の選択を循環"),
                new ShortcutEntry($"{mod} + 3", "分子量表示の切替"),
                new ShortcutEntry($"{mod} + 4", "MW Y モードを循環"),
                new ShortcutEntry("F2", "凡例名 TextBox にフォーカス"),
                new ShortcutEntry("F1", "このヘルプ"),
            }),
            new ShortcutGroup("プロット操作", new[]
            {
                new ShortcutEntry("左ドラッグ", "パン"),
                new ShortcutEntry("右ドラッグ", "範囲ズーム"),
                new ShortcutEntry("ホイール", "拡大 / 縮小"),
                new ShortcutEntry($"{mod} + ホイール", "横方向のみ拡縮"),
                new ShortcutEntry("Shift + ホイール", "縦方向のみ拡縮"),
                new ShortcutEntry("ダブルクリック", "ベンチマーク表示"),
            }),
        },
        AppKind.Spectrum => new[]
        {
            new ShortcutGroup("ファイル / 解析", new[]
            {
                new ShortcutEntry($"{mod} + O", "JASCO TXT を開く"),
                new ShortcutEntry($"{mod} + S", "グラフを PNG / SVG で保存"),
                new ShortcutEntry($"{mod} + E", "解析結果を CSV / xlsx で出力"),
                new ShortcutEntry($"{mod} + Shift + S", "解析条件を保存"),
                new ShortcutEntry($"{mod} + Shift + O", "解析条件を読み込み"),
            }),
            new ShortcutGroup("表示", new[]
            {
                new ShortcutEntry($"{mod} + L", "重ね描きの切替"),
                new ShortcutEntry($"{mod} + R", "軸範囲を自動に戻す"),
                new ShortcutEntry($"{mod} + G", "プロットグリッドの表示切替"),
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
                new ShortcutEntry($"{mod} + ホイール", "横方向のみ拡縮"),
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
        AppKind.Portal => new[]
        {
            new ShortcutGroup("アプリ起動", new[]
            {
                new ShortcutEntry($"{mod} + 1", "GPC Analyzer を開く"),
                new ShortcutEntry($"{mod} + 2", "UV-Vis Analyzer を開く"),
                new ShortcutEntry($"{mod} + 3", "DLS Analyzer を開く"),
                new ShortcutEntry($"{mod} + 4", "Data Viewer を開く"),
            }),
            new ShortcutGroup("一般", new[]
            {
                new ShortcutEntry("F1", "このヘルプ"),
            }),
        },
        AppKind.Viewer => new[]
        {
            new ShortcutGroup("ファイル", new[]
            {
                new ShortcutEntry($"{mod} + O", "CSV / TSV / xlsx を開く"),
                new ShortcutEntry($"{mod} + V", "クリップボードの表を貼り付け"),
                new ShortcutEntry($"{mod} + S", "グラフを PNG / SVG で保存"),
                new ShortcutEntry($"{mod} + E", "表示中の系列を CSV / xlsx で出力"),
                new ShortcutEntry($"{mod} + Shift + S", "表示条件を保存 (.gvjson)"),
                new ShortcutEntry($"{mod} + Shift + O", "表示条件を読み込み (.gvjson)"),
            }),
            new ShortcutGroup("表示", new[]
            {
                new ShortcutEntry($"{mod} + R", "軸範囲を自動に戻す"),
                new ShortcutEntry($"{mod} + G", "プロットグリッドの表示切替"),
                new ShortcutEntry("F2", "凡例名 TextBox にフォーカス"),
                new ShortcutEntry("F1", "このヘルプ"),
            }),
            new ShortcutGroup("プロット操作", new[]
            {
                new ShortcutEntry("左ドラッグ", "パン"),
                new ShortcutEntry("右ドラッグ", "範囲ズーム"),
                new ShortcutEntry("ホイール", "拡大 / 縮小"),
                new ShortcutEntry($"{mod} + ホイール", "横方向のみ拡縮"),
                new ShortcutEntry("Shift + ホイール", "縦方向のみ拡縮"),
            }),
        },
        _ => System.Array.Empty<ShortcutGroup>(),
        };
    }

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
    Portal,
    Viewer,
}

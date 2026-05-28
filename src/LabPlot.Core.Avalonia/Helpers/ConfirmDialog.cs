using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// 破壊的操作 (既定値に戻す / 解析履歴をクリア など) の前に「よろしいですか?」を
/// 確認する軽量 Yes/No モーダル。Avalonia には標準 MessageBox が無いので、
/// PortalWindow.ShowComingSoonAsync と同方針で都度 Window を組み立てる。
/// 戻り値 true = 「実行」/false = 「キャンセル」。Owner 配下で ShowDialog する。
///
/// v1.3.5: 旧実装は Button を直接 SolidColorBrush で塗っており、CommonStyles の
/// ControlTheme と整合が取れず「文字が左寄り」「色が浮く」と見えていた。
/// `PrimaryButtonStyle` / `DestructiveButtonStyle` / `SecondaryButtonStyle` の
/// 既存 ControlTheme を `Theme = ...` で当て、Hover lift・押し込み・FocusRing・
/// 角丸まで他画面の Button と統一する。
/// </summary>
public static class ConfirmDialog
{
    /// <param name="owner">親 Window。CenterOwner と modal 親付けに使う。</param>
    /// <param name="title">タイトルバー / 見出しに出すラベル。</param>
    /// <param name="message">本文。1〜3 行程度で書き、TextWrapping=Wrap が効く。</param>
    /// <param name="confirmLabel">「実行」ボタンのラベル (例: 「戻す」「削除」)。</param>
    /// <param name="cancelLabel">「キャンセル」ボタンのラベル。</param>
    /// <param name="isDestructive">true なら「実行」ボタンを赤系 DestructiveButtonStyle にして危険操作と明示する。</param>
    public static async Task<bool> ShowAsync(
        Window owner,
        string title,
        string message,
        string confirmLabel = "実行",
        string cancelLabel = "キャンセル",
        bool isDestructive = false)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            MinHeight = 180,
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

        // ControlTheme を Application リソースから引いて 3 ボタンに当てる。
        // PrimaryButtonStyle / DestructiveButtonStyle / SecondaryButtonStyle は
        // CommonStyles.axaml に定義済み。FindResource が null を返した場合は
        // Avalonia Fluent default Theme にフォールバックする (= 旧実装相当)。
        var app = Application.Current;
        var confirmThemeKey = isDestructive ? "DestructiveButtonStyle" : "PrimaryButtonStyle";
        var confirmTheme = app?.FindResource(confirmThemeKey) as ControlTheme;
        var cancelTheme = app?.FindResource("SecondaryButtonStyle") as ControlTheme;

        var confirmButton = new Button
        {
            Content = confirmLabel,
            MinWidth = 96,
            IsDefault = true,
        };
        if (confirmTheme is not null) confirmButton.Theme = confirmTheme;
        confirmButton.Click += (_, _) => { result = true; dialog.Close(); };

        var cancelButton = new Button
        {
            Content = cancelLabel,
            MinWidth = 96,
            IsCancel = true,
        };
        if (cancelTheme is not null) cancelButton.Theme = cancelTheme;
        cancelButton.Click += (_, _) => { result = false; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { cancelButton, confirmButton },
        };

        // 「!」マーカー + 見出し + 本文の縦積み。マーカーは isDestructive のとき
        // のみ赤系、それ以外はアクセント青で「注意喚起」のニュアンスを出す。
        var markerColor = isDestructive ? "#DC2626" : "#2563EB";
        var markerBackground = isDestructive ? "#FEE2E2" : "#DBEAFE";
        var markerBadge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.Parse(markerBackground)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "!",
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse(markerColor)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
        };
        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#475569")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var textColumn = new StackPanel
        {
            Spacing = 0,
            Children = { titleBlock, messageBlock },
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 0, 0, 18),
        };
        Grid.SetColumn(markerBadge, 0);
        markerBadge.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(textColumn, 1);
        header.Children.Add(markerBadge);
        header.Children.Add(textColumn);

        var body = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 20),
            Children = { header, buttons },
        };

        // 全体を SectionStyle 相当の枠で包み、Window.Background との段差を出す。
        // ControlTheme リソースは Owner Window 経由でも引けるが、Application
        // resources から直接引いた方が dialog 単体の独立性が保てる。
        var rootBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.Parse("#E2E8F0")),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12),
            Child = body,
        };

        dialog.Content = rootBorder;
        await dialog.ShowDialog(owner);
        return result;
    }
}

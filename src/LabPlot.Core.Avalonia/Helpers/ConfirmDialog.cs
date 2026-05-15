using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// 破壊的操作 (既定値に戻す / 解析履歴をクリア など) の前に「よろしいですか?」を
/// 確認する軽量 Yes/No モーダル。Avalonia には標準 MessageBox が無いので、
/// PortalWindow.ShowComingSoonAsync と同方針で都度 Window を組み立てる。
/// 戻り値 true = 「実行」/false = 「キャンセル」。Owner 配下で ShowDialog する。
/// </summary>
public static class ConfirmDialog
{
    /// <param name="owner">親 Window。CenterOwner と modal 親付けに使う。</param>
    /// <param name="title">タイトルバー / 見出しに出すラベル。</param>
    /// <param name="message">本文。1〜3 行程度で書き、TextWrapping=Wrap が効く。</param>
    /// <param name="confirmLabel">「実行」ボタンのラベル (例: 「戻す」「削除」)。</param>
    /// <param name="cancelLabel">「キャンセル」ボタンのラベル。</param>
    /// <param name="isDestructive">true なら「実行」ボタンを赤系にして危険操作と明示する。</param>
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
            Width = 380,
            SizeToContent = SizeToContent.Height,
            MinHeight = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
            FontFamily = new FontFamily("Segoe UI, Yu Gothic UI, Meiryo UI, sans-serif"),
            FontSize = 13,
            UseLayoutRounding = true,
            SystemDecorations = SystemDecorations.BorderOnly,
        };

        var confirmButton = new Button
        {
            Content = confirmLabel,
            MinWidth = 92,
            Padding = new Thickness(16, 6),
            Background = new SolidColorBrush(isDestructive
                ? Color.Parse("#DC2626")
                : Color.Parse("#2563EB")),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            IsDefault = true,
        };
        confirmButton.Click += (_, _) => { result = true; dialog.Close(); };

        var cancelButton = new Button
        {
            Content = cancelLabel,
            MinWidth = 92,
            Padding = new Thickness(16, 6),
            Background = new SolidColorBrush(Color.Parse("#F1F5F9")),
            Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            IsCancel = true,
        };
        cancelButton.Click += (_, _) => { result = false; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { cancelButton, confirmButton },
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                },
                new TextBlock
                {
                    Text = message,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.Parse("#475569")),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                },
                buttons,
            },
        };

        dialog.Content = stack;
        await dialog.ShowDialog(owner);
        return result;
    }
}

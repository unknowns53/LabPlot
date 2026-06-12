using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DataViewer.Core;

namespace LabPlot.Viewer.Avalonia;

/// <summary>
/// 複数シートを含む xlsx を開いたときのシート選択モーダル。ConfirmDialog と
/// 同方針でコードから Window を組み立てる (既定は全シート選択)。
/// 戻り値はチェックされたシートのテーブル列、キャンセル時は null。
/// </summary>
internal static class SheetSelectionDialog
{
    public static async Task<IReadOnlyList<ViewerTable>?> ShowAsync(
        Window owner,
        string fileName,
        IReadOnlyList<ViewerTable> tables)
    {
        IReadOnlyList<ViewerTable>? result = null;
        var dialog = new Window
        {
            Title = "シートを選択",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            MinHeight = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Application.Current?.FindResource("MainBgSurfaceBrush") as IBrush)
                ?? new SolidColorBrush(Color.Parse("#F7F8FA")),
            FontFamily = new FontFamily("Segoe UI, Yu Gothic UI, Meiryo UI, sans-serif"),
            FontSize = 13,
            UseLayoutRounding = true,
            SystemDecorations = SystemDecorations.BorderOnly,
        };

        var app = Application.Current;
        var confirmTheme = app?.FindResource("PrimaryButtonStyle") as ControlTheme;
        var cancelTheme = app?.FindResource("SecondaryButtonStyle") as ControlTheme;

        var titleBlock = new TextBlock
        {
            Text = $"{fileName} には複数のシートがあります",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
            TextWrapping = TextWrapping.Wrap,
        };
        var messageBlock = new TextBlock
        {
            Text = "読み込むシートを選択してください。",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#475569")),
            Margin = new Thickness(0, 6, 0, 12),
        };

        var checkBoxes = new List<(CheckBox Box, ViewerTable Table)>(tables.Count);
        var checkList = new StackPanel { Spacing = 4 };
        foreach (var table in tables)
        {
            var box = new CheckBox
            {
                Content = $"{table.SheetName ?? "Sheet"} ({table.RowCount:N0} 行 × {table.Columns.Count} 列)",
                IsChecked = true,
            };
            checkBoxes.Add((box, table));
            checkList.Children.Add(box);
        }

        var checkScroll = new ScrollViewer
        {
            Content = checkList,
            MaxHeight = 260,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var confirmButton = new Button
        {
            Content = "読み込む",
            MinWidth = 96,
            IsDefault = true,
        };
        if (confirmTheme is not null) confirmButton.Theme = confirmTheme;
        confirmButton.Click += (_, _) =>
        {
            result = checkBoxes
                .Where(static entry => entry.Box.IsChecked == true)
                .Select(static entry => entry.Table)
                .ToArray();
            dialog.Close();
        };

        var cancelButton = new Button
        {
            Content = "キャンセル",
            MinWidth = 96,
            IsCancel = true,
        };
        if (cancelTheme is not null) cancelButton.Theme = cancelTheme;
        cancelButton.Click += (_, _) => { result = null; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { cancelButton, confirmButton },
        };

        var body = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 20),
            Children = { titleBlock, messageBlock, checkScroll, buttons },
        };

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

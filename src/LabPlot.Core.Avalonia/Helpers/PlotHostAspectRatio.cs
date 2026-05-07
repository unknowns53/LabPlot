using System;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LabPlot.Core.Avalonia.Helpers;

/// <summary>
/// Resizes a plot host (e.g. ScottPlot.Avalonia <c>AvaPlot</c>) inside its
/// containing <see cref="Border"/> so the rendered plot area honors a chosen
/// aspect ratio while still fitting inside the available pane. WPF 版の
/// <c>FrameworkElement.ActualWidth / ActualHeight</c> は Avalonia では
/// <see cref="Visual.Bounds"/> 経由で取得するため、入力は <see cref="Control"/> 派生に
/// 限定し ActualWidth/Height の代わりに Bounds.Width / Bounds.Height を読む。
/// </summary>
public static class PlotHostAspectRatio
{
    public static void Apply(
        Control? plotHost,
        Border? container,
        double? aspectRatio)
    {
        if (plotHost is null || container is null) return;

        if (!aspectRatio.HasValue)
        {
            plotHost.Width = double.NaN;
            plotHost.Height = double.NaN;
            plotHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            plotHost.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        var availableWidth = container.Bounds.Width
            - container.BorderThickness.Left
            - container.BorderThickness.Right;
        var availableHeight = container.Bounds.Height
            - container.BorderThickness.Top
            - container.BorderThickness.Bottom;

        if (availableWidth <= 0 || availableHeight <= 0) return;

        var ratio = aspectRatio.Value;
        var targetWidth = availableWidth;
        var targetHeight = targetWidth / ratio;
        if (targetHeight > availableHeight)
        {
            targetHeight = availableHeight;
            targetWidth = targetHeight * ratio;
        }

        plotHost.HorizontalAlignment = HorizontalAlignment.Center;
        plotHost.VerticalAlignment = VerticalAlignment.Center;
        plotHost.Width = Math.Max(0, targetWidth);
        plotHost.Height = Math.Max(0, targetHeight);
    }
}

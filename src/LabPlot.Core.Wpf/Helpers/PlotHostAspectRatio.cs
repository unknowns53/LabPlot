using System;
using System.Windows;
using System.Windows.Controls;

namespace LabPlot.Core.Wpf.Helpers;

/// <summary>
/// Resizes a plot host (e.g. ScottPlot's <c>WpfPlot</c>) inside its containing
/// <see cref="Border"/> so the rendered plot area honors a chosen aspect ratio
/// while still fitting inside the available pane. Pulled out of GPC / Spectrum
/// / DLS so all three apps render the preview identically and only the call
/// sites differ.
/// </summary>
public static class PlotHostAspectRatio
{
    /// <summary>
    /// Resize <paramref name="plotHost"/> within <paramref name="container"/> so
    /// that its width / height ratio matches <paramref name="aspectRatio"/>.
    /// Pass <c>null</c> (or an "Auto" value) to release the constraint and let
    /// the host stretch back to its container's full client area.
    /// </summary>
    /// <remarks>
    /// The container's <see cref="Border.BorderThickness"/> is subtracted so the
    /// child never overlaps the frame stroke. Both axes have to land on a
    /// strictly positive size, otherwise the call returns without mutating the
    /// host (e.g. during initial layout passes when ActualWidth / ActualHeight
    /// are still zero).
    /// </remarks>
    public static void Apply(
        FrameworkElement? plotHost,
        Border? container,
        double? aspectRatio)
    {
        if (plotHost is null || container is null)
        {
            return;
        }

        if (!aspectRatio.HasValue)
        {
            plotHost.Width = double.NaN;
            plotHost.Height = double.NaN;
            plotHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            plotHost.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        var availableWidth = container.ActualWidth
            - container.BorderThickness.Left
            - container.BorderThickness.Right;
        var availableHeight = container.ActualHeight
            - container.BorderThickness.Top
            - container.BorderThickness.Bottom;

        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

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

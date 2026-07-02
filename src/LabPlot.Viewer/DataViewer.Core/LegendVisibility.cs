namespace DataViewer.Core;

/// <summary>
/// Decides whether the plot legend should auto-show. Shared by
/// <c>RefreshPlot</c> (full rebuild) and the inline-rename lightweight path
/// (<c>TryUpdatePlottedLegendText</c>) so both recompute the same rule instead
/// of re-implementing it.
/// </summary>
public static class LegendVisibility
{
    /// <summary>
    /// True once more than one series is plotted, or at least one plotted
    /// series carries a custom legend name (a single auto-named series has no
    /// need for a legend, but a custom name signals the user wants it labeled).
    /// </summary>
    public static bool ShouldAutoShow(int plottedSeriesCount, bool hasCustomLegendName)
        => plottedSeriesCount > 1 || hasCustomLegendName;
}

namespace DataViewer.Core;

/// <summary>
/// Chooses between ScottPlot's <c>Scatter</c> and <c>SignalXY</c> plottables for
/// a rendered series. <c>SignalXY</c> renders noticeably faster than
/// <c>Scatter</c> for connected lines (measured: 8x1000 pts 16.5→13.6ms,
/// 8x10000 pts 79.5→42.9ms) but its public contract only promises correct
/// output when X is "sorted in ascending order". That contract's behavior for
/// duplicate X values is not documented, so this selector interprets it
/// conservatively as strictly increasing (no duplicates, no descending runs)
/// and falls back to <c>Scatter</c> otherwise.
/// </summary>
public static class SeriesPlotKindSelector
{
    /// <summary>
    /// True when <paramref name="xs"/> has no adjacent pair that is equal or
    /// decreasing. Empty and single-element lists are trivially increasing.
    /// </summary>
    public static bool IsStrictlyIncreasing(IReadOnlyList<double> xs)
    {
        for (var i = 1; i < xs.Count; i++)
        {
            if (xs[i] <= xs[i - 1]) return false;
        }

        return true;
    }

    /// <summary>
    /// True when the series should render with <c>SignalXY</c> instead of
    /// <c>Scatter</c>. Only <see cref="ViewerChartType.Line"/> qualifies:
    /// per <c>ViewerChartType.cs</c> it is the sole chart type with
    /// <c>ShowsLine() == true</c> and <c>ShowsMarkers() == false</c>
    /// (connecting line, no markers), which is the shape <c>SignalXY</c> is
    /// built for. <see cref="ViewerChartType.LineMarkers"/> also shows a
    /// line but needs marker rendering too, so it stays on <c>Scatter</c>.
    /// <paramref name="xs"/> is expected to already be the post-transform,
    /// NaN-filtered array (i.e. after <c>ExtractFinitePairs</c>): value
    /// filtering and the log10 transform both preserve relative order, so
    /// evaluating monotonicity on the transformed array gives the same
    /// answer as evaluating it on the raw column.
    /// </summary>
    public static bool ShouldUseSignalXY(ViewerChartType chartType, IReadOnlyList<double> xs)
        => chartType == ViewerChartType.Line && IsStrictlyIncreasing(xs);
}

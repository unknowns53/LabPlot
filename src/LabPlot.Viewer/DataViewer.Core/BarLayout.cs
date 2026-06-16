namespace DataViewer.Core;

/// <summary>
/// Excel-style grouped (dodged) bar placement. When several series are drawn
/// as bars they share each X position as a "category": the slot is split
/// evenly, every series gets a narrower bar, and bars are offset left/right of
/// the category centre so they sit side by side instead of overlapping.
/// Pure arithmetic so it can be unit tested without a plot surface.
/// </summary>
public static class BarLayout
{
    /// <summary>Fraction of the category spacing the whole bar group occupies (rest is the gap between groups).</summary>
    public const double GroupFillFraction = 0.8;

    /// <summary>Fraction of a single slot a bar fills (rest is the gap between dodged bars).</summary>
    public const double BarFillFraction = 0.9;

    /// <summary>One series' dodge geometry: where its bars sit and how wide they are.</summary>
    public readonly record struct BarSlot(double Offset, double Size);

    /// <summary>
    /// Estimate the category spacing from the bar series' X positions: the
    /// smallest positive gap between adjacent (sorted) X values across every
    /// series. Falls back to 1.0 when there is no usable spacing (single point
    /// or all-equal X), which keeps a lone bar visible.
    /// </summary>
    public static double EstimateGroupWidth(IEnumerable<IReadOnlyList<double>> barSeriesXs)
    {
        var minGap = double.PositiveInfinity;
        foreach (var xs in barSeriesXs)
        {
            var sorted = xs.Where(double.IsFinite).Distinct().OrderBy(static x => x).ToArray();
            for (var i = 1; i < sorted.Length; i++)
            {
                var gap = sorted[i] - sorted[i - 1];
                if (gap > 0 && gap < minGap)
                {
                    minGap = gap;
                }
            }
        }

        return double.IsFinite(minGap) && minGap > 0 ? minGap : 1.0;
    }

    /// <summary>
    /// Dodge geometry for the <paramref name="seriesOrdinal"/>-th bar series of
    /// <paramref name="seriesCount"/> total, given the category spacing. With a
    /// single series the offset is zero and the bar fills most of the slot.
    /// </summary>
    public static BarSlot ComputeSlot(int seriesOrdinal, int seriesCount, double groupWidth)
    {
        if (seriesCount < 1) seriesCount = 1;
        var span = (double.IsFinite(groupWidth) && groupWidth > 0 ? groupWidth : 1.0) * GroupFillFraction;
        var slot = span / seriesCount;
        var offset = (seriesOrdinal - (seriesCount - 1) / 2.0) * slot;
        return new BarSlot(offset, slot * BarFillFraction);
    }
}

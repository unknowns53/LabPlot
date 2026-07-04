namespace NMRAnalyzer.Core;

/// <summary>
/// Configuration for <see cref="NmrPeakDetector"/>. Thresholds are in the
/// spectrum's own intensity units; the UI typically derives a default from
/// the tallest peak.
/// </summary>
public sealed record NmrPeakFinderConfig
{
    /// <summary>
    /// Minimum intensity a local maximum must reach to be reported. Negative
    /// values disable the filter so every local maximum is returned.
    /// </summary>
    public double MinimumIntensity { get; init; } = 0.0;

    /// <summary>
    /// Minimum prominence (intensity units) a candidate must clear — the
    /// <c>scipy.signal.find_peaks</c> definition: height above the higher of
    /// the two valleys reached while walking outward until a strictly higher
    /// sample is met. Filters ripples on a sloped baseline. Negative disables.
    /// </summary>
    public double MinimumProminence { get; init; } = 0.0;

    /// <summary>
    /// Half-window (in data points) for validating a local maximum: the
    /// candidate must be the largest value within ±Window neighbours.
    /// </summary>
    public int Window { get; init; } = 3;

    /// <summary>
    /// Maximum number of peaks to return, sorted by intensity descending.
    /// Use 0 to return every peak that passed the filters.
    /// </summary>
    public int MaxPeaks { get; init; } = 20;

    /// <summary>Restrict detection to ppm &gt;= this value. NaN disables.</summary>
    public double PpmMin { get; init; } = double.NaN;

    /// <summary>Restrict detection to ppm &lt;= this value. NaN disables.</summary>
    public double PpmMax { get; init; } = double.NaN;
}

/// <summary>
/// One detected intensity maximum. The ppm position is refined by fitting a
/// parabola through the maximum and its two neighbours, so it is not snapped
/// to the sampling grid.
/// </summary>
public sealed record NmrPeakResult
{
    public required double Ppm { get; init; }

    public required double Intensity { get; init; }

    /// <summary>Index of the candidate sample in the dataset.</summary>
    public required int SampleIndex { get; init; }

    public bool HasResult => double.IsFinite(Ppm) && double.IsFinite(Intensity);
}

/// <summary>
/// Default snap window (ppm) for refining a click-added peak marker. ¹H
/// spectra are sampled finely, so a small window catches the clicked peak
/// without crossing into a neighbouring multiplet.
/// </summary>
public static class NmrPeakManualDefaults
{
    public const double SnapWindowPpm = 0.05;
}

/// <summary>
/// Locates intensity maxima on the real part of a 1D NMR spectrum. Ported
/// from <c>SpectrumAnalyzer.Core.IrPeakFinder</c>; the parabolic
/// interpolation keeps the signed X direction, so a ppm-descending axis (the
/// NMR convention) places the apex on the correct side of the peak.
/// </summary>
public static class NmrPeakDetector
{
    /// <summary>
    /// Refine a clicked ppm position by snapping to the local intensity
    /// maximum within <paramref name="snapWindowPpm"/> and interpolating.
    /// Returns null when the dataset has no finite samples.
    /// </summary>
    public static NmrPeakResult? RefineManualPeak(
        NmrDataset dataset,
        double clickedPpm,
        double snapWindowPpm = NmrPeakManualDefaults.SnapWindowPpm)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (!double.IsFinite(clickedPpm))
        {
            return null;
        }

        var xs = dataset.XValues;
        var ys = dataset.YValues;
        if (xs.Length == 0)
        {
            return null;
        }

        var window = double.IsFinite(snapWindowPpm) ? Math.Abs(snapWindowPpm) : 0.0;
        var lo = clickedPpm - window;
        var hi = clickedPpm + window;

        var bestIdx = -1;
        var bestY = double.NegativeInfinity;
        for (var i = 0; i < xs.Length; i++)
        {
            if (xs[i] < lo || xs[i] > hi || !double.IsFinite(ys[i]))
            {
                continue;
            }

            if (ys[i] > bestY)
            {
                bestY = ys[i];
                bestIdx = i;
            }
        }

        if (bestIdx < 0)
        {
            // Window empty: fall back to the globally nearest finite sample.
            var bestDist = double.PositiveInfinity;
            for (var i = 0; i < xs.Length; i++)
            {
                if (!double.IsFinite(ys[i]))
                {
                    continue;
                }

                var d = Math.Abs(xs[i] - clickedPpm);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                    bestY = ys[i];
                }
            }

            if (bestIdx < 0)
            {
                return null;
            }
        }

        return Interpolated(xs, ys, bestIdx, bestY);
    }

    public static IReadOnlyList<NmrPeakResult> Find(NmrDataset dataset, NmrPeakFinderConfig config)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(config);

        var xs = dataset.XValues;
        var ys = dataset.YValues;
        if (xs.Length < 3)
        {
            return Array.Empty<NmrPeakResult>();
        }

        var window = Math.Max(1, config.Window);
        var minY = config.MinimumIntensity;
        var minProm = config.MinimumProminence;
        var lo = double.IsFinite(config.PpmMin) ? config.PpmMin : double.NegativeInfinity;
        var hi = double.IsFinite(config.PpmMax) ? config.PpmMax : double.PositiveInfinity;

        var peaks = new List<NmrPeakResult>();
        for (var i = 1; i < xs.Length - 1; i++)
        {
            if (xs[i] < lo || xs[i] > hi)
            {
                continue;
            }

            var y = ys[i];
            if (!double.IsFinite(y) || y < minY || !IsLocalMaximum(ys, i, window))
            {
                continue;
            }

            if (double.IsFinite(minProm) && minProm > 0)
            {
                var prominence = ComputeProminence(ys, i);
                if (!double.IsFinite(prominence) || prominence < minProm)
                {
                    continue;
                }
            }

            peaks.Add(Interpolated(xs, ys, i, ys[i]));
        }

        if (peaks.Count == 0)
        {
            return Array.Empty<NmrPeakResult>();
        }

        peaks.Sort((a, b) => b.Intensity.CompareTo(a.Intensity));

        if (config.MaxPeaks > 0 && peaks.Count > config.MaxPeaks)
        {
            peaks.RemoveRange(config.MaxPeaks, peaks.Count - config.MaxPeaks);
        }

        return peaks;
    }

    private static NmrPeakResult Interpolated(double[] xs, double[] ys, int index, double fallbackY)
    {
        var px = xs[index];
        var py = fallbackY;
        if (index > 0 && index < xs.Length - 1
            && double.IsFinite(ys[index - 1]) && double.IsFinite(ys[index + 1]))
        {
            var (ix, iy) = ParabolicInterpolate(
                xs[index - 1], ys[index - 1],
                xs[index], ys[index],
                xs[index + 1], ys[index + 1]);
            if (double.IsFinite(ix) && double.IsFinite(iy))
            {
                px = ix;
                py = iy;
            }
        }

        return new NmrPeakResult { Ppm = px, Intensity = py, SampleIndex = index };
    }

    private static double ComputeProminence(double[] ys, int index)
    {
        var peakY = ys[index];
        if (!double.IsFinite(peakY))
        {
            return double.NaN;
        }

        var leftMin = peakY;
        for (var j = index - 1; j >= 0; j--)
        {
            var y = ys[j];
            if (!double.IsFinite(y))
            {
                continue;
            }

            if (y > peakY)
            {
                break;
            }

            if (y < leftMin)
            {
                leftMin = y;
            }
        }

        var rightMin = peakY;
        for (var j = index + 1; j < ys.Length; j++)
        {
            var y = ys[j];
            if (!double.IsFinite(y))
            {
                continue;
            }

            if (y > peakY)
            {
                break;
            }

            if (y < rightMin)
            {
                rightMin = y;
            }
        }

        return peakY - Math.Max(leftMin, rightMin);
    }

    private static bool IsLocalMaximum(double[] ys, int index, int window)
    {
        var lo = Math.Max(0, index - window);
        var hi = Math.Min(ys.Length - 1, index + window);
        for (var j = lo; j <= hi; j++)
        {
            if (j == index || !double.IsFinite(ys[j]))
            {
                continue;
            }

            if (ys[j] > ys[index])
            {
                return false;
            }
        }

        // Flat-top plateau collapse: report only the leftmost element of an
        // equal-valued plateau.
        for (var j = lo; j < index; j++)
        {
            if (double.IsFinite(ys[j]) && ys[j] < ys[index])
            {
                return true;
            }
        }

        if (lo == index)
        {
            for (var j = index + 1; j <= hi; j++)
            {
                if (double.IsFinite(ys[j]) && ys[j] < ys[index])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (double X, double Y) ParabolicInterpolate(
        double x0, double y0,
        double x1, double y1,
        double x2, double y2)
    {
        var denom = y0 - 2 * y1 + y2;
        if (Math.Abs(denom) < 1e-12)
        {
            return (x1, y1);
        }

        // Symmetric form; keeps the signed X direction so a ppm-descending
        // axis doesn't flip the apex to the wrong side of x1.
        var offset = 0.5 * (y0 - y2) / denom;
        var dx = (x2 - x0) / 2.0;
        var apexX = x1 + offset * dx;
        var apexY = y1 - 0.25 * (y0 - y2) * offset;
        return double.IsFinite(apexX) && double.IsFinite(apexY) ? (apexX, apexY) : (x1, y1);
    }
}

namespace SpectrumAnalyzer.Core;

/// <summary>
/// Configuration for <see cref="LambdaMaxFinder"/>.
/// </summary>
public sealed record LambdaMaxFinderConfig
{
    /// <summary>
    /// Minimum absorbance a local maximum must reach before being reported.
    /// Filters out noise spikes near the baseline. Negative values disable
    /// the filter so every local maximum is returned.
    /// </summary>
    public double MinimumAbsorbance { get; init; } = 0.01;

    /// <summary>
    /// Minimum half-window (in data points) used when validating a local
    /// maximum: the candidate must be the largest value within <c>±Window</c>
    /// neighbours. Larger values reject narrow noise spikes.
    /// </summary>
    public int Window { get; init; } = 3;

    /// <summary>
    /// Maximum number of peaks to return, sorted by absorbance descending.
    /// Use 0 to return all peaks that passed the filters.
    /// </summary>
    public int MaxPeaks { get; init; } = 5;

    /// <summary>
    /// Restrict detection to wavelengths &gt;= this value (nm). Set to NaN
    /// to disable.
    /// </summary>
    public double WavelengthMinNm { get; init; } = double.NaN;

    /// <summary>
    /// Restrict detection to wavelengths &lt;= this value (nm). Set to NaN
    /// to disable.
    /// </summary>
    public double WavelengthMaxNm { get; init; } = double.NaN;
}

/// <summary>
/// One detected absorbance maximum. The wavelength is interpolated by
/// fitting a parabola through the maximum and its two neighbours so the
/// reported value is not snapped to the underlying sampling grid.
/// </summary>
public sealed record LambdaMaxResult
{
    public required double WavelengthNm { get; init; }

    public required double AbsorbanceValue { get; init; }

    /// <summary>
    /// Index of the candidate sample in the dataset (the one whose
    /// absorbance was largest in the local window). Useful when callers want
    /// to render a marker at the original data point too.
    /// </summary>
    public required int SampleIndex { get; init; }

    public bool HasResult => double.IsFinite(WavelengthNm) && double.IsFinite(AbsorbanceValue);
}

/// <summary>
/// Locates absorbance maxima (λmax) on UV-Vis wavelength scans. Operates in
/// Absorbance space; transmittance datasets are converted on the fly.
/// Datasets that are neither A nor T are ignored.
/// </summary>
public static class LambdaMaxFinder
{
    public static IReadOnlyList<LambdaMaxResult> Find(
        SpectrumDataset dataset,
        LambdaMaxFinderConfig config)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(config);

        if (!dataset.IsWavelengthScan)
        {
            return Array.Empty<LambdaMaxResult>();
        }

        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance))
        {
            return Array.Empty<LambdaMaxResult>();
        }

        var xs = dataset.XValues;
        var ys = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Absorbance);
        if (xs.Length < 3)
        {
            return Array.Empty<LambdaMaxResult>();
        }

        var window = Math.Max(1, config.Window);
        var minAbs = config.MinimumAbsorbance;
        var lo = double.IsFinite(config.WavelengthMinNm) ? config.WavelengthMinNm : double.NegativeInfinity;
        var hi = double.IsFinite(config.WavelengthMaxNm) ? config.WavelengthMaxNm : double.PositiveInfinity;

        var peaks = new List<LambdaMaxResult>();
        for (var i = 1; i < xs.Length - 1; i++)
        {
            if (xs[i] < lo || xs[i] > hi) continue;
            var y = ys[i];
            if (!double.IsFinite(y) || y < minAbs) continue;
            if (!IsLocalMaximum(ys, i, window)) continue;

            var (interpX, interpY) = ParabolicInterpolate(
                xs[i - 1], ys[i - 1],
                xs[i], ys[i],
                xs[i + 1], ys[i + 1]);

            peaks.Add(new LambdaMaxResult
            {
                WavelengthNm = interpX,
                AbsorbanceValue = interpY,
                SampleIndex = i,
            });
        }

        if (peaks.Count == 0)
        {
            return Array.Empty<LambdaMaxResult>();
        }

        peaks.Sort((a, b) => b.AbsorbanceValue.CompareTo(a.AbsorbanceValue));

        if (config.MaxPeaks > 0 && peaks.Count > config.MaxPeaks)
        {
            peaks.RemoveRange(config.MaxPeaks, peaks.Count - config.MaxPeaks);
        }

        return peaks;
    }

    private static bool IsLocalMaximum(double[] ys, int index, int window)
    {
        var lo = Math.Max(0, index - window);
        var hi = Math.Min(ys.Length - 1, index + window);
        for (var j = lo; j <= hi; j++)
        {
            if (j == index) continue;
            if (!double.IsFinite(ys[j])) continue;
            if (ys[j] > ys[index]) return false;
        }

        // Reject perfectly flat regions (every neighbour equal): they are not
        // peaks in any meaningful sense.
        var anyLess = false;
        for (var j = lo; j <= hi; j++)
        {
            if (j == index) continue;
            if (double.IsFinite(ys[j]) && ys[j] < ys[index])
            {
                anyLess = true;
                break;
            }
        }

        return anyLess;
    }

    /// <summary>
    /// Returns the (x, y) of the apex of the parabola through the three
    /// supplied points. Falls back to the centre point when the discriminant
    /// is non-positive (flat or rising/falling triple).
    /// </summary>
    private static (double X, double Y) ParabolicInterpolate(
        double x0, double y0,
        double x1, double y1,
        double x2, double y2)
    {
        var denom = (y0 - 2 * y1 + y2);
        if (Math.Abs(denom) < 1e-12)
        {
            return (x1, y1);
        }

        // Symmetric form: assumes near-uniform spacing. JASCO V-series
        // wavelength scans have a constant DELTAX, so this is exact.
        var offset = 0.5 * (y0 - y2) / denom;
        var dxLeft = x1 - x0;
        var dxRight = x2 - x1;
        var dx = (Math.Abs(dxLeft) + Math.Abs(dxRight)) / 2.0;
        var apexX = x1 + offset * dx;
        var apexY = y1 - 0.25 * (y0 - y2) * offset;
        if (!double.IsFinite(apexX) || !double.IsFinite(apexY))
        {
            return (x1, y1);
        }

        return (apexX, apexY);
    }
}

namespace SpectrumAnalyzer.Core;

/// <summary>
/// Configuration for <see cref="IrPeakFinder"/>.
/// </summary>
public sealed record IrPeakFinderConfig
{
    /// <summary>
    /// Minimum absorbance a local maximum must reach before being reported.
    /// Filters out noise spikes near the baseline. Negative values disable
    /// the filter so every local maximum is returned.
    /// </summary>
    public double MinimumAbsorbance { get; init; } = 0.05;

    /// <summary>
    /// Minimum prominence (in absorbance units) a candidate must clear.
    /// Prominence is the candidate's height above the highest valley reached
    /// while walking outward in either direction until a strictly higher
    /// point is encountered (or the data ends). This filters out ripples on
    /// top of a sloped baseline and the mini-bumps that flank a true peak —
    /// the classic <c>scipy.signal.find_peaks</c> definition. Negative values
    /// disable the filter.
    /// </summary>
    public double MinimumProminence { get; init; } = 0.02;

    /// <summary>
    /// Minimum half-window (in data points) used when validating a local
    /// maximum: the candidate must be the largest value within <c>±Window</c>
    /// neighbours. Larger values reject narrow noise spikes. IR scans are
    /// usually denser than UV-Vis so the default is wider here.
    /// </summary>
    public int Window { get; init; } = 5;

    /// <summary>
    /// Maximum number of peaks to return, sorted by absorbance descending.
    /// Use 0 to return all peaks that passed the filters.
    /// </summary>
    public int MaxPeaks { get; init; } = 5;

    /// <summary>
    /// Restrict detection to wavenumbers &gt;= this value (cm⁻¹). Set to NaN
    /// to disable. Useful when the user wants to ignore the high-frequency
    /// O-H / N-H stretch region.
    /// </summary>
    public double WavenumberMinCm1 { get; init; } = double.NaN;

    /// <summary>
    /// Restrict detection to wavenumbers &lt;= this value (cm⁻¹). Set to NaN
    /// to disable. Useful when the user wants to ignore the fingerprint
    /// region.
    /// </summary>
    public double WavenumberMaxCm1 { get; init; } = double.NaN;
}

/// <summary>
/// One detected absorbance maximum on an IR (wavenumber-axis) spectrum. The
/// wavenumber is interpolated by fitting a parabola through the maximum and
/// its two neighbours so the reported value is not snapped to the underlying
/// sampling grid.
/// </summary>
public sealed record IrPeakResult
{
    public required double WavenumberCm1 { get; init; }

    public required double AbsorbanceValue { get; init; }

    /// <summary>
    /// Index of the candidate sample in the dataset (the one whose
    /// absorbance was largest in the local window). Useful when callers want
    /// to render a marker at the original data point too.
    /// </summary>
    public required int SampleIndex { get; init; }

    public bool HasResult => double.IsFinite(WavenumberCm1) && double.IsFinite(AbsorbanceValue);
}

/// <summary>
/// One manually-added IR peak marker. Stored alongside the formatting config
/// so it survives session save/load. The dataset is identified by a stable
/// key (Title → SourceFilePath → synthetic) and the wavenumber is stored as
/// the already-refined cm⁻¹ value (snapped to the local maximum at click time).
/// </summary>
public sealed record ManualIrPeakEntry
{
    public required string DatasetKey { get; init; }

    public required double WavenumberCm1 { get; init; }
}

/// <summary>
/// Default snap window (cm⁻¹) used when refining a click-added IR peak
/// marker against the underlying data points. IR scans are typically sampled
/// at 1–4 cm⁻¹ steps, so a 20 cm⁻¹ window catches the peak the user clicked
/// near without crossing into a neighbouring band.
/// </summary>
public static class IrPeakManualDefaults
{
    public const double SnapWindowCm1 = 20.0;
}

/// <summary>
/// Locates absorbance maxima on IR (wavenumber-axis) scans. Operates in
/// Absorbance space; transmittance datasets are converted on the fly so a
/// transmittance dip — visually a downward spike — is detected as the same
/// absorbance peak it represents. Datasets that are neither A nor T (e.g.
/// reflectance) are ignored.
/// </summary>
public static class IrPeakFinder
{
    /// <summary>
    /// Builds an <see cref="IrPeakResult"/> from a clicked wavenumber by
    /// snapping to the local absorbance maximum within
    /// <paramref name="snapWindowCm1"/> cm⁻¹ of the click and refining the
    /// position with parabolic interpolation. Returns <c>null</c> when the
    /// dataset is not an IR / wavenumber scan, has no Absorbance representation,
    /// or has no finite samples within the snap window (and the global
    /// nearest-neighbour fallback also fails).
    /// </summary>
    public static IrPeakResult? RefineManualPeak(
        SpectrumDataset dataset,
        double clickedWavenumberCm1,
        double snapWindowCm1 = IrPeakManualDefaults.SnapWindowCm1)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (!dataset.IsWavenumberAxis) return null;
        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance)) return null;
        if (!double.IsFinite(clickedWavenumberCm1)) return null;

        var xs = dataset.XValues;
        var ys = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Absorbance);
        if (xs.Length == 0) return null;

        var window = double.IsFinite(snapWindowCm1) ? Math.Abs(snapWindowCm1) : 0.0;
        var lo = clickedWavenumberCm1 - window;
        var hi = clickedWavenumberCm1 + window;

        var bestIdx = -1;
        var bestY = double.NegativeInfinity;
        for (var i = 0; i < xs.Length; i++)
        {
            if (xs[i] < lo || xs[i] > hi) continue;
            if (!double.IsFinite(ys[i])) continue;
            if (ys[i] > bestY)
            {
                bestY = ys[i];
                bestIdx = i;
            }
        }

        // Window is empty (clicked outside the scan range, or window=0):
        // fall back to the globally nearest finite sample so the marker
        // still lands somewhere meaningful.
        if (bestIdx < 0)
        {
            var bestDist = double.PositiveInfinity;
            for (var i = 0; i < xs.Length; i++)
            {
                if (!double.IsFinite(ys[i])) continue;
                var d = Math.Abs(xs[i] - clickedWavenumberCm1);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                    bestY = ys[i];
                }
            }
            if (bestIdx < 0) return null;
        }

        var interpX = xs[bestIdx];
        var interpY = bestY;
        if (bestIdx > 0 && bestIdx < xs.Length - 1
            && double.IsFinite(ys[bestIdx - 1]) && double.IsFinite(ys[bestIdx + 1]))
        {
            var (px, py) = ParabolicInterpolate(
                xs[bestIdx - 1], ys[bestIdx - 1],
                xs[bestIdx], ys[bestIdx],
                xs[bestIdx + 1], ys[bestIdx + 1]);
            if (double.IsFinite(px) && double.IsFinite(py))
            {
                interpX = px;
                interpY = py;
            }
        }

        return new IrPeakResult
        {
            WavenumberCm1 = interpX,
            AbsorbanceValue = interpY,
            SampleIndex = bestIdx,
        };
    }

    public static IReadOnlyList<IrPeakResult> Find(
        SpectrumDataset dataset,
        IrPeakFinderConfig config)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(config);

        if (!dataset.IsWavenumberAxis)
        {
            return Array.Empty<IrPeakResult>();
        }

        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance))
        {
            return Array.Empty<IrPeakResult>();
        }

        var xs = dataset.XValues;
        var ys = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Absorbance);
        if (xs.Length < 3)
        {
            return Array.Empty<IrPeakResult>();
        }

        var window = Math.Max(1, config.Window);
        var minAbs = config.MinimumAbsorbance;
        var minProm = config.MinimumProminence;
        var lo = double.IsFinite(config.WavenumberMinCm1) ? config.WavenumberMinCm1 : double.NegativeInfinity;
        var hi = double.IsFinite(config.WavenumberMaxCm1) ? config.WavenumberMaxCm1 : double.PositiveInfinity;

        var peaks = new List<IrPeakResult>();
        for (var i = 1; i < xs.Length - 1; i++)
        {
            if (xs[i] < lo || xs[i] > hi) continue;
            var y = ys[i];
            if (!double.IsFinite(y) || y < minAbs) continue;
            if (!IsLocalMaximum(ys, i, window)) continue;

            // Prominence rejects bumps that ride on a sloped baseline or
            // sit next to a much taller neighbour: those are exactly the
            // false positives that pile up around outliers / spikes.
            if (double.IsFinite(minProm) && minProm > 0)
            {
                var prominence = ComputeProminence(ys, i);
                if (!double.IsFinite(prominence) || prominence < minProm) continue;
            }

            var (interpX, interpY) = ParabolicInterpolate(
                xs[i - 1], ys[i - 1],
                xs[i], ys[i],
                xs[i + 1], ys[i + 1]);

            peaks.Add(new IrPeakResult
            {
                WavenumberCm1 = interpX,
                AbsorbanceValue = interpY,
                SampleIndex = i,
            });
        }

        if (peaks.Count == 0)
        {
            return Array.Empty<IrPeakResult>();
        }

        peaks.Sort((a, b) => b.AbsorbanceValue.CompareTo(a.AbsorbanceValue));

        if (config.MaxPeaks > 0 && peaks.Count > config.MaxPeaks)
        {
            peaks.RemoveRange(config.MaxPeaks, peaks.Count - config.MaxPeaks);
        }

        return peaks;
    }

    /// <summary>
    /// Classic <c>scipy.signal.find_peaks</c> prominence: walk outward from
    /// <paramref name="index"/> in each direction until a strictly higher
    /// sample is encountered (or the data ends), tracking the lowest sample
    /// passed along the way. The peak's prominence is its height above the
    /// higher of the two outer minima — the "shorter" base. Returns NaN
    /// when the candidate is not larger than at least one neighbour or the
    /// window is empty (degenerate input).
    /// </summary>
    private static double ComputeProminence(double[] ys, int index)
    {
        var peakY = ys[index];
        if (!double.IsFinite(peakY)) return double.NaN;

        // Walk left until we hit a sample strictly higher than the peak
        // (= a "wall"), or reach the edge. Track the minimum y along the way.
        var leftMin = peakY;
        for (var j = index - 1; j >= 0; j--)
        {
            var y = ys[j];
            if (!double.IsFinite(y)) continue;
            if (y > peakY) break;
            if (y < leftMin) leftMin = y;
        }

        var rightMin = peakY;
        for (var j = index + 1; j < ys.Length; j++)
        {
            var y = ys[j];
            if (!double.IsFinite(y)) continue;
            if (y > peakY) break;
            if (y < rightMin) rightMin = y;
        }

        // The "base" is the higher of the two outer minima — that is the
        // valley the peak still has to clear before merging into the
        // surrounding terrain.
        var baseLine = Math.Max(leftMin, rightMin);
        return peakY - baseLine;
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

        // Symmetric form: assumes near-uniform spacing. JASCO IR scans use a
        // constant DELTAX, so this is exact.
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

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
    /// Minimum prominence (in absorbance units) a candidate must clear.
    /// Prominence is the candidate's height above the highest valley reached
    /// while walking outward in either direction until a strictly higher
    /// point is encountered (or the data ends) — the same definition
    /// <c>scipy.signal.find_peaks</c> uses. Filters out shoulder ripples on
    /// a sloped baseline and the mini-bumps that flank a true peak. Default
    /// is 0 (disabled) so existing UV-Vis workflows keep their current
    /// behaviour; the IR finder uses a non-zero default because IR spectra
    /// have many more closely-spaced bumps.
    /// </summary>
    public double MinimumProminence { get; init; } = 0.0;

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
/// One manually-added λmax marker. Stored alongside the formatting config so
/// it survives session save/load. The dataset is identified by a stable key
/// (Title → SourceFilePath → synthetic) and the wavelength is stored as the
/// already-refined nm value (snapped to the local maximum at click time).
/// </summary>
public sealed record ManualLambdaMaxEntry
{
    public required string DatasetKey { get; init; }

    public required double WavelengthNm { get; init; }
}

/// <summary>
/// Default snap window (nm) used when refining a click-added λmax marker
/// against the underlying data points.
/// </summary>
public static class LambdaMaxManualDefaults
{
    public const double SnapWindowNm = 5.0;
}

/// <summary>
/// Locates absorbance maxima (λmax) on UV-Vis wavelength scans. Operates in
/// Absorbance space; transmittance datasets are converted on the fly.
/// Datasets that are neither A nor T are ignored.
/// </summary>
public static class LambdaMaxFinder
{
    /// <summary>
    /// Builds a <see cref="LambdaMaxResult"/> from a clicked wavelength by
    /// snapping to the local absorbance maximum within
    /// <paramref name="snapWindowNm"/> nm of the click and refining the
    /// position with parabolic interpolation. Returns <c>null</c> when the
    /// dataset is not a wavelength scan, has no Absorbance representation,
    /// or has no finite samples within the snap window (and the global
    /// nearest-neighbour fallback also fails).
    /// </summary>
    /// <remarks>
    /// Using the same Absorbance-space + parabolic interpolation pipeline as
    /// <see cref="Find"/> guarantees that a manual marker placed directly on
    /// the same peak the auto-detector found will land on the identical
    /// wavelength, so the two cannot disagree numerically.
    /// </remarks>
    public static LambdaMaxResult? RefineManualPeak(
        SpectrumDataset dataset,
        double clickedWavelengthNm,
        double snapWindowNm = LambdaMaxManualDefaults.SnapWindowNm)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (!dataset.IsWavelengthScan) return null;
        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance)) return null;
        if (!double.IsFinite(clickedWavelengthNm)) return null;

        var xs = dataset.XValues;
        var ys = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Absorbance);
        if (xs.Length == 0) return null;

        var window = double.IsFinite(snapWindowNm) ? Math.Abs(snapWindowNm) : 0.0;
        var lo = clickedWavelengthNm - window;
        var hi = clickedWavelengthNm + window;

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
                var d = Math.Abs(xs[i] - clickedWavelengthNm);
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

        return new LambdaMaxResult
        {
            WavelengthNm = interpX,
            AbsorbanceValue = interpY,
            SampleIndex = bestIdx,
        };
    }

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
        var minProm = config.MinimumProminence;
        var lo = double.IsFinite(config.WavelengthMinNm) ? config.WavelengthMinNm : double.NegativeInfinity;
        var hi = double.IsFinite(config.WavelengthMaxNm) ? config.WavelengthMaxNm : double.PositiveInfinity;

        var peaks = new List<LambdaMaxResult>();
        for (var i = 1; i < xs.Length - 1; i++)
        {
            if (xs[i] < lo || xs[i] > hi) continue;
            var y = ys[i];
            if (!double.IsFinite(y) || y < minAbs) continue;
            if (!IsLocalMaximum(ys, i, window)) continue;

            // Prominence filter: optional in UV-Vis (default off) because
            // most absorbance scans only have a handful of broad peaks, but
            // when enabled it removes shoulders / ripples next to a stronger
            // peak — the false positives that pile up around outliers.
            if (double.IsFinite(minProm) && minProm > 0)
            {
                var prominence = ComputeProminence(ys, i);
                if (!double.IsFinite(prominence) || prominence < minProm) continue;
            }

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

    /// <summary>
    /// Classic <c>scipy.signal.find_peaks</c> prominence: walk outward from
    /// <paramref name="index"/> in each direction until a strictly higher
    /// sample is encountered (or the data ends), tracking the lowest sample
    /// passed along the way. The peak's prominence is its height above the
    /// higher of the two outer minima — the "shorter" base. Returns NaN
    /// when the candidate sits on a flat plateau wider than the trace or
    /// the input is degenerate.
    /// </summary>
    private static double ComputeProminence(double[] ys, int index)
    {
        var peakY = ys[index];
        if (!double.IsFinite(peakY)) return double.NaN;

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

        // Flat-top plateau collapse: only the leftmost element of an
        // equal-valued plateau gets reported as a peak. Require at least
        // one strictly-less neighbour on the LEFT side; plateau interior
        // and right-edge points fall through to false even though their
        // window technically contains a strictly-less neighbour.
        for (var j = lo; j < index; j++)
        {
            if (double.IsFinite(ys[j]) && ys[j] < ys[index])
            {
                return true;
            }
        }

        // Boundary case: index sits at the very left of the trace (or
        // every left-side neighbour is equal). Accept only if a
        // strictly-less neighbour exists on the right, otherwise the
        // window is genuinely flat and not a peak.
        if (lo == index)
        {
            for (var j = index + 1; j <= hi; j++)
            {
                if (double.IsFinite(ys[j]) && ys[j] < ys[index]) return true;
            }
        }

        return false;
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

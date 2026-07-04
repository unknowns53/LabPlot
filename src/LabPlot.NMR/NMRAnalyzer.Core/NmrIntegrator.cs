namespace NMRAnalyzer.Core;

/// <summary>
/// Trapezoidal-rule integration of an NMR spectrum over a user-defined ppm
/// region, with optional linear baseline subtraction. Ported from
/// <c>SpectrumAnalyzer.Core.SpectrumIntegrator</c> and simplified: it works
/// on the real intensities directly (no Absorbance conversion) and its input
/// axis is ppm-descending, so the samples are reordered ascending once before
/// the direction-sensitive grid logic runs.
/// </summary>
public static class NmrIntegrator
{
    public static NmrIntegrationResult Integrate(NmrDataset dataset, NmrIntegrationRegion region)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(region);

        var grid = BuildGrid(dataset, region);
        if (grid is null)
        {
            return Empty(region);
        }

        var (gridX, gridY, yAtMin, yAtMax, pointCount) = grid.Value;
        var rawArea = Trapezoid(gridX, gridY);
        var baselineArea = region.Baseline == NmrBaselineMode.Linear
            ? Trapezoid(gridX, BuildLinear(gridX, yAtMin, yAtMax, region))
            : 0.0;

        return new NmrIntegrationResult
        {
            Region = region,
            Area = rawArea - baselineArea,
            RawArea = rawArea,
            BaselineArea = baselineArea,
            PointCount = pointCount,
        };
    }

    /// <summary>
    /// Return a copy of <paramref name="results"/> with <c>Ratio</c> filled
    /// in relative to <paramref name="referenceIndex"/>, scaled so the
    /// reference region equals <paramref name="referenceValue"/> (e.g. set
    /// the reference to the known proton count). Results with a
    /// non-positive reference area get a NaN ratio.
    /// </summary>
    public static IReadOnlyList<NmrIntegrationResult> NormalizeToReference(
        IReadOnlyList<NmrIntegrationResult> results,
        int referenceIndex,
        double referenceValue = 1.0)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (referenceIndex < 0 || referenceIndex >= results.Count)
        {
            return results;
        }

        var referenceArea = results[referenceIndex].Area;
        var scale = double.IsFinite(referenceArea) && referenceArea != 0.0
            ? referenceValue / referenceArea
            : double.NaN;

        var normalized = new NmrIntegrationResult[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            normalized[i] = results[i] with { Ratio = results[i].Area * scale };
        }

        return normalized;
    }

    /// <summary>
    /// Build the integration grid in ascending ppm order: dataset points
    /// strictly inside the region plus interpolated endpoint values. Returns
    /// null when the region is invalid, degenerate, or outside the data range.
    /// </summary>
    private static (double[] GridX, double[] GridY, double YAtMin, double YAtMax, int PointCount)? BuildGrid(
        NmrDataset dataset, NmrIntegrationRegion region)
    {
        if (!region.IsValid)
        {
            return null;
        }

        var (xs, ys) = AscendingSamples(dataset);
        if (xs.Length < 2 || region.PpmMin < xs[0] || region.PpmMax > xs[^1])
        {
            return null;
        }

        var first = -1;
        var last = -1;
        for (var i = 0; i < xs.Length; i++)
        {
            if (xs[i] >= region.PpmMin && xs[i] <= region.PpmMax)
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }
        }

        var yAtMin = InterpolateY(xs, ys, region.PpmMin);
        var yAtMax = InterpolateY(xs, ys, region.PpmMax);
        if (yAtMin is null || yAtMax is null)
        {
            return null;
        }

        if (first < 0 || last < first)
        {
            // No raw sample fell inside a narrow region: the trapezoid is
            // still defined by the two interpolated boundary values.
            return (
                new[] { region.PpmMin, region.PpmMax },
                new[] { yAtMin.Value, yAtMax.Value },
                yAtMin.Value,
                yAtMax.Value,
                0);
        }

        var prependMin = xs[first] > region.PpmMin;
        var appendMax = xs[last] < region.PpmMax;
        var len = (last - first + 1) + (prependMin ? 1 : 0) + (appendMax ? 1 : 0);
        var gridX = new double[len];
        var gridY = new double[len];

        var idx = 0;
        if (prependMin)
        {
            gridX[idx] = region.PpmMin;
            gridY[idx] = yAtMin.Value;
            idx++;
        }

        for (var i = first; i <= last; i++)
        {
            gridX[idx] = xs[i];
            gridY[idx] = ys[i];
            idx++;
        }

        if (appendMax)
        {
            gridX[idx] = region.PpmMax;
            gridY[idx] = yAtMax.Value;
        }

        return (gridX, gridY, yAtMin.Value, yAtMax.Value, last - first + 1);
    }

    /// <summary>
    /// The dataset's ppm axis is stored descending (high ppm first). Return
    /// (x, y) reordered ascending so the grid / trapezoid logic — which
    /// assumes increasing X — works unchanged.
    /// </summary>
    private static (double[] Xs, double[] Ys) AscendingSamples(NmrDataset dataset)
    {
        var xs = dataset.XValues;
        var ys = dataset.YValues;
        if (xs.Length < 2 || xs[0] <= xs[^1])
        {
            return (xs, ys);
        }

        var n = xs.Length;
        var rx = new double[n];
        var ry = new double[n];
        for (var i = 0; i < n; i++)
        {
            rx[i] = xs[n - 1 - i];
            ry[i] = ys[n - 1 - i];
        }

        return (rx, ry);
    }

    private static double Trapezoid(double[] x, double[] y)
    {
        var sum = 0.0;
        for (var i = 0; i < x.Length - 1; i++)
        {
            sum += (x[i + 1] - x[i]) * (y[i] + y[i + 1]) / 2.0;
        }

        return sum;
    }

    private static double[] BuildLinear(double[] gridX, double yAtMin, double yAtMax, NmrIntegrationRegion region)
    {
        var span = region.PpmMax - region.PpmMin;
        var slope = span > 0 ? (yAtMax - yAtMin) / span : 0.0;
        var result = new double[gridX.Length];
        for (var i = 0; i < gridX.Length; i++)
        {
            result[i] = yAtMin + slope * (gridX[i] - region.PpmMin);
        }

        return result;
    }

    private static double? InterpolateY(double[] xs, double[] ys, double x)
    {
        if (xs.Length < 2 || x < xs[0] || x > xs[^1])
        {
            return null;
        }

        var lo = 0;
        var hi = xs.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        if (xs[lo] == x)
        {
            return ys[lo];
        }

        if (xs[hi] == x)
        {
            return ys[hi];
        }

        var t = (x - xs[lo]) / (xs[hi] - xs[lo]);
        return ys[lo] + t * (ys[hi] - ys[lo]);
    }

    private static NmrIntegrationResult Empty(NmrIntegrationRegion region) => new()
    {
        Region = region,
        Area = double.NaN,
        RawArea = double.NaN,
        BaselineArea = double.NaN,
        PointCount = 0,
    };
}

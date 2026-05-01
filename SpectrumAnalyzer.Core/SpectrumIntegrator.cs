namespace SpectrumAnalyzer.Core;

/// <summary>
/// Trapezoidal-rule integration of a spectrum dataset over a user-defined
/// region, with optional linear baseline subtraction. Always operates in
/// Absorbance space — the dataset is internally converted from Transmittance
/// (T %) when needed via <see cref="SpectrumYAxisConverter"/>. Datasets whose
/// YUNITS cannot be expressed as Absorbance (Reflectance, temperature, …)
/// return an empty result.
/// </summary>
public static class SpectrumIntegrator
{
    public static IntegrationResult Integrate(SpectrumDataset dataset, IntegrationRegion region)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(region);

        if (!region.IsValid)
        {
            return Empty(region);
        }

        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance))
        {
            return Empty(region);
        }

        var xs = dataset.XValues;
        var ys = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Absorbance);
        if (xs.Length < 2)
        {
            return Empty(region);
        }

        // The dataset is sorted ascending by X (JascoSpectrumReader sorts on
        // load). If the region falls outside the dataset's X range we return
        // an empty result — the user should narrow their region.
        if (region.XMin < xs[0] || region.XMax > xs[^1])
        {
            return Empty(region);
        }

        // Collect indices of dataset points strictly inside [XMin, XMax].
        var first = -1;
        var last = -1;
        for (var i = 0; i < xs.Length; i++)
        {
            if (xs[i] >= region.XMin && xs[i] <= region.XMax)
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }
        }

        if (first < 0 || last < first)
        {
            return Empty(region);
        }

        // Y at the exact region boundaries via linear interpolation between
        // the bracketing data points. This keeps the integral accurate even
        // when XMin / XMax don't coincide with a sampled X.
        var yAtMin = InterpolateY(xs, ys, region.XMin) ?? ys[first];
        var yAtMax = InterpolateY(xs, ys, region.XMax) ?? ys[last];

        var rawArea = 0.0;

        // Leading partial trapezoid: from XMin (interpolated) to xs[first].
        if (xs[first] > region.XMin)
        {
            rawArea += (xs[first] - region.XMin) * (yAtMin + ys[first]) / 2.0;
        }

        // Interior full trapezoids.
        for (var i = first; i < last; i++)
        {
            rawArea += (xs[i + 1] - xs[i]) * (ys[i] + ys[i + 1]) / 2.0;
        }

        // Trailing partial trapezoid: from xs[last] to XMax (interpolated).
        if (xs[last] < region.XMax)
        {
            rawArea += (region.XMax - xs[last]) * (ys[last] + yAtMax) / 2.0;
        }

        var baselineArea = region.BaselineMethod switch
        {
            BaselineMethod.None => 0.0,
            BaselineMethod.Linear => (region.XMax - region.XMin) * (yAtMin + yAtMax) / 2.0,
            _ => 0.0,
        };

        return new IntegrationResult
        {
            Region = region,
            Area = rawArea - baselineArea,
            RawArea = rawArea,
            BaselineArea = baselineArea,
            PointCount = last - first + 1,
        };
    }

    private static IntegrationResult Empty(IntegrationRegion region) => new()
    {
        Region = region,
        Area = double.NaN,
        RawArea = double.NaN,
        BaselineArea = double.NaN,
        PointCount = 0,
    };

    /// <summary>
    /// Linear interpolation of Y at the given X using the bracketing
    /// dataset points. Returns null if X is outside the dataset's range or
    /// fewer than two points are available.
    /// </summary>
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
}

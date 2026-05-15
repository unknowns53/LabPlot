namespace SpectrumAnalyzer.Core;

/// <summary>
/// Trapezoidal-rule integration of a spectrum dataset over a user-defined
/// region, with optional baseline subtraction. Always operates in
/// Absorbance space — the dataset is internally converted from Transmittance
/// (T %) when needed via <see cref="SpectrumYAxisConverter"/>. Datasets whose
/// YUNITS cannot be expressed as Absorbance (Reflectance, temperature, …)
/// return an empty result.
/// </summary>
public static class SpectrumIntegrator
{
    private const int MaxPolynomialOrder = 5;

    public static IntegrationResult Integrate(SpectrumDataset dataset, IntegrationRegion region)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(region);

        var grid = BuildGrid(dataset, region);
        if (grid is null)
        {
            return Empty(region);
        }

        var (gridX, gridY, _, _, pointCount) = grid.Value;
        var rawArea = Trapezoid(gridX, gridY);
        var baselineY = BuildBaseline(grid.Value, region);
        var baselineArea = baselineY is null ? 0.0 : Trapezoid(gridX, baselineY);

        return new IntegrationResult
        {
            Region = region,
            Area = rawArea - baselineArea,
            RawArea = rawArea,
            BaselineArea = baselineArea,
            PointCount = pointCount,
        };
    }

    /// <summary>
    /// Returns the (X, baseline Y) curve in Absorbance space for the given
    /// region, or <c>null</c> if the region is empty / invalid / the dataset
    /// cannot be expressed as Absorbance. Used by the UI to draw the actual
    /// baseline overlay; numerically equivalent to the baseline series
    /// integrated in <see cref="Integrate"/>.
    /// </summary>
    public static (double[] GridX, double[] BaselineY)? BuildBaselineCurve(
        SpectrumDataset dataset, IntegrationRegion region)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(region);

        var grid = BuildGrid(dataset, region);
        if (grid is null)
        {
            return null;
        }

        var baselineY = BuildBaseline(grid.Value, region);
        if (baselineY is null)
        {
            return null;
        }

        return (grid.Value.GridX, baselineY);
    }

    /// <summary>
    /// Build the integration grid: dataset points strictly inside the region
    /// plus interpolated endpoint values, in ascending X order. Returns
    /// <c>null</c> when the region is unintegrable for the same reasons
    /// <see cref="Integrate"/> returns an empty result.
    /// </summary>
    private static (double[] GridX, double[] GridY, double YAtMin, double YAtMax, int PointCount)? BuildGrid(
        SpectrumDataset dataset, IntegrationRegion region)
    {
        if (!region.IsValid)
        {
            return null;
        }

        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Absorbance))
        {
            return null;
        }

        var xs = dataset.XValues;
        var ys = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Absorbance);
        if (xs.Length < 2)
        {
            return null;
        }

        // The dataset is sorted ascending by X (JascoSpectrumReader sorts on
        // load). If the region falls outside the dataset's X range we refuse
        // — the user should narrow their region.
        if (region.XMin < xs[0] || region.XMax > xs[^1])
        {
            return null;
        }

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
            // No raw samples fell strictly inside the region — typical
            // for narrow regions sandwiched between two consecutive
            // dataset points. The trapezoidal area is still well-defined
            // from the two linearly-interpolated boundary values, so
            // return a two-point grid with PointCount=0 marking "no raw
            // samples". Integrate / BuildBaselineCurve then proceed on
            // gridX.Length >= 2 rather than requiring raw data inside.
            var yMinInterp = InterpolateY(xs, ys, region.XMin);
            var yMaxInterp = InterpolateY(xs, ys, region.XMax);
            if (yMinInterp is null || yMaxInterp is null)
            {
                return null;
            }
            return (
                new[] { region.XMin, region.XMax },
                new[] { yMinInterp.Value, yMaxInterp.Value },
                yMinInterp.Value,
                yMaxInterp.Value,
                0);
        }

        var yAtMin = InterpolateY(xs, ys, region.XMin) ?? ys[first];
        var yAtMax = InterpolateY(xs, ys, region.XMax) ?? ys[last];

        var prependXMin = xs[first] > region.XMin;
        var appendXMax = xs[last] < region.XMax;

        var len = (last - first + 1) + (prependXMin ? 1 : 0) + (appendXMax ? 1 : 0);
        var gridX = new double[len];
        var gridY = new double[len];

        var idx = 0;
        if (prependXMin)
        {
            gridX[idx] = region.XMin;
            gridY[idx] = yAtMin;
            idx++;
        }

        for (var i = first; i <= last; i++)
        {
            gridX[idx] = xs[i];
            gridY[idx] = ys[i];
            idx++;
        }

        if (appendXMax)
        {
            gridX[idx] = region.XMax;
            gridY[idx] = yAtMax;
        }

        return (gridX, gridY, yAtMin, yAtMax, last - first + 1);
    }

    private static double[]? BuildBaseline(
        (double[] GridX, double[] GridY, double YAtMin, double YAtMax, int PointCount) grid,
        IntegrationRegion region)
    {
        var (gridX, gridY, yAtMin, yAtMax, _) = grid;

        return region.BaselineMethod switch
        {
            BaselineMethod.None => null,
            BaselineMethod.Linear => BuildLinear(gridX, yAtMin, yAtMax, region),
            BaselineMethod.ConvexHull => BuildConvexHull(gridX, gridY)
                                         ?? BuildLinear(gridX, yAtMin, yAtMax, region),
            BaselineMethod.RubberBand => BuildRubberBand(gridX, gridY, region)
                                         ?? BuildLinear(gridX, yAtMin, yAtMax, region),
            BaselineMethod.RubberBandHull => BuildRubberBandHull(gridX, gridY, region)
                                         ?? BuildLinear(gridX, yAtMin, yAtMax, region),
            BaselineMethod.Polynomial => BuildPolynomial(gridX, gridY, region)
                                         ?? BuildLinear(gridX, yAtMin, yAtMax, region),
            _ => null,
        };
    }

    /// <summary>
    /// Trapezoidal-rule integration of two parallel arrays sampled at the
    /// same X grid.
    /// </summary>
    private static double Trapezoid(double[] x, double[] y)
    {
        var sum = 0.0;
        for (var i = 0; i < x.Length - 1; i++)
        {
            sum += (x[i + 1] - x[i]) * (y[i] + y[i + 1]) / 2.0;
        }

        return sum;
    }

    private static double[] BuildLinear(double[] gridX, double yAtMin, double yAtMax, IntegrationRegion region)
    {
        var span = region.XMax - region.XMin;
        var slope = span > 0 ? (yAtMax - yAtMin) / span : 0.0;
        var result = new double[gridX.Length];
        for (var i = 0; i < gridX.Length; i++)
        {
            result[i] = yAtMin + slope * (gridX[i] - region.XMin);
        }

        return result;
    }

    private static double[]? BuildConvexHull(double[] gridX, double[] gridY)
    {
        var hull = LowerHullIndices(gridX, gridY);
        return hull.Count < 2 ? null : SamplePiecewiseLinear(gridX, gridY, hull);
    }

    private static double[]? BuildRubberBand(double[] gridX, double[] gridY, IntegrationRegion region)
    {
        if (gridX.Length < 4)
        {
            return null;
        }

        var segments = Math.Clamp(region.RubberBandSegments, 2, gridX.Length);
        var xMin = gridX[0];
        var xMax = gridX[^1];
        var span = xMax - xMin;
        if (span <= 0)
        {
            return null;
        }

        // Rubber-band: split [XMin, XMax] into N equal-width segments, take
        // the lowest-Y point in each, then connect those minima with linear
        // segments. N is the user knob — small N gives a chord-like baseline
        // (segments are wider than the peak so each minimum lands on the
        // peak shoulders), while large N tracks the curve more closely.
        // Endpoints are pinned so the baseline always touches the region
        // edges and never extrapolates.
        var anchorIdx = new List<int> { 0 };
        var step = span / segments;
        for (var k = 0; k < segments; k++)
        {
            var loX = xMin + k * step;
            var hiX = k == segments - 1 ? xMax + 1.0 : xMin + (k + 1) * step;

            var bestIdx = -1;
            var bestY = double.PositiveInfinity;
            for (var i = 0; i < gridX.Length; i++)
            {
                if (gridX[i] >= loX && gridX[i] < hiX && gridY[i] < bestY)
                {
                    bestY = gridY[i];
                    bestIdx = i;
                }
            }

            if (bestIdx > anchorIdx[^1])
            {
                anchorIdx.Add(bestIdx);
            }
        }

        if (anchorIdx[^1] != gridX.Length - 1)
        {
            anchorIdx.Add(gridX.Length - 1);
        }

        return anchorIdx.Count < 2 ? null : SamplePiecewiseLinear(gridX, gridY, anchorIdx);
    }

    /// <summary>
    /// Bruker OPUS-style rubber-band: same N segment-minimum candidates as
    /// <see cref="BuildRubberBand"/>, but keep only the lower convex hull of
    /// those candidates so the baseline never rises onto a peak. Smoother
    /// (and safer) than the plain rubber-band, but the segment-count knob
    /// has limited effect once the hull has converged.
    /// </summary>
    private static double[]? BuildRubberBandHull(double[] gridX, double[] gridY, IntegrationRegion region)
    {
        if (gridX.Length < 4)
        {
            return null;
        }

        var segments = Math.Clamp(region.RubberBandSegments, 2, gridX.Length);
        var xMin = gridX[0];
        var xMax = gridX[^1];
        var span = xMax - xMin;
        if (span <= 0)
        {
            return null;
        }

        var candidates = new List<int> { 0 };
        var step = span / segments;
        for (var k = 0; k < segments; k++)
        {
            var loX = xMin + k * step;
            var hiX = k == segments - 1 ? xMax + 1.0 : xMin + (k + 1) * step;

            var bestIdx = -1;
            var bestY = double.PositiveInfinity;
            for (var i = 0; i < gridX.Length; i++)
            {
                if (gridX[i] >= loX && gridX[i] < hiX && gridY[i] < bestY)
                {
                    bestY = gridY[i];
                    bestIdx = i;
                }
            }

            if (bestIdx > candidates[^1])
            {
                candidates.Add(bestIdx);
            }
        }

        if (candidates[^1] != gridX.Length - 1)
        {
            candidates.Add(gridX.Length - 1);
        }

        var hull = LowerHullIndicesSubset(gridX, gridY, candidates);
        return hull.Count < 2 ? null : SamplePiecewiseLinear(gridX, gridY, hull);
    }

    private static double[]? BuildPolynomial(double[] gridX, double[] gridY, IntegrationRegion region)
    {
        var hull = LowerHullIndices(gridX, gridY);
        var order = Math.Clamp(region.PolynomialOrder, 1, MaxPolynomialOrder);
        if (hull.Count < order + 1)
        {
            return null;
        }

        var xMid = (region.XMin + region.XMax) / 2.0;
        var xHalf = (region.XMax - region.XMin) / 2.0;
        if (xHalf <= 0)
        {
            return null;
        }

        var n = hull.Count;
        var m = order + 1;
        var a = new double[n, m];
        var b = new double[n];
        for (var i = 0; i < n; i++)
        {
            var u = (gridX[hull[i]] - xMid) / xHalf;
            var p = 1.0;
            for (var j = 0; j < m; j++)
            {
                a[i, j] = p;
                p *= u;
            }

            b[i] = gridY[hull[i]];
        }

        var coeffs = SolveNormalEquations(a, b, n, m);
        if (coeffs is null)
        {
            return null;
        }

        var result = new double[gridX.Length];
        for (var i = 0; i < gridX.Length; i++)
        {
            var u = (gridX[i] - xMid) / xHalf;
            var y = 0.0;
            var p = 1.0;
            for (var j = 0; j < m; j++)
            {
                y += coeffs[j] * p;
                p *= u;
            }

            result[i] = y;
        }

        return result;
    }

    /// <summary>
    /// Indices of the lower convex hull of the (x, y) sequence (x ascending),
    /// computed via the monotone-chain algorithm (Andrew). Going left to
    /// right, the lower hull turns counterclockwise at each vertex; we drop
    /// clockwise / collinear turns (cross ≤ 0) so the returned list contains
    /// only true vertices.
    /// </summary>
    private static List<int> LowerHullIndices(double[] x, double[] y)
    {
        var hull = new List<int>(x.Length);
        for (var i = 0; i < x.Length; i++)
        {
            while (hull.Count >= 2 && Cross(x, y, hull[^2], hull[^1], i) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(i);
        }

        return hull;
    }

    /// <summary>
    /// Same as <see cref="LowerHullIndices"/> but operates on a pre-selected
    /// subset of indices into the (x, y) arrays (e.g. segment minima for
    /// the rubber-band-with-hull variant).
    /// </summary>
    private static List<int> LowerHullIndicesSubset(double[] x, double[] y, IReadOnlyList<int> indices)
    {
        var hull = new List<int>(indices.Count);
        for (var k = 0; k < indices.Count; k++)
        {
            var i = indices[k];
            while (hull.Count >= 2 && Cross(x, y, hull[^2], hull[^1], i) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(i);
        }

        return hull;
    }

    private static double Cross(double[] x, double[] y, int a, int b, int c)
    {
        return (x[b] - x[a]) * (y[c] - y[a]) - (y[b] - y[a]) * (x[c] - x[a]);
    }

    /// <summary>
    /// Linearly interpolate (x, y) at every grid X using the polyline
    /// connecting <paramref name="anchorIdx"/> samples (indices into x / y).
    /// Anchor X must be strictly increasing.
    /// </summary>
    private static double[] SamplePiecewiseLinear(double[] gridX, double[] gridY, IReadOnlyList<int> anchorIdx)
    {
        var result = new double[gridX.Length];
        var k = 0;
        for (var i = 0; i < gridX.Length; i++)
        {
            while (k < anchorIdx.Count - 2 && gridX[i] > gridX[anchorIdx[k + 1]])
            {
                k++;
            }

            var x0 = gridX[anchorIdx[k]];
            var x1 = gridX[anchorIdx[k + 1]];
            var y0 = gridY[anchorIdx[k]];
            var y1 = gridY[anchorIdx[k + 1]];
            var dx = x1 - x0;
            result[i] = dx > 0
                ? y0 + (y1 - y0) * (gridX[i] - x0) / dx
                : y0;
        }

        return result;
    }

    /// <summary>
    /// Solve the over-determined system A·c = b in the least-squares sense
    /// via the normal equations AᵀA · c = Aᵀb, using Gaussian elimination
    /// with partial pivoting. Returns <c>null</c> if the system is rank
    /// deficient (any pivot below 1e-14).
    /// </summary>
    private static double[]? SolveNormalEquations(double[,] a, double[] b, int rows, int cols)
    {
        // Form the cols × cols normal matrix and the cols-length rhs.
        var ata = new double[cols, cols];
        var atb = new double[cols];
        for (var i = 0; i < cols; i++)
        {
            for (var j = i; j < cols; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < rows; k++)
                {
                    sum += a[k, i] * a[k, j];
                }

                ata[i, j] = sum;
                ata[j, i] = sum;
            }

            var rhs = 0.0;
            for (var k = 0; k < rows; k++)
            {
                rhs += a[k, i] * b[k];
            }

            atb[i] = rhs;
        }

        // Gaussian elimination with partial pivoting on [ata | atb].
        for (var i = 0; i < cols; i++)
        {
            var pivotRow = i;
            var pivotMag = Math.Abs(ata[i, i]);
            for (var k = i + 1; k < cols; k++)
            {
                var mag = Math.Abs(ata[k, i]);
                if (mag > pivotMag)
                {
                    pivotMag = mag;
                    pivotRow = k;
                }
            }

            if (pivotMag < 1e-14)
            {
                return null;
            }

            if (pivotRow != i)
            {
                for (var j = i; j < cols; j++)
                {
                    (ata[i, j], ata[pivotRow, j]) = (ata[pivotRow, j], ata[i, j]);
                }

                (atb[i], atb[pivotRow]) = (atb[pivotRow], atb[i]);
            }

            var pivot = ata[i, i];
            for (var k = i + 1; k < cols; k++)
            {
                var factor = ata[k, i] / pivot;
                for (var j = i; j < cols; j++)
                {
                    ata[k, j] -= factor * ata[i, j];
                }

                atb[k] -= factor * atb[i];
            }
        }

        var coeffs = new double[cols];
        for (var i = cols - 1; i >= 0; i--)
        {
            var sum = atb[i];
            for (var j = i + 1; j < cols; j++)
            {
                sum -= ata[i, j] * coeffs[j];
            }

            coeffs[i] = sum / ata[i, i];
        }

        return coeffs;
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

namespace SpectrumAnalyzer.Core;

/// <summary>
/// Method used to estimate the cloud-point temperature (Tc) of a polymer
/// solution from a transmittance vs temperature curve.
/// </summary>
public enum CloudPointMethod
{
    /// <summary>
    /// Midpoint method: Tc is the temperature at which transmittance crosses
    /// a user-defined threshold (default 50 % of the curve's vertical range).
    /// Linear interpolation between the bracketing samples.
    /// </summary>
    Midpoint = 0,

    /// <summary>
    /// First-derivative peak method: Tc is the temperature at which the
    /// magnitude of dT/dTemp is largest. Computed with a centred difference
    /// after smoothing with a small moving average.
    /// </summary>
    FirstDerivativePeak = 1,

    /// <summary>
    /// Second-derivative extremum (onset) method: returns the temperature at
    /// which |d²T/dTemp²| is largest, restricted to the side of the
    /// inflection point that corresponds to the *start* of the original
    /// sweep. For a sigmoid this picks the curvature peak adjacent to the
    /// pre-transition baseline rather than the inflection itself, giving an
    /// estimate of the transition onset.
    /// </summary>
    SecondDerivativeExtremum = 2,
}

/// <summary>
/// Configuration for <see cref="CloudPointDetector"/>. Defaults are tuned for
/// PNIPAM-style LCST sweeps in transmittance (%).
/// </summary>
public sealed record CloudPointDetectionConfig
{
    public CloudPointMethod Method { get; init; } = CloudPointMethod.Midpoint;

    /// <summary>
    /// Threshold for the midpoint method in percent. 50 % matches the
    /// classical "T₅₀" definition; users sometimes prefer 80 % for sharper
    /// transitions.
    /// </summary>
    public double TransmittanceThresholdPercent { get; init; } = 50.0;

    /// <summary>
    /// Window size (number of points) for the moving average applied to T
    /// before computing the first derivative. A value &lt;= 1 disables
    /// smoothing.
    /// </summary>
    public int SmoothingWindow { get; init; } = 3;
}

/// <summary>
/// Outcome of a single cloud-point detection on a temperature scan.
/// </summary>
public sealed record CloudPointResult
{
    public required CloudPointMethod Method { get; init; }

    public required double TemperatureCelsius { get; init; }

    /// <summary>
    /// The transmittance (%) at the detected temperature. For the midpoint
    /// method this equals the configured threshold; for the derivative method
    /// it's the interpolated value at the steepest point.
    /// </summary>
    public required double TransmittancePercentAtTc { get; init; }

    /// <summary>
    /// Original direction of the underlying scan, recovered from the file
    /// header. Hysteresis pairing keys off this value.
    /// </summary>
    public required ScanDirection Direction { get; init; }

    /// <summary>
    /// True when the dataset's transmittance curve actually contains a usable
    /// transition around the threshold (or a sufficiently sharp slope for the
    /// derivative method).
    /// </summary>
    public bool HasResult => double.IsFinite(TemperatureCelsius);

    public static CloudPointResult Empty(CloudPointMethod method, ScanDirection direction) => new()
    {
        Method = method,
        TemperatureCelsius = double.NaN,
        TransmittancePercentAtTc = double.NaN,
        Direction = direction,
    };
}

/// <summary>
/// Estimates the cloud-point temperature (Tc) from a transmittance vs
/// temperature dataset.
/// </summary>
/// <remarks>
/// Operates in transmittance space (T %). When the source dataset is recorded
/// in absorbance the values are converted on the fly via
/// <see cref="SpectrumYAxisConverter"/>. Datasets that are not temperature
/// scans, or whose Y units are neither A nor T, return an empty result.
/// </remarks>
public static class CloudPointDetector
{
    public static CloudPointResult Detect(SpectrumDataset dataset, CloudPointDetectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(config);

        var direction = dataset.OriginalScanDirection;

        if (!dataset.IsTemperatureScan)
        {
            return CloudPointResult.Empty(config.Method, direction);
        }

        if (!SpectrumYAxisConverter.CanDisplay(dataset, YAxisDisplayMode.Transmittance))
        {
            return CloudPointResult.Empty(config.Method, direction);
        }

        var xs = dataset.XValues;
        var ts = SpectrumYAxisConverter.GetDisplayYValues(dataset, YAxisDisplayMode.Transmittance);
        if (xs.Length < 3)
        {
            return CloudPointResult.Empty(config.Method, direction);
        }

        return config.Method switch
        {
            CloudPointMethod.Midpoint => DetectByMidpoint(xs, ts, config, direction),
            CloudPointMethod.FirstDerivativePeak => DetectByDerivative(xs, ts, config, direction),
            CloudPointMethod.SecondDerivativeExtremum => DetectBySecondDerivativeExtremum(xs, ts, config, direction),
            _ => CloudPointResult.Empty(config.Method, direction),
        };
    }

    private static CloudPointResult DetectByMidpoint(
        double[] xs,
        double[] ts,
        CloudPointDetectionConfig config,
        ScanDirection direction)
    {
        var threshold = config.TransmittanceThresholdPercent;
        if (!double.IsFinite(threshold))
        {
            return CloudPointResult.Empty(CloudPointMethod.Midpoint, direction);
        }

        // Find the first interval [i, i+1] in which the curve crosses the
        // threshold. The dataset's points are sorted ascending in X (set up
        // by the reader), so we scan in that order regardless of which
        // direction the original sweep was acquired.
        for (var i = 0; i < ts.Length - 1; i++)
        {
            var y0 = ts[i];
            var y1 = ts[i + 1];
            if (!double.IsFinite(y0) || !double.IsFinite(y1)) continue;

            var crosses = (y0 - threshold) * (y1 - threshold) <= 0
                          && Math.Abs(y0 - y1) > double.Epsilon;
            if (!crosses) continue;

            var t = (threshold - y0) / (y1 - y0);
            var tc = xs[i] + t * (xs[i + 1] - xs[i]);
            return new CloudPointResult
            {
                Method = CloudPointMethod.Midpoint,
                TemperatureCelsius = tc,
                TransmittancePercentAtTc = threshold,
                Direction = direction,
            };
        }

        return CloudPointResult.Empty(CloudPointMethod.Midpoint, direction);
    }

    private static CloudPointResult DetectByDerivative(
        double[] xs,
        double[] ts,
        CloudPointDetectionConfig config,
        ScanDirection direction)
    {
        var smoothed = MovingAverage(ts, Math.Max(1, config.SmoothingWindow));
        var bestIndex = FindFirstDerivativePeakIndex(xs, smoothed);

        if (bestIndex < 0)
        {
            return CloudPointResult.Empty(CloudPointMethod.FirstDerivativePeak, direction);
        }

        return new CloudPointResult
        {
            Method = CloudPointMethod.FirstDerivativePeak,
            TemperatureCelsius = xs[bestIndex],
            TransmittancePercentAtTc = smoothed[bestIndex],
            Direction = direction,
        };
    }

    private static CloudPointResult DetectBySecondDerivativeExtremum(
        double[] xs,
        double[] ts,
        CloudPointDetectionConfig config,
        ScanDirection direction)
    {
        // Find the curvature peak (|d²T/dTemp²| max) on the side of the
        // inflection that corresponds to the original sweep's *baseline*
        // — i.e. the start of the experiment. Result reads as a transition
        // onset rather than the inflection itself.
        var smoothed = MovingAverage(ts, Math.Max(1, config.SmoothingWindow));
        var inflectionIndex = FindFirstDerivativePeakIndex(xs, smoothed);
        if (inflectionIndex < 1 || inflectionIndex >= smoothed.Length - 1)
        {
            return CloudPointResult.Empty(CloudPointMethod.SecondDerivativeExtremum, direction);
        }

        // Sorted X is always ascending after the reader. The "baseline side"
        // of the sweep depends on whether the original scan was heating
        // (started at low T → indices 1..inflection) or cooling (started at
        // high T → indices inflection..end). When the direction is unknown
        // we search both sides and pick the larger magnitude.
        //
        // The first/last `radius` points of `smoothed` see one-sided averages,
        // which injects a spurious curvature spike at the very edge even on
        // strictly linear data. Trim those points from the search so a true
        // sigmoid is required to produce a non-zero result.
        var radius = Math.Max(1, config.SmoothingWindow) / 2;
        var lowerBound = radius + 1;
        var upperBound = smoothed.Length - 2 - radius;
        if (lowerBound > upperBound)
        {
            return CloudPointResult.Empty(CloudPointMethod.SecondDerivativeExtremum, direction);
        }

        var (searchStart, searchEnd) = direction switch
        {
            ScanDirection.Heating => (lowerBound, Math.Min(inflectionIndex, upperBound)),
            ScanDirection.Cooling => (Math.Max(inflectionIndex, lowerBound), upperBound),
            _ => (lowerBound, upperBound),
        };

        if (searchStart > searchEnd)
        {
            return CloudPointResult.Empty(CloudPointMethod.SecondDerivativeExtremum, direction);
        }

        var bestIndex = -1;
        var bestAbsCurvature = 0.0;
        for (var i = searchStart; i <= searchEnd; i++)
        {
            if (i < 1 || i >= smoothed.Length - 1) continue;
            if (!double.IsFinite(smoothed[i - 1])
                || !double.IsFinite(smoothed[i])
                || !double.IsFinite(smoothed[i + 1]))
            {
                continue;
            }

            var dxLeft = xs[i] - xs[i - 1];
            var dxRight = xs[i + 1] - xs[i];
            if (dxLeft <= 0 || dxRight <= 0) continue;

            // Centred non-uniform second difference.
            var slopeLeft = (smoothed[i] - smoothed[i - 1]) / dxLeft;
            var slopeRight = (smoothed[i + 1] - smoothed[i]) / dxRight;
            var curvature = 2.0 * (slopeRight - slopeLeft) / (dxLeft + dxRight);

            var absCurvature = Math.Abs(curvature);
            if (absCurvature > bestAbsCurvature)
            {
                bestAbsCurvature = absCurvature;
                bestIndex = i;
            }
        }

        if (bestIndex < 0 || bestAbsCurvature <= 0)
        {
            return CloudPointResult.Empty(CloudPointMethod.SecondDerivativeExtremum, direction);
        }

        return new CloudPointResult
        {
            Method = CloudPointMethod.SecondDerivativeExtremum,
            TemperatureCelsius = xs[bestIndex],
            TransmittancePercentAtTc = smoothed[bestIndex],
            Direction = direction,
        };
    }

    private static int FindFirstDerivativePeakIndex(double[] xs, double[] smoothed)
    {
        var bestIndex = -1;
        var bestSlope = 0.0;
        for (var i = 1; i < smoothed.Length - 1; i++)
        {
            var dx = xs[i + 1] - xs[i - 1];
            if (dx <= 0 || !double.IsFinite(smoothed[i + 1]) || !double.IsFinite(smoothed[i - 1]))
            {
                continue;
            }

            var slope = (smoothed[i + 1] - smoothed[i - 1]) / dx;
            if (Math.Abs(slope) > Math.Abs(bestSlope))
            {
                bestSlope = slope;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static double[] MovingAverage(double[] source, int window)
    {
        if (window <= 1 || source.Length == 0)
        {
            return source;
        }

        var radius = window / 2;
        var result = new double[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            var sum = 0.0;
            var count = 0;
            for (var j = Math.Max(0, i - radius); j <= Math.Min(source.Length - 1, i + radius); j++)
            {
                if (!double.IsFinite(source[j])) continue;
                sum += source[j];
                count++;
            }

            result[i] = count > 0 ? sum / count : double.NaN;
        }

        return result;
    }
}

/// <summary>
/// Pairs heating/cooling cloud-point results to expose the hysteresis width
/// ΔT = Tc(cooling) − Tc(heating). Returns NaN when the pair is incomplete.
/// </summary>
public static class HysteresisAnalyzer
{
    public static double ComputeHysteresis(CloudPointResult? heating, CloudPointResult? cooling)
    {
        if (heating is null || cooling is null) return double.NaN;
        if (!heating.HasResult || !cooling.HasResult) return double.NaN;
        return cooling.TemperatureCelsius - heating.TemperatureCelsius;
    }
}

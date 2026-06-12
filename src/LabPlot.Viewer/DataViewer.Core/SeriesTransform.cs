namespace DataViewer.Core;

/// <summary>
/// Non-destructive per-series display transform. Applied in the fixed
/// order smoothing → normalize → offset (so the normalized maximum stays
/// exactly 1 and the offset shifts the final trace).
/// </summary>
public sealed record SeriesTransform
{
    /// <summary>Scale the series so its maximum absolute value becomes 1.</summary>
    public bool Normalize { get; init; }

    public double YOffset { get; init; }

    /// <summary>
    /// Centered moving-average window length. 0 / 1 disables smoothing;
    /// even values are rounded up to the next odd number.
    /// </summary>
    public int SmoothingWindow { get; init; }

    public static SeriesTransform Identity { get; } = new();

    public bool IsIdentity =>
        !Normalize && YOffset == 0 && SmoothingWindow <= 1;
}

public static class SeriesTransformer
{
    /// <summary>
    /// Applies <paramref name="transform"/> to a copy of
    /// <paramref name="values"/>; the input is never modified. NaN gaps
    /// are excluded from smoothing windows and from the normalization
    /// maximum, and stay NaN in the output.
    /// </summary>
    public static double[] Apply(ReadOnlySpan<double> values, SeriesTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        var result = values.ToArray();
        if (transform.IsIdentity)
        {
            return result;
        }

        if (transform.SmoothingWindow > 1)
        {
            result = SmoothCentered(result, transform.SmoothingWindow);
        }

        if (transform.Normalize)
        {
            NormalizeToUnitMax(result);
        }

        if (transform.YOffset != 0)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] += transform.YOffset;
            }
        }

        return result;
    }

    private static double[] SmoothCentered(double[] source, int window)
    {
        // 偶数窓は中心が定まらないので次の奇数へ丸める
        if (window % 2 == 0)
        {
            window++;
        }

        var half = window / 2;
        var result = new double[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            if (double.IsNaN(source[i]))
            {
                result[i] = double.NaN;
                continue;
            }

            var sum = 0.0;
            var count = 0;
            var from = Math.Max(0, i - half);
            var to = Math.Min(source.Length - 1, i + half);
            for (var j = from; j <= to; j++)
            {
                if (double.IsFinite(source[j]))
                {
                    sum += source[j];
                    count++;
                }
            }

            result[i] = count > 0 ? sum / count : double.NaN;
        }

        return result;
    }

    private static void NormalizeToUnitMax(double[] values)
    {
        var maxAbs = 0.0;
        foreach (var value in values)
        {
            if (double.IsFinite(value))
            {
                maxAbs = Math.Max(maxAbs, Math.Abs(value));
            }
        }

        // 全 NaN や最大 0 の系列はスケール不能なので素通しする
        if (maxAbs <= 0)
        {
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= maxAbs;
        }
    }
}

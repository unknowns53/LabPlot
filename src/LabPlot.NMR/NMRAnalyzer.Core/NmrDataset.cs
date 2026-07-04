namespace NMRAnalyzer.Core;

/// <summary>
/// Immutable 1D NMR spectrum. Modeled after <c>SpectrumAnalyzer.Core</c>'s
/// <c>SpectrumDataset</c>: lazily-cached axis arrays plus an axis-inversion
/// flag. The spectrum is the real part of the (already FT- and
/// phase-corrected) complex data read from a JEOL <c>.jdf</c> file.
/// </summary>
public sealed class NmrDataset
{
    private double[]? _xValues;
    private double[]? _yValues;

    public string? SourceFilePath { get; init; }

    public string? Title { get; init; }

    /// <summary>
    /// Number of spectral dimensions. Always 1 in this version — kept as a
    /// field so a future 2D reader has a place to branch without reshaping
    /// every caller. See <see cref="JdfReader"/> for the 2D-rejection point.
    /// </summary>
    public int Dimensions { get; init; } = 1;

    /// <summary>
    /// ppm value at the first sample (the high-ppm end, e.g. 12.52), taken
    /// verbatim from the <c>.jdf</c> header's <c>data_axis_start</c>.
    /// </summary>
    public double AxisStartPpm { get; init; }

    /// <summary>
    /// ppm value at the last sample (the low-ppm end, e.g. -2.47), from the
    /// header's <c>data_axis_stop</c>.
    /// </summary>
    public double AxisStopPpm { get; init; }

    /// <summary>
    /// Observed (carrier) frequency in MHz, if recovered from the parameter
    /// section. Null when the parameter block was not parsed.
    /// </summary>
    public double? ObservedFrequencyMHz { get; init; }

    /// <summary>Measured nucleus label (e.g. "1H"), if known; otherwise null.</summary>
    public string? Nucleus { get; init; }

    /// <summary>Real part of the spectrum — this is what gets plotted.</summary>
    public IReadOnlyList<double> RealValues { get; init; } = Array.Empty<double>();

    /// <summary>Imaginary part; present only for complex spectra.</summary>
    public IReadOnlyList<double>? ImaginaryValues { get; init; }

    /// <summary>
    /// ppm axis, linearly spaced from <see cref="AxisStartPpm"/> to
    /// <see cref="AxisStopPpm"/> across the sample count. Built from the
    /// header's direct axis values, NOT from the sw/obs/car back-calculation
    /// (<c>guess_udic</c>), which is off by ~1.8 ppm on processed spectra.
    /// Lazily cached.
    /// </summary>
    public double[] XValues => _xValues ??= BuildPpmAxis();

    /// <summary>Real part as a materialized array for plotting. Lazily cached.</summary>
    public double[] YValues => _yValues ??= RealValues as double[] ?? RealValues.ToArray();

    /// <summary>
    /// ppm axes are conventionally displayed descending (high ppm on the
    /// left). Mirrors <c>SpectrumDataset.IsWavenumberAxis</c>; drives the
    /// automatic X-axis inversion in the UI.
    /// </summary>
    public bool IsPpmAxis { get; init; } = true;

    private double[] BuildPpmAxis()
    {
        var n = RealValues.Count;
        if (n == 0)
        {
            return Array.Empty<double>();
        }

        if (n == 1)
        {
            return new[] { AxisStartPpm };
        }

        var axis = new double[n];
        var step = (AxisStopPpm - AxisStartPpm) / (n - 1);
        for (var i = 0; i < n; i++)
        {
            axis[i] = AxisStartPpm + step * i;
        }

        // Pin the exact endpoint so floating-point drift can't shift the
        // last sample away from the header's declared stop value.
        axis[n - 1] = AxisStopPpm;
        return axis;
    }
}

namespace GpcAnalyzer.Core;

public sealed class MolecularWeightDataset
{
    private double[]? _logMolecularWeightValues;
    private double[]? _signalValues;

    public string? SourceFilePath { get; init; }

    public required string Solvent { get; init; }

    public required string Detector { get; init; }

    public string XLabel { get; init; } = DefaultLabels.MolecularWeightDatasetXLabel;

    public string YLabel { get; init; } = DefaultLabels.MolecularWeightDatasetYLabel;

    public MolecularWeightYMode YMode { get; init; } = MolecularWeightYMode.Signal;

    public MolecularWeightStatistics? Statistics { get; init; }

    public double MinMolecularWeight { get; init; } = MolecularWeightConverter.DefaultMinMolecularWeight;

    public double MaxMolecularWeight { get; init; } = MolecularWeightConverter.DefaultMaxMolecularWeight;

    public int SourcePointCount { get; init; }

    public int FilteredOutPointCount => Math.Max(0, SourcePointCount - Points.Count);

    /// <summary>
    /// Number of source retention-time points whose calibration result
    /// overflowed past Math.Pow(10, logM) → Infinity. These rows are
    /// dropped from the dataset (they cannot be plotted on a log MW axis)
    /// but counting them lets the UI surface a "your data goes outside the
    /// calibration window" warning instead of silently discarding peaks.
    /// </summary>
    public int OverflowedPointCount { get; init; }

    /// <summary>
    /// Number of adjacent retention-time-sorted pairs where the calibration
    /// polynomial's logM moved against the dominant direction. Non-zero
    /// values indicate the chromatogram contains points outside the
    /// retention-time window the cubic fit was originally trained on, so MW
    /// assignment for those points is extrapolation rather than calibration.
    /// </summary>
    public int CalibrationDirectionReversalCount { get; init; }

    /// <summary>
    /// True when at least one warning condition was hit during conversion
    /// (overflow, calibration direction reversal). Used to decorate the UI
    /// rather than to abort processing — the data still gets plotted on
    /// whatever portion of the trace is well-behaved.
    /// </summary>
    public bool HasCalibrationWarnings =>
        OverflowedPointCount > 0 || CalibrationDirectionReversalCount > 0;

    public IReadOnlyList<MolecularWeightDataPoint> Points { get; init; } = Array.Empty<MolecularWeightDataPoint>();

    public double[] LogMolecularWeightValues =>
        _logMolecularWeightValues ??= Points.Select(GetLogMolecularWeight).ToArray();

    public double[] SignalValues => _signalValues ??= Points.Select(point => point.Signal).ToArray();

    private static double GetLogMolecularWeight(MolecularWeightDataPoint point)
    {
        if (double.IsFinite(point.LogMolecularWeight))
        {
            return point.LogMolecularWeight;
        }

        return point.MolecularWeight > 0
            ? Math.Log10(point.MolecularWeight)
            : double.NaN;
    }
}

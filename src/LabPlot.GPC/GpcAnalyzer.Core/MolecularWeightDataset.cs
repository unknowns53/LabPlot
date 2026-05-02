namespace GpcAnalyzer.Core;

public sealed class MolecularWeightDataset
{
    private double[]? _logMolecularWeightValues;
    private double[]? _signalValues;

    public string? SourceFilePath { get; init; }

    public required string Solvent { get; init; }

    public required string Detector { get; init; }

    public string XLabel { get; init; } = "Molecular Weight [Da]";

    public string YLabel { get; init; } = "Signal";

    public MolecularWeightYMode YMode { get; init; } = MolecularWeightYMode.Signal;

    public MolecularWeightStatistics? Statistics { get; init; }

    public double MinMolecularWeight { get; init; } = MolecularWeightConverter.DefaultMinMolecularWeight;

    public double MaxMolecularWeight { get; init; } = MolecularWeightConverter.DefaultMaxMolecularWeight;

    public int SourcePointCount { get; init; }

    public int FilteredOutPointCount => Math.Max(0, SourcePointCount - Points.Count);

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

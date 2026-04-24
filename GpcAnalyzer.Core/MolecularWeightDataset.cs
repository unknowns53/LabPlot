namespace GpcAnalyzer.Core;

public sealed class MolecularWeightDataset
{
    public string? SourceFilePath { get; init; }

    public required string Solvent { get; init; }

    public required string Detector { get; init; }

    public string XLabel { get; init; } = "Molecular Weight [Da]";

    public string YLabel { get; init; } = "Signal";

    public MolecularWeightYMode YMode { get; init; } = MolecularWeightYMode.Signal;

    public double MinMolecularWeight { get; init; } = MolecularWeightConverter.DefaultMinMolecularWeight;

    public double MaxMolecularWeight { get; init; } = MolecularWeightConverter.DefaultMaxMolecularWeight;

    public int SourcePointCount { get; init; }

    public int FilteredOutPointCount => Math.Max(0, SourcePointCount - Points.Count);

    public IReadOnlyList<MolecularWeightDataPoint> Points { get; init; } = Array.Empty<MolecularWeightDataPoint>();
}

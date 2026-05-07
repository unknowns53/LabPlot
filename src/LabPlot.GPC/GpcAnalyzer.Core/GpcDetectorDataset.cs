namespace GpcAnalyzer.Core;

public sealed class GpcDetectorDataset
{
    public required string Detector { get; init; }

    public string XLabel { get; init; } = DefaultLabels.ChromatogramDatasetXLabel;

    public string YLabel { get; init; } = DefaultLabels.ChromatogramDatasetYLabel;

    public MolecularWeightStatistics? MolecularWeightStatistics { get; init; }

    public IReadOnlyList<GpcDataPoint> Points { get; init; } = Array.Empty<GpcDataPoint>();
}

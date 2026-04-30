namespace GpcAnalyzer.Core;

public sealed class AnalysisExportEntry
{
    public required string DisplayName { get; init; }

    public string? SourceFilePath { get; init; }

    public string? Detector { get; init; }

    public string XLabel { get; init; } = "X";

    public string YLabel { get; init; } = "Y";

    public IReadOnlyList<GpcDataPoint> ChromatogramPoints { get; init; } = Array.Empty<GpcDataPoint>();

    public MolecularWeightStatistics? Statistics { get; init; }

    public MolecularWeightDataset? MolecularWeightDataset { get; init; }
}

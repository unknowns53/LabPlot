namespace GpcAnalyzer.Core;

public sealed class GpcDataset
{
    public string? SourceFilePath { get; init; }

    public string XLabel { get; init; } = "X";

    public string YLabel { get; init; } = "Y";

    public IReadOnlyList<GpcDataPoint> Points { get; init; } = Array.Empty<GpcDataPoint>();
}

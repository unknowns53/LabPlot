namespace GpcAnalyzer.Core;

public sealed class GpcDetectorDataset
{
    public required string Detector { get; init; }

    public string XLabel { get; init; } = "X";

    public string YLabel { get; init; } = "Y";

    public IReadOnlyList<GpcDataPoint> Points { get; init; } = Array.Empty<GpcDataPoint>();
}

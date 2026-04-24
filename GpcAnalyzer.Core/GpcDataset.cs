namespace GpcAnalyzer.Core;

public sealed class GpcDataset
{
    public string? SourceFilePath { get; init; }

    public string? Detector { get; init; }

    public string XLabel { get; init; } = "X";

    public string YLabel { get; init; } = "Y";

    public IReadOnlyList<GpcDataPoint> Points { get; init; } = Array.Empty<GpcDataPoint>();

    public IReadOnlyDictionary<string, GpcDetectorDataset> DetectorDatasets { get; init; }
        = new Dictionary<string, GpcDetectorDataset>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> AvailableDetectors => DetectorDatasets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool TryGetDetectorDataset(string detector, out GpcDetectorDataset detectorDataset)
    {
        return DetectorDatasets.TryGetValue(detector, out detectorDataset!);
    }

    public GpcDataset WithDetector(string detector)
    {
        if (!TryGetDetectorDataset(detector, out var detectorDataset))
        {
            return this;
        }

        return new GpcDataset
        {
            SourceFilePath = SourceFilePath,
            Detector = detectorDataset.Detector,
            XLabel = detectorDataset.XLabel,
            YLabel = detectorDataset.YLabel,
            Points = detectorDataset.Points,
            DetectorDatasets = DetectorDatasets,
        };
    }
}

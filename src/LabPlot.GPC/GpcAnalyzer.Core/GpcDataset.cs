namespace GpcAnalyzer.Core;

public sealed class GpcDataset
{
    private double[]? _xValues;
    private double[]? _yValues;
    private readonly Dictionary<string, GpcDataset> _detectorCache = new(StringComparer.OrdinalIgnoreCase);

    public string? SourceFilePath { get; init; }

    public string? Detector { get; init; }

    public string XLabel { get; init; } = DefaultLabels.ChromatogramDatasetXLabel;

    public string YLabel { get; init; } = DefaultLabels.ChromatogramDatasetYLabel;

    public MolecularWeightStatistics? MolecularWeightStatistics { get; init; }

    public IReadOnlyList<GpcDataPoint> Points { get; init; } = Array.Empty<GpcDataPoint>();

    public double[] XValues => _xValues ??= Points.Select(point => point.X).ToArray();

    public double[] YValues => _yValues ??= Points.Select(point => point.Y).ToArray();

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

        if (_detectorCache.TryGetValue(detectorDataset.Detector, out var cached))
        {
            return cached;
        }

        var dataset = new GpcDataset
        {
            SourceFilePath = SourceFilePath,
            Detector = detectorDataset.Detector,
            XLabel = detectorDataset.XLabel,
            YLabel = detectorDataset.YLabel,
            MolecularWeightStatistics = detectorDataset.MolecularWeightStatistics,
            Points = detectorDataset.Points,
            DetectorDatasets = DetectorDatasets,
        };
        _detectorCache[detectorDataset.Detector] = dataset;
        return dataset;
    }
}

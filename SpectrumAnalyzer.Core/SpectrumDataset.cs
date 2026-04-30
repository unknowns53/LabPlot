namespace SpectrumAnalyzer.Core;

public sealed class SpectrumDataset
{
    private double[]? _xValues;
    private double[]? _yValues;

    public string? SourceFilePath { get; init; }

    public string XLabel { get; init; } = "X";

    public string YLabel { get; init; } = "Y";

    public string? RawXUnits { get; init; }

    public string? RawYUnits { get; init; }

    public string? Title { get; init; }

    public IReadOnlyList<SpectrumDataPoint> Points { get; init; } = Array.Empty<SpectrumDataPoint>();

    public double[] XValues => _xValues ??= Points.Select(point => point.X).ToArray();

    public double[] YValues => _yValues ??= Points.Select(point => point.Y).ToArray();
}

namespace SpectrumAnalyzer.Core;

public sealed class AnalysisExportEntry
{
    public required string DisplayName { get; init; }

    public string? SourceFilePath { get; init; }

    public string XLabel { get; init; } = "X";

    public string YLabel { get; init; } = "Y";

    public IReadOnlyList<SpectrumDataPoint> Points { get; init; } = Array.Empty<SpectrumDataPoint>();
}

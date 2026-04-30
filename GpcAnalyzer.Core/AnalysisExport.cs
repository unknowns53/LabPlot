namespace GpcAnalyzer.Core;

public sealed class AnalysisExport
{
    public IReadOnlyList<AnalysisExportEntry> Entries { get; init; } = Array.Empty<AnalysisExportEntry>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string GeneratorName { get; init; } = "GPC Visualization";
}

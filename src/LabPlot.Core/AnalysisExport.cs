namespace LabPlot.Core;

/// <summary>
/// Container passed to every <see cref="IAnalysisExporter"/>. Holds the entry
/// list (one entry per dataset to export), a generation timestamp, and the
/// generator name that exporters write into the output header.
/// </summary>
/// <remarks>
/// <see cref="Entries"/> is typed as the abstract base entry; concrete
/// exporters cast each item to their app-specific subclass when reading the
/// dataset payload. <see cref="GeneratorName"/> defaults to <c>"LabPlot"</c>
/// so callers that forget to set it produce a recognisable string; in
/// practice each app sets it explicitly when building the export
/// (<c>"GPC Visualization"</c> / <c>"Spectrum Visualization"</c>).
/// </remarks>
public sealed class AnalysisExport
{
    public IReadOnlyList<AnalysisExportEntry> Entries { get; init; } = Array.Empty<AnalysisExportEntry>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string GeneratorName { get; init; } = "LabPlot";
}

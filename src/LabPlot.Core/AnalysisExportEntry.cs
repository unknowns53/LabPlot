namespace LabPlot.Core;

/// <summary>
/// Shared base for the per-app analysis export entries. Every LabPlot app
/// produces export rows with a display name, an optional source path and
/// X / Y axis labels — those four fields live here so each app's CSV / XLSX
/// exporter can rely on them via the base type.
/// </summary>
/// <remarks>
/// Each app declares its own concrete subclass that adds the dataset payload
/// it needs (GPC: chromatogram points + molecular weight statistics; Spectrum:
/// X-Y points). Exporters cast the base entry to their concrete subclass to
/// reach the payload — abstract here keeps consumers from accidentally
/// instantiating an empty base entry.
/// </remarks>
public abstract class AnalysisExportEntry
{
    public required string DisplayName { get; init; }

    public string? SourceFilePath { get; init; }

    public string XLabel { get; init; } = "X";

    public string YLabel { get; init; } = "Y";
}

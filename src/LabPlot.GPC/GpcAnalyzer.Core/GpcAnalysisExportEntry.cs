using LabPlot.Core;

namespace GpcAnalyzer.Core;

/// <summary>
/// GPC-specific analysis export row. Inherits the shared display / source /
/// axis-label fields from <see cref="AnalysisExportEntry"/> and adds the
/// chromatogram payload + molecular weight statistics that GPC reports
/// produce.
/// </summary>
public sealed class GpcAnalysisExportEntry : AnalysisExportEntry
{
    public string? Detector { get; init; }

    public IReadOnlyList<GpcDataPoint> ChromatogramPoints { get; init; } = Array.Empty<GpcDataPoint>();

    public MolecularWeightStatistics? Statistics { get; init; }

    public MolecularWeightDataset? MolecularWeightDataset { get; init; }
}

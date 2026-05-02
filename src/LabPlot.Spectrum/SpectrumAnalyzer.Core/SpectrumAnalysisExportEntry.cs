using LabPlot.Core;

namespace SpectrumAnalyzer.Core;

/// <summary>
/// Spectrum-specific analysis export row. Inherits the shared display /
/// source / axis-label fields from <see cref="AnalysisExportEntry"/> and
/// adds the X-Y point payload that spectrum reports produce.
/// </summary>
public sealed class SpectrumAnalysisExportEntry : AnalysisExportEntry
{
    public IReadOnlyList<SpectrumDataPoint> Points { get; init; } = Array.Empty<SpectrumDataPoint>();
}
